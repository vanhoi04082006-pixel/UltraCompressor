using System.Globalization;
using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Search;

/// <summary>
/// Khoảng byte <b>hợp lý</b> cho một ước lượng, suy ra từ độ lan tỉa quan sát được trên
/// bộ mẫu hiệu chỉnh.
/// </summary>
/// <remarks>
/// <para><b>Đây KHÔNG phải khoảng tin cậy thống kê.</b> Không có gì trong bộ mẫu cho ta
/// phân phối xác suất, nên không có cơ sở để nói "95% rơi vào đây". Cái thật duy nhất biết
/// được là: trên <i>N</i> tệp đã đo, tỉ số nằm trong khoảng này. Gọi nó là khoảng tin cậy
/// 95% là nói dối, và hậu quả là ai đó sẽ dựa vào độ chắc chắn không có thật để ra quyết
/// định không thể hoàn tác. Vì vậy tên và ý nghĩa là "biên heuristics" — đủ để <b>giữ mình
/// bên thận trọng</b>, không đủ để <b>tuyên bố</b>.</para>
///
/// <para>Khoảng này dùng để làm gì: khi so một ứng viên với bản gốc, ta hỏi cả ứng viên có
/// thể nhỏ tới đâu chứ không chỉ ước lượng nó nhỏ bao nhiêu. Không có khoảng thì chỉ còn
/// cách so một con số điểm, và một sai số 15% đủ để lật ngược kết luận.</para>
/// </remarks>
/// <param name="MinBytes">Nhỏ nhất trong khoảng quan sát được — ứng viên có thể nhỏ tới đây.</param>
/// <param name="MaxBytes">Lớn nhất trong khoảng quan sát được.</param>
public readonly record struct HeuristicEstimateBounds(long MinBytes, long MaxBytes)
{
    /// <summary>Có dùng được để ra quyết định bảo thủ không.</summary>
    public bool IsUsable => MaxBytes > 0 && MinBytes >= 0 && MinBytes <= MaxBytes;

    /// <summary>
    /// Ngay cả ở biên nhỏ nhất, ứng viên có còn nhỏ hơn <paramref name="referenceBytes"/>
    /// không — tức lợi ích dung lượng có đủ chắc chắn để hành động không.
    /// </summary>
    public bool BeatsAtBest(long referenceBytes) => IsUsable && MinBytes < referenceBytes;
}

