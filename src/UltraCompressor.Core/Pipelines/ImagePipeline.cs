using System.Globalization;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén ảnh JPEG/PNG bằng ffmpeg.
///
/// Tham số đến từ kế hoạch: <c>-q:v</c> và bề rộng mục tiêu đã được tính cho riêng tệp
/// này. Cộng thêm <c>-map_metadata 0</c> để không mất EXIF/hướng ảnh (bản gốc bỏ trống nên
/// ảnh chụp bị mất hướng).
///
/// Không có tiến độ thật: ảnh tĩnh chỉ có một khung hình, ffmpeg encode xong trong dưới
/// một giây. Báo 0 rồi 100.
/// </summary>
public sealed class ImagePipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Image;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var temp = context.TempPath;

        var plan = CompressionPlanner.PlanImage(
            context.Level.ToGoal(),
            context.Probe,
            context.Item.SourceWidth,
            context.Item.OldSize);

        onProgress(0);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-i", context.SourcePath,
            "-map_metadata", "0",
            "-q:v", plan.QScale.ToString(CultureInfo.InvariantCulture),
        };

        // TargetWidth = 0 nghĩa là kế hoạch quyết định giữ nguyên bề rộng (ví dụ ảnh đã quá nhỏ).
        if (plan.TargetWidth > 0)
        {
            args.AddRange(["-vf", ImageFilter.Build(plan.TargetWidth)]);
        }

        args.AddRange(["-y", temp]);

        var (result, duration) = await ExecuteAsync(context, args, onProgress, token);
        var outcome = Interpret(result, duration, context.Item, temp);
        if (outcome.Success) onProgress(100);
        return outcome;
    }
}

internal static class ImageFilter
{
    /// <summary>Chỉ thu nhỏ, không bao giờ phóng to: <c>min()</c> nên không cần biết kích thước nguồn.</summary>
    public static string Build(int maxWidth) => $"scale='min({maxWidth},iw)':-2";
}
