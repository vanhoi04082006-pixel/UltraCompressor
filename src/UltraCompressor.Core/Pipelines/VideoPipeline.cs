using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén video bằng libx264 (H.264). Tham số CRF/preset/scale giữ nguyên bản gốc.
///
/// Khác bản gốc:
///  - thêm <c>-nostdin</c> (bản gốc có thể treo vô hạn vì để stdin mở),
///  - dùng <c>-map 0:v:0?</c> / <c>-map 0:a:0?</c> nên video không có tiếng vẫn nén được,
///  - giữ metadata nguồn.
/// </summary>
public sealed class VideoPipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Video;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var profile = CompressionProfile.For(context.Level);
        var temp = context.TempPath;

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-i", context.SourcePath,
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-map_metadata", "0",
            "-c:v", "libx264",
            "-crf", profile.VideoCrf.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-preset", profile.VideoPreset,
            "-vf", profile.ScaleFilter,
        };

        // Không ép codec âm thanh nếu nguồn không có tiếng. Thông tin đã lấy sẵn ở bước probe
        // nên không cần chạy ffmpeg thêm một lần ở đây.
        if (context.Item.HasAudio == false)
        {
            args.Add("-an");
        }
        else
        {
            args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        }

        args.AddRange(["-movflags", "+faststart", "-y", temp]);

        var (result, duration) = await ExecuteAsync(context, args, onProgress, token);
        var outcome = Interpret(result, duration, context.Item, temp);
        if (outcome.Success) onProgress(100);
        return outcome;
    }
}
