using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Ngưỡng chất lượng tối thiểu cho một mode, gắn với đúng model VMAF đang dùng.
///
/// <para>Ngưỡng là <b>ràng buộc cứng</b>. Ứng viên đạt saving đẹp mà rớt ngưỡng thì bị loại:
/// người dùng thà nhận dung lượng lớn hơn còn hơn nhận một tệp hỏng mà không ai báo.</para>
/// </summary>
public sealed record QualityFloor(double VmafMean, double VmafP5)
{
    /// <summary>
    /// Ngưỡng P5 luôn thấp hơn ngưỡng mean khoảng 4 điểm, theo đúng khoảng cách quan sát
    /// được trên thư viện thật: mean − P5 dao động 1,2…7 điểm tuỳ cảnh. Đặt sát nhau thì
    /// phần trăm lớn khung đẹp giấu được cảnh nào hỏng; đặt quá xa thì ngưỡng P5 thành
    /// ràng buộc chết tiết và mọi ứng viên đều rớt.
    /// </summary>
    public double P5Gap => VmafMean - VmafP5;

    public bool Accepts(QualitySample sample) =>
        sample.Mean >= VmafMean && sample.P5 >= VmafP5;

    public override string ToString() => $"VMAF ≥ {VmafMean:F0} (P5 ≥ {VmafP5:F0})";
}

/// <summary>Chính sách chất lượng theo mode.</summary>
public static class QualityPolicy
{
    /// <summary>
    /// Ngưỡng cho <see cref="VmafModels.Neg"/> — thế hệ v0, thang [0, 100].
    ///
    /// <para><b>Không</b> chép số từ đặc tả sản phẩm: bản đó viết cho VMAF v1, cho điểm
    /// cao hơn ở cùng một mức chất lượng nên dùng lại nguyên xi sẽ siết nén quá tay.
    /// Số ở đây rút từ lần quét thật trên thư viện của người dùng, xem
    /// <c>docs/QUALITY-CALIBRATION.md</c>.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<CompressionLevel, QualityFloor> V0 = new Dictionary<CompressionLevel, QualityFloor>
    {
        // Nhẹ. Trên clip anime 1080p, x265 đạt P5 ≥ 89 ở CRF 18–26. Chọn ngưỡng này nghĩa
        // là "gần như không nhìn ra khác biệt", đúng như tên chế độ.
        [CompressionLevel.Light] = new(93.0, 89.0),

        // Cân bằng, là mặc định. P5 ≥ 85 rơi vào khoảng CRF 22–30 tuỳ độ khó của cảnh.
        [CompressionLevel.Balanced] = new(89.0, 85.0),

        // Mạnh. Vẫn có sàn ở 84/80: quanh đây thì khung xấu nhất bắt đầu lộ rõ. Không có
        // sàn thì "Mạnh" chỉ là tên gọi cho việc phá tệp.
        [CompressionLevel.Strong] = new(84.0, 80.0),
    };

    /// <summary>
    /// Ngưỡng cho thế hệ v1 — thang [0, 100]. Giữ sẵn để khi đổi bản dựng ffmpeg là bật lại
    /// được ngay. Chưa hiệu chỉnh lại vì <b>chưa từng đo được</b>: bản dựng hiện tại thiếu
    /// feature <c>speed</c>, <c>adm3</c>, <c>motion3</c> nên không chạy nổi model v1.
    /// </summary>
    private static readonly IReadOnlyDictionary<CompressionLevel, QualityFloor> V1 = new Dictionary<CompressionLevel, QualityFloor>
    {
        [CompressionLevel.Light] = new(95.0, 90.0),
        [CompressionLevel.Balanced] = new(92.0, 86.0),
        [CompressionLevel.Strong] = new(88.0, 80.0),
    };

    /// <summary>Ngưỡng của mode, theo đúng model sẽ dùng để đo.</summary>
    public static QualityFloor For(CompressionLevel level, VmafModel model)
    {
        var table = model.Id.StartsWith("vmaf_v1", StringComparison.OrdinalIgnoreCase) ? V1 : V0;
        return table.TryGetValue(level, out var floor) ? floor : table[CompressionLevel.Balanced];
    }

    /// <summary>
    /// SSIM <b>không</b> dùng làm cổng, dù vẫn được đo và ghi lại.
    ///
    /// <para>Lý do do đo, không phải do tài liệu: trên clip thật, ứng viên VMAF 72 (đã rõ
    /// là hỏng) vẫn cho SSIM 0,9887. Ngưỡng SSIM 0,985 kiểu đặc tả vì vậy <b>cho qua</b>
    /// tới mức ứng viên tệ — dùng nó làm cổng thì cổng không tồn tại. Số liệu vẫn giữ vì
    /// hữu ích khi chẩn đoán vì sao một tệp trông lạ.</para>
    /// </summary>
    public static bool Accepts(QualityFloor floor, QualitySample sample) => floor.Accepts(sample);
}
