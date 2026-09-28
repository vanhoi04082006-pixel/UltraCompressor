using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén audio. Chỉ nhận MP3/M4A/AAC — những container chịu được AAC ở bitrate cố định.
/// WAV/FLAC bị loại có chủ ý ở <see cref="MediaClassifier"/>: đó thường là bản lưu trữ.
///
/// Sửa so với bản gốc: không ép bitrate khi nguồn đã nhỏ hơn (bản gốc ép 320k cho cả file
/// nguồn 128k, chỉ làm file to thêm mà không thu được gì).
/// </summary>
public sealed class AudioPipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Audio;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var profile = CompressionProfile.For(context.Level);
        var targetBitrate = profile.AudioBitrateKbps;

        // Nguồn đã nhỏ hơn mức đích thì giữ nguyên bitrate để không nâng chất lượng vô ích.
        if (context.Item.SourceBitrateKbps is { } source && source > 0 && source <= targetBitrate)
        {
            var bitrate = (int)Math.Round(source);
            var (noOp, noOpDuration) = await ExecuteAsync(
                context,
                BuildArgs(context, bitrate),
                onProgress,
                token);
            var unchanged = Interpret(noOp, noOpDuration, context.Item, context.TempPath);
            if (unchanged.Success) onProgress(100);
            return unchanged;
        }

        var (result, duration) = await ExecuteAsync(context, BuildArgs(context, targetBitrate), onProgress, token);
        var outcome = Interpret(result, duration, context.Item, context.TempPath);
        if (outcome.Success) onProgress(100);
        return outcome;
    }

    private static List<string> BuildArgs(PipelineContext context, int bitrateKbps) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-nostdin",
        "-i", context.SourcePath,
        "-vn",
        "-map_metadata", "0",
        "-b:a", $"{bitrateKbps}k",
        "-y", context.TempPath,
    ];
}
