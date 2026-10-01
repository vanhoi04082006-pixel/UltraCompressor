using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>Đường dẫn công cụ đã được xác định cho lần chạy này.</summary>
public sealed record ToolResolution(string? FFmpeg, string? FFplay, string? Gifsicle, string? Ghostscript);

/// <summary>
/// Mọi thứ một pipeline cần để nén một tệp.
///
/// <b>Pipeline không tự quyết định tham số.</b> Tham số đã được
/// <c>CompressionEngine</c> tính sẵn cho đúng tệp này từ mức mục tiêu của job, rồi đưa
/// vào đây. Nhờ vậy cùng một tệp luôn cho cùng một kết quả, quy tắc lập kế hoạch nằm gọn
/// trong một chỗ kiểm thử được, và pipeline chỉ còn lo dựng lệnh.
/// </summary>
public sealed record PipelineContext
{
    public required JobItem Item { get; init; }

    /// <summary>Nơi ghi kết quả tạm. Pipeline <b>không</b> được tự chọn đường dẫn này.</summary>
    public required string TempPath { get; init; }

    /// <summary>Mức mục tiêu của job, để pipeline dựng thông điệp và bộ lọc.</summary>
    public required CompressionLevel Level { get; init; }

    /// <summary>Tham số đã tính riêng cho tệp này. Không có thì pipeline tự suy từ probe.</summary>
    public MediaInfo? Probe { get; init; }

    public required AppConfig Config { get; init; }

    public required ToolResolution Tools { get; init; }

    /// <summary>Chỉ khi Job.DryRun mới điền — nơi đích cuối cùng thay cho tệp gốc.</summary>
    public string? OutputPath { get; init; }

    public string SourcePath => Item.FilePath;
}

public sealed record PipelineResult
{
    public required bool Success { get; init; }

    public SkipReason Skip { get; init; } = SkipReason.None;

    public long NewSize { get; init; }

    public double DurationSeconds { get; init; }

    public string? Message { get; init; }

    /// <summary>
    /// Mã kết cục máy đọc được, để đưa vào <c>item.DecisionReason</c> và gom số liệu.
    /// </summary>
    /// <remarks>
    /// Rỗng khi pipeline không biết nói gì tử tế; <see cref="TerminalReason"/> sẽ dựng mã thay
    /// thế từ <see cref="Skip"/>. Pipeline biết bốn kết cục của đường thích ứng thì đặt mã
    /// <see cref="SearchDecisionReasons"/> tại đây.
    /// </remarks>
    public string? Reason { get; init; }

    public static PipelineResult NotWorthIt(SkipReason reason, long newSize, string? message = null)
        => new() { Success = false, Skip = reason, NewSize = newSize, Message = message };

    public static PipelineResult Failed(SkipReason reason, string message)
        => new() { Success = false, Skip = reason, Message = message };

    public static PipelineResult Ok(long newSize, double duration = 0, string? message = null)
        => new() { Success = true, NewSize = newSize, DurationSeconds = duration, Message = message };
}

/// <summary>Một chiến lược nén cho một loại media.</summary>
public interface IMediaPipeline
{
    MediaKind Kind { get; }

    Task<PipelineResult> RunAsync(
        PipelineContext context,
        Action<int> onProgress,
        CancellationToken token);
}
