using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>Chọn pipeline theo loại media.</summary>
public sealed class PipelineFactory
{
    private readonly Dictionary<MediaKind, IMediaPipeline> _pipelines = new()
    {
        [MediaKind.Image] = new ImagePipeline(),

        // `AdaptiveVideoPipeline` tự chuyển về `VideoPipeline` khi
        // `AppConfig.EnableAdaptiveSearch` tắt. Đăng ký ở đây thay vì chọn lúc khởi tạo để
        // công tắc bật/tắt nằm ở một chỗ, và để đổi cờ giữa phiên là có tác dụng.
        [MediaKind.Video] = new AdaptiveVideoPipeline(new VideoPipeline()),
        [MediaKind.Audio] = new AudioPipeline(),
        [MediaKind.Gif] = new GifPipeline(),
        [MediaKind.Pdf] = new PdfPipeline(),
    };

    public IMediaPipeline? Resolve(MediaKind kind) => _pipelines.GetValueOrDefault(kind);

    public IReadOnlyCollection<IMediaPipeline> All => _pipelines.Values;
}
