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

    /// <summary>
    /// Các phần mở rộng là container MPEG-TS.
    /// </summary>
    /// <remarks>
    /// <para>Tách ra khỏi <see cref="VideoExts"/> vì chúng không đáng tin như nhau. Ta chưa có
    /// đường nào remux sang MPEG-TS, nhưng đã đo được một hiện tượng: remux <b>cùng nội dung,
    /// stream copy, không đổi một pixel</b> sang MPEG-TS làm VMAF rơi 4,1 điểm, và nguyên nhân
    /// chưa truy ra được (ghi chú tại <c>QualityProbe</c>).</para>
    ///
    /// <para>Hệ quả cho quyết định: với nguồn MPEG-TS, số đo chất lượng có thể lệch vì
    /// <b>dấu thời gian</b> chứ không phải vì nén. Số đo lệch theo hướng bi quan thì dẫn tới
    /// kết luận "không ứng viên nào đạt" — một kết luận <b>không hoàn tác được</b>. Nên nguồn
    /// MPEG-TS bị đánh dấu là đo không chắc, và số đo đó không được dùng để kết luận giữ bản
    /// gốc.</para>
    ///
    /// <para>Không mở rộng danh sách này theo phỏng đoán: mỗi phần mở rộng thêm là một quyết
    /// định giảm chất lượng lựa chọn của người dùng mà không kèm số đo nào.</para>
    /// </remarks>
    private static readonly HashSet<string> MpegTsExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".m2ts",
    };

    /// <summary>
    /// Tệp này có phải container MPEG-TS không — <b>theo phần mở rộng</b>.
    /// </summary>
    /// <remarks>
    /// Dùng phần mở rộng vì probe hiện không đọc <c>format_name</c> của ffmpeg; đây là tín
    /// hiệu yếu và được ghi nhận đúng như vậy. Nó chỉ dùng để <b>hạ mức tin cậy</b> của phép
    /// đo, không dùng để quyết định giữ hay nén.
    /// </remarks>
    public static bool IsMpegTs(string path) => MpegTsExts.Contains(Path.GetExtension(path));

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