/// <summary>
/// Nguồn gốc của các hằng số hiệu chỉnh: đo trên đâu, bao nhiêu mẫu, trạng thái, và tài liệu.
/// </summary>
/// <remarks>
/// <para>Tách riêng khỏi con số vì con số không nói được mình đáng tin bao nhiêu. Một
/// hệ số trông chính xác mà không kèm số mẫu thì y như không có hệ số — người đọc không biết
/// đó là trung bình 7 tệp hay 7 tệp của cùng một người quay.</para>
/// </remarks>
/// <param name="Component">Tên thành phần, ví dụ "video".</param>
/// <param name="Status">
/// Trạng thái hiệu chỉnh. <c>provisional</c> nghĩa là số đúng với bộ mẫu đã đo và <b>chưa</b>
/// được xác nhận trên tệp ngoài bộ mẫu đó.
/// </param>
/// <param name="SampleCount">Số tệp đo độc lập.</param>
/// <param name="ObservedMin">Tỉ số nhỏ nhất từng đo.</param>
/// <param name="ObservedMax">Tỉ số lớn nhất từng đo.</param>
/// <param name="Corpus">Bộ mẫu gồm những gì.</param>
/// <param name="Documentation">Nơi ghi bảng số đo đầy đủ.</param>
public sealed record SizeCalibrationProvenance(
    string Component,
    string Status,
    int SampleCount,
    double ObservedMin,
    double ObservedMax,
    string Corpus,
    string Documentation);

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

    /// <summary>
    /// Khoảng byte hợp lý, hoặc <c>null</c> khi không đủ dữ liệu để dựng.
    /// </summary>
    /// <remarks>
    /// <c>null</c> nghĩa là "không có ý kiến" chứ không phải "hẹp". Người gọi phải xử lý nó
    /// như vậy: thiếu biên thì không được kết luận từ ước lượng, chỉ được xếp hạng.
    /// </remarks>
    public HeuristicEstimateBounds? Bounds { get; init; }

    /// <summary>
    /// Nguồn gốc hiệu chỉnh đã dùng để dựng ước lượng này.
    /// </summary>
    /// <remarks>
    /// Rỗng khi ước lượng hỏng (không có clip thành công) — lúc đó không có con số nào để
    /// mà truy vết.
    /// </remarks>
    public IReadOnlyList<SizeCalibrationProvenance> Calibration { get; init; } = [];

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
    /// Nguồn gốc, số mẫu và trạng thái của toàn bộ hằng số hiệu chỉnh ở trên.
    /// </summary>
    /// <remarks>
    /// <para><b>Đặt ở đây, cạnh chính các hằng số, là chủ đích.</b> Yêu cầu là không được
    /// rải 0,72 / 0,98 / 1208 / 678 ra nhiều nơi: hằng số nằm ở <see cref="SizeEstimator"/>
    /// và <b>mọi thứ nói về chúng</b> cũng nằm ở đây. Tách sang file khác chỉ để "cho gọn"
    /// là làm mất đúng thứ ta cần giữ: số mẫu và trạng thái hiệu chỉnh đi cùng con số.</para>
    ///
    /// <para><b>Trạng thái là <c>provisional</c>, không phải <c>calibrated</c>.</b> Bộ mẫu
    /// nhỏ và thiên lệch về một dạng nội dung; xem ma trận khoảng trống trong
    /// <c>docs/ARCHITECTURE.md</c>. Con số này chưa được kiểm chứng ngoài bộ mẫu đó, và gọi
    /// nó là "đã hiệu chỉnh" là nói quá.</para>
    /// </remarks>
    public static class Calibration
    {
        /// <summary>
        /// Trạng thái hiệu chỉnh. Đổi sang chuỗi khác khi nhiều dạng nội dung được đo.
        /// </summary>
        public const string Status = "provisional-calibrated-on-limited-corpus";

        /// <summary>Số tệp đo được hệ số video: 3 tệp đợt đầu + 4 tệp đợt hai.</summary>
        public const int VideoSampleCount = 7;

        /// <summary>Số tệp đo được hệ số audio (2 nguồn × 2 CRF, cùng mục tiêu 192 kb/s).</summary>
        public const int AudioSampleCount = 4;

        /// <summary>Số lần đo vỏ container (2 tệp 60 s + 1 tệp 120 s).</summary>
        public const int ContainerSampleCount = 3;

        /// <summary>Tỉ số video nhỏ nhất từng đo — dùng làm biên dưới.</summary>
        public const double VideoObservedMin = 0.61;

        /// <summary>Tỉ số video lớn nhất từng đo — dùng làm biên trên.</summary>
        public const double VideoObservedMax = 0.89;

        /// <summary>Tỉ số audio nhỏ nhất từng đo (0,975), làm tròn xuống cho biên.</summary>
        public const double AudioObservedMin = 0.975;

        /// <summary>Tỉ số audio lớn nhất từng đo (0,989), làm tròn lên cho biên.</summary>
        public const double AudioObservedMax = 0.989;

        /// <summary>Bộ mẫu dùng để hiệu chỉnh, mô tả ngắn gọn.</summary>
        public const string Corpus =
            "2 nguồn 1080p dài 60,1s và 1 nguồn 120,1s; x264 preset medium; 3 đoạn pilot 3,0s; "
            + "cùng cấu hình cho pilot và cho encode toàn tệp";

        /// <summary>Nơi ghi bảng số đo đầy đủ.</summary>
        public const string Documentation =
            "docs/ARCHITECTURE.md — mục \"Hiệu chỉnh ước lượng dung lượng trên encode toàn tệp thật (đợt 2)\"";

        /// <summary>Nguồn gốc của hệ số video.</summary>
        public static SizeCalibrationProvenance Video { get; } = new(
            "video",
            Status,
            VideoSampleCount,
            VideoObservedMin,
            VideoObservedMax,
            Corpus,
            Documentation);

        /// <summary>Nguồn gốc của hệ số audio.</summary>
        public static SizeCalibrationProvenance Audio { get; } = new(
            "audio",
            Status,
            AudioSampleCount,
            AudioObservedMin,
            AudioObservedMax,
            Corpus,
            Documentation);

        /// <summary>Nguồn gốc của phần vỏ container.</summary>
        public static SizeCalibrationProvenance Container { get; } = new(
            "container",
            Status,
            ContainerSampleCount,
            0,
            0,
            "đo bằng Mp4TrackSizes trên encode thật, 2 tệp 60 s và 1 tệp 120 s",
            Documentation);

        /// <summary>Toàn bộ nguồn gốc, để đính kèm vào một ước lượng.</summary>
        public static IReadOnlyList<SizeCalibrationProvenance> All { get; } = [Video, Audio, Container];
    }

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
                Bounds = null,
                Calibration = [],
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
        var rawVideoBytes = videoBytesPerSecond * duration;
        var videoBytes = (long)Math.Round(
            rawVideoBytes * VideoBytesCalibrationFactor, MidpointRounding.AwayFromZero);

        // Biên heuristic: dùng độ lan tỉa QUAN SÁT ĐƯỢC, không phải sai số thống kê. Đây là
        // câu hỏi "ứng viên này có thể nhỏ tới đâu", và câu trả lời phải thành thật về phía
        // nhỏ để người quyết định không hành động theo một ước lượng có thể sai.
        var videoLow = (long)Math.Round(
            rawVideoBytes * Calibration.VideoObservedMin, MidpointRounding.AwayFromZero);
        var videoHigh = (long)Math.Round(
            rawVideoBytes * Calibration.VideoObservedMax, MidpointRounding.AwayFromZero);

        assumptions.Add(
            $"video: {videoBytesPerSecond.ToString("0", CultureInfo.InvariantCulture)} B/s "
                + $"lấy từ đoạn đắc nhất ({reference.Role}, "
                + $"{reference.Bytes} B / {reference.ReferenceWindow.LengthSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s) "
                + $"; {usable.Count} đoạn dùng để so sánh, không lấy trung bình; "
                + $"nhân hệ số hiệu chỉnh {VideoBytesCalibrationFactor} (đo trên encode toàn tệp thật) — "
                + "thừa thì báo cáo bi tiết kiệm chứ không nói dối rằng ta tiết kiệm nhiều hơn sự thật");

        var reliable = true;

        long audioBytes = 0;
        long audioLow = 0;
        long audioHigh = 0;
        if (hasAudio)
        {
            if (sourceAudioBitrateKbps is { } kbps and > 0)
            {
                var rawAudioBytes = kbps * 1000.0 / 8.0 * duration;
                audioBytes = (long)Math.Round(
                    rawAudioBytes * AudioBytesCalibrationFactor, MidpointRounding.AwayFromZero);

                // Sai số audio hẹp hơn nhiều (0,975…0,989) nên biên cũng hẹp — nhưng vẫn dựng
                // từ số đo chứ không gộp vào video, để khi đọc thì biết phần nào rộng phần nào
                // hẹp thay vì giả định cả hai đều chắc như nhau.
                audioLow = (long)Math.Round(
                    rawAudioBytes * Calibration.AudioObservedMin, MidpointRounding.AwayFromZero);
                audioHigh = (long)Math.Round(
                    rawAudioBytes * Calibration.AudioObservedMax, MidpointRounding.AwayFromZero);

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

        // Vỏ container đo trực tiếp bằng Mp4TrackSizes trên encode thật nên được cộng vào CẢ HAI
        // đầu: điểm ước lượng có nó, thì khoảng bao quanh điểm ước lượng cũng phải có. Chỉ dựng
        // khoảng khi thời lượng hợp lệ và âm thanh đã biết — thiếu một trong hai thì khoảng sẽ
        // thành "chắc chắn 0" và đó là khẳng định sai, nên để null thành "không có ý kiến".
        var bounds = duration > 0 && (!hasAudio || sourceAudioBitrateKbps is > 0)
            ? new HeuristicEstimateBounds(
                Math.Max(0, videoLow + audioLow + containerBytes),
                Math.Max(0, videoHigh + audioHigh + containerBytes))
            : (HeuristicEstimateBounds?)null;

        return new SizeEstimate
        {
            TotalBytes = Math.Max(0, total),
            VideoBytes = Math.Max(0, videoBytes),
            AudioBytes = Math.Max(0, audioBytes),
            ContainerBytes = Math.Max(0, containerBytes),
            DurationSeconds = duration,
            IsReliable = reliable,
            Assumptions = assumptions,
            Bounds = bounds,
            Calibration = Calibration.All,
        };
    }
}
