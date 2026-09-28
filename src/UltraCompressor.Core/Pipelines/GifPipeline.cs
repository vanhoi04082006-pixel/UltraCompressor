using UltraCompressor.Core.Models;
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
        var profile = CompressionProfile.For(context.Level);
        var temp = context.TempPath;
        var gifsicle = context.Tools.Gifsicle;

        // Không có gifsicle thì ghi thẳng ra tệp cuối.
        var intermediate = gifsicle is null
            ? temp
            : temp + ".stage1" + Path.GetExtension(temp);

        var (result, duration) = await ExecuteAsync(
            context,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-i", context.SourcePath,
                "-vf", profile.GifFilter,
                "-loop", "0",
                "-y", intermediate,
            ],
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

        var (gifsicleResult, _) = await ExecuteToolAsync(
            gifsicle,
            [
                $"--lossy={profile.GifLossy}",
                "-O3",
                "--colors", "256",
                intermediate,
                "-o", temp,
            ],
            _ => onProgress(85),
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
