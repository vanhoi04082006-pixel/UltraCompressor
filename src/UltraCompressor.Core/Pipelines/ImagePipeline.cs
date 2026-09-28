using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén ảnh JPEG/PNG. Dùng đúng tham số bản gốc, cộng thêm <c>-map_metadata 0</c>
/// để không mất EXIF/orientation (bản gốc bỏ trống nên ảnh chụp bị mất hướng).
/// </summary>
public sealed class ImagePipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Image;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var profile = CompressionProfile.For(context.Level);
        var temp = context.TempPath;

        onProgress(0);

        var (result, duration) = await ExecuteAsync(
            context,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-i", context.SourcePath,
                "-map_metadata", "0",
                "-q:v", profile.ImageQuality.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-vf", profile.ScaleFilter,
                "-y", temp,
            ],
            onProgress,
            token);

        // Ảnh không có khung hình nên không có % thật — báo 50 rồi 100 như bản gốc.
        onProgress(50);
        var outcome = Interpret(result, duration, context.Item, temp);
        if (outcome.Success) onProgress(100);
        return outcome;
    }
}
