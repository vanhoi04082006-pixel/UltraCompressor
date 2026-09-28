using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Pipelines;

/// <summary>Phần dùng chung cho các pipeline gọi ffmpeg: chạy lệnh và báo tiến độ.</summary>
public abstract class FFmpegPipelineBase : IMediaPipeline
{
    public abstract MediaKind Kind { get; }

    public abstract Task<PipelineResult> RunAsync(
        PipelineContext context,
        Action<int> onProgress,
        CancellationToken token);

    protected static async Task<(ProcessResult Result, TimeSpan? Duration)> ExecuteAsync(
        PipelineContext context,
        IReadOnlyList<string> arguments,
        Action<int> onProgress,
        CancellationToken token)
    {
        var ffmpeg = context.Tools.FFmpeg
            ?? throw new SkipException(SkipReason.MissingTool, "Không tìm thấy ffmpeg.exe.");

        return await ExecuteToolAsync(ffmpeg, arguments, onProgress, token);
    }

    /// <summary>
    /// Chạy một công cụ bất kỳ (ffmpeg, gifsicle, gswin64c) và theo dõi tiến độ nếu công cụ
    /// có in dòng <c>time=</c> như ffmpeg.
    /// </summary>
    protected static async Task<(ProcessResult Result, TimeSpan? Duration)> ExecuteToolAsync(
        string toolPath,
        IReadOnlyList<string> arguments,
        Action<int> onProgress,
        CancellationToken token)
    {
        TimeSpan? duration = null;
        var reported = 0;

        var result = await ProcessRunner.RunAsync(
            toolPath,
            arguments,
            onStdoutLine: null,
            onStderrLine: line =>
            {
                if (duration is null)
                {
                    duration = FFmpegOutputParser.ParseDuration(line);
                }

                if (duration is { } total && total > TimeSpan.Zero)
                {
                    var position = FFmpegOutputParser.ParseTime(line);
                    if (position is { } pos && FFmpegOutputParser.ToPercent(pos, total) is { } percent && percent > reported)
                    {
                        reported = percent;
                        onProgress(percent);
                    }
                }
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
