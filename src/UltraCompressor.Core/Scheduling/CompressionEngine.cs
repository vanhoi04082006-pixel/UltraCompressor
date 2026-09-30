using UltraCompressor.Core.Diagnostics;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Storage;
using UltraCompressor.Core.Toolchain;

namespace UltraCompressor.Core.Scheduling;

public enum ChangeReason
{
    Jobs,
    JobProgress,
    JobStatus,
    ItemProgress,
    Tools,
    Global,
}

public sealed class EngineEventArgs(ChangeReason reason, string? jobId = null) : EventArgs
{
    public ChangeReason Reason { get; } = reason;

    public string? JobId { get; } = jobId;
}

/// <summary>
/// Điều phối toàn bộ việc nén: hàng đổi job, chạy song song có giới hạn, tạm dừng/hủy,
/// ETA, và ghi session. Không phụ thuộc giao diện.
/// </summary>
public sealed class CompressionEngine : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly ToolChain _tools;
    private readonly PipelineFactory _pipelines = new();
    private readonly ILogger _log;
    private readonly SessionStore _session;
    private readonly TempWorkspace _workspace;
    private readonly FolderScanner _scanner;
    private readonly MediaProbe? _probe;
    private readonly PauseGate _globalGate = new();
    private readonly SemaphoreSlim _limiter;
    private readonly Lock _jobsGate = new();
    private readonly List<Job> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _jobCts = [];
    private readonly Dictionary<string, PauseGate> _jobGates = [];
    private readonly EtaEstimator _eta = new();
    private readonly EtaEstimator _jobEta = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _disposed;

    public CompressionEngine(
        AppConfig config,
        ToolChain tools,
        SessionStore session,
        TempWorkspace workspace,
        ILogger log)
    {
        _config = config;
        _tools = tools;
        _session = session;
        _workspace = workspace;
        _log = log;
        _scanner = new FolderScanner(config);
        _probe = tools.PathOf(ToolKind.FFmpeg) is { } ffmpeg ? new MediaProbe(ffmpeg) : null;
        _limiter = new SemaphoreSlim(ResolveConcurrency(config));
    }

    public event EventHandler<EngineEventArgs>? Changed;

    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_jobsGate) return [.. _jobs];
        }
    }

    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>
    /// Khi bật, job nén thật kết thúc sẽ <b>không</b> tự xoá bản gốc, dù đã chạy xong.
    /// Cửa sổ bật lúc chuẩn bị hỏi người dùng đóng hay không, rồi tắt lại nếu họ ở lại.
    /// </summary>
    public bool DeferAutoApprove { get; set; }

    public AppConfig Config => _config;

    public ToolChain Tools => _tools;

    /// <summary>Số luồng nén đang dùng.</summary>
    public int Concurrency => ResolveConcurrency(_config);

    /// <summary>Kho tệp tạm dùng chung, để phần so sánh không tạo thêm một thư mục tạm.</summary>
    public TempWorkspace Workspace => _workspace;

    public static int ResolveConcurrency(AppConfig config)
    {
        if (config.MaxConcurrent > 0) return config.MaxConcurrent;

        // Ảnh/PDF nhẹ, video nặng — dùng một nửa số nhân logic, trần 8.
        var byCpu = (int)Math.Round(Environment.ProcessorCount * Math.Clamp(config.ConcurrencyScale, 0.1, 2.0));
        return Math.Clamp(byCpu, 2, 8);
    }

    public Job? Find(string jobId)
    {
        lock (_jobsGate) return _jobs.FirstOrDefault(j => j.Id == jobId);
    }

    // ---------------------------------------------------------------- thêm / bớt job

    public ScanResult AddFolder(string path, CompressionLevel level, bool dryRun, string? outputFolder)
    {
        var full = Path.GetFullPath(path);
        lock (_jobsGate)
        {
            if (_jobs.Any(j => !j.IsFileJob && string.Equals(j.FolderPath, full, StringComparison.OrdinalIgnoreCase)))
            {
                var existing = _jobs.First(j => !j.IsFileJob && string.Equals(j.FolderPath, full, StringComparison.OrdinalIgnoreCase));
                return new ScanResult(existing, ["Thư mục này đã có trong danh sách."]);
            }
        }

        var result = _scanner.Scan(full, level, dryRun, outputFolder);
        if (result.Errors.Count == 0 || result.Job.Items.Count > 0)
        {
            lock (_jobsGate) _jobs.Add(result.Job);
        }

        Raise(ChangeReason.Jobs);
        _ = SaveSessionAsync();
        return result;
    }

    /// <summary>Thêm đúng một tệp lẻ vào danh sách.</summary>
    public ScanResult AddFile(string path, CompressionLevel level, bool dryRun, string? outputFolder)
    {
        var full = Path.GetFullPath(path);
        lock (_jobsGate)
        {
            if (_jobs.Any(j => j.IsFileJob
                && string.Equals(j.SingleFilePath, full, StringComparison.OrdinalIgnoreCase)))
            {
                var existing = _jobs.First(j => j.IsFileJob
                    && string.Equals(j.SingleFilePath, full, StringComparison.OrdinalIgnoreCase));
                return new ScanResult(existing, ["Tệp này đã có trong danh sách."]);
            }
        }

        var result = _scanner.ScanFile(full, level, dryRun, outputFolder);
        if (result.Errors.Count == 0 || result.Job.Items.Count > 0)
        {
            lock (_jobsGate) _jobs.Add(result.Job);
        }

        Raise(ChangeReason.Jobs);
        _ = SaveSessionAsync();
        return result;
    }

    /// <summary>
    /// Thêm một danh sách đường dẫn, tự phân biệt thư mục với tệp lẻ. Đây là cửa duy nhất
    /// dùng cho cả nút bấm, hộp thoại và kéo-thả, nên ba đường đó không thể lệch nhau.
    /// </summary>
    public IReadOnlyList<ScanResult> AddPaths(
        IEnumerable<string> paths,
        CompressionLevel level,
        bool dryRun,
        string? outputFolder)
    {
        var results = new List<ScanResult>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                results.Add(new ScanResult(
                    new Job { FolderPath = path, Level = level, IsFileJob = true, SingleFilePath = path },
                    [$"Đường dẫn không hợp lệ: '{path}'."]));
                continue;
            }

            results.Add(Directory.Exists(full)
                ? AddFolder(full, level, dryRun, outputFolder)
                : AddFile(full, level, dryRun, outputFolder));
        }

        return results;
    }


    public async Task<(IReadOnlyList<Job> Jobs, string? Error)> LoadSessionAsync(CancellationToken token = default)
    {
        var (jobs, error) = await _session.LoadAsync(token);
        if (jobs.Count == 0) return ([], error);

        lock (_jobsGate) _jobs.AddRange(jobs);
        Raise(ChangeReason.Jobs);
        return (jobs, error);
    }

    public void RemoveJob(string jobId)
    {
        lock (_jobsGate)
        {
            foreach (var job in _jobs.Where(j => j.Id == jobId))
            {
                ReleaseStagedFiles(job);
            }

            _jobs.RemoveAll(j => j.Id == jobId);
            _jobCts.Remove(jobId);
            _jobGates.Remove(jobId);
        }

        Raise(ChangeReason.Jobs);
        _ = SaveSessionAsync();
    }

    /// <summary>
    /// Xoá các tệp nén đang chờ duyệt.
    ///
    /// Bản chế độ thử giữ tệp nén lại để so sánh, nên job bị bỏ đi thì tệp đó phải đi theo
    /// — còn lại thì mỗi lần xoá job là thêm vài trăm MB rác trong thư mục tạm.
    /// </summary>
    private void ReleaseStagedFiles(Job job)
    {
        foreach (var item in job.Items)
        {
            if (item.StagedPath is not { } staged) continue;
            _workspace.Release(staged);
            item.StagedPath = null;
        }
    }

    public void ClearAll()
    {
        lock (_jobsGate)
        {
            foreach (var job in _jobs) ReleaseStagedFiles(job);
            _jobs.Clear();
            _jobCts.Clear();
            _jobGates.Clear();
        }

        _session.Delete();
        Raise(ChangeReason.Jobs);
    }

    // ---------------------------------------------------------------- chạy / dừng

    public async Task StartAsync(IEnumerable<string> jobIds, CancellationToken token = default)
    {
        if (IsRunning) return;

        var targets = Jobs.Where(j => jobIds.Contains(j.Id) && j.Status is JobStatus.Waiting or JobStatus.Paused).ToList();
        if (targets.Count == 0) return;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _eta.Start();

        foreach (var job in targets)
        {
            job.Status = JobStatus.Running;
            job.StartedAt ??= DateTimeOffset.Now;
            job.CompletedAt = null;
            job.ErrorMessage = null;
            job.Committed = false;

            lock (_jobsGate)
            {
                _jobCts[job.Id] = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
                _jobGates[job.Id] = new PauseGate();
            }

            // Tạm dừng mức job chỉ có tác dụng khi đang chạy, nên dọn trạng thái cũ.
            foreach (var item in job.Items)
            {
                item.IsProcessing = false;
                item.Percent = 0;
            }
        }

        Raise(ChangeReason.JobStatus);

        _runTask = Task.Run(() => RunAllAsync(targets, _runCts.Token), CancellationToken.None);
        await _runTask;
    }

    /// <summary>
    /// Chạy các job <b>tuần tự</b> theo đúng thứ tự người dùng thêm vào.
    ///
    /// Trước đây là <c>Task.WhenAll</c> — tất cả job cùng chạy song song. Với danh sách
    /// nhiều thư mục thì bản đồ hóa 20 tập phim cùng lúc, mỗi job một tiến trình ffmpeg
    /// riêng, tốc độ tổng tụt và máy nghẽn. Người dùng thêm thư mục A, thư mục B, một tệp
    /// lẻ, thư mục C thì mong từng cái xong rồi mới tới cái sau.
    ///
    /// Thứ tự là thứ tự chèn vào danh sách, vì <c>_jobs</c> chỉ có thêm vào chứ không sắp
    /// xếp lại; <c>StartAsync</c> cũng lấy target theo chính thứ tự đó.
    ///
    /// Bên trong một job vẫn chạy song song tới giới hạn luồng — đó là chỗ tốn thời gian
    /// thật sự, và nén song song trong cùng một thư mục vẫn cho tốc độ cao nhất.
    /// </summary>
    private async Task RunAllAsync(List<Job> targets, CancellationToken token)
    {
        // Đồng hồ ETA có vòng đời riêng: nó dừng khi các job xong, không đợi tới lúc hủy.
        // Nếu dùng chung token với lần chạy thì RunAllAsync sẽ không bao giờ kết thúc.
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var etaTicker = StartEtaTicker(tickerCts.Token);

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var job = targets[i];
                _log.LogInfo(
                    "engine",
                    $"Xử lý job {i + 1}/{targets.Count}: {job.DisplayName} " +
                    $"({job.TotalFiles} tệp, mức {job.Level}).");

                await RunJobAsync(job, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Người dùng hủy — trạng thái đã được xử lý trong RunJobAsync. Các job chưa tới
            // phải chuyển sang "đã hủy" luôn, nếu không chúng treo ở "hàng chờ" vĩnh viễn
            // dù cả phiên nén đã kết thúc.
            foreach (var remaining in targets.Where(j => j.Status == JobStatus.Waiting))
            {
                remaining.Status = JobStatus.Cancelled;
                remaining.CompletedAt = DateTimeOffset.Now;
                remaining.EtaSeconds = -1;
            }

            Raise(ChangeReason.JobStatus);
        }
        catch (Exception ex)
        {
            _log.LogError("engine", "Lỗi không mong đợi khi chạy job", ex);
        }
        finally
        {
            await tickerCts.CancelAsync();
            await etaTicker;
            _globalGate.Resume();
            Raise(ChangeReason.Global);
            await SaveSessionAsync();
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken globalToken)
    {
        CancellationTokenSource? cts;
        PauseGate? gate;
        lock (_jobsGate)
        {
            _jobCts.TryGetValue(job.Id, out cts);
            _jobGates.TryGetValue(job.Id, out gate);
        }

        if (cts is null || gate is null) return;

        var token = cts.Token;

        try
        {
            // Chỉ xử lý mục chưa xong. Mục đã bị loại lúc quét (không phải định dạng được hỗ trợ,
            // bị bộ lọc chặn) đã IsComplete nên không được chạy lại — nếu không sẽ ghi đè
            // lý do loại trừ bằng kết quả của lần xử lý sau.
            foreach (var item in job.Items.Where(i => !i.IsComplete))
            {
                // Chờ tạm dừng: dùng gate nên hủy cũng thoát (fix bug B15/B16).
                await gate.WaitAsync(token);
                await _globalGate.WaitAsync(token);

                try
                {
                    token.ThrowIfCancellationRequested();

                    if (job.Status == JobStatus.Paused) job.Status = JobStatus.Running;

                    await _limiter.WaitAsync(token);
                    await ProcessItemAsync(job, item, token);
                    Raise(ChangeReason.ItemProgress, job.Id);
                }
                finally
                {
                    _limiter.Release();
                }

                await SaveSessionAsync();
            }

            // Nén thật thì tự duyệt. Bản nén đã thay gốc ngay lúc nén xong, bản gốc
            // nằm trong .bak chỉ để có đường lui tạm thời; người dùng đã quyết định ở
            // lúc bấm "Nén thật" rồi, không cần bấm Duyệt thêm lần nữa — mà bấm cũng
            // chẳng còn gì để xem, vì bản gốc đã bị dùng làm .bak. Chạy thử thì giữ
            // nguyên việc duyệt tay: đó mới là chỗ xem bằng mắt trước khi mất gốc.
            await AutoApproveRealJobAsync(job);

            job.Status = job.Committed ? JobStatus.Committed : JobStatus.PendingReview;
            job.CompletedAt = DateTimeOffset.Now;
            job.EtaSeconds = 0;
            Raise(ChangeReason.JobStatus, job.Id);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.Now;
            job.EtaSeconds = -1;
            _log.LogWarning("engine", $"Job '{job.FolderName}' đã hủy.", job.Id);
            Raise(ChangeReason.JobStatus, job.Id);
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTimeOffset.Now;
            _log.LogError("engine", $"Job '{job.FolderName}' lỗi: {ex.Message}", ex);
            Raise(ChangeReason.JobStatus, job.Id);
        }
        finally
        {
            foreach (var remaining in job.Items.Where(i => i.IsProcessing))
            {
                remaining.IsProcessing = false;
                remaining.Percent = 0;
            }
        }
    }

    // ---------------------------------------------------------------- xử lý một tệp

    /// <summary>
    /// Tóm tắt tham số đã chọn cho tệp này, để hiện trong bảng chi tiết và ghi vào nhật ký.
    ///
    /// Người dùng chỉ chọn "mức mục tiêu", không chọn tham số. Nếu không in ra tham số thật
    /// đã dùng thì ứng dụng không còn minh bạch: người dùng thấy tệp nhỏ đi 40% mà không
    /// biết vì sao, và không tin được. Dòng này là câu trả lời.
    /// </summary>
    private static string DescribePlan(PipelineContext context)
    {
        var goal = context.Level.ToGoal();
        var item = context.Item;

        return item.Kind switch
        {
            MediaKind.Video => DescribeVideoPlan(goal, context),
            MediaKind.Image => DescribeImagePlan(goal, context),
            MediaKind.Gif => DescribeGifPlan(goal, context),
            MediaKind.Audio => DescribeAudioPlan(goal, context),
            MediaKind.Pdf => $"PDF {CompressionPlanner.PlanPdf(goal).Preset}",
            _ => string.Empty,
        };
    }

    private static string DescribeVideoPlan(CompressionGoal goal, PipelineContext context)
    {
        var plan = CompressionPlanner.PlanVideo(
            goal, context.Probe, context.Item.SourceWidth, context.Item.SourceBitrateKbps,
            context.Item.HasAudio ?? true);

        var parts = new List<string> { $"CRF {plan.Crf} ({plan.Preset})" };

        if (plan.TargetWidth > 0 && context.Item.SourceWidth is { } w && w > 0)
        {
            parts.Add(w > plan.TargetWidth
                ? $"{w}px → {plan.TargetWidth}px"
                : $"giữ {w}px");
        }

        if (plan.TargetFps is { } fps) parts.Add($"{fps:0.#} fps");
        if (plan.DropAudio) parts.Add("bỏ tiếng");
        else parts.Add($"tiếng {plan.AudioBitrateKbps}k");

        if (plan.Reason.Length > 0) parts.Add(plan.Reason);

        return string.Join(" · ", parts);
    }

    private static string DescribeImagePlan(CompressionGoal goal, PipelineContext context)
    {
        var plan = CompressionPlanner.PlanImage(
            goal, context.Probe, context.Item.SourceWidth, context.Item.OldSize);

        // Không in "-q:v 31" cho tệp bị bỏ qua: con số đó là tham số tệ nhất của MJPEG, đọc
        // lên sẽ tưởng ảnh bị nén tệ. Tệp bị bỏ qua thì nói thẳng là bỏ qua.
        if (plan.Delta == PlanDelta.NotWorthIt)
        {
            return $"không nén — {plan.Reason}";
        }

        var parts = new List<string> { $"-q:v {plan.QScale}" };

        if (plan.TargetWidth > 0 && context.Item.SourceWidth is { } w && w > 0)
        {
            parts.Add(w > plan.TargetWidth ? $"{w}px → {plan.TargetWidth}px" : $"giữ {w}px");
        }

        if (plan.Reason.Length > 0) parts.Add(plan.Reason);

        return string.Join(" · ", parts);
    }

    private static string DescribeGifPlan(CompressionGoal goal, PipelineContext context)
    {
        var plan = CompressionPlanner.PlanGif(
            goal, context.Probe, context.Item.SourceWidth, context.Item.OldSize);

        var parts = new List<string>
        {
            $"{plan.Fps} fps",
            $"lossy {plan.Lossy}",
        };

        if (plan.TargetWidth > 0 && context.Item.SourceWidth is { } w && w > 0)
        {
            parts.Add(w > plan.TargetWidth ? $"{w}px → {plan.TargetWidth}px" : $"giữ {w}px");
        }

        if (plan.Reason.Length > 0) parts.Add(plan.Reason);

        return string.Join(" · ", parts);
    }

    private static string DescribeAudioPlan(CompressionGoal goal, PipelineContext context)
    {
        var plan = CompressionPlanner.PlanAudio(goal, context.Probe);
        return plan.Reason.Length > 0
            ? $"{plan.BitrateKbps}k — {plan.Reason}"
            : $"{plan.BitrateKbps}k";
    }

    private async Task ProcessItemAsync(Job job, JobItem item, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        if (!File.Exists(item.FilePath))
        {
            item.Skip = SkipReason.FileMissing;
            item.IsComplete = true;
            item.Message = "Tệp không còn tồn tại.";
            return;
        }

        item.IsProcessing = true;
        item.Percent = 0;

        _log.LogDebug("item", $"Bắt đầu xử lý '{item.FileName}' ({item.Kind}, {Format.Size(item.OldSize)}).");

        try
        {
            item.OldSize = new FileInfo(item.FilePath).Length;

            // Bước probe: lấy thời lượng, fps, codec, bề rộng, có tiếng, bitrate gốc.
            // Đây là đầu vào của bước lập kế hoạch — cùng một mức mục tiêu nhưng mỗi tệp ra
            // tham số khác nhau, tuỳ đặc tính của chính nó.
            MediaInfo? probe = null;
            if (_probe is not null)
            {
                try
                {
                    probe = await _probe.ProbeAsync(item.FilePath, token);
                    item.DurationSeconds = probe.Duration?.TotalSeconds;
                    item.HasAudio = probe.HasAudio;
                    item.SourceBitrateKbps = probe.BitrateKbps;
                    item.SourceWidth = probe.Width;
                    item.SourceHeight = probe.Height;
                }
                catch (Exception ex)
                {
                    // Mất thông tin probe không nên chặn nén — kế hoạch sẽ rơi về tham số
                    // nền theo mức mục tiêu.
                    _log.LogDebug("probe", $"Không probe được '{item.FileName}': {ex.Message}");
                }
            }

            var pipeline = _pipelines.Resolve(item.Kind);
            if (pipeline is null)
            {
                item.Skip = SkipReason.UnsupportedFormat;
                item.IsComplete = true;
                item.NewSize = item.OldSize;
                return;
            }

            var temp = _workspace.CreatePath(item.FilePath);
            var context = new PipelineContext
            {
                Item = item,
                TempPath = temp,
                Level = job.Level,
                Probe = probe,
                Config = _config,
                Tools = BuildToolResolution(),
                OutputPath = BuildOutputPath(job, item),
            };

            item.Plan = DescribePlan(context);

            var qualityGate = QualityGateFor();

            var percent = 0;
            var result = await pipeline.RunAsync(context, p =>
            {
                // Chỉ báo khi tăng thật để giảm tải cho UI.
                if (p <= percent) return;
                percent = p;
                item.Percent = p;
                Raise(ChangeReason.ItemProgress, job.Id);
            }, token);

            item.Percent = 100;
            item.ElapsedSeconds = watch.Elapsed.TotalSeconds;
            item.Message = result.Message;

            if (!result.Success)
            {
                item.Skip = result.Skip;
                item.NewSize = item.OldSize;
                item.IsComplete = true;
                _log.LogSkipped(result.Skip.ToString(), item.FilePath, job.Id);
                return;
            }

            // Lưới an toàn sau khi nén: giữ bản gốc nếu ứng viên lớn hơn, tiết kiệm quá
            // ít, hoặc đo ra dưới ngưỡng chất lượng của mode. Một chỗ duy nhất quyết định
            // có dùng kết quả hay không — trước đây là hai khối so sánh kích thước rời rạc,
            // không hề có đo chất lượng nào.
            var decision = await qualityGate.EvaluateAsync(
                sourceSize: item.OldSize,
                candidateSize: result.NewSize,
                level: job.Level,
                kind: item.Kind,
                sourcePath: item.FilePath,
                candidatePath: temp,
                durationSeconds: item.DurationSeconds,
                displayWidth: item.SourceWidth,
                displayHeight: item.SourceHeight,
                token);

            item.DecisionReason = decision.Reason;
            item.QualityScore = decision.Quality?.Mean;
            item.QualityP5 = decision.Quality?.P5;

            if (!decision.Accept)
            {
                // Ứng viên bị loại thì tệp tạm phải đi. Giữ lại chỉ để tệp sót trong
                // data\tmp tới lúc dọn cả phiên.
                _workspace.Release(temp);
                item.Skip = decision.Skip;
                item.NewSize = item.OldSize;
                item.Message = decision.Message;
                item.IsComplete = true;
                _log.LogSkipped($"{decision.Reason}: {decision.Message}", item.FilePath, job.Id);
                return;
            }

            item.NewSize = result.NewSize;
            item.BackupPath = FileTransaction.BackupPathFor(item.FilePath);

            if (job.DryRun && string.IsNullOrEmpty(job.OutputFolder))
            {
                // Chế độ thử: GIỮ tệp nén lại để người dùng mở so sánh và phát cả hai
                // bản, rồi mới bấm "Duyệt".
                //
                // Bản trước xoá tệp ngay (`_workspace.Release(temp)`), nên không còn gì
                // để so sánh: hộp so sánh báo "chưa có bản nén", và bấm "Duyệt" phải nén
                // lại từ đầu. Người dùng phải xem kết quả rồi mới quyết định — bỏ qua bước
                // xem đó thì "duyệt" chỉ là một nút mù.
                item.StagedPath = temp;
                item.IsApplied = false;
                item.IsPredicted = true;
                item.IsComplete = true;
                return;
            }

            item.IsPredicted = false;

            if (string.IsNullOrEmpty(job.OutputFolder))
            {
                // Ghi thẳng lên tệp gốc.
                var error = FileTransaction.Commit(item.FilePath, temp);
                if (error is not null)
                {
                    item.Skip = SkipReason.Error;
                    item.Message = error;
                    item.NewSize = item.OldSize;
                    item.IsComplete = true;
                    _log.LogError("apply", $"{item.FileName}: {error}");
                    return;
                }

                item.IsApplied = true;
            }
            else
            {
                // Xuất ra thư mục khác: tệp gốc giữ nguyên, giữ bố cục tương đối bên trong.
                var destination = context.OutputPath!;
                var exportError = FileTransaction.Export(temp, destination);
                item.OutputPath = exportError is null ? destination : null;
                item.IsApplied = false;

                if (exportError is not null)
                {
                    _workspace.Release(temp);
                    item.Skip = SkipReason.Error;
                    item.Message = exportError;
                    item.NewSize = item.OldSize;
                    item.IsComplete = true;
                    _log.LogError("apply", $"{item.FileName}: {exportError}");
                    return;
                }
            }

            item.Skip = SkipReason.None;
            item.IsComplete = true;
            _log.LogInfo("apply", $"{item.FileName}: {Format.Size(item.OldSize)} -> {Format.Size(item.NewSize)} "
                + $"({Format.Size(item.SavedBytes)}, {item.SavedPercent:F1}%) trong {item.ElapsedSeconds:F1}s"
                + (item.QualityScore is { } q ? $", VMAF {q:F1}" : string.Empty));
        }
        catch (SkipException ex)
        {
            item.Skip = ex.Reason;
            item.IsComplete = true;
            item.NewSize = item.OldSize;
            item.Message = ex.Message;
            _log.LogSkipped(ex.Message, item.FilePath, job.Id);
        }
        catch (OperationCanceledException)
        {
            item.Skip = SkipReason.Cancelled;
            item.IsComplete = true;
            item.NewSize = item.OldSize;
            throw;
        }
        catch (Exception ex)
        {
            item.Skip = SkipReason.Error;
            item.IsComplete = true;
            item.NewSize = item.OldSize;
            item.Message = ex.Message;
            _log.LogError("item", $"{item.FileName}: {ex.Message}", ex);
        }
        finally
        {
            item.IsProcessing = false;

            // Job nén video có thể chạy hàng chục phút mà không ghi gì. Dòng này là manh
            // mối duy nhất cho biết tiến trình còn sống hay đã kẹt.
            _log.LogDebug("item", $"Kết thúc '{item.FileName}': {(item.IsComplete ? "xong" : "CHƯA XONG")} " +
                $"({item.Skip.ToString()}, {item.Message}) sau {watch.Elapsed.TotalSeconds:0.0}s.");
        }
    }

    private ToolResolution BuildToolResolution() => new(
        _tools.PathOf(ToolKind.FFmpeg),
        _tools.PathOf(ToolKind.FFplay),
        _tools.PathOf(ToolKind.Gifsicle),
        _tools.PathOf(ToolKind.Ghostscript));

    private static string? BuildOutputPath(Job job, JobItem item)
    {
        if (string.IsNullOrEmpty(job.OutputFolder)) return null;

        var relative = Path.GetRelativePath(job.FolderPath, item.FilePath);
        return Path.Combine(job.OutputFolder, relative);
    }

    // ---------------------------------------------------------------- lưới an toàn

    private QualityGate? _qualityGate;
    private bool _qualityGateResolved;

    /// <summary>
    /// Lưới an toàn dùng chung cho mọi tệp, cùng lý do như bộ đo SI/TI: mỗi tệp một tiến
    /// trình ffmpeg là lãng phí, và <c>QualityProbe</c> giữ sẵn cache theo
    /// (nguồn, ứng viên, cửa sổ, model) nên đo lại cùng một tệp là miễn phí.
    ///
    /// <para>Không có ffmpeg thì truyền probe null: lưới kích thước vẫn chạy, chỉ không
    /// đo được chất lượng. Thiếu ffmpeg thì job đã hỏng trước đó rồi, không phải lúc để
    /// ném lỗi thứ hai.</para>
    /// </summary>
    private QualityGate QualityGateFor()
    {
        if (!_qualityGateResolved)
        {
            _qualityGateResolved = true;
            var ffmpeg = _tools.PathOf(ToolKind.FFmpeg);
            _qualityGate = new QualityGate(
                _config,
                ffmpeg is null ? null : new QualityProbe(ffmpeg, _workspace.Root),
                ffmpeg is null ? null : new TimelineScanner(ffmpeg, _workspace.Root));
        }

        return _qualityGate!;
    }

    // ---------------------------------------------------------------- tự duyệt

    /// <summary>
    /// Duyệt tự động cho job nén thật: xoá bản gốc đã nằm trong <c>.bak</c> khi tệp nén
    /// đã thay xong. Job chạy thử và job xuất sang thư mục khác không đi qua đây.
    /// </summary>
    /// <remarks>
    /// Cố ý chỉ xoá khi job chạy tới cuối bình thường, không xoá trong vòng lặp từng tệp:
    /// bấm Huỷ giữa chừng thì những tệp đã thay vẫn còn <c>.bak</c> để hoàn tác. Đổi lại
    /// trong lúc chạy vẫn phải chịu dung lượng tạm — đúng bằng tình huống có nút Duyệt
    /// tay trước đây, chỉ khác là không còn phải bấm nữa.
    /// </remarks>
    private async Task AutoApproveRealJobAsync(Job job)
    {
        if (job.DryRun) return;

        // Đang trong lúc chuẩn bị đóng cửa sổ. Job sẽ chạy nốt tệp đang dở rồi kết thúc,
        // và nếu tự duyệt ở đây thì .bak bị xoá mất trước khi người dùng kịp bấm
        // "Hoàn tác rồi thoát" — hộp thoại lúc ấy chỉ còn bày ra lựa chọn đã không còn
        // gì để chọn. Giữ lại .bak cho tới khi người dùng quyết.
        if (DeferAutoApprove)
        {
            _log.LogInfo("apply", $"Giữ bản gốc của '{job.FolderName}' lại để người dùng chọn khi thoát.");
            return;
        }

        if (!string.IsNullOrEmpty(job.OutputFolder))
        {
            // Xuất sang thư mục khác: bản gốc nằm nguyên ở chỗ cũ, không sinh .bak, không
            // có gì để duyệt. Trước đây job này vẫn dừng ở "Chờ duyệt" với 0 tệp chờ,
            // bấm Duyệt thì không làm gì cả.
            job.Committed = true;
            return;
        }

        // Không tệp nào được thay thế — hết lỗi, hoặc bị huỷ. Không được báo "Đã ghi":
        // sẽ giống hệt lúc bấm Duyệt trên một job không có gì để duyệt.
        if (!job.Items.Any(i => i.IsApplied)) return;

        var result = UndoService.ReleaseBackups(job);

        if (result.Errors.Count > 0)
        {
            _log.LogWarning(
                "apply",
                $"Tự động duyệt: xoá bản gốc {result.Released}/{result.Applied} tệp, "
                + $"{result.Failed} tệp xoá chưa được ({string.Join("; ", result.Errors)}).");
            job.ErrorMessage = string.Join("; ", result.Errors);
        }
        else
        {
            _log.LogInfo(
                "apply",
                $"Tự động duyệt: xoá bản gốc của {result.Released} tệp, không hoàn tác được nữa. "
                + "Muốn xem trước khi mất bản gốc thì bật chế độ Chạy thử.");
        }

        await SaveSessionAsync();
    }

    // ---------------------------------------------------------------- tạm dừng / hủy

    public void PauseJob(string jobId)
    {
        var job = Find(jobId);
        if (job is null || job.Status != JobStatus.Running) return;

        lock (_jobsGate)
        {
            if (_jobGates.TryGetValue(jobId, out var gate)) gate.Pause();
        }

        job.Status = JobStatus.Paused;
        Raise(ChangeReason.JobStatus, jobId);
    }

    public void ResumeJob(string jobId)
    {
        var job = Find(jobId);
        if (job is null || job.Status != JobStatus.Paused) return;

        lock (_jobsGate)
        {
            if (_jobGates.TryGetValue(jobId, out var gate)) gate.Resume();
        }

        job.Status = JobStatus.Running;
        Raise(ChangeReason.JobStatus, jobId);
    }

    public void CancelJob(string jobId)
    {
        lock (_jobsGate)
        {
            if (_jobCts.TryGetValue(jobId, out var cts)) cts.Cancel();
        }

        var job = Find(jobId);
        if (job is { Status: JobStatus.Running or JobStatus.Paused })
        {
            job.Status = JobStatus.Cancelled;
            Raise(ChangeReason.JobStatus, jobId);
        }
    }

    public void PauseAll()
    {
        _globalGate.Pause();
        foreach (var job in Jobs.Where(j => j.Status == JobStatus.Running))
        {
            job.Status = JobStatus.Paused;
        }

        Raise(ChangeReason.Global);
    }

    public void ResumeAll()
    {
        _globalGate.Resume();
        foreach (var job in Jobs.Where(j => j.Status == JobStatus.Paused))
        {
            lock (_jobsGate)
            {
                if (_jobGates.TryGetValue(job.Id, out var gate)) gate.Resume();
            }
        }

        Raise(ChangeReason.Global);
    }

    public void CancelAll()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Đã giải phóng.
        }

        foreach (var job in Jobs.Where(j => j.Status is JobStatus.Running or JobStatus.Paused))
        {
            job.Status = JobStatus.Cancelled;
        }

        Raise(ChangeReason.Global);
    }

    // ---------------------------------------------------------------- duyệt / hoàn tác

    public async Task<UndoService.CommitResult> CommitAsync(string jobId)
    {
        var job = Find(jobId);
        if (job is null) return new UndoService.CommitResult(0, 0, 0, ["Không tìm thấy job."]);

        // Job chạy ở chế độ thử: kết quả nén đã nằm sẵn trên đĩa, chỉ chưa thay thế tệp
        // gốc. "Duyệt" nghĩa là dùng đúng tệp đó — KHÔNG nén lại, vì nén lại thì kết quả
        // có thể khác và người dùng vừa xem so sánh xong lại bị thay bằng thứ khác.
        //
        // Ở chế độ thử thì bản gốc còn nguyên và chưa từng bị thay thế, nên duyệt xong
        // KHÔNG để lại .bak nào: thay tệp rồi xoá bản gốc luôn. Người dùng đã xem kết quả
        // ở hộp so sánh rồi mới bấm Duyệt, nên giữ lại 526 MB là tốn chỗ vô ích.
        if (job.DryRun)
        {
            var applied = 0;
            var errors = new List<string>();
            var notReleased = 0;
            var stale = 0;

            foreach (var item in job.Items.Where(i => i.IsPredicted))
            {
                var staged = item.StagedPath;

                if (staged is null || !File.Exists(staged))
                {
                    // Tệp kết quả biến mất (dọn tệp tạm, đổi máy, phiên cũ). Nói rõ
                    // thay vì âm thầm bỏ qua, để người dùng biết vì sao tệp này chưa
                    // được duyệt.
                    errors.Add($"{item.FileName}: không còn tệp nén đã so sánh, cần nén lại.");
                    stale++;
                    continue;
                }

                var swap = FileTransaction.CommitAndRelease(item.FilePath, staged);

                if (swap.Error is not null)
                {
                    errors.Add($"{item.FileName}: {swap.Error}");
                    continue;
                }

                applied++;
                item.IsApplied = true;
                item.IsPredicted = false;

                // Bản gốc đã đi vào .bak rồi bị xoá, nên không còn lối quay lui cho tệp
                // này. Ghi lại đúng sự thật thay vì để mặc định trỏ vào .bak đã không có.
                item.BackupPath = null;

                // Thay thế được nhưng xoá bản gốc hỏng (tệp đang mở). Bản nén đã ở chỗ,
                // nên không tính là duyệt hỏng — nhưng phải nói rõ vì chỗ chưa giải phóng.
                if (swap.ReleaseError is not null)
                {
                    notReleased++;
                    errors.Add($"{item.FileName}: đã duyệt nhưng chưa xoá được bản gốc ({swap.ReleaseError})");
                }
            }

            job.DryRun = false;

            // Tệp nào còn giữ lại thì phải dọn, không để rác trong thư mục tạm.
            foreach (var item in job.Items)
            {
                if (item.StagedPath is { } left && File.Exists(left)) _workspace.Release(left);
                item.StagedPath = null;
            }

            // Chỉ giữ "Chờ duyệt" khi thật sự có tệp chưa duyệt được. Lỗi xoá bản gốc
            // không làm nghẽn việc duyệt — bản nén vẫn nằm đúng chỗ.
            var failed = stale + errors.Count - notReleased;
            job.Status = failed == 0 ? JobStatus.Committed : JobStatus.PendingReview;
            if (failed == 0) job.Committed = true;

            Raise(ChangeReason.JobStatus, jobId);
            await SaveSessionAsync();

            var releasedNote = notReleased > 0 ? $", {notReleased} tệp chưa xoá được bản gốc" : "";
            _log.LogInfo("commit", $"Duyệt {applied}/{applied + stale + errors.Count - notReleased} tệp ở chế độ thử{releasedNote}.");

            // Tệp nào mất rồi thì chạy lại thật, nếu không job sẽ đứng ở "Chờ duyệt" mãi
            // với một tệp không bao giờ duyệt được.
            if (stale > 0)
            {
                foreach (var item in job.Items.Where(i => i.Skip == SkipReason.None && !i.IsApplied && i.IsComplete))
                {
                    item.IsComplete = false;
                    item.NewSize = 0;
                }

                job.Status = JobStatus.Waiting;
                Raise(ChangeReason.JobStatus, jobId);
                _ = Task.Run(() => StartAsync([jobId]));
            }

            return new UndoService.CommitResult(applied, applied - notReleased, stale + errors.Count - notReleased, errors);
        }

        // Job nén thật: bản nén đã nằm ở chỗ từ lúc chạy, bản gốc nằm trong .bak để
        // người dùng còn so sánh và còn hoàn tác được. Bấm Duyệt nghĩa là họ đã xem và
        // chấp nhận, nên xoá bản gốc ngay để giải phóng dung lượng.
        var result = UndoService.ReleaseBackups(job);
        Raise(ChangeReason.JobStatus, jobId);
        await SaveSessionAsync();
        return result;
    }

    public async Task<UndoService.BatchResult> UndoAsync(string jobId)
    {
        var job = Find(jobId);
        if (job is null) return new UndoService.BatchResult(0, 0, ["Không tìm thấy job."]);

        var result = UndoService.RestoreAll(job);

        // Bản nén đang chờ duyệt không còn ý nghĩa gì sau khi hoàn tác bản gốc.
        ReleaseStagedFiles(job);

        Raise(ChangeReason.JobStatus, jobId);
        await SaveSessionAsync();
        return result;
    }

    // ---------------------------------------------------------------- ETA

    private async Task StartEtaTicker(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);

                var running = Jobs.Where(j => j.Status is JobStatus.Running or JobStatus.Paused).ToList();
                if (running.Count == 0) continue;

                var done = running.Sum(j => j.Items.Where(i => i.IsComplete).Sum(i => i.OldSize));
                var remaining = running.Sum(j => j.Items.Where(i => !i.IsComplete).Sum(i => i.OldSize));

                _eta.AddProgress(done);
                var rate = _eta.BytesPerSecond();

                foreach (var job in running)
                {
                    var jobDone = job.Items.Where(i => i.IsComplete).Sum(i => i.OldSize);
                    _jobEta.AddProgress(jobDone);
                    var jobRemaining = job.Items.Where(i => !i.IsComplete).Sum(i => i.OldSize);
                    var jobRate = _jobEta.BytesPerSecond();

                    job.EtaSeconds = jobRemaining > 0
                        ? (jobRate > 0 ? jobRemaining / jobRate : -1)
                        : 0;

                    if (rate <= 0 && jobRate > 0) job.EtaSeconds = jobRemaining / jobRate;
                }

                Raise(ChangeReason.Global);
            }
        }
        catch (OperationCanceledException)
        {
            // Tắt đồng hồ khi chạy xong.
        }
    }

    public double GlobalBytesPerSecond() => _eta.BytesPerSecond();

    // ---------------------------------------------------------------- phụ trợ

    private void Raise(ChangeReason reason, string? jobId = null) => Changed?.Invoke(this, new EngineEventArgs(reason, jobId));

    private async Task SaveSessionAsync()
    {
        try
        {
            await _session.SaveAsync(Jobs);
        }
        catch (Exception ex)
        {
            _log.LogError("session", $"Không lưu được phiên: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        CancelAll();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Không chờ nổi thì vẫn tiếp tục dọn dẹp.
            }
        }

        await SaveSessionAsync();

        _runCts?.Dispose();
        _limiter.Dispose();
        _workspace.Dispose();
    }
}
