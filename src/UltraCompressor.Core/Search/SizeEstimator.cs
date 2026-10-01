using System.Globalization;
using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Search;

/// <summary>Ước lượng dung lượng tệp đầu ra toàn tệp, kèm các giả định đã dùng.</summary>
public sealed record SizeEstimate
{
    /// <summary>Tổng ước lượng, byte. Không bao giờ âm.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Phần video, suy ra từ clip thử nghiệm không có âm thanh.</summary>
    public required long VideoBytes { get; init; }

    /// <summary>
    /// Phần âm thanh. Clip thử nghiệm <b>không</b> mã hoá âm thanh, nên con số này suy ra
    /// từ metadata của nguồn chứ không phải từ phép đo.
    /// </summary>
    public required long AudioBytes { get; init; }

    /// <summary>Phần vỏ container không quy được về video hay âm thanh.</summary>
    public required long ContainerBytes { get; init; }

    public required double DurationSeconds { get; init; }

    /// <summary>Ước lượng có đáng tin không. Sai thì vẫn dùng được cho xếp hạng, nhưng phải biết.</summary>
    public required bool IsReliable { get; init; }

    /// <summary>Các giả định, đọc được bởi người chẩn đoán. Không rỗng khi có điều bất định.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>Tỉ lệ tiết kiệm so với nguồn, phần trăm. Âm nghĩa là ước lượng ra lớn hơn nguồn.</summary>
    public double SavingPercent(long sourceBytes) =>
        sourceBytes <= 0 ? 0 : (double)(sourceBytes - TotalBytes) * 100.0 / sourceBytes;
}

