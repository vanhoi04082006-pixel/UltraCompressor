using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Pipelines;

/// <summary>Phần dùng chung cho các pipeline gọi ffmpeg: chạy lệnh và báo tiến độ.</summary>
public abstract class FFmpegPipelineBase : IMediaPipeline
{
    /// <summary>
    /// Tham số bắt buộc cho mọi lệnh ffmpeg dùng để báo tiến độ.
    ///
    /// Vì sao không bật <c>-stats</c>: dòng tiến độ của ffmpeg nằm trên <b>stderr</b> ở mức
    /// <c>info</c>, nên với <c>-loglevel error</c> mà pipeline đang dùng thì nó
    /// <b>không bao giờ được in</b> — và trước đây đúng là vậy, nên
    /// <c>FFmpegOutputParser.ParseTime</c> không bao giờ khớp và tiến độ từng tệp đứng yên
    /// ở 0% suốt tới khi xong.
    ///
    /// <c>-progress pipe:1</c> ghi <c>out_time_us=</c> ra <b>stdout</b> dạng
    /// <c>key=value</c>, hoạt động ở mọi mức log, và không lẫn thông tin nhập/xuất vào
    /// stderr nên tệp tạm vẫn ghi đúng những gì pipeline cần đọc.
    /// </summary>
    protected static readonly string[] ProgressArgs = ["-progress", "pipe:1"];

    public abstract MediaKind Kind { get; }

    public abstract Task<PipelineResult> RunAsync(
        PipelineContext context,
        Action<int> onProgress,
        CancellationToken token);

    protected static Task<(ProcessResult Result, TimeSpan? Duration)> ExecuteAsync(
        PipelineContext context,
        IReadOnlyList<string> arguments,
        Action<int> onProgress,
        CancellationToken token)
    {
        var ffmpeg = context.Tools.FFmpeg
            ?? throw new SkipException(SkipReason.MissingTool, "Không tìm thấy ffmpeg.exe.");

        return ExecuteToolAsync(ffmpeg, arguments, onProgress, token, context.Item.DurationSeconds);
    }

    /// <summary>
    /// Chạy một công cụ bất kỳ (ffmpeg, gifsicle, gswin64c) và báo tiến độ.
    ///
    /// <paramref name="knownDurationSeconds"/> là thời lượng đã biết từ bước probe. Có nó
    /// thì phần trăm chính xác ngay từ đầu; không có thì thử đọc <c>Duration:</c> trên
    /// stderr như trước (chỉ chạy được khi công cụ không bị chặn log).
    /// </summary>
    protected static async Task<(ProcessResult Result, TimeSpan? Duration)> ExecuteToolAsync(
        string toolPath,
        IReadOnlyList<string> arguments,
        Action<int> onProgress,
        CancellationToken token,
        double? knownDurationSeconds = null)
    {
        TimeSpan? duration = null;
        var reported = 0;

        if (knownDurationSeconds is > 0)
        {
            duration = TimeSpan.FromSeconds(knownDurationSeconds.Value);
        }

        var result = await ProcessRunner.RunAsync(
            toolPath,
            arguments,
            onStdoutLine: line =>
            {
                if (duration is null) return;

                var position = FFmpegOutputParser.ParseProgressTime(line);
                if (position is not { } pos) return;

                if (FFmpegOutputParser.ToPercent(pos, duration.Value) is not { } percent) return;
                if (percent <= reported) return;

                reported = percent;
                onProgress(percent);
            },
            onStderrLine: line =>
            {
                if (duration is not null) return;

                // Dự phòng cho công cụ không dùng -progress (Ghostscript) hoặc khi probe
                // không lấy được thời lượng.
                duration = FFmpegOutputParser.ParseDuration(line);
            },
            token);

        return (result, duration);
    }

    /// <summary>Chuyển kết quả tiến trình thành PipelineResult.</summary>
    protected static PipelineResult Interpret(ProcessResult result, TimeSpan? duration, JobItem item, string outputPath)
    {
        if (result.Cancelled)
        {
            return PipelineResult.Failed(SkipReason.Cancelled, "Đã hủy.");
        }

        if (!result.Succeeded)
        {
            var detail = result.StandardErrorText;
            if (detail.Length > 500) detail = detail[^500..];
            return PipelineResult.Failed(
                SkipReason.ProcessFailed,
                $"ffmpeg trả về mã {result.ExitCode}. {detail}".Trim());
        }

        if (!File.Exists(outputPath))
        {
            return PipelineResult.Failed(SkipReason.ProcessFailed, "ffmpeg báo thành công nhưng không tạo ra tệp.");
        }

        var size = new FileInfo(outputPath).Length;
        return PipelineResult.Ok(size, duration?.TotalSeconds ?? 0);
    }
}

/// <summary>Báo "bỏ qua" do cấu hình, không phải do lỗi kỹ thuật.</summary>
public sealed class SkipException(SkipReason reason, string message) : Exception(message)
{
    public SkipReason Reason { get; } = reason;
}
