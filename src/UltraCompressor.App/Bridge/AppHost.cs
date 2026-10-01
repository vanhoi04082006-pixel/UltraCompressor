using System.Globalization;
using System.IO;
using System.Text.Json;
using UltraCompressor.Core;
using UltraCompressor.Core.Compare;
using UltraCompressor.Core.Diagnostics;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Processes;
using UltraCompressor.Core.Scheduling;
using UltraCompressor.Core.Storage;
using UltraCompressor.Core.Toolchain;

namespace UltraCompressor.App.Bridge;

/// <summary>Điều phối lệnh từ giao diện web tới lõi, và đẩy trạng thái ngược lại.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Tệp <c>.bak</c> cũ hơn ngần này thì coi là rơi vãi và dọn được.
    ///
    /// <para>Không cấu hình được, cố ý. Ngưỡng này chỉ dùng cho nút "Dọn tệp .bak rơi
    /// vãi" — dọn những tệp sót lại do ứng dụng bị tắt giữa chừng. Nếu để người dùng
    /// đặt ngưỡng quá nhỏ thì nút này sẽ xoá luôn bản gốc của những tệp **đang chờ
    /// duyệt**, tức là mất dữ liệu thật chứ không phải dọn rác.</para>
    /// </summary>
    private const int StrayBackupDays = 30;

    private readonly AppConfig _config;
    private readonly ToolChain _tools;
    private readonly FileLogger _logger;
    private readonly SessionStore _session;
    private readonly CompressionEngine _engine;
    private readonly CompareService _compare;

    private Func<string, Task>? _send;
    private CancellationTokenSource? _pumpCts;
    private volatile bool _dirty = true;
    private string? _openJobId;
    private ItemQuery _openQuery = ItemQuery.Default;
    private readonly Dictionary<string, (DateTimeOffset At, long Count)> _backupCount = new(StringComparer.Ordinal);
    private bool _disposed;


    public AppHost(AppConfig config, ToolChain tools, FileLogger logger, SessionStore session)
    {
        _config = config;
        _tools = tools;
        _logger = logger;
        _session = session;
        _engine = new CompressionEngine(config, tools, session, new TempWorkspace(AppPaths.TempRoot), logger);
        _engine.Changed += OnEngineChanged;
        _compare = new CompareService(tools.Locator, _engine.Workspace, logger);
    }

    public CompressionEngine Engine => _engine;

    /// <summary>Mở hộp chọn thư mục của hệ điều hành. Gán từ cửa sổ chủ.</summary>
    public Func<IReadOnlyList<string>>? PickFolders { get; set; }

    /// <summary>Mở hộp chọn tệp lẻ (đa chọn). Gán từ cửa sổ chủ.</summary>
    public Func<IReadOnlyList<string>>? PickFiles { get; set; }

    /// <summary>Mở hộp chọn thư mục đích xuất kết quả. Gán từ cửa sổ chủ.</summary>
    public Func<string?>? PickExportFolder { get; set; }


    /// <summary>Đăng ký một tệp media và trả URL để giao diện nhúng trình phát.</summary>
    public Func<string, string?>? RegisterMedia { get; set; }

    /// <summary>Mở hộp chọn tệp thực thi. Gán từ cửa sổ chủ.</summary>
    public Func<string?>? PickToolFile { get; set; }

    // ---------------------------------------------------------------- đóng cửa sổ

    /// <summary>Người dùng chọn gì khi bấm đóng cửa sổ đang xử lý dở.</summary>
    public enum ExitChoice
    {
        /// <summary>Không thoát.</summary>
        Stay,

        /// <summary>Khôi phục bản gốc mọi tệp đã nén xong, rồi thoát.</summary>
        UndoAndExit,

        /// <summary>Giữ nguyên kết quả và ghi phiên, để lần sau bấm "Tiếp tục".</summary>
        SaveAndExit,
    }

    /// <summary>Tình trạng lúc đóng, đủ để dựng nội dung hộp thoại.</summary>
    public sealed record ExitPlan(
        bool IsBusy,
        int RunningJobs,
        int ActiveFiles,
        int AppliedFiles,
        IReadOnlyList<string> RunningJobNames,
        IReadOnlyList<string> AppliedJobNames);

    /// <summary>Chụp tình trạng hiện tại. Không khóa lâu, gọi được từ luồng đóng cửa sổ.</summary>
    public ExitPlan PlanExit()
    {
        var jobs = _engine.Jobs;
        var running = jobs.Where(j => j.Status is JobStatus.Running or JobStatus.Paused).ToList();

        return new ExitPlan(
            IsBusy: _engine.IsRunning || running.Count > 0,
            RunningJobs: running.Count,
            ActiveFiles: jobs.Sum(j => j.Items.Count(i => i.IsProcessing)),
            AppliedFiles: (int)jobs.Sum(j => PendingBackupCount(j)),
            RunningJobNames: [.. running.Select(j => j.FolderName)],
            AppliedJobNames: [.. jobs.Where(j => PendingBackupCount(j) > 0).Select(j => j.FolderName)]);
    }

    /// <summary>
    /// Khôi phục bản gốc mọi tệp đã nén xong. Dừng nén trước, không nén tiếp trong lúc
    /// khôi phục — nếu không sẽ tranh nhau trên cùng một tệp.
    /// </summary>
    public async Task<(int Restored, int Failed, IReadOnlyList<string> Errors)> UndoEverythingAsync()
    {
        await StopForExitAsync();

        var restored = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var job in _engine.Jobs)
        {
            var result = UndoService.RestoreAll(job);
            restored += result.Restored;
            failed += result.Failed;
            errors.AddRange(result.Errors);
        }

        ForgetBackupCounts();
        _logger.LogInfo("exit", $"Đóng khi đang xử lý: khôi phục {restored} bản gốc, {failed} lỗi.");
        await SaveSessionAsync();
        return (restored, failed, errors);
    }

    /// <summary>
    /// Dừng ở chỗ có thể nối tiếp: tạm dừng các job đang chạy rồi đợi tệp đang xử lý chạy
    /// nốt. Không huỷ, vì huỷ giữa lúc ffmpeg đang chạy thì lần sau phải nén lại từ đầu và
    /// dễ dính nén hai lần lên cùng một tệp.
    /// </summary>
    public async Task<bool> SaveForResumeAsync()
    {
        await StopForExitAsync();

        _logger.LogInfo("exit", "Đóng khi đang xử lý: lưu phiên để chạy tiếp lần sau.");
        await SaveSessionAsync();
        return true;
    }

    /// <summary>Tạm dừng và chờ tới khi không còn tệp nào đang xử lý.</summary>
    private async Task StopForExitAsync()
    {
        // Bật TRƯỚC khi tạm dừng: tệp đang chạy sẽ nốt rồi job kết thúc, và nếu lúc đó
        // tự duyệt thì .bak bị xoá mất trước khi người dùng kịp bấm "Hoàn tác".
        _engine.DeferAutoApprove = true;
        _engine.PauseAll();

        var deadline = DateTimeOffset.Now.AddSeconds(90);
        while (DateTimeOffset.Now < deadline)
        {
            if (!_engine.IsRunning && !_engine.Jobs.Any(j => j.Items.Any(i => i.IsProcessing))) break;
            await Task.Delay(200);
        }

        if (DateTimeOffset.Now >= deadline)
        {
            // Quá hạn: huỷ để không treo cửa sổ đang thoát. Tệp đang chạy bị bỏ dở sẽ nén
            // lại lần sau — thà mất một tệp còn hơn không đóng được.
            _logger.LogWarning("exit", "Tạm dừng quá 90 giây, chuyển sang huỷ để đóng.");
            _engine.CancelAll();
            await Task.Delay(500);
        }

        MakeResumable();
    }

    /// <summary>
    /// Đưa job về trạng thái "chờ" để lần sau bấm "Tiếp tục" là chạy được. Job nén thật
    /// đã giữ <c>.bak</c> thì nó đang ở "Chờ duyệt", mà engine chỉ nhận "Chờ"/"Tạm dừng".
    /// </summary>
    private void MakeResumable()
    {
        foreach (var job in _engine.Jobs)
        {
            if (job.Status is JobStatus.Running or JobStatus.PendingReview or JobStatus.Cancelled)
                job.Status = JobStatus.Waiting;
        }
    }

    /// <summary>Người dùng chọn ở lại: bỏ chế độ chờ duyệt, job trở lại tự duyệt bình thường.</summary>
    public void AbandonExitPreparation() => _engine.DeferAutoApprove = false;


    private async Task SaveSessionAsync() => await _session.SaveAsync(_engine.Jobs);

    // ---------------------------------------------------------------- khôi phục phiên

    private readonly HashSet<string> _restoredJobIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Nạp phiên lưu lần trước. Engine đã tự hạ trạng thái "đang chạy" về "chờ" và xoá cờ
    /// đang-xử-lý, nên job nạp vào đều có thể bấm chạy tiếp.
    /// </summary>
    public async Task<int> RestoreSessionAsync()
    {
        var (jobs, error) = await _engine.LoadSessionAsync();
        if (error is not null) _logger.LogWarning("session", $"Không đọc được phiên: {error}");

        lock (_restoredJobIds)
        {
            foreach (var job in jobs.Where(j => j.Status == JobStatus.Waiting)) _restoredJobIds.Add(job.Id);
        }

        if (jobs.Count > 0)
            _logger.LogInfo("session", $"Khôi phục {jobs.Count} job từ lần chạy trước.");

        return jobs.Count;
    }

    /// <summary>Job nạp từ phiên còn đang chờ thì bỏ cờ "khôi phục" khi người dùng bắt đầu.</summary>
    public void MarkResumed(IEnumerable<string> jobIds)
    {
        lock (_restoredJobIds)
        {
            foreach (var id in jobIds) _restoredJobIds.Remove(id);
        }
    }

    private bool WasRestored(Job job)
    {
        lock (_restoredJobIds) return _restoredJobIds.Contains(job.Id);
    }

    public void Attach(Func<string, Task> send)
    {
        _send = send;
        _pumpCts = new CancellationTokenSource();
        _ = PumpAsync(_pumpCts.Token);
    }

    public string? OpenJobId
    {
        get => _openJobId;
        set
        {
            _openJobId = value;
            QueuePush(force: true);
        }
    }

    // ---------------------------------------------------------------- đẩy trạng thái

    private void OnEngineChanged(object? sender, EngineEventArgs e) => QueuePush();

    /// <summary>
    /// Một vòng lặp nền gom các thay đổi rồi đẩy sang giao diện, tối đa một lần mỗi
    /// <see cref="PushInterval"/>. Engine báo thay đổi rất dày (mỗi lần tăng phần trăm) —
    /// nếu đẩy ngay từng lần thì giao diện sẽ nghẽn khi job có nhiều tệp.
    /// </summary>
    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(PushInterval);
            do
            {
                if (_dirty)
                {
                    _dirty = false;
                    await PushAsync();
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Đóng ứng dụng.
        }
    }

    private void QueuePush(bool force = false)
    {
        _dirty = true;
        if (force) _ = PushAsync();
    }

    private async Task PushAsync()
    {
        var send = _send;
        if (send is null) return;

        try
        {
            await send(BridgeJson.Serialize(new BridgeMessage { Event = "state", Data = JsonSerializer.SerializeToNode(BuildState(), BridgeJson.Options) }));

            if (!string.IsNullOrEmpty(_openJobId))
            {
                // Dùng lại đúng bộ lọc người dùng đang đặt. Trước đây lần đẩy định kỳ gửi
                // toàn bộ tệp của job, nên mỗi lần nhấn phím tìm kiếm chỉ sống được tới
                // lần đẩy kế tiếp (250 ms) — danh sách nhảy về nguyên trạng liên tục.
                var payload = BuildItems(_openJobId, _openQuery);
                if (payload is not null)
                {
                    await send(BridgeJson.Serialize(new BridgeMessage
                    {
                        Event = "items",
                        Data = JsonSerializer.SerializeToNode(payload, BridgeJson.Options),
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("bridge", $"Không gửi trạng thái: {ex.Message}", ex);
        }
    }


    private UiState BuildState()
    {
        var jobs = _engine.Jobs;
        var originals = jobs.Sum(j => j.BytesOriginal);
        var saved = jobs.Sum(j => j.BytesSaved);

        var byKind = jobs
            .SelectMany(j => j.Items)
            .Where(i => i.Succeeded)
            .GroupBy(i => i.Kind)
            .Select(g => new KindTotal(g.Key.ToString(), g.Sum(i => i.SavedBytes), g.Sum(i => i.OldSize), g.Count()))
            .OrderByDescending(k => k.SavedBytes)
            .ToList();

        return new UiState
        {
            Jobs = [.. jobs.Select(j => JobDto.From(j, PendingBackupCount(j), _config.Level, WasRestored(j)))],

            TotalBytesSaved = saved,
            TotalSavedText = Format.Size(saved),
            TotalSavedPercentText = originals > 0 ? Format.Percent((double)saved * 100.0 / originals) : Format.Percent(0),
            TotalBytesOriginal = originals,
            TotalFiles = jobs.Sum(j => j.TotalFiles),
            ProcessedFiles = jobs.Sum(j => j.ProcessedCount),
            ByKind = byKind,
            Tools = [.. _tools.Reports.Select(ToolDto.From)],
            Config = _config,
            IsRunning = _engine.IsRunning,
            IsPaused = jobs.Any(j => j.Status == JobStatus.Paused),
            SpeedBytesPerSecond = (long)_engine.GlobalBytesPerSecond(),
            SpeedText = Format.Size((long)_engine.GlobalBytesPerSecond()) + "/s",
            Concurrency = _engine.Concurrency,
            AppDirectory = AppPaths.BaseDirectory,
            ProjectDirectory = AppPaths.RootDirectory,
            DataDirectory = AppPaths.DataDirectory,
            SupportedExtensions = [.. MediaClassifier.AllSupportedExtensions.OrderBy(e => e)],
        };
    }

    // ---------------------------------------------------------------- nhận lệnh

    /// <summary>
    /// Lệnh cố tình không trả dữ liệu về. Cần khai báo đầy đủ ở đây, nếu không lệnh đó
    /// sẽ bị hiểu nhầm là không tồn tại.
    /// </summary>
    private static readonly HashSet<string> CommandsWithoutResult = new(StringComparer.Ordinal)
    {
        "removeJob", "clearAll", "pauseAll", "resumeAll", "cancelAll",
        "pauseJob", "resumeJob", "cancelJob", "undo", "undoItem",
        "purgeBackups", "log",
    };

    public async Task<BridgeMessage> DispatchAsync(BridgeMessage message)
    {
        if (string.IsNullOrEmpty(message.Cmd)) return Fail(message, "Thiếu tên lệnh.");

        try
        {
            var data = message.Cmd switch
            {
                "getState" => ToNode(BuildState()),
                "getItems" => GetItems(message),
                "addPaths" => await AddPathsAsync(message),
                "browseFolder" => await BrowseAsync(folderPicker: true),
                "browseFiles" => await BrowseAsync(folderPicker: false),
                "browseExport" => BrowseExport(),
                "openProject" => OpenFolder(AppPaths.RootDirectory),
                "removeJob" => RemoveJob(message),
                "clearAll" => ClearAll(),
                "start" => await StartAsync(message),
                "resumeJobs" => ResumeJobs(message),
                "pauseAll" => PauseAll(),
                "resumeAll" => ResumeAll(),
                "cancelAll" => CancelAll(),
                "pauseJob" => PauseJob(message),
                "resumeJob" => ResumeJob(message),
                "cancelJob" => CancelJob(message),
                "applyLevel" => ApplyLevelToJob(message),
                "commit" => await CommitAsync(message),
                "undo" => await UndoAsync(message),
                "undoItem" => UndoItem(message),
                "saveConfig" => await SaveConfigAsync(message),
                "checkTools" => await CheckToolsAsync(),
                "setToolPath" => SetToolPath(message),
                "purgeBackups" => PurgeBackups(),
                "preview" => await PreviewAsync(message),
                "openPath" => OpenFolder(SelectedFolderFrom(message)),
                "openLogs" => OpenFolder(AppPaths.LogDirectory),
                "openData" => OpenFolder(AppPaths.DataDirectory),
                "logTail" => LogTail(message),
                "log" => LogMessage(message),
                "getCompare" => await GetCompareAsync(message),
                "playFile" => PlayFile(message),
                "playBothExternal" => await PlayBothExternal(message),
                "guide" => ToNode(BuildGuide()),
                _ => null,
            };

            // Lệnh trả null nghĩa là "làm xong, không có gì để trả" (xoá list, tạm dừng,
            // hoàn tác...). Trước đây null bị hiểu là lệnh không tồn tại nên giao diện báo
            // "Lệnh không hợp lệ" sau khi đã thực hiện xong — vẫn lỗi cho người dùng.
            if (data is null && !CommandsWithoutResult.Contains(message.Cmd))
            {
                return Fail(message, $"Lệnh không hợp lệ: '{message.Cmd}'.");
            }

            QueuePush(force: true);
            return new BridgeMessage { Id = message.Id, Data = data };
        }
        catch (Exception ex)
        {
            _logger.LogError("cmd", $"Lệnh '{message.Cmd}' lỗi: {ex.Message}", ex);
            return Fail(message, ex.Message);
        }
    }

    private static BridgeMessage Fail(BridgeMessage message, string error) =>
        new() { Id = message.Id, Error = error };

    private static System.Text.Json.Nodes.JsonNode? ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, BridgeJson.Options);

    /// <summary>
    /// Đếm tệp còn bản sao lưu, có bộ nhớ đệm ngắn.
    ///
    /// <c>UndoService.PendingBackups</c> phải <c>File.Exists</c> cho từng tệp đã nén, và
    /// <see cref="BuildState"/> chạy 4 lần mỗi giây. Với một job vài nghìn tệp đã nén thì
    /// đó là hàng nghìn lệnh I/O mỗi giây chỉ để lấy một con số, và nó là một trong các
    /// nguồn làm giao diện nặng. Cache 3 giây, và bị xoá ngay khi Duyệt / Hoàn tác.
    /// </summary>
    private long PendingBackupCount(Job job)
    {
        var now = DateTimeOffset.Now;

        if (_backupCount.TryGetValue(job.Id, out var cached) && now - cached.At < TimeSpan.FromSeconds(3))
        {
            return cached.Count;
        }

        var count = UndoService.PendingBackups(job).Count;
        _backupCount[job.Id] = (now, count);
        return count;
    }

    private void ForgetBackupCounts() => _backupCount.Clear();


    private static string? SelectedFolderFrom(BridgeMessage message) => BridgeJson.GetString(message, "path");

    /// <summary>Bộ lọc người dùng đang đặt trên bảng chi tiết.</summary>
    private readonly record struct ItemQuery(string Search, string State, string Kind, int Limit)
    {
        public static ItemQuery Default { get; } = new(string.Empty, "all", "all", 1000);
    }

    private System.Text.Json.Nodes.JsonNode? GetItems(BridgeMessage message)
    {
        var query = new ItemQuery(
            BridgeJson.GetString(message, "search")?.Trim() ?? string.Empty,
            BridgeJson.GetString(message, "state") ?? "all",
            BridgeJson.GetString(message, "kind") ?? "all",
            Math.Clamp(BridgeJson.GetInt(message, "limit") ?? 1000, 1, 20000));

        var jobId = BridgeJson.GetString(message, "jobId") ?? string.Empty;

        // Ghi nhớ job và bộ lọc đang mở để các lần đẩy định kỳ đi kèm luôn danh sách tệp
        // đúng như người dùng đang xem. Không có hai dòng này thì bảng chi tiết chỉ hiện
        // ảnh chụp tại lúc mở và tiến độ từng tệp không bao giờ chạy.
        _openJobId = jobId;
        _openQuery = query;

        return BuildItems(jobId, query);
    }

    /// <summary>
    /// Dựng danh sách tệp cho bảng chi tiết.
    ///
    /// Thứ tự là <b>thứ tự lúc quét</b> và giữ nguyên như vậy suốt lần chạy. Trước đây sắp
    /// xếp theo <c>SavedBytes</c> giảm dần, tức là bảng đảo lại mỗi khi một tệp xong —
    /// người dùng nhìn một dòng thì dòng đó nhảy đi, không theo dõi được tệp nào đang chạy
    /// bao nhiêu phần trăm.
    /// </summary>
    private System.Text.Json.Nodes.JsonNode? BuildItems(string jobId, ItemQuery query)
    {
        var job = _engine.Find(jobId);
        if (job is null) return null;

        var position = new Dictionary<JobItem, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < job.Items.Count; i++)
        {
            position[job.Items[i]] = i;
        }

        IEnumerable<JobItem> items = job.Items;

        if (!string.IsNullOrEmpty(query.Search))
        {
            items = items.Where(i => i.FileName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        }

        items = query.State switch
        {
            "done" => items.Where(i => i.Succeeded),
            "skipped" => items.Where(i => i.IsComplete && i.Skip != SkipReason.None),
            "pending" => items.Where(i => !i.IsComplete),
            _ => items,
        };

        if (query.Kind != "all")
        {
            items = items.Where(i => string.Equals(i.Kind.ToString(), query.Kind, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = items
            .OrderBy(i => position[i])
            .Take(query.Limit)
            .Select(ItemDto.From)
            .ToList();

        return ToNode(new { jobId, total = job.TotalFiles, items = ordered });
    }


    /// <summary>
    /// Thêm một danh sách đường dẫn từ giao diện. Dùng chung cho nút bấm, hộp thoại và
    /// kéo-thả nên tự phân biệt thư mục với tệp lẻ.
    /// </summary>
    private async Task<System.Text.Json.Nodes.JsonNode?> AddPathsAsync(BridgeMessage message)
    {
        var paths = BridgeJson.GetObject<List<string>>(message, "paths") ?? [];
        if (paths.Count == 0) return null;

        var results = new List<object>();
        var problems = new List<string>();

        foreach (var scan in _engine.AddPaths(paths, _config.Level, _config.DryRunDefault, null))
        {
            problems.AddRange(scan.Errors);
            results.Add(new
            {
                path = scan.Job.IsFileJob ? scan.Job.SingleFilePath : scan.Job.FolderPath,
                isFile = scan.Job.IsFileJob,
                added = scan.Job.Items.Count > 0,
                files = scan.Job.Items.Count,
            });
        }

        await _session.SaveAsync(_engine.Jobs);
        return ToNode(new { results, problems });
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> BrowseAsync(bool folderPicker)
    {
        var chosen = folderPicker ? PickFolders?.Invoke() : PickFiles?.Invoke();

        if (chosen is null)
        {
            return ToNode(new
            {
                ok = false,
                error = folderPicker
                    ? "Chưa mở được hộp chọn thư mục."
                    : "Chưa mở được hộp chọn tệp.",
            });
        }

        if (chosen.Count == 0) return ToNode(new { cancelled = true });

        // Gộp kết quả của nhiều đường dẫn lại để giao diện báo một lần.
        var results = new List<object>();
        var problems = new List<string>();

        foreach (var scan in _engine.AddPaths(chosen, _config.Level, _config.DryRunDefault, null))
        {
            problems.AddRange(scan.Errors);
            results.Add(new
            {
                path = scan.Job.IsFileJob ? scan.Job.SingleFilePath : scan.Job.FolderPath,
                isFile = scan.Job.IsFileJob,
                added = scan.Job.Items.Count > 0,
                files = scan.Job.Items.Count,
            });
        }

        await _session.SaveAsync(_engine.Jobs);
        return ToNode(new { cancelled = false, results, problems });
    }


    /// <summary>
    /// Hộp chọn thư mục đích khi xuất kết quả. Trước đây ô này chỉ gõ tay được — bắt
    /// người dùng tự nhớ và gõ đúng đường dẫn, gõ sai là nén xong mới biết.
    /// </summary>
    private System.Text.Json.Nodes.JsonNode? BrowseExport()
    {
        var pick = PickExportFolder?.Invoke();
        return pick is null
            ? ToNode(new { ok = false, error = "Chưa mở được hộp chọn thư mục." })
            : ToNode(new { ok = true, path = pick });
    }

    private System.Text.Json.Nodes.JsonNode? RemoveJob(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;
        ForgetBackupCounts();
        _engine.RemoveJob(jobId);
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? ClearAll()
    {
        ForgetBackupCounts();
        _engine.ClearAll();
        return null;
    }

    /// <summary>Các công cụ thiếu/hỏng chặn được các job này, theo loại media của chúng.</summary>
    private List<string> BlockingToolsFor(IReadOnlyCollection<string> jobIds)
    {
        var blocked = new List<string>();
        foreach (var job in _engine.Jobs.Where(j => jobIds.Contains(j.Id)))
        {
            var kinds = job.Items.Select(i => i.Kind).Distinct().ToList();
            foreach (var report in _tools.BlockingProblemsFor(kinds))
            {
                blocked.Add($"{report.DisplayName}: {report.Message ?? report.Health.ToString()}");
            }
        }

        return blocked;
    }

    /// <summary>
    /// Chạy tiếp các job nạp từ phiên. Không đụng vào <c>DryRun</c>/<c>OutputFolder</c>:
    /// mỗi job giữ đúng chế độ đã lưu, vì nút "Tiếp tục" nghĩa là làm tiếp việc đang dở,
    /// không phải bắt đầu lại bằng chế độ đang chọn trên thanh công cụ.
    /// </summary>
    private System.Text.Json.Nodes.JsonNode? ResumeJobs(BridgeMessage message)
    {
        var jobIds = BridgeJson.GetObject<List<string>>(message, "jobIds");
        var ids = jobIds is { Count: > 0 } ? jobIds : _engine.Jobs.Select(j => j.Id).ToList();

        var blocked = BlockingToolsFor(ids);
        if (blocked.Count > 0)
        {
            return ToNode(new { started = false, blocked = blocked.Distinct().ToList() });
        }

        MarkResumed(ids);
        _ = Task.Run(() => _engine.StartAsync(ids));
        return ToNode(new { started = true, blocked = Array.Empty<string>() });
    }


    private async Task<System.Text.Json.Nodes.JsonNode?> StartAsync(BridgeMessage message)
    {
        var jobIds = BridgeJson.GetObject<List<string>>(message, "jobIds");
        var ids = jobIds is { Count: > 0 } ? jobIds : _engine.Jobs.Select(j => j.Id).ToList();

        var blocked = BlockingToolsFor(ids);
        if (blocked.Count > 0)
        {
            return ToNode(new { started = false, blocked = blocked.Distinct().ToList() });
        }

        var dryRun = BridgeJson.GetBool(message, "dryRun");
        var outputFolder = BridgeJson.GetString(message, "outputFolder");

        if (dryRun || !string.IsNullOrEmpty(outputFolder))
        {
            var available = DiskSpace.AvailableBytesFor(outputFolder ?? _engine.Jobs.First(j => ids.Contains(j.Id)).FolderPath);
            var needed = _engine.Jobs.Where(j => ids.Contains(j.Id))
                .SelectMany(j => j.Items.Where(i => !i.IsComplete))
                .Sum(i => i.OldSize);
            if (!DiskSpace.HasRoom(outputFolder ?? string.Empty, needed, out _, out _))
            {
                return ToNode(new
                {
                    started = false,
                    blocked = new List<string> { DiskSpace.Describe(available, needed + needed / 10) },
                });
            }
        }

        foreach (var job in _engine.Jobs.Where(j => ids.Contains(j.Id)))
        {
            job.DryRun = dryRun;
            job.OutputFolder = string.IsNullOrEmpty(outputFolder) ? null : outputFolder;
        }

        // Người dùng đã bấm chạy: các job nạp từ phiên lần trước không còn là "chờ
        // khôi phục" nữa, nên gỡ cờ để lời mời "Tiếp tục" biến mất.
        MarkResumed(ids);

        // Không chặn: trả về ngay, trạng thái đẩy lên sau.
        _ = Task.Run(() => _engine.StartAsync(ids));
        return ToNode(new { started = true, blocked = Array.Empty<string>() });
    }

    private System.Text.Json.Nodes.JsonNode? PauseAll()
    {
        _engine.PauseAll();
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? ResumeAll()
    {
        _engine.ResumeAll();
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? CancelAll()
    {
        _engine.CancelAll();
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? PauseJob(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is not null) _engine.PauseJob(jobId);
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? ResumeJob(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is not null) _engine.ResumeJob(jobId);
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? CancelJob(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is not null) _engine.CancelJob(jobId);
        return null;
    }

    /// <summary>
    /// Đặt lại mức nén của một job theo mức đang chọn trong thanh công cụ.
    ///
    /// Mức nén được chụp lúc thêm thư mục, nên nếu người dùng đổi dropdown *sau khi* đã
    /// thêm thì job cũ vẫn nén bằng mức cũ — và bằng thị giác không có cách nào biết là
    /// sao. Lệnh này là lối thoát: chỉ đổi mức, **không** chạy lại những tệp đã xử lý.
    /// </summary>
    private System.Text.Json.Nodes.JsonNode? ApplyLevelToJob(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;

        var job = _engine.Find(jobId);
        if (job is null) return ToNode(new { ok = false, error = "Không tìm thấy job." });

        // Job đang chạy hoặc đã xong thì đổi mức cũng không có tác dụng gì cho tệp đã xử
        // lý, và dễ làm người dùng tưởng sẽ nén lại. Từ chối rõ ràng hơn là báo thành công
        // rồi không thấy gì đổi.
        if (job.Status is JobStatus.Running or JobStatus.Paused)
        {
            return ToNode(new { ok = false, error = "Job đang chạy. Hãy dừng rồi đổi mức nén." });
        }

        var before = job.Level;
        job.Level = _config.Level;

        return ToNode(new { ok = true, level = job.Level.ToString(), before = before.ToString() });
    }


    private async Task<System.Text.Json.Nodes.JsonNode?> CommitAsync(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;

        ForgetBackupCounts();
        var result = await _engine.CommitAsync(jobId);
        return ToNode(new { result.Applied, result.Released, result.Failed, result.Errors });
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> UndoAsync(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;

        ForgetBackupCounts();
        var result = await _engine.UndoAsync(jobId);
        return ToNode(new { result.Restored, result.Failed, result.Errors });
    }

    private System.Text.Json.Nodes.JsonNode? UndoItem(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        var filePath = BridgeJson.GetString(message, "filePath");
        var job = _engine.Find(jobId ?? string.Empty);
        var item = job?.Items.FirstOrDefault(i => i.FilePath == filePath);
        if (item is null) return null;

        ForgetBackupCounts();
        var error = UndoService.RestoreOne(item);
        return ToNode(new { ok = error is null, error });
    }


    private async Task<System.Text.Json.Nodes.JsonNode?> SaveConfigAsync(BridgeMessage message)
    {
        var incoming = BridgeJson.GetObject<AppConfig>(message, "config");
        if (incoming is null) return null;

        // Gán vào đúng đối tượng đang chạy để engine nhìn thấy ngay.
        CopyConfig(incoming, _config);
        _logger.MinimumLevel = ParseLogLevel(_config.LogLevel);

        await new ConfigStore(AppPaths.ConfigFile).SaveAsync(_config);
        _ = _tools.CheckAllAsync();

        return ToNode(new { ok = true });
    }

    internal static void CopyConfig(AppConfig from, AppConfig to)
    {
        // `EnableAdaptiveSearch` cố ý KHÔNG lấy từ `from`.
        //
        // Giao diện không có ô bật cho đường thích ứng, nên `from` không mang trường này và
        // giá trị của nó luôn là mặc định `false`. Nếu gán thẳng như các trường khác thì mỗi
        // lần bấm "Lưu cấu hình" sẽ TẮT cờ đang bật — và vì `SaveAsync` ghi lại đúng đối
        // tượng đó, giá trị tắt còn được lưu xuống đĩa. Người dùng bật cờ thủ công trong
        // tệp cấu hình rồi lưu một thứ khác trên giao diện là mất cờ, mà không có dấu hiệu
        // gì cho biết.
        //
        // Giữ nguyên thay vì gán: cờ chỉ đổi khi ai đó sửa tệp cấu hình, tức là chủ ý.
        var adaptive = to.EnableAdaptiveSearch;

        to.Level = from.Level;
        to.DryRunDefault = from.DryRunDefault;
        to.MaxConcurrent = from.MaxConcurrent;
        to.Tools = from.Tools;
        to.MinSavingPercent = from.MinSavingPercent;
        to.MinFileSizeBytes = from.MinFileSizeBytes;
        to.IncludeSubfolders = from.IncludeSubfolders;
        to.ExcludePatterns = from.ExcludePatterns;
        to.MeasureQuality = from.MeasureQuality;
        to.CheckFreeSpace = from.CheckFreeSpace;
        to.ConcurrencyScale = from.ConcurrencyScale;
        to.LogLevel = from.LogLevel;
        to.Theme = from.Theme;

        to.EnableAdaptiveSearch = adaptive;
    }

    private static LogLevel ParseLogLevel(string? value) => value?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    private async Task<System.Text.Json.Nodes.JsonNode?> CheckToolsAsync()
    {
        var reports = await _tools.CheckAllAsync();
        return ToNode(new { tools = reports.Select(ToolDto.From).ToList() });
    }

    private System.Text.Json.Nodes.JsonNode? SetToolPath(BridgeMessage message)
    {
        var kind = BridgeJson.GetString(message, "kind") ?? string.Empty;
        var path = BridgeJson.GetString(message, "path");

        // Không truyền đường dẫn = mở hộp chọn tệp của hệ điều hành.
        if (path is null && PickToolFile is not null)
        {
            path = PickToolFile();
            if (string.IsNullOrWhiteSpace(path)) return ToNode(new { ok = false, cancelled = true });
        }

        var parsed = Enum.TryParse<ToolKind>(kind, ignoreCase: true, out var tool) ? tool : (ToolKind?)null;
        if (parsed is null) return ToNode(new { ok = false, error = "Loại công cụ không hợp lệ." });

        switch (parsed.Value)
        {
            case ToolKind.FFmpeg: _config.Tools.FFmpeg = path; break;
            case ToolKind.FFplay: _config.Tools.FFplay = path; break;
            case ToolKind.Gifsicle: _config.Tools.Gifsicle = path; break;
            case ToolKind.Ghostscript: _config.Tools.Ghostscript = path; break;
        }

        _ = new ConfigStore(AppPaths.ConfigFile).SaveAsync(_config);
        _ = _tools.CheckAllAsync();
        return ToNode(new { ok = true, path });
    }

    private System.Text.Json.Nodes.JsonNode? PurgeBackups()
    {
        ForgetBackupCounts();

        // Ngưỡng cố định, không cấu hình được. Nút này dọn tệp .bak RƠI VÃI — tức là
        // những tệp mà ứng dụng quên dọn do treo máy hoặc bị tắt giữa chừng — chứ
        // không phải bản sao lưu đang chờ người dùng duyệt. Duyệt xong thì .bak đã bị
        // xoá ngay, nên ở đây chỉ còn tệp sót.
        //
        // Trước đây lấy ngưỡng từ KeepBackupDays, mà ngưỡng đó vốn quyết định việc duyệt
        // có xoá .bak hay không. Sau khi bỏ tuỳ chọn đó, dùng chung một hằng số sẽ khiến
        // nút này âm thầm xoá bản sao lưu còn đang chờ duyệt.
        var removed = UndoService.PurgeExpiredBackups(_engine.Jobs.Select(j => j.FolderPath), StrayBackupDays);
        return ToNode(new { removed });
    }


    private async Task<System.Text.Json.Nodes.JsonNode?> PreviewAsync(BridgeMessage message)
    {
        var filePath = BridgeJson.GetString(message, "filePath");
        var backup = BridgeJson.GetBool(message, "backup");

        if (filePath is null) return ToNode(new { ok = false, error = "Thiếu đường dẫn." });

        var target = backup ? FileTransaction.BackupPathFor(filePath) : filePath;
        if (!File.Exists(target))
        {
            return ToNode(new { ok = false, error = backup ? "Không có bản gốc để xem trước." : "Tệp không tồn tại." });
        }

        var ffplay = _tools.PathOf(ToolKind.FFplay);
        if (ffplay is null)
        {
            return ToNode(new { ok = false, error = "Chưa có ffplay.exe nên không xem trước được." });
        }

        // -autoexit để cửa sổ tự đóng khi hết bài. Không dùng -fs: phóng toàn màn hình
        // thì không đóng hay thu nhỏ được, buộc phải nhấn Esc.
        var title = backup ? "BAN GOC" : "DA NEN";

        try
        {
            // Phải khởi chạy tách rời, không chờ. Bản trước chờ ffplay với thời hạn 3 giây,
            // mà hết thời hạn thì ProcessRunner giết cả cây tiến trình — cửa sổ xem trước
            // vừa mở là biến mất.
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffplay,
                UseShellExecute = false,
            };

            start.ArgumentList.Add("-window_title");
            start.ArgumentList.Add($"UltraCompressor — {title} — {Path.GetFileName(target)}");
            start.ArgumentList.Add("-x"); start.ArgumentList.Add("960");
            start.ArgumentList.Add("-y"); start.ArgumentList.Add("540");
            start.ArgumentList.Add("-autoexit");
            start.ArgumentList.Add(target);

            System.Diagnostics.Process.Start(start);
            return ToNode(new { ok = true });
        }
        catch (Exception ex)
        {
            return ToNode(new { ok = false, error = ex.Message });
        }
    }

    private static System.Text.Json.Nodes.JsonNode? OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

            if (!Directory.Exists(target))
            {
                return ToNode(new { ok = false, error = "Đường dẫn không tồn tại." });
            }

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { target },
                UseShellExecute = false,
            });

            return ToNode(new { ok = true });
        }
        catch (Exception ex)
        {
            return ToNode(new { ok = false, error = ex.Message });
        }
    }

    private System.Text.Json.Nodes.JsonNode? LogTail(BridgeMessage message)
    {
        var max = Math.Clamp(BridgeJson.GetInt(message, "max") ?? 200, 1, 2000);
        var entries = _logger.Recent(max).Select(e => new
        {
            at = e.At,
            level = e.Level.ToString(),
            category = e.Category,
            message = e.Message,
            filePath = e.FilePath,
        });
        return ToNode(new { entries });
    }

    /// <summary>
    /// Ghi một dòng nhật ký do giao diện web gửi lên. Chủ yếu để truy vết những thao tác
    /// không làm được bằng script, ví dụ mở hộp thoại hay phát trình phát — nếu nó hỏng thì
    /// nhật ký là manh mối duy nhất còn lại.
    /// </summary>
    private static System.Text.Json.Nodes.JsonNode? LogMessage(BridgeMessage message)
    {
        var text = BridgeJson.GetString(message, "message");
        if (!string.IsNullOrWhiteSpace(text)) Diagnostic.Log($"[js] {text}");
        return null;
    }

    /// <summary>Dữ liệu cho màn hình so sánh: bản gốc đứng cạnh bản đã nén.</summary>
    private async Task<System.Text.Json.Nodes.JsonNode?> GetCompareAsync(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        var filePath = BridgeJson.GetString(message, "filePath");
        if (jobId is null || filePath is null) return null;

        var job = _engine.Find(jobId);
        var item = job?.Items.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (item is null) return ToNode(new { ok = false, error = "Không tìm thấy tệp trong job." });

        try
        {
            var result = await _compare.BuildAsync(item, job!.OutputFolder);
            if (result is null) return ToNode(new { ok = false, error = "Tệp không còn tồn tại trên đĩa." });

            // Cấp URL nhúng cho hai bên. Không có thì giao diện vẫn hiện được ảnh xem
            // trước và số liệu, chỉ mất phần phát trực tiếp.
            return ToNode(new
            {
                ok = true,
                busy = _engine.IsRunning,
                compare = result with
                {
                    Original = result.Original with { Url = RegisterMedia?.Invoke(result.Original.Path) },
                    Compressed = result.Compressed is null
                        ? null
                        : result.Compressed with { Url = RegisterMedia?.Invoke(result.Compressed.Path) },
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError("compare", $"Không dựng được dữ liệu so sánh: {ex.Message}", ex);
            return ToNode(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Mở tệp bằng trình phát ngoài, để so nghe/xem thật hai bên.</summary>
    private System.Text.Json.Nodes.JsonNode? PlayFile(BridgeMessage message)
    {
        var path = BridgeJson.GetString(message, "path");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return ToNode(new { ok = false, error = "Không tìm thấy tệp." });
        }

        var ffplay = _tools.PathOf(ToolKind.FFplay);

        try
        {
            if (ffplay is not null && File.Exists(ffplay))
            {
                // KHÔNG dùng -fs. Bản trước bật toàn màn hình nên người dùng không thể
                // đóng hay thu nhỏ được, buộc phải nhấn Esc. Cửa sổ thường có nút đóng,
                // nút thu nhỏ và thanh tiêu đề để nhận biết đang phát tệp nào.
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffplay,
                    UseShellExecute = false,
                };

                start.ArgumentList.Add("-autoexit");
                start.ArgumentList.Add("-window_title");
                start.ArgumentList.Add($"UltraCompressor — {Path.GetFileName(path)}");
                start.ArgumentList.Add("-x"); start.ArgumentList.Add("960");
                start.ArgumentList.Add("-y"); start.ArgumentList.Add("540");
                start.ArgumentList.Add(path);

                System.Diagnostics.Process.Start(start);
                return ToNode(new { ok = true, player = "ffplay" });
            }

            new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                },
            }.Start();

            return ToNode(new { ok = true, player = "system" });
        }
        catch (Exception ex)
        {
            return ToNode(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Mở bản gốc và bản đã nén cạnh nhau trong MỘT cửa sổ.
    ///
    /// <para>Dựng lệnh nằm ở <see cref="SideBySidePlayer"/> trong tầng Core để test được;
    /// hàm này chỉ lo phần tìm công cụ và nối pipe.</para>
    /// </summary>
    private async Task<System.Text.Json.Nodes.JsonNode?> PlayBothExternal(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        var filePath = BridgeJson.GetString(message, "filePath");
        if (jobId is null || filePath is null) return ToNode(new { ok = false, error = "Thiếu thông tin tệp." });

        var job = _engine.Find(jobId);
        var item = job?.Items.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (item is null) return ToNode(new { ok = false, error = "Không tìm thấy tệp trong job." });

        var result = await _compare.BuildAsync(item, job!.OutputFolder);
        if (result is null) return ToNode(new { ok = false, error = "Tệp không còn tồn tại trên đĩa." });

        if (!result.Original.Exists) return ToNode(new { ok = false, error = "Không tìm thấy bản gốc." });
        if (result.Compressed is null || !result.Compressed.Exists)
        {
            return ToNode(new { ok = false, error = "Chưa có bản nén trên đĩa để so sánh." });
        }

        if (result.Original.Kind != nameof(MediaKind.Video) || result.Compressed.Kind != nameof(MediaKind.Video))
        {
            return ToNode(new { ok = false, error = "Ghép cạnh nhau chỉ dành cho video." });
        }

        var ffplay = _tools.PathOf(ToolKind.FFplay);
        if (ffplay is null || !File.Exists(ffplay))
        {
            return ToNode(new
            {
                ok = false,
                error = "Chưa có ffplay.exe. Mở Cài đặt → Công cụ ngoài → dòng FFplay → Chọn… rồi trỏ tới ffplay.exe.",
            });
        }

        var ffmpeg = _tools.PathOf(ToolKind.FFmpeg);
        if (ffmpeg is null || !File.Exists(ffmpeg))
        {
            return ToNode(new { ok = false, error = "Chưa có ffmpeg.exe để ghép hai bản." });
        }

        var command = SideBySidePlayer.Build(
            ffmpeg,
            ffplay,
            result.Original.Path,
            result.Compressed.Path,
            result.Original.Width,
            result.Original.Height,
            result.Compressed.Width,
            result.Compressed.Height,
            $"UltraCompressor — {item.FileName} (gốc | đã nén)");

        try
        {
            var encode = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in command.FfmpegArguments) encode.ArgumentList.Add(argument);

            var play = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command.FfplayPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in command.FfplayArguments) play.ArgumentList.Add(argument);

            // ffplay phải mở trước: nó đọc pipe, và nếu ffmpeg ghi vào một pipe chưa có
            // đầu đọc thì lần ghi đầu tiên bị treo, rồi ffplay mở ra và chờ dữ liệu
            // không bao giờ tới.
            using var player = System.Diagnostics.Process.Start(play);
            using var encoder = System.Diagnostics.Process.Start(encode);

            if (player is null || encoder is null)
            {
                return ToNode(new { ok = false, error = "Không khởi chạy được ffmpeg hoặc ffplay." });
            }

            // Nối stdout của ffmpeg sang stdin của ffplay. CopyToAsync chạy nền, nếu chạy
            // tuần tự thì ffmpeg sẽ kẹt khi pipe đầy trong lúc chờ ta đọc.
            //
            // Bọc trong Task.Run vì đây là việc nền: gọi thẳng CopyToAsync rồi bỏ kết quả
            // sẽ bị CS4014 khi build -warnaserror (mà CI đang bật).
            _ = Task.Run(
                () =>
                {
                    try
                    {
                        encoder.StandardOutput.BaseStream.CopyToAsync(player.StandardInput.BaseStream)
                            .GetAwaiter().GetResult();

                        // ffplay chỉ đóng khi pipe hết dữ liệu, nên đóng stdin ở đây là
                        // điều kiện cần để nó biết đã xem xong.
                        player.StandardInput.Close();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("compare", $"Nối pipe ffmpeg -> ffplay hỏng: {ex.Message}");
                    }
                },
                CancellationToken.None);

            // Đọc lỗi của cả hai về sau, để khi có hỏng thì biết hỏng ở đâu thay vì chỉ
            // thấy cửa sổ không mở. Không chặn ở đây.
            Drain(encoder, "ffmpeg");
            Drain(player, "ffplay");

            _logger.LogInfo("compare", $"Mở so sánh cạnh nhau: {command.Display()}");
            return ToNode(new { ok = true, player = "ffplay", command = command.Display() });
        }
        catch (Exception ex)
        {
            _logger.LogError("compare", $"Không mở được cửa sổ so sánh: {ex.Message} — lệnh: {command.Display()}", ex);
            return ToNode(new { ok = false, error = $"{ex.Message} — lệnh: {command.Display()}" });
        }
    }

    /// <summary>
    /// Đọc stderr của một tiến trình con về nhật ký rồi bỏ, không chặn người dùng.
    ///
    /// <para>Cố ý không await: việc này bắt buộc phải chạy nền, và ta trả về ngay cho
    /// giao diện biết cửa sổ đã mở. Gọi một hàm <c>async</c> mà không await sẽ bị
    /// CS4014 khi build <c>-warnaserror</c> — mà CI có bật. Nên hàm này không phải
    /// <c>async</c>: nó bọc công việc đó vào <c>Task.Run</c> rồi trả về ngay.</para>
    /// </summary>
    private void Drain(System.Diagnostics.Process process, string label)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Take(20))
                    {
                        _logger.LogDebug("compare", $"{label}: {line.Trim()}");
                    }

                    if (lines.Length > 0)
                    {
                        _logger.LogInfo("compare", $"{label} kết thúc với mã {process.ExitCode}: {lines[0].Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("compare", $"Không đọc được stderr của {label}: {ex.Message}");
                }
            },
            CancellationToken.None);
    }

    private static System.Text.Json.Nodes.JsonNode? BuildGuide() => ToNode(new
    {
        levels = CompressionProfile.All.Select(p => new
        {
            key = p.Level.ToString(),
            name = p.DisplayName,
            crf = p.VideoCrf,
            preset = p.VideoPreset,
            videoMaxWidth = p.VideoMaxWidth,
            imageQuality = p.ImageQuality,
            imageMaxWidth = p.ImageMaxWidth,
            audioKbps = p.AudioBitrateKbps,
            pdf = p.PdfPreset,
            gifLossy = p.GifLossy,
            gifFps = p.GifFps,
            gifScale = p.GifWidthScale,
        }),
        extensions = MediaClassifier.AllSupportedExtensions.OrderBy(e => e),
        dataDirectory = AppPaths.DataDirectory,
        appDirectory = AppPaths.RootDirectory,
    });


    private static string CompressionLevelKey(CompressionProfile profile) =>
        profile.VideoCrf switch
        {
            20 => "Light",
            23 => "Balanced",
            _ => "Strong",
        };

    public void Invalidate() => QueuePush(force: true);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Changed -= OnEngineChanged;
        await _engine.DisposeAsync();
    }
}