/// <summary>
/// Ước lượng dung lượng tệp đầu ra toàn tệp từ clip thử nghiệm.
///
/// <para><b>Ước lượng không bao giờ là nguồn sự thật.</b> Nguồn sự thật là
/// <c>FileInfo(fullOutput).Length</c> sau khi encode xong, và lưới chất lượng cuối giữ
/// bất biến <c>NewSize &lt;= OldSize</c>. Ước lượng chỉ để <b>xếp hạng</b> các ứng viên
/// khi chưa encode tệp nào — tức là để biết ứng viên nào đáng tốn công encode.</para>
///
/// <para>Vì vậy hàm này <b>tách riêng</b> video, âm thanh và vỏ container, thay vì lấy
/// <c>pilotBytes / pilotDuration * fullDuration</c>. Cách nhân thẳng đó sai theo ba lý do
/// cùng lúc:</para>
/// <list type="number">
/// <item><description>clip thử nghiệm <b>không có</b> âm thanh, còn tệp cuối thì có — phần
/// âm thanh bị thiếu hẳn, và với video nhiều khung/nhiều giây thì sai số tuyến tính;</description></item>
/// <item><description>phần vỏ container không tỉ lệ thuần với thời lượng, nhất là khi có nhiều
/// đoạn ghép;</description></item>
/// <item><description>một tệp dài có thể có bitrate thấp hơn ở các đoạn tĩnh, nên nhân từ
/// clip thử nghiệm thường <b>thiếu</b> dung lượng, tức ước lượng quá nhỏ và ứng viên tệ
/// trông hấp dẫn giả.</description></item>
/// </list>
///
/// <para>Khi thiếu metadata, hàm trả kèm <see cref="SizeEstimate.IsReliable"/> = false và
/// nêu rõ đang giả định gì, thay vì trả một con số trông chắc chắn.</para>
///
/// <para><b>ĐÃ HIỆU CHỈNH trên encode toàn tệp thật, và kết quả phải được đọc trước khi
/// dùng.</b> Hai nguồn 1080p (60,1s), x264 preset medium, ba đoạn pilot 3,0s, cùng cấu
/// hình cho phần thử nghiệm và cho encode toàn tệp. Tỉ số = byte thật / ước lượng thô
/// (đoạn đắc nhất × thời lượng):</para>
///
/// <list type="table">
/// <item><term>nguồn A crf24</term><description>video 0,631 · audio 0,975 · overhead 41.734 B</description></item>
/// <item><term>nguồn A crf32</term><description>video 0,607 · audio 0,975 · overhead 41.734 B</description></item>
/// <item><term>nguồn B crf24</term><description>video 0,661 · audio 0,989 · overhead 42.179 B</description></item>
/// <item><term>nguồn B crf32</term><description>video 0,663 · audio 0,989 · overhead 42.179 B</description></item>
/// <item><term>nguồn A 120s crf24</term><description>video — · audio — · overhead 82.634 B (≈2× điểm 60s)</description></item>
/// </list>
/// <para>Cộng 3 tỉ số video cũ (0,789 · 0,814 · 0,889, nguồn 120s khác): trung bình 7 mẫu
/// = <b>0,72</b>. Audio trung bình 4 mẫu = <b>0,98</b>. Overhead fit tuyến tính theo thời
/// lượng (xem <see cref="ContainerBaseBytes"/>).</para>
///
/// <para><b>Hệ số sửa độ lệch trung bình, không sửa được phương sai.</b> Video dao động
/// 0,61…0,89 giữa các nguồn — sau hiệu chỉnh vẫn còn sai ±15%. Vì vậy con số này <b>chỉ
/// để xếp hạng</b> (phép nhân đơn điệu không đổi thứ tự), không bao giờ để loại cứng.
/// Chân lý cuối vẫn là <c>FileInfo(fullOutput).Length</c> + lưới Phase 5A.</para>
///
/// <para><b>Vì sao lấy đoạn đắc nhất chứ không lấy trung bình.</b> Đo một lần cho thấy
/// trung bình thiếu ~21–26% còn đoạn đắc nhất thừa ~12–27%: sai số gần tương đương nhưng
/// <b>sai theo hướng ngược lại</b>. Thiếu thì báo cáo thấy tệp cuối nhỏ hơn thực tế, tức
/// nói với người dùng rằng ta tiết kiệm được nhiều hơn sự thật. Thừa thì báo cáo bi tiết
/// kiệm — ít đẹp hơn nhưng không nói dối. Ước lượng thừa là lựa chọn có ý thức (và nay đã
/// được hệ số hiệu chỉnh kéo về gần sự thật hơn), không phải thiếu cẩn thận.</para>
///
/// <para><b>Phần vỏ container không quan trọng.</b> Trên tệp 60s thì ~42 KB (~0,1% dung
/// lượng). Đo cho đúng nguyên tắc, không phải vì nó quyết định điều gì.</para>
///
/// </summary>
public static class SizeEstimator
{
    /// <summary>
    /// Hệ số hiệu chỉnh phần video: byte video thật / ước lượng thô từ pilot.
    ///
    /// <para>Ước lượng thô (đoạn đắc nhất × thời lượng) thừa có hệ thống vì đoạn khó không
    /// đại diện cho cả tệp. Hệ số này kéo báo cáo về gần sự thật hơn. Nó là phép nhân đơn
    /// điệu nên <b>không đổi thứ tự xếp hạng</b> — ứng viên nào lớn hơn trước thì vẫn lớn
    /// hơn sau; chỉ con số tuyệt đối là bớt sai.</para>
    ///
    /// <para>Giá trị = trung bình 7 tỉ số đo trên encode toàn tệp thật (2 nguồn, x264 medium,
    /// xem bảng dưới): 0,789 · 0,814 · 0,889 · 0,631 · 0,607 · 0,661 · 0,663 → <b>0,72</b>.
    /// Sai số quanh giá trị này vẫn còn (0,61…0,89) vì mỗi nguồn mỗi khác — hệ số sửa độ
    /// lệch trung bình, không sửa được phương sai. Ai đọc con số này mà tưởng nó chính
    /// xác thì đọc lại câu này.</para>
    /// </summary>
    public const double VideoBytesCalibrationFactor = 0.72;

    /// <summary>
    /// Hệ số hiệu chỉnh phần audio: byte audio thật / ước lượng từ bitrate mục tiêu.
    ///
    /// <para>Đo 4 mẫu (2 nguồn × 2 CRF, cùng mục tiêu 192k): 0,975 · 0,975 · 0,989 · 0,989
    /// → <b>0,98</b>. Encoder giữ mục tiêu trong 2% (thiếu một chút do khung im lặng và
    /// khớp khung) — sai số có hệ thống thật, khoảng hẹp, nên hiệu chỉnh được.</para>
    /// </summary>
    public const double AudioBytesCalibrationFactor = 0.98;

