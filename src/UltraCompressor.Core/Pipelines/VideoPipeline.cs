using System.Globalization;
using UltraCompressor.Core.Encoders;
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

    /// <summary>
    /// Bộ đo SI/TI dùng chung cho mọi tệp. Cùng lý do như <see cref="EncoderCache"/>: mỗi tệp
    /// một tiến trình ffmpeg là lãng phí, và tệp nhỏ thì việc khởi tạo còn nặng hơn cả việc đo.
    /// </summary>
    private static ContentComplexityProbe? _complexityProbe;
    private static readonly Lock ProbeLock = new();

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var temp = context.TempPath;
        var probe = context.Probe ?? new MediaInfo();

        // Đo SI/TI trước khi lập kế hoạch: nhóm nội dung quyết định codec, mà codec quyết
        // định CRF. Đo sau thì phải tính lại kế hoạch lần hai.
        //
        // Bọc trong thử: đây là tối ưu, không phải điều kiện để nén. Không có nó thì vẫn nén
        // được bằng đường lùi.
        probe = await WithComplexityAsync(context, probe, token).ConfigureAwait(false);

        var plan = CompressionPlanner.PlanVideo(
            context.Level.ToGoal(),
            probe,
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
                Crf = Math.Max(0, plan.Crf - HevcCrfOffset),
                Reason = string.Join("; ", "bản ffmpeg này không có libx265 — dùng H.264", plan.Reason),
            };
        }

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(ProgressArgs);
        args.AddRange(["-i", context.SourcePath, "-map", "0:v:0?", "-map", "0:a:0?", "-map_metadata", "0"]);

        // Dựng phần encoder từ cấu hình có kiểu, không ghép tay từ các chuỗi rời rạc.
        // Đây là tầng duy nhất được phép biết tên công tắc ffmpeg, nên cũng là tầng duy
        // nhất có thể sinh ra một cặp công tắc/giá trị sai.
        var encoder = EncoderConfigurationFor(plan);
        if (!encoder.Validate(out var configurationFailure))
        {
            return PipelineResult.Failed(
                SkipReason.Error,
                $"Cấu hình encoder không hợp lệ: {configurationFailure}");
        }

        args.AddRange(encoder.ToArguments());

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
    /// Lệch thang CRF giữa H.264 và HEVC. Tái số từ planner để nhánh fallback về H.264 bù
    /// đúng bằng thứ đã cộng — để một hằng số ở hai nơi là cách chắc chắn nhất để chúng lệch.
    /// </summary>
    private const int HevcCrfOffset = CompressionPlanner.HevcCrfOffsetForH264;

    /// <summary>
    /// Ép kế hoạch của planner cũ thành cấu hình encode có kiểu.
    ///
    /// <para>Đây là chỗ nối duy nhất giữa đường legacy và mô hình tuỳ chọn mới. Nó tồn tại
    /// để <see cref="CompressionPlanner"/> (vốn trả chuỗi trần) không thể tự dựng được
    /// lệnh; khi giai đoạn tìm kiếm nối vào, chỗ này biến mất cùng đường legacy.</para>
    ///
    /// <para>Chọn lớp tuỳ chọn theo <b>tên encoder</b>, không theo tên codec trong kế
    /// hoạch: <c>-crf 0..51</c> cho x26x và <c>-crf 0..63</c> cho libaom là hai miền khác
    /// nhau, còn <c>-cpu-used</c> thì không dùng chung với <c>-preset</c>.</para>
    /// </summary>
    public static EncoderConfiguration EncoderConfigurationFor(VideoPlan plan)
    {
        var kind = EncoderRanges.KindOf(plan.VideoEncoder);
        var crf = plan.Crf;

        return new EncoderConfiguration
        {
            EncoderName = plan.VideoEncoder,
            Quality = kind switch
            {
                EncoderKind.LibaomAv1 => QualityOption.LibaomCrf(crf),
                EncoderKind.SvtAv1 => QualityOption.SvtAv1Qp(crf),
                EncoderKind.Hardware => QualityOption.HardwareConstantQuality(crf),
                _ => QualityOption.X26xCrf(crf),
            },
            Speed = kind switch
            {
                EncoderKind.LibaomAv1 => SpeedOption.AomCpuUsed(6),
                EncoderKind.SvtAv1 => SpeedOption.SvtAv1Preset(8),
                EncoderKind.Hardware => SpeedOption.HardwareEncoderPreset(plan.Preset),
                _ => SpeedOption.X26xPreset(plan.Preset),
            },
            PixelFormat = "yuv420p",
        };
    }

    /// <summary>
    /// Bổ sung đặc trưng nội dung vào kết quả probe. Không ném lỗi ra ngoài: mất đo được
    /// thì vẫn nén, chỉ là không có tín hiệu để chọn codec.
    /// </summary>
    private static async Task<MediaInfo> WithComplexityAsync(
        PipelineContext context, MediaInfo probe, CancellationToken token)
    {
        if (probe.Complexity is not null) return probe;
        if (!probe.HasVideo) return probe;

        var ffmpeg = context.Tools.FFmpeg;
        if (ffmpeg is null) return probe;

        ContentComplexityProbe tool;
        lock (ProbeLock)
        {
            _complexityProbe ??= new ContentComplexityProbe(ffmpeg);
            tool = _complexityProbe;
        }

        try
        {
            return probe with { Complexity = await tool.ProbeAsync(context.SourcePath, probe.Duration, token) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return probe;
        }
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
