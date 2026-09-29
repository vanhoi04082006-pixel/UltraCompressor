using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Processes;

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
            context.Item.HasAudio ?? true,
            context.Config.VideoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase));

        // Chỉ dùng HEVC khi bản dựng ffmpeg thực sự có libx265. Bản dựng không có thì
        // lệnh sẽ chết ngay và không ra tệp, mà người dùng chỉ thấy job lỗi chung chung.
        // Lùi về H.264 thì luôn chạy được.
        if (plan.VideoEncoder == "libx265" && !await HasEncoderAsync(context, "libx265", token))
        {
            // Bù lại phần CRF đã cộng cho HEVC, và ghi rõ lý do vào kế hoạch để người
            // dùng thấy — im lặng đổi codec thì họ tưởng đã nén bằng HEVC.
            plan = plan with
            {
                VideoEncoder = "libx264",
                Crf = Math.Max(0, plan.Crf - 2),
                Reason = string.Join("; ", "bản ffmpeg này không có libx265 — dùng H.264", plan.Reason),
            };
        }

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(ProgressArgs);
        args.AddRange(
        [
            "-i", context.SourcePath,
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-map_metadata", "0",
            "-c:v", plan.VideoEncoder,
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

    /// <summary>
    /// Kiểm tra bản dựng ffmpeg có encoder không. Kết quả nhớ lại — hỏi lại cho từng tệp là
    /// mỗi tệp lại mở một tiến trình chỉ để liệt kê encoder.
    /// </summary>
    private static readonly Dictionary<string, bool> EncoderCache = new(StringComparer.Ordinal);

    private static async Task<bool> HasEncoderAsync(PipelineContext context, string encoder, CancellationToken token)
    {
        if (EncoderCache.TryGetValue(encoder, out var known)) return known;

        var ffmpeg = context.Tools.FFmpeg;
        if (ffmpeg is null) return false;

        var result = await ProcessRunner.RunAsync(
            ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-encoders"],
            TimeSpan.FromSeconds(20),
            token);

        var found = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(encoder, StringComparison.Ordinal));

        EncoderCache[encoder] = found;
        return found;
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