    /// <summary>
    /// Phần vỏ container của TỆP ĐẦU RA, byte: một phần cố định cộng một phần tỉ lệ với
    /// thời lượng.
    ///
    /// <para>Đây là hằng số trên mỗi tệp, KHÔNG nhân với số đoạn thử nghiệm. Bản trước
    /// nhân với số đoạn, dựa trên suy luận rằng vỏ container tính theo từng đoạn ghép —
    /// nhưng tệp đầu ra là MỘT tệp, và con số phần vỏ không phụ thuộc vào việc ta đã thử
    /// nghiệm bao nhiêu ứng viên. Ba đoạn thử nghiệm không làm tệp đầu ra nặng thêm ba lần
    /// phần vỏ.</para>
    ///
    /// <para>Đo bằng <see cref="Mp4TrackSizes"/> trên encode thật (cùng lệnh full encode
    /// của production, +faststart): 60,1s → 41.734/42.179 B; 120,1s → 82.634 B. Tỉ lệ gần
    /// như đúng gấp đôi khi thời lượng gấp đôi — vì bảng mẫu trong moov tăng theo số
    /// frame. Fit tuyến tính: base 1.208 B + 678 B/s. Trên tệp 60s thì phần này là ~0,1%
    /// dung lượng: đo cho đúng nguyên tắc, không phải vì nó quyết định điều gì.</para>
    /// </summary>
    public const long ContainerBaseBytes = 1208;

    /// <summary>
    /// Phần vỏ container tăng thêm theo mỗi giây thời lượng. Xem
    /// <see cref="ContainerBaseBytes"/> về cách đo.
    /// </summary>
    public const long ContainerBytesPerSecond = 678;

    /// <summary>
    /// Ước lượng từ các clip thử nghiệm của một ứng viên.
    /// </summary>
    /// <param name="artifacts">
    /// Các clip thử nghiệm đã encode thành công. Clip hỏng bị bỏ qua — nhưng nếu hỏng hết thì
    /// không còn gì để ước lượng.
    /// </param>
    /// <param name="fullDurationSeconds">Thời lượng tệp nguồn, giây.</param>
    /// <param name="sourceAudioBitrateKbps">
    /// Bitrate âm thanh mà bản full encode SẼ DÙNG — tức bitrate mục tiêu theo profile, đã
    /// lấy min với nguồn. Truyền bitrate nguồn thô vào đây là sai đúng một trường hợp:
    /// nguồn lớn hơn mục tiêu (ví dụ nguồn 250k, mục tiêu 192k), và sai đúng bằng phần
    /// chênh — đủ để loại cả ứng viên tốt trên tệp dài.
    /// </param>
    /// <param name="hasAudio">Tệp nguồn có âm thanh không.</param>
    public static SizeEstimate Estimate(
        IReadOnlyList<PilotArtifact> artifacts,
        double fullDurationSeconds,
        double? sourceAudioBitrateKbps,
        bool hasAudio)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var assumptions = new List<string>(4);
        var usable = artifacts
            .Where(a => a.Success && a.Bytes > 0 && a.ReferenceWindow.LengthSeconds > 0)
            .ToList();

        if (usable.Count == 0)
        {
            return new SizeEstimate
            {
                TotalBytes = 0,
                VideoBytes = 0,
                AudioBytes = 0,
                ContainerBytes = 0,
                DurationSeconds = fullDurationSeconds,
                IsReliable = false,
                Assumptions = ["không có clip thử nghiệm nào thành công để ước lượng"],
            };
        }

        // Dùng THỬ NGHIỆM SẮC NHẤT thay vì trung bình. Một clip rẻ bất thường (cảnh tĩnh)
        // kéo trung bình xuống, và ứng viên trông nhỏ hơn thực tế.
        //
        // Lý do chọn hướng THỪA, đã đo trên encode toàn tệp thật: trung bình thiếu 21–26%,
        // đoạn đắc nhất thừa 12–27%. Sai số gần bằng nhau, nhưng hướng thì không tương đương.
        // Thiếu thì báo cáo thấy tệp cuối nhỏ hơn thực tế, tức nói với người dùng ta tiết
        // kiệm được nhiều hơn sự thật. Thừa thì báo cáo bi tiết kiệm.
        var reference = usable
            .OrderByDescending(a => a.Bytes / a.ReferenceWindow.LengthSeconds)
            .First();

