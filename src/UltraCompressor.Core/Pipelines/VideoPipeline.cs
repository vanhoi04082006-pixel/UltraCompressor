using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén video bằng libx264 (H.264).
///
/// Pipeline này **không tự chọn tham số**. Nó nhận kế hoạch do
/// <c>CompressionEngine</c> tính riêng cho đúng tệp này: cùng một mức mục tiêu nhưng CRF,
/// bề rộng và số khung hình khác nhau, tuỳ nguồn còn dư chi tiết hay không, nguồn dùng
/// codec nào, và đã bị nén tới đâu.
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
        var temp = context.TempPath;

        var plan = CompressionPlanner.PlanVideo(
            context.Level.ToGoal(),
            context.Probe,
            context.Item.SourceWidth,
            context.Item.SourceBitrateKbps,
            context.Item.HasAudio ?? true);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(ProgressArgs);
        args.AddRange(
        [
            "-i", context.SourcePath,
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-map_metadata", "0",
            "-c:v", "libx264",
            "-crf", plan.Crf.ToString(CultureInfo.InvariantCulture),
            "-preset", plan.Preset,
        ]);

        // Rỗng = giữ nguyên cả bề rộng lẫn số khung hình, không dựng `-vf` cho nên không tốn công gì.
        var filter = VideoFilter.Build(plan.TargetWidth, plan.TargetFps);
        if (filter.Length > 0)
        {
            args.AddRange(["-vf", filter]);
        }

        if (plan.DropAudio)
        {
            args.Add("-an");
        }
        else
        {
            // Không nâng bitrate của tệp nguồn vốn đã nhỏ hơn — chỉ làm tệp to thêm mà
            // không thu được gì. Cùng logic với AudioPipeline.
            var target = plan.AudioBitrateKbps;
            if (context.Item.SourceBitrateKbps is { } audioSource && audioSource > 0 && audioSource < target)
            {
                target = (int)Math.Round(audioSource);
            }

            args.AddRange(["-c:a", "aac", "-b:a", $"{target.ToString(CultureInfo.InvariantCulture)}k"]);
        }

        args.AddRange(["-movflags", "+faststart", "-y", temp]);

        var (result, duration) = await ExecuteAsync(context, args, onProgress, token);
        var outcome = Interpret(result, duration, context.Item, temp);
        if (outcome.Success) onProgress(100);
        return outcome;
    }
}

/// <summary>Dựng chuỗi <c>-vf</c> cho video.</summary>
internal static class VideoFilter
{
    /// <summary>
    /// Hạ số khung hình phải đứng <b>trước</b> bước thu nhỏ: `fps` trước `scale` nghĩa là
    /// những khung bị bỏ đi không phải thu nhỏ, làm nhanh hơn. Ngược lại sẽ thu nhỏ cả rồi
    /// mới bỏ, tốn công vô ích trên video dài.
    /// </summary>
    public static string Build(int? maxWidth, double? fps)
    {
        var parts = new List<string>(2);

        if (fps is { } f && f > 0)
        {
            parts.Add($"fps={f.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (maxWidth is { } w && w > 0)
        {
            // `min()` nên không cần biết bề rộng thật của nguồn, và không bao giờ phóng to.
            parts.Add($"scale='min({w},iw)':-2");
        }

        return string.Join(",", parts);
    }
}
