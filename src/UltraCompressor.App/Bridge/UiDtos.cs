using System.IO;
using UltraCompressor.Core;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Scheduling;
using UltraCompressor.Core.Storage;
using UltraCompressor.Core.Toolchain;

namespace UltraCompressor.App.Bridge;

/// <summary>
/// DTO gửi sang giao diện web. Cố tình tách khỏi <see cref="Job"/>: job chứa cả danh sách
/// tệp, gửi cả lúc cập nhật tiến độ sẽ nghẽn giao diện với job vài nghìn tệp.
/// </summary>
public sealed record JobDto
{
    public required string Id { get; init; }

    public required string FolderName { get; init; }

    /// <summary>
    /// Tên hiện trên dòng danh sách. Khác <see cref="FolderName"/> khi job là một tệp lẻ:
    /// lúc đó hiện tên tệp chứ không phải tên thư mục cha.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>True khi job chỉ gồm một tệp lẻ, không phải cả thư mục.</summary>
    public bool IsFileJob { get; init; }

    /// <summary>
    /// Số tệp theo loại trong job, ví dụ <c>{"Video": 12, "Image": 3}</c>. Giao diện dùng để
    /// chọn badge ở cột đầu mà không phải dựng danh sách tệp cho từng job mỗi lần vẽ.
    /// </summary>
    public IReadOnlyDictionary<string, int> KindCounts { get; init; } = new Dictionary<string, int>();


    public required string FolderPath { get; init; }

    public required string Status { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public required string Level { get; init; }

    /// <summary>
    /// Mức nén của job khác mức đang chọn trong thanh công cụ không. Mức được chụp lúc
    /// thêm thư mục, nên đổi dropdown sau đó không đụng tới job cũ — dùng trường này để
    /// giao diện hiện nút "áp dụng mức hiện tại", thay vì im lặng nén bằng mức cũ.
    /// </summary>
    public bool LevelDiffersFromCurrent { get; init; }


    public bool DryRun { get; init; }

    public string? OutputFolder { get; init; }

    public long TotalFiles { get; init; }

    public long ProcessedCount { get; init; }

    public int Progress { get; init; }

    public long BytesOriginal { get; init; }

    public long BytesSaved { get; init; }

    public string SavedText { get; init; } = string.Empty;

    public string SavedPercentText { get; init; } = string.Empty;

    public double EtaSeconds { get; init; } = -1;

    public string EtaText { get; init; } = string.Empty;

    public string? ErrorMessage { get; init; }

    public bool Committed { get; init; }

    public bool CanPause { get; init; }

    public bool CanCancel { get; init; }

    public bool CanReview { get; init; }

    public long PendingBackups { get; init; }

    /// <summary>Tệp đang được nén, để dòng job hiện tiến độ tới từng tệp chứ không chỉ tổng.</summary>
    public string? ActiveFileName { get; init; }

    /// <summary>Phần trăm của tệp đang nén, -1 khi không có tệp nào đang chạy.</summary>
    public int ActivePercent { get; init; } = -1;

    public static JobDto From(Job job, long pendingBackups = -1, CompressionLevel? currentLevel = null) => new()
    {
        Id = job.Id,
        FolderName = job.FolderName,
        DisplayName = job.DisplayName,
        IsFileJob = job.IsFileJob,
        KindCounts = job.Items
            .GroupBy(i => i.Kind.ToString())
            .ToDictionary(g => g.Key, g => g.Count()),
        FolderPath = job.FolderPath,
        Status = job.Status.ToString(),
        StatusText = JobStatusLabel(job.Status),
        Level = CompressionProfileText(job.Level),
        LevelDiffersFromCurrent = currentLevel is { } current && current != job.Level,
        DryRun = job.DryRun,
        OutputFolder = job.OutputFolder,
        TotalFiles = job.TotalFiles,
        ProcessedCount = job.ProcessedCount,
        Progress = job.Progress,
        BytesOriginal = job.BytesOriginal,
        BytesSaved = job.BytesSaved,
        SavedText = Format.Size(job.BytesSaved),
        SavedPercentText = job.BytesOriginal > 0
            ? Format.Percent((double)job.BytesSaved * 100.0 / job.BytesOriginal)
            : Format.Percent(0),
        EtaSeconds = job.EtaSeconds,
        EtaText = job.EtaSeconds < 0 ? string.Empty : Format.Time(job.EtaSeconds),
        ErrorMessage = job.ErrorMessage,
        Committed = job.Committed,
        CanPause = job.Status is JobStatus.Running,
        CanCancel = job.Status is JobStatus.Running or JobStatus.Paused,
        CanReview = job.Status is JobStatus.PendingReview or JobStatus.Committed or JobStatus.Cancelled,
        PendingBackups = pendingBackups >= 0 ? pendingBackups : UndoService.PendingBackups(job).Count,
        ActiveFileName = ActiveItemOf(job)?.FileName,
        ActivePercent = ActiveItemOf(job)?.Percent ?? -1,
    };

    /// <summary>
    /// Tệp đang nén của job. Job có thể chạy nhiều luồng, lấy tệp nào đang chạy nhiều nhất
    /// để con số trên dòng job khớp với thứ đang thấy ở bảng chi tiết.
    /// </summary>
    private static JobItem? ActiveItemOf(Job job)
    {
        JobItem? best = null;
        foreach (var item in job.Items)
        {
            if (item.IsProcessing && (best is null || item.Percent > best.Percent)) best = item;
        }

        return best;
    }

    private static string JobStatusLabel(JobStatus status) => status switch
    {
        JobStatus.Waiting => "Hàng chờ",
        JobStatus.Running => "Đang chạy",
        JobStatus.Paused => "Tạm dừng",
        JobStatus.PendingReview => "Chờ duyệt",
        JobStatus.Committed => "Đã duyệt",
        JobStatus.Failed => "Lỗi",
        JobStatus.Cancelled => "Đã hủy",
        _ => status.ToString(),
    };

    private static string CompressionProfileText(CompressionLevel level) => level switch
    {
        CompressionLevel.Light => "Nhẹ",
        CompressionLevel.Balanced => "Cân bằng",
        CompressionLevel.Strong => "Mạnh",
        _ => level.ToString(),
    };
}

public sealed record ItemDto
{
    public required string FileName { get; init; }

