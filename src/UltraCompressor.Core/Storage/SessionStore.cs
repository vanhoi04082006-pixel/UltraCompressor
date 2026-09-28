using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Storage;

/// <summary>Lưu và đọc danh sách job để người dùng không mất công khi đóng ứng dụng.</summary>
public sealed class SessionStore(string path) : IDisposable
{
    public string Path { get; } = path;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task SaveAsync(IEnumerable<Job> jobs, CancellationToken token = default)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            // Chỉ lưu phần có ý nghĩa: trạng thái chạy về "chờ" để lần sau bấm Bắt đầu là tiếp.
            var snapshot = jobs.Select(Project).ToList();
            await JsonStore.SaveAsync(Path, snapshot, token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    public async Task<(IReadOnlyList<Job> Jobs, string? Error)> LoadAsync(CancellationToken token = default)
    {
        var (value, error) = await JsonStore.TryLoadAsync<List<Job>>(Path, token);
        if (value is null) return ([], error);

        var jobs = value.Where(j => !string.IsNullOrWhiteSpace(j.FolderPath)).ToList();

        // Job đang chạy lúc bị tắt máy thành "chờ" — không tự chạy tiếp khi mở lại.
        foreach (var job in jobs)
        {
            if (job.Status is JobStatus.Running or JobStatus.Paused) job.Status = JobStatus.Waiting;
            foreach (var item in job.Items)
            {
                item.IsProcessing = false;
                item.Percent = 0;
            }
        }

        return (jobs, null);
    }

    public void Delete() => FileTransaction.TryDelete(Path);

    /// <summary>Bỏ các trường chỉ có ý nghĩa lúc chạy, trước khi ghi xuống đĩa.</summary>
    private static Job Project(Job job) => new()
    {
        Id = job.Id,
        FolderPath = job.FolderPath,
        IsFileJob = job.IsFileJob,
        SingleFilePath = job.SingleFilePath,
        Status = job.Status == JobStatus.Running ? JobStatus.Waiting : job.Status,
        Level = job.Level,
        DryRun = job.DryRun,
        OutputFolder = job.OutputFolder,
        EtaSeconds = job.EtaSeconds,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        ErrorMessage = job.ErrorMessage,
        Committed = job.Committed,
        Items = job.Items.Select(Project).ToList(),
    };

    private static JobItem Project(JobItem item) => new()
    {
        FilePath = item.FilePath,
        Kind = item.Kind,
        OldSize = item.OldSize,
        NewSize = item.NewSize,
        IsComplete = item.IsComplete,
        IsApplied = item.IsApplied,
        IsPredicted = item.IsPredicted,
        Skip = item.Skip,
        BackupPath = item.BackupPath,
        OutputPath = item.OutputPath,
        ElapsedSeconds = item.ElapsedSeconds,
        Message = item.Message,
        QualityScore = item.QualityScore,
        DurationSeconds = item.DurationSeconds,
    };
}
