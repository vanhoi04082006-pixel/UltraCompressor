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
/// dùng.</b> Nguồn thử: 1920x1080 h264, 120,1s, 34,0 MB, ba đoạn 3,0s, x264 preset medium,
/// cùng cấu hình cho phần thử nghiệm và cho encode toàn tệp:</para>
///
/// <list type="table">
/// <item><term>crf16</term><description>ước 78,4 MB — thật 61,9 MB — thừa 26,7%</description></item>
/// <item><term>crf24</term><description>ước 34,4 MB — thật 28,0 MB — thừa 23,0%</description></item>
/// <item><term>crf32</term><description>ước 14,4 MB — thật 12,8 MB — thừa 12,4%</description></item>
/// </list>
///
/// <para><b>Sai lệch KHÔNG phải một hệ số cố định</b> (12,4% → 26,7%), nên không hiệu chỉnh
/// được bằng một hằng số. Nhưng thứ tự xếp hạng được giữ đúng trên cả ba ứng viên — và thứ tự
/// xếp hạng là điều duy nhất hàm này được phép làm.</para>
///
/// <para><b>Vì sao lấy đoạn đắc nhất chứ không lấy trung bình.</b> Trung bình cho sai lệch
/// gần tương đương (thiếu 21–26%) nhưng <b>sai theo hướng ngược lại</b>: thiếu thì báo cáo
/// thấy tệp cuối nhỏ hơn thực tế, tức nói với người dùng rằng ta tiết kiệm được nhiều hơn
/// sự thật. Thừa thì báo cáo bi tiết kiệm — ít đẹp hơn nhưng không nói dối. Ước lượng thừa là
/// lựa chọn có ý thức, không phải thiếu cẩn thận.</para>
///
/// <para><b>Phần vỏ container không quan trọng.</b> Giả định 1.024 B là 0,0016% dung lượng
/// tệp thật trên tệp đã đo. Giữ hằng số vì nó có tên và được ghi rõ là sơ bộ, không phải vì
/// nó quyết định điều gì.</para>
///
/// </summary>
public static class SizeEstimator
{
    /// <summary>
    /// Phần vỏ container của TỆP ĐẦU RA, byte.
    ///
    /// <para>Đây là hằng số trên mỗi tệp, KHÔNG nhân với số đoạn thử nghiệm. Bản trước
    /// nhân với số đoạn, dựa trên suy luận rằng vỏ container tính theo từng đoạn ghép —
    /// nhưng tệp đầu ra là MỘT tệp, và con số phần vỏ không phụ thuộc vào việc ta đã thử
    /// nghiệm bao nhiêu ứng viên. Ba đoạn thử nghiệm không làm tệp đầu ra nặng thêm ba lần
    /// phần vỏ.</para>
    ///
    /// <para>Con số này <b>chưa được đo</b> trong kho. Giữ một giá trị nhỏ có tên và được
    /// ghi rõ là sơ bộ, tốt hơn là bỏ trống rồi âm thầm thiếu. Khi có dữ liệu thay số này.</para>
    /// </summary>
    public const long ContainerBytesAssumed = 1024;

    /// <summary>
    /// Ước lượng từ các clip thử nghiệm của một ứng viên.
    /// </summary>
    /// <param name="artifacts">
    /// Các clip thử nghiệm đã encode thành công. Clip hỏng bị bỏ qua — nhưng nếu hỏng hết thì
    /// không còn gì để ước lượng.
    /// </param>
    /// <param name="fullDurationSeconds">Thời lượng tệp nguồn, giây.</param>
    /// <param name="sourceAudioBitrateKbps">
    /// Bitrate âm thanh nguồn. Dùng cùng giả định với đường ống hiện tại, vốn không nâng
    /// bitrate của một nguồn vốn đã nhỏ hơn.
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
        var videoBytes = (long)Math.Round(videoBytesPerSecond * duration, MidpointRounding.AwayFromZero);

        assumptions.Add(
            $"video: {videoBytesPerSecond.ToString("0", CultureInfo.InvariantCulture)} B/s "
                + $"lấy từ đoạn đắc nhất ({reference.Role}, "
                + $"{reference.Bytes} B / {reference.ReferenceWindow.LengthSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s) "
                + $"; {usable.Count} đoạn dùng để so sánh, không lấy trung bình — "
                + "đo trên encode toàn tệp thật, hướng này THỪA 12–27%, và thừa thì báo cáo "
                + "bi tiết kiệm chứ không nói dối rằng ta tiết kiệm nhiều hơn sự thật");

        var reliable = true;

        long audioBytes = 0;
        if (hasAudio)
        {
            if (sourceAudioBitrateKbps is { } kbps and > 0)
            {
                audioBytes = (long)Math.Round(kbps * 1000.0 / 8.0 * duration, MidpointRounding.AwayFromZero);
                assumptions.Add(
                    $"audio: {kbps.ToString("0", CultureInfo.InvariantCulture)} kb/s × {duration.ToString("0", CultureInfo.InvariantCulture)}s, "
                    + "lấy nguyên bitrate nguồn — đường ống hiện tại không nâng bitrate của nguồn vốn đã nhỏ hơn mục tiêu");
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

        // Vỏ container: một tệp đầu ra thì một phần vỏ, bất kể đã thử nghiệm bao nhiêu ứng
        // viên và đã cắt bao nhiêu đoạn.
        var containerBytes = ContainerBytesAssumed;
        assumptions.Add(
            $"container: {ContainerBytesAssumed} B cho ca tệp đầu ra — GIÁ ĐỊNH SƠ BỘ, chưa đo trong kho");

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
