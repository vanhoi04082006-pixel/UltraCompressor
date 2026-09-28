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

    /// <summary>Đăng ký một tệp media và trả URL để giao diện nhúng trình phát.</summary>
    public Func<string, string?>? RegisterMedia { get; set; }

    /// <summary>Mở hộp chọn tệp thực thi. Gán từ cửa sổ chủ.</summary>
    public Func<string?>? PickToolFile { get; set; }

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
                var items = _engine.Find(_openJobId)?.Items.Select(ItemDto.From).ToList() ?? [];
                await send(BridgeJson.Serialize(new BridgeMessage
                {
                    Event = "items",
                    Data = JsonSerializer.SerializeToNode(new { jobId = _openJobId, items }, BridgeJson.Options),
                }));
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
            Jobs = [.. jobs.Select(JobDto.From)],
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
                "addFolder" => await AddFolderAsync(message),
                "browseFolder" => await BrowseFolderAsync(),
                "removeJob" => RemoveJob(message),
                "clearAll" => ClearAll(),
                "start" => await StartAsync(message),
                "pauseAll" => PauseAll(),
                "resumeAll" => ResumeAll(),
                "cancelAll" => CancelAll(),
                "pauseJob" => PauseJob(message),
                "resumeJob" => ResumeJob(message),
                "cancelJob" => CancelJob(message),
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

    private static string? SelectedFolderFrom(BridgeMessage message) => BridgeJson.GetString(message, "path");

    private System.Text.Json.Nodes.JsonNode? GetItems(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId") ?? string.Empty;
        var search = BridgeJson.GetString(message, "search")?.Trim();
        var stateFilter = BridgeJson.GetString(message, "state") ?? "all";
        var kindFilter = BridgeJson.GetString(message, "kind") ?? "all";
        var limit = Math.Clamp(BridgeJson.GetInt(message, "limit") ?? 2000, 1, 20000);

        var job = _engine.Find(jobId);
        if (job is null) return null;

        // Ghi nhớ job đang mở để các lần đẩy trạng thái về sau kèm luôn danh sách tệp.
        // Không có dòng này thì bảng chi tiết chỉ hiện ảnh chụp tại lúc mở và tiến độ
        // từng tệp không bao giờ chạy.
        _openJobId = jobId;

        var items = job.Items.AsEnumerable();

        if (!string.IsNullOrEmpty(search))
        {
            items = items.Where(i => i.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        items = stateFilter switch
        {
            "done" => items.Where(i => i.Succeeded),
            "skipped" => items.Where(i => i.IsComplete && i.Skip != SkipReason.None),
            "changed" => items.Where(i => i.Succeeded),
            "pending" => items.Where(i => !i.IsComplete),
            _ => items,
        };

        if (kindFilter != "all")
        {
            items = items.Where(i => string.Equals(i.Kind.ToString(), kindFilter, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = items.OrderByDescending(i => i.SavedBytes).Take(limit).Select(ItemDto.From).ToList();
        return ToNode(new { jobId, total = job.TotalFiles, items = ordered });
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> AddFolderAsync(BridgeMessage message)
    {
        var paths = BridgeJson.GetObject<List<string>>(message, "paths") ?? [];
        if (paths.Count == 0) return null;

        var results = new List<object>();
        var problems = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var scan = _engine.AddFolder(path, _config.Level, _config.DryRunDefault, null);
            problems.AddRange(scan.Errors);
            results.Add(new { path, added = scan.Job.Items.Count > 0, files = scan.Job.Items.Count });
        }

        await _session.SaveAsync(_engine.Jobs);
        return ToNode(new { results, problems });
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> BrowseFolderAsync()
    {
        if (PickFolders is null) return ToNode(new { ok = false, error = "Chưa mở được hộp chọn thư mục." });

        var chosen = PickFolders();
        if (chosen.Count == 0) return ToNode(new { cancelled = true });

        // Gộp kết quả của nhiều thư mục lại để giao diện báo một lần.
        var results = new List<object>();
        var problems = new List<string>();

        foreach (var path in chosen)
        {
            var scan = _engine.AddFolder(path, _config.Level, _config.DryRunDefault, null);
            problems.AddRange(scan.Errors);
            results.Add(new { path, added = scan.Job.Items.Count > 0, files = scan.Job.Items.Count });
        }

        await _session.SaveAsync(_engine.Jobs);
        return ToNode(new { cancelled = false, results, problems });
    }

    private System.Text.Json.Nodes.JsonNode? AddFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var scan = _engine.AddFolder(path, _config.Level, _config.DryRunDefault, null);
        return ToNode(new { path, added = scan.Job.Items.Count > 0, files = scan.Job.Items.Count, problems = scan.Errors });
    }

    private System.Text.Json.Nodes.JsonNode? RemoveJob(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;
        _engine.RemoveJob(jobId);
        return null;
    }

    private System.Text.Json.Nodes.JsonNode? ClearAll()
    {
        _engine.ClearAll();
        return null;
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> StartAsync(BridgeMessage message)
    {
        var jobIds = BridgeJson.GetObject<List<string>>(message, "jobIds");
        var ids = jobIds is { Count: > 0 } ? jobIds : _engine.Jobs.Select(j => j.Id).ToList();

        var blocked = new List<string>();
        foreach (var job in _engine.Jobs.Where(j => ids.Contains(j.Id)))
        {
            var kinds = job.Items.Select(i => i.Kind).Distinct().ToList();
            foreach (var report in _tools.BlockingProblemsFor(kinds))
            {
                blocked.Add($"{report.DisplayName}: {report.Message ?? report.Health.ToString()}");
            }
        }

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

    private async Task<System.Text.Json.Nodes.JsonNode?> CommitAsync(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;

        var result = await _engine.CommitAsync(jobId, _config.KeepBackupDays);
        return ToNode(new { result.Restored, result.Failed, result.Errors });
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> UndoAsync(BridgeMessage message)
    {
        var jobId = BridgeJson.GetString(message, "jobId");
        if (jobId is null) return null;

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
        to.Level = from.Level;
        to.DryRunDefault = from.DryRunDefault;
        to.MaxConcurrent = from.MaxConcurrent;
        to.Tools = from.Tools;
        to.MinSavingPercent = from.MinSavingPercent;
        to.MinFileSizeBytes = from.MinFileSizeBytes;
        to.IncludeSubfolders = from.IncludeSubfolders;
        to.ExcludePatterns = from.ExcludePatterns;
        to.KeepBackupDays = from.KeepBackupDays;
        to.MeasureQuality = from.MeasureQuality;
        to.CheckFreeSpace = from.CheckFreeSpace;
        to.ConcurrencyScale = from.ConcurrencyScale;
        to.LogLevel = from.LogLevel;
        to.Theme = from.Theme;
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
        var removed = UndoService.PurgeExpiredBackups(_engine.Jobs.Select(j => j.FolderPath), _config.KeepBackupDays);
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
    /// Ghi một dòng nhật ký do giao diện web gửi lên. Chủ yếu để truy vết phần kéo-thả:
    /// thao tác kéo chuột từ Explorer không làm được bằng script, nên nếu nó hỏng thì
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

    private static System.Text.Json.Nodes.JsonNode? BuildGuide() => ToNode(new
    {
        levels = CompressionProfile.All.Select(p => new
        {
            key = p.Level.ToString(),
            name = p.DisplayName,
            crf = p.VideoCrf,
            preset = p.VideoPreset,
            imageQuality = p.ImageQuality,
            maxWidth = p.MaxWidth,
            audioKbps = p.AudioBitrateKbps,
            pdf = p.PdfPreset,
            gifLossy = p.GifLossy,
        }),
        extensions = MediaClassifier.AllSupportedExtensions.OrderBy(e => e),
        dataDirectory = AppPaths.DataDirectory,
        appDirectory = AppPaths.BaseDirectory,
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