    public required string FilePath { get; init; }

    public required string Kind { get; init; }

    public long OldSize { get; init; }

    public long NewSize { get; init; }

    public string OldSizeText { get; init; } = string.Empty;

    public string NewSizeText { get; init; } = string.Empty;

    public long SavedBytes { get; init; }

    public string SavedText { get; init; } = string.Empty;

    public string SavedPercentText { get; init; } = string.Empty;

    public required string State { get; init; }

    public string StateText { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string? SkipReasonText { get; init; }

    public string? Message { get; init; }

    /// <summary>
    /// Tham số nén thật đã dùng cho tệp này, do planner tính từ mức mục tiêu cộng đặc
    /// tính của tệp. Rỗng trước khi tệp được xử lý.
    /// </summary>
    public string Plan { get; init; } = string.Empty;

    public int Percent { get; init; }

    public bool IsProcessing { get; init; }

    public bool IsApplied { get; init; }

    /// <summary>Chỉ là kết quả dự đoán của chế độ thử, chưa ghi vào tệp gốc.</summary>
    public bool IsPredicted { get; init; }

    public bool HasBackup { get; init; }

    public bool CanPreview { get; init; }

    /// <summary>Thời gian đã dùng để nén tệp này, dạng "m:ss". Rỗng khi chưa xong lần nào.</summary>
    public string ElapsedText { get; init; } = string.Empty;


    public double? QualityScore { get; init; }

    public double DurationSeconds { get; init; }

    public string DurationText { get; init; } = string.Empty;

    public static ItemDto From(JobItem item) => new()
    {
        FileName = item.FileName,
        FilePath = item.FilePath,
        Kind = KindText(item.Kind),
        OldSize = item.OldSize,
        NewSize = item.NewSize,
        OldSizeText = Format.Size(item.OldSize),
        NewSizeText = Format.Size(item.NewSize),
        SavedBytes = item.SavedBytes,
        SavedText = Format.SizeSigned(item.SavedBytes),
        SavedPercentText = item.SavedBytes > 0 ? "-" + Format.Percent(item.SavedPercent) : string.Empty,
        State = StateOf(item),
        StateText = ItemStateLabel(item),
        Detail = DetailText(item),
        SkipReasonText = item.Skip == SkipReason.None ? null : item.Skip.ToString(),
        Message = item.Message,
        Plan = item.Plan ?? string.Empty,
        Percent = item.Percent,
        IsProcessing = item.IsProcessing,
        IsApplied = item.IsApplied,
        IsPredicted = item.IsPredicted,
        HasBackup = item.IsApplied && File.Exists(FileTransaction.BackupPathFor(item.FilePath)),
        CanPreview = item.Kind is MediaKind.Video or MediaKind.Audio,
        ElapsedText = item.ElapsedSeconds > 0 ? Format.Time(item.ElapsedSeconds) : string.Empty,
        QualityScore = item.QualityScore,
        DurationSeconds = item.DurationSeconds ?? 0,
        DurationText = item.DurationSeconds is { } d ? Format.Time(d) : string.Empty,
    };

    private static string KindText(MediaKind kind) => kind switch
    {
        MediaKind.Image => "Ảnh",
        MediaKind.Video => "Video",
        MediaKind.Audio => "Âm thanh",
        MediaKind.Gif => "GIF",
        MediaKind.Pdf => "PDF",
        _ => "Khác",
    };

    private static string StateOf(JobItem item) => item switch
    {
        { IsComplete: false, IsProcessing: true } => "processing",
        { IsComplete: false } => "queued",
        { Skip: not SkipReason.None } => "skipped",
        _ => "done",
    };

    private static string ItemStateLabel(JobItem item) => item switch
    {
        { IsComplete: false, IsProcessing: true } => "Đang xử lý",
        { IsComplete: false } => "Chờ",
        { IsPredicted: true } => "Dự kiến",
        { Succeeded: true } => "Thành công",
        { Skip: SkipReason.NoSizeGain } => "Giữ nguyên",
        { Skip: SkipReason.BelowMinSaving } => "Tiết kiệm ít",
        { Skip: SkipReason.ExcludedByFilter } => "Bị lọc",
        { Skip: SkipReason.UnsupportedFormat } => "Không hỗ trợ",
        { Skip: SkipReason.FileMissing } => "Thiếu tệp",
        { Skip: SkipReason.MissingTool } => "Thiếu công cụ",
        { Skip: SkipReason.ProcessFailed } => "Lỗi nén",
        { Skip: SkipReason.Cancelled } => "Đã hủy",
        { Skip: SkipReason.Error } => "Lỗi",
        _ => "Giữ nguyên",
    };

    private static string DetailText(JobItem item) => item switch
    {
        { IsComplete: false } => string.Empty,
        { Succeeded: true } => $"-{Format.Size(item.SavedBytes)} ({Format.Percent(item.SavedPercent)})",
        _ => string.IsNullOrEmpty(item.Message) ? "Không giảm" : item.Message,
    };
}

public sealed record ToolDto
{
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    public bool Required { get; init; }

