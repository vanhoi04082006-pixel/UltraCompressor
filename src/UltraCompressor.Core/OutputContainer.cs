using UltraCompressor.Core.Models;

namespace UltraCompressor.Core;

/// <summary>
/// Container của tệp <b>đầu ra</b> — một nguồn sự thật duy nhất.
///
/// <para>Container nguồn và container đầu ra là <b>hai thứ khác nhau</b>, và trước đây bị
/// nhập làm một: <c>TempWorkspace.CreatePath</c> sao chép phần mở rộng của tệp nguồn sang tệp
/// tạm, còn lệnh encode thì mang tuỳ chọn của MP4. ffmpeg chọn muxer theo phần mở rộng của
/// tệp đầu ra, nên phần mở rộng đó đã âm thầm quyết định container.</para>
///
/// <para><b>Đo được trên ffmpeg thật</b>, cùng một lệnh encode, chỉ khác phần mở rộng đầu ra:</para>
/// <list type="table">
/// <item><term><c>out.mp4</c></term><description><c>ftyp isom</c> — MP4 ✓</description></item>
/// <item><term><c>out.mov</c></term><description><c>ftyp qt</c> — QuickTime ✓</description></item>
/// <item><term><c>out.mkv</c></term><description><c>1A 45 DF A3</c> — <b>Matroska</b>, không phải MP4</description></item>
/// <item><term><c>out.ts</c></term><description><c>47</c> — <b>MPEG-TS</b>, không phải MP4</description></item>
/// </list>
///
/// <para>Không hề có lỗi nào được báo: exit code 0, tệp tạo ra thành công. Nhưng
/// <c>-movflags +faststart</c> là tuỳ chọn riêng của muxer mov/mp4, nên với Matroska và MPEG-TS
/// nó bị <b>bỏ qua lặng lẽ</b> — tệp ra không có faststart, tức mất khả năng phát trực tiếp
/// trên web, thứ mà ứng dụng này cần. Nguồn <c>.mkv</c> hay <c>.ts</c> vì vậy đã nhận một
/// container mà ứng dụng không hề định tạo ra.</para>
///
/// <para>Và nghiêm trọng hơn: nguồn <c>.webm</c> có tiếng <b>hỏng hẳn</b> — lệnh encode của ta
/// dùng <c>-c:a aac</c>, mà WebM chỉ nhận Vorbis/Opus, nên ffmpeg trả về
/// <c>Only VP8 or VP9 or AV1 video and Vorbis or Opus audio … are supported for WebM</c> và
/// từ chối ghi tệp. Lỗi này do chính cái ghép sai container đó gây ra.</para>
///
/// <para><b>Nguyên tắc: container đầu ra do lệnh encode quyết định, không do tệp nguồn.</b>
/// Xem <see cref="ExtensionFor"/> để biết từng loại media ra sao.</para>
/// </remarks>
public static class OutputContainer
{
    /// <summary>Container của mọi tệp video mà ta ghi ra: MP4.</summary>
    public const string Mp4 = ".mp4";

    /// <summary>Container của ảnh tĩnh JPEG.</summary>
    public const string Jpeg = ".jpg";

    /// <summary>
    /// Phần mở rộng của tệp đầu ra, theo loại media.
    /// </summary>
    /// <param name="kind">Loại media đang nén.</param>
    /// <param name="sourcePath">Tệp nguồn, dùng cho các loại mà container đầu ra bám theo nguồn.</param>
    /// <remarks>
    /// <para><b>Video luôn là MP4</b>, bất kể nguồn là gì. Không phải tuỳ chọn thẩm mỹ: lệnh
    /// encode của cả đường cũ lẫn đường thích ứng đều mang <c>-movflags +faststart</c>, đây là
    /// tuỳ chọn của muxer mov/mp4, và app chỉ hỗ trợ phát video trên web. Nếu để phần mở rộng
    /// nguồn quyết định thì Matroska và MPEG-TS sẽ nuốt tuỳ chọn đó trong im lặng.</para>
    ///
    /// <para><b>Các loại khác bám theo nguồn, và đó là ý nghĩa.</b> Lệnh của chúng không có
    /// <c>-f</c> nên container đúng là container của nguồn: audio vào <c>.mp3</c> thì ra
    /// <c>.mp3</c>, ảnh <c>.png</c> thì ra <c>.png</c>, gifsicle cần <c>.gif</c> để chạy,
    /// Ghostscript cần <c>.pdf</c>. Ép chúng về một container cố định sẽ phá công cụ đang
    /// dùng, chứ không sửa được lỗi nào.</para>
    /// </remarks>
    public static string ExtensionFor(MediaKind kind, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        return kind == MediaKind.Video ? Mp4 : SourceExtension(sourcePath);
    }

    /// <summary>
    /// Phần mở rộng của nguồn, chuẩn hoá về chữ thường và có dấu chấm; không có thì rỗng.
    /// </summary>
    public static string SourceExtension(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        var ext = Path.GetExtension(sourcePath);
        return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
    }
}
