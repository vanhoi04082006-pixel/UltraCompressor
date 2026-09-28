using System.Globalization;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Nén audio. Chỉ nhận MP3/M4A/AAC — những container chịu được AAC ở bitrate cố định.
/// WAV/FLAC bị loại có chủ ý ở <see cref="MediaClassifier"/>: đó thường là bản lưu trữ.
///
/// Bitrate đích đến từ kế hoạch, đã tính theo mức mục tiêu **và số kênh** của tệp: file
/// mono không cần 192k cho 16 kHz.
/// </summary>
public sealed class AudioPipeline : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Audio;

    public override async Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var plan = CompressionPlanner.PlanAudio(context.Level.ToGoal(), context.Probe);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(ProgressArgs);
        args.AddRange(
        [
            "-i", context.SourcePath,
            "-vn",
            "-map_metadata", "0",
            "-b:a", $"{plan.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k",
            "-y", context.TempPath,
        ]);

        var (result, duration) = await ExecuteAsync(context, args, onProgress, token);
        var outcome = Interpret(result, duration, context.Item, context.TempPath);
        if (outcome.Success) onProgress(100);
        return outcome;
    }
}
