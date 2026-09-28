using System.Globalization;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén video bằng libx264 (H.264). Tham số CRF/preset/scale lấy từ mức nén đang chọn.
///
/// Khác bản gốc:
///  - thêm <c>-nostdin</c> (bản gốc có thể treo vô hạn vì để stdin mở),
///  - dùng <c>-map 0:v:0?</c> / <c>-map 0:a:0?</c> nên video không có tiếng vẫn nén được,
///  - giữ metadata nguồn,
///  - <b>bitrate âm thanh lấy từ mức nén</b> thay vì ghim cứng 128k. Trước đây dòng này
///    là <c>-b:a 128k</c> nên chọn "Nhẹ" (320k) hay "Mạnh" (128k) thì âm thanh trong video
///    vẫn luôn ra 128k, còn mục Âm thanh trong bảng hướng dẫn lại hiện 320k/192k/128k.
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
        };

        args.AddRange(ProgressArgs);
        args.AddRange(
        [
            "-i", context.SourcePath,
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-map_metadata", "0",
            "-c:v", "libx264",
            "-crf", profile.VideoCrf.ToString(CultureInfo.InvariantCulture),
            "-preset", profile.VideoPreset,
            "-vf", profile.VideoFilter,
        ]);

        // Không ép codec âm thanh nếu nguồn không có tiếng. Thông tin đã lấy sẵn ở bước probe
        // nên không cần chạy ffmpeg thêm một lần ở đây.
        if (context.Item.HasAudio == false)
        {
            args.Add("-an");
        }
        else
        {
            // Không nâng bitrate của tệp nguồn vốn đã nhỏ hơn — chỉ làm file to thêm mà
            // không thu được gì. Cùng logic với AudioPipeline.
            var target = profile.AudioBitrateKbps;
            if (context.Item.SourceBitrateKbps is { } source && source > 0 && source < target)
            {
                target = (int)Math.Round(source);
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
