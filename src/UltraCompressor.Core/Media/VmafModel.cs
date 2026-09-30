namespace UltraCompressor.Core.Media;

/// <summary>
/// Một model VMAF cụ thể, kèm miền điểm của nó.
///
/// <para>Việc ghim model là bắt buộc, không phải sở thích. Cùng một tệp đo bằng
/// <c>v0.6.1</c> và <c>v0.6.1neg</c> ra hai con số khác nhau; đổi model giữa lúc này và
/// lúc sau làm mọi ngưỡng đã hiệu chỉnh trở nên vô nghĩa mà không có gì báo lỗi.</para>
///
/// <para>Miền điểm cũng khác nhau: bản 4K 3H của thế hệ v1 chạy trên [0, 110]. Ngưỡng
/// viết cho [0,100] mà áp vào mô hình [0,110] thì tương đương nới lỏng mà không ai hề
/// biết.</para>
/// </summary>
public sealed record VmafModel(
    string Id,
    string DisplayName,
    double MinScore,
    double MaxScore,
    string Note)
{
    /// <summary>Điểm cao nhất trên thang của model, dùng để chuẩn hoá khi so sánh ứng viên.</summary>
    public double Ceiling => MaxScore;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Các model VMAF mà bản dựng ffmpeg đóng kèm thực sự chạy được.
///
/// <para>Danh sách này được đo, không phải suy ra từ tài liệu. Bản
/// <c>ffmpeg 8.0.1-essentials_build</c> mà dự án đóng gói có <c>libvmaf</c> nhưng
/// <b>thiếu</b> các feature <c>speed</c>, <c>adm3</c>, <c>motion3</c> — tức không chạy
/// được bộ model thế hệ v1 của Netflix (tháng 6/2026). Thử nạp cho lỗi:</para>
/// <code>
/// libvmaf ERROR could not initialize feature extractor "Cambi_feature_cambi_score"
/// </code>
///
/// <para>Nên ở đây chỉ liệt kê model đã chạy thật. Khi nào đổi sang bản dựng có libvmaf
/// mới hơn, chỉ cần bổ sung mục vào đây và thả file model vào
/// <c>tools/vmaf/</c> — không phải sửa chỗ nào khác.</para>
/// </summary>
public static class VmafModels
{
    /// <summary>
    /// <c>neg</c> là biến thể VMAF v0.6.1 được thiết kế cho dùng khi tối ưu tham số
    /// encoder — điểm của nó tăng đơn điệu với chất lượng cảm nhận, nên cùng một tệp sẽ
    /// cho điểm thấp hơn bản gốc một chút. Dùng bản gốc thì nén quá tay mà vẫn trông
    /// "đạt", vì điểm không phản ánh đúng thứ mắt thấy.
    /// </summary>
    public static readonly VmafModel Neg = new(
        Id: "vmaf_v0.6.1neg",
        DisplayName: "VMAF v0.6.1neg",
        MinScore: 0,
        MaxScore: 100,
        Note: "Biến thể dành cho tối ưu encoder. Có trong bản dựng hiện tại.");

    /// <summary>Bản gốc, giữ để đối chiếu khi cần. Dùng đo sản phẩm đã xong xuôi.</summary>
    public static readonly VmafModel Original = new(
        Id: "vmaf_v0.6.1",
        DisplayName: "VMAF v0.6.1",
        MinScore: 0,
        MaxScore: 100,
        Note: "Bản gốc của thế hệ v0. Có trong bản dựng hiện tại.");

    /// <summary>Mô hình dùng cho cổng chất lượng khi nén tự động.</summary>
    public static VmafModel Default => Neg;

    /// <summary>Tất cả model biết, dùng để dò xem bản dựng ffmpeg hỗ trợ cái nào.</summary>
    public static IReadOnlyList<VmafModel> All { get; } = [Neg, Original];

    /// <summary>Model theo định danh, hoặc null nếu không biết.</summary>
    public static VmafModel? ById(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
