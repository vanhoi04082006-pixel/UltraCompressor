using UltraCompressor.Core.Models;

namespace UltraCompressor.Core;

/// <summary>Phân loại tệp theo phần mở rộng.</summary>
public static class MediaClassifier
{
    // Bản gốc v12: .mp4 .mkv .mov .avi .webm .flv .wmv .m4v .jpg .jpeg .png .gif .mp3 .pdf
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp", ".vob",
    };

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png",
    };

    // Chỉ nhận các container chịu được codec AAC ở bitrate cố định.
    // .wav / .flac bị loại có chủ ý: chúng thường là bản lưu trữ, nén lại chỉ tổn hại.
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac",
    };

    public static readonly HashSet<string> PdfExts = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    private static readonly HashSet<string> GifExts = new(StringComparer.OrdinalIgnoreCase) { ".gif" };

    public static MediaKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExts.Contains(ext)) return MediaKind.Image;
        if (GifExts.Contains(ext)) return MediaKind.Gif;
        if (AudioExts.Contains(ext)) return MediaKind.Audio;
        if (PdfExts.Contains(ext)) return MediaKind.Pdf;
        if (VideoExts.Contains(ext)) return MediaKind.Video;
        return MediaKind.Unknown;
    }

    public static bool IsSupported(string path) => Classify(path) != MediaKind.Unknown;

    /// <summary>Tập hợp tất cả phần mở rộng được hỗ trợ, để hiển thị trong hướng dẫn.</summary>
    public static IReadOnlyCollection<string> AllSupportedExtensions =>
        [.. VideoExts, .. ImageExts, .. AudioExts, .. PdfExts, .. GifExts];
}