        var measuredSeconds = usable.Sum(a => a.ReferenceWindow.LengthSeconds);
        var videoBytesPerSecond = reference.Bytes / reference.ReferenceWindow.LengthSeconds;

        var duration = fullDurationSeconds > 0 ? fullDurationSeconds : 0;
        var videoBytes = (long)Math.Round(
            videoBytesPerSecond * duration * VideoBytesCalibrationFactor, MidpointRounding.AwayFromZero);

        assumptions.Add(
            $"video: {videoBytesPerSecond.ToString("0", CultureInfo.InvariantCulture)} B/s "
                + $"lấy từ đoạn đắc nhất ({reference.Role}, "
                + $"{reference.Bytes} B / {reference.ReferenceWindow.LengthSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s) "
                + $"; {usable.Count} đoạn dùng để so sánh, không lấy trung bình; "
                + $"nhân hệ số hiệu chỉnh {VideoBytesCalibrationFactor} (đo trên encode toàn tệp thật) — "
                + "thừa thì báo cáo bi tiết kiệm chứ không nói dối rằng ta tiết kiệm nhiều hơn sự thật");

        var reliable = true;

        long audioBytes = 0;
        if (hasAudio)
        {
            if (sourceAudioBitrateKbps is { } kbps and > 0)
            {
                audioBytes = (long)Math.Round(
                    kbps * 1000.0 / 8.0 * duration * AudioBytesCalibrationFactor,
                    MidpointRounding.AwayFromZero);
                assumptions.Add(
                    $"audio: {kbps.ToString("0", CultureInfo.InvariantCulture)} kb/s × {duration.ToString("0", CultureInfo.InvariantCulture)}s — "
                    + "đây phải là bitrate mục tiêu của bản full encode (đã lấy min với nguồn), "
                    + "vì encoder giữ đúng mục tiêu này");
            }
            else
            {
                audioBytes = 0;
                reliable = false;
                assumptions.Add(
                    "audio: KHÔNG biết bitrate nguồn nên phần này ước bằng 0 — ước lượng chắc chắn THẤP hơn thực tế");
            }
        }
        else
        {
            assumptions.Add("audio: nguồn không có âm thanh");
        }

        // Vỏ container: một tệp đầu ra thì một phần vỏ (cố định + theo thời lượng), bất kể
        // đã thử nghiệm bao nhiêu ứng viên và đã cắt bao nhiêu đoạn.
        var containerBytes = ContainerBaseBytes
            + (long)Math.Round(ContainerBytesPerSecond * duration, MidpointRounding.AwayFromZero);
        assumptions.Add(
            $"container: {ContainerBaseBytes} B + {ContainerBytesPerSecond} B/s × "
            + $"{duration.ToString("0", CultureInfo.InvariantCulture)}s cho tệp đầu ra — "
            + "đo bằng Mp4TrackSizes trên encode thật, tỉ lệ tuyến tính theo thời lượng");

        if (duration <= 0)
        {
            reliable = false;
            assumptions.Add("thời lượng nguồn bằng 0 hoặc không biết — không thể nhân tỉ lệ");
        }

        if (usable.Count < 2)
        {
            assumptions.Add(
                "chỉ có 1 đoạn thử nghiệm nên không kiểm tra được độ đồng nhất của bitrate giữa các đoạn");
        }

        var total = videoBytes + audioBytes + containerBytes;

        return new SizeEstimate
        {
            TotalBytes = Math.Max(0, total),
            VideoBytes = Math.Max(0, videoBytes),
            AudioBytes = Math.Max(0, audioBytes),
            ContainerBytes = Math.Max(0, containerBytes),
            DurationSeconds = duration,
            IsReliable = reliable,
            Assumptions = assumptions,
        };
    }
}
