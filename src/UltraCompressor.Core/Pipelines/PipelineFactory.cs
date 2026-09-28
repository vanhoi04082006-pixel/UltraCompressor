using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>Chọn pipeline theo loại media.</summary>
public sealed class PipelineFactory
{
    private readonly Dictionary<MediaKind, IMediaPipeline> _pipelines = new()
    {
        [MediaKind.Image] = new ImagePipeline(),
        [MediaKind.Video] = new VideoPipeline(),
        [MediaKind.Audio] = new AudioPipeline(),
        [MediaKind.Gif] = new GifPipeline(),
        [MediaKind.Pdf] = new PdfPipeline(),
    };

    public IMediaPipeline? Resolve(MediaKind kind) => _pipelines.GetValueOrDefault(kind);

    public IReadOnlyCollection<IMediaPipeline> All => _pipelines.Values;
}