    public string? Path { get; init; }

    public required string Health { get; init; }

    public string? Version { get; init; }

    public string? Message { get; init; }

    public static ToolDto From(ToolReport r) => new()
    {
        Kind = r.Kind.ToString(),
        DisplayName = r.DisplayName,
        Required = r.Required,
        Path = r.Path,
        Health = r.Health.ToString(),
        Version = r.Version,
        Message = r.Message,
    };
}

public sealed record KindTotal(string Kind, long SavedBytes, long OriginalBytes, long Count);

public sealed record UiState
{
    public required IReadOnlyList<JobDto> Jobs { get; init; }

    public long TotalBytesSaved { get; init; }

    public string TotalSavedText { get; init; } = string.Empty;

    public string TotalSavedPercentText { get; init; } = string.Empty;

    public long TotalBytesOriginal { get; init; }

    public long TotalFiles { get; init; }

    public long ProcessedFiles { get; init; }

    public IReadOnlyList<KindTotal> ByKind { get; init; } = [];

    public IReadOnlyList<ToolDto> Tools { get; init; } = [];

    public required AppConfig Config { get; init; }

    public bool IsRunning { get; init; }

    public bool IsPaused { get; init; }

    public long SpeedBytesPerSecond { get; init; }

    public string SpeedText { get; init; } = string.Empty;

    public int Concurrency { get; init; }

    public string AppDirectory { get; init; } = string.Empty;

    /// <summary>Thư mục gốc dự án — nơi mọi tệp của dự án được gom về.</summary>
    public string ProjectDirectory { get; init; } = string.Empty;

    public string DataDirectory { get; init; } = string.Empty;

    public IReadOnlyList<string> SupportedExtensions { get; init; } = [];
}
