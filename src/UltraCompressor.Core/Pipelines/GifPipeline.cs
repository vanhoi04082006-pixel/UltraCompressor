using System.Globalization;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén GIF theo đúng hai bước: ffmpeg giảm fps / thu nhỏ / tạo palette, rồi gifsicle tối ưu.
///
/// Sửa bug B1 của bản gốc: bản gốc chạy <c>if (File.Exists(gifsicle))</c> — tức khi CÓ gifsicle
/// thì chỉ gọi gifsicle (không giảm fps, không resize, không palettegen), còn khi THIẾU gifsicle
/// mới chạy ffmpeg. Điều đó làm nhánh ffmpeg (fps=15, fps=20) thành code chết. Ở đây luôn chạy
/// ffmpeg trước, gifsicle là bước tuỳ chọn phía sau.
/// </summary>
public sealed class GifPipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Gif;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var temp = context.TempPath;
        var gifsicle = context.Tools.Gifsicle;

        var plan = CompressionPlanner.PlanGif(
            context.Level.ToGoal(),
            context.Probe,
            context.Item.SourceWidth,
            context.Item.OldSize);

        // Không có gifsicle thì ghi thẳng ra tệp cuối.
        var intermediate = gifsicle is null
            ? temp
            : temp + ".stage1" + Path.GetExtension(temp);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(ProgressArgs);
        args.AddRange(
        [
            "-i", context.SourcePath,
            "-vf", GifFilter.Build(plan),
            "-loop", "0",
            "-y", intermediate,
        ]);

        var (result, duration) = await ExecuteAsync(
            context,
            args,
            // Bước 1 chiếm 80% tiến độ, bước gifsicle chiếm 20% còn lại.
            p => onProgress((int)(p * 0.8)),
            token);

        var first = Interpret(result, duration, context.Item, intermediate);
        if (!first.Success)
        {
            SafeDelete(intermediate);
            return first;
        }

        if (gifsicle is null)
        {
            onProgress(100);
            return PipelineResult.Ok(first.NewSize, first.DurationSeconds,
                "Không có gifsicle nên chỉ dùng bước ffmpeg.");
        }

        // gifsicle không in tiến độ: nó làm việc nhanh và không nhận đầu vào có tỉ lệ.
        // 80% rồi nhảy thẳng 100%, thay vì báo đều đặn 85% như trước — số 85 đó chỉ là
        // số bịa, gifsicle chẳng báo gì cả.
        var (gifsicleResult, _) = await ExecuteToolAsync(
            gifsicle,
            [
                $"--lossy={plan.Lossy.ToString(CultureInfo.InvariantCulture)}",
                "-O3",
                "--colors", "256",
                intermediate,
                "-o", temp,
            ],
            _ => onProgress(80),
            token);

        // Ưu tiên kết quả nhỏ hơn giữa hai bước — gifsicle đôi khi làm file to hơn.
        if (gifsicleResult.Succeeded && File.Exists(temp))
        {
            var optimized = new FileInfo(temp).Length;
            var best = Math.Min(optimized, first.NewSize);
            if (optimized >= first.NewSize && File.Exists(intermediate))
            {
                File.Copy(intermediate, temp, overwrite: true);
            }
            SafeDelete(intermediate);
            onProgress(100);
            return PipelineResult.Ok(best, first.DurationSeconds,
                optimized < first.NewSize ? "ffmpeg + gifsicle" : "chỉ ffmpeg (gifsicle không nhỏ hơn)");
        }

        // gifsicle lỗi: giữ kết quả bước một thay vì mất trắng.
        SafeDelete(temp);
        File.Copy(intermediate, temp, overwrite: true);
        SafeDelete(intermediate);
        onProgress(100);
        return PipelineResult.Ok(first.NewSize, first.DurationSeconds,
            $"gifsicle lỗi (exit {gifsicleResult.ExitCode}), giữ kết quả bước ffmpeg.");
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Tệp tạm bị khoá — sẽ dọn ở lần quét sau.
        }
    }
}

internal static class GifFilter
{
    /// <summary>
    /// Bộ lọc GIF: giảm fps → thu nhỏ → tách palette.
    ///
    /// Thứ tự có ý nghĩa: hạ fps trước rồi mới thu nhỏ, vì khung bị bỏ đi không nên phải
    /// thu nhỏ. `palettegen` rồi tới `paletteuse` dùng chung một bảng màu để màu không bị
    /// dịch chuyển giữa các khung — thiếu bước này thì GIF nhấp nháy màu.
    /// </summary>
    public static string Build(GifPlan plan)
    {
        var scale = plan.TargetWidth > 0
            ? $"scale='min({plan.TargetWidth},iw)':-1:flags=lanczos"
            : "scale=iw:-1:flags=lanczos";

        return $"fps={plan.Fps},{scale},split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";
    }
}
