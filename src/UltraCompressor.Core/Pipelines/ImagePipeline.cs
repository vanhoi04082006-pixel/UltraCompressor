using System.Globalization;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén ảnh JPEG/PNG. Dùng tham số của mức nén đang chọn, cộng thêm <c>-map_metadata 0</c>
/// để không mất EXIF/orientation (bản gốc bỏ trống nên ảnh chụp bị mất hướng).
///
/// Không có tiến độ thật: ảnh tĩnh chỉ có một khung hình, ffmpeg encode xong trong
/// dưới một giây. Báo 0 rồi 100 — trước đây báo 0 → 50 → 100, con số 50 là bịa ra và làm
/// thanh tiến độ nhảy về giữa rồi mới nhảy tiếp, trông như kẹt.
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
                "-q:v", profile.ImageQuality.ToString(CultureInfo.InvariantCulture),
                "-vf", profile.ImageFilter,
                "-y", temp,
            ],
            onProgress,
            token);

        var outcome = Interpret(result, duration, context.Item, temp);
        if (outcome.Success) onProgress(100);
        return outcome;
    }
}
