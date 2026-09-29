namespace UltraCompressor.Core.Media;

/// <summary>
/// Nhóm nội dung, suy ra từ đặc trưng SI/TI của ITU-T P.910.
///
/// <para>Đây là trục thứ hai của bảng quyết định, bên cạnh mức mục tiêu. Lý do cần nó:
/// hiệu quả nén phụ thuộc mạnh vào loại nội dung, không chỉ vào codec. Đo thật trên
/// thư viện của người dùng cho thấy hai tệp cùng là 1918px 30fps lại ngược nhau:
///
/// <list type="bullet">
/// <item>anime: HEVC nhỏ hơn H.264 <b>52–62%</b> ở cùng CRF quy đổi, SSIM chỉ lệch ~0.003;</item>
/// <item>quay màn hình: HEVC <b>to hơn 2%</b> và SSIM <b>kém hơn</b> (0.98998 so với 0.99459).</item>
/// </list>
///
/// Nếu không phân biệt được hai loại đó thì không thể chọn codec đúng cho cả hai.</para>
/// </summary>
public enum ContentProfile
{
    /// <summary>Chưa đủ dữ liệu để kết luận. Mọi quy tắc phải có đường lùi về đây.</summary>
    Unknown = 0,

    /// <summary>
    /// Giao diện, chữ, con trỏ — tức nội dung quay/chụp màn hình. Đặc trưng: rất nhiều cạnh
    /// sắc (SI cao) nhưng gần như không chuyển động (TI ~ 0).
    ///
    /// <para>H.264 thắng rõ ở nhóm này. Lý do có cơ sở: HEVC gốc không sinh ra cho nội dung
    /// màn hình — HEVC có phần mở rộng riêng cho loại đó (HEVC Screen Content Coding), vì
    /// bản thân HEVC phân tích khối 64x64 và biến đổi dài làm hỏng cạnh chữ sắc nét.</para>
    /// </summary>
    ScreenContent = 1,

    /// <summary>
    /// Chi tiết thấp và đứng yên: anime hạn chế chuyển động, ảnh tĩnh, slideshow.
    /// Rẻ để nén, nhưng cũng dễ rơi vào bẫy "nén quá tay" nên cần nâng CRF thay vì hạ.
    /// </summary>
    FlatMotionless = 2,

    /// <summary>Chuyển động vừa phải — dạng phổ biến nhất của phim truyện.</summary>
    ModerateMotion = 3,

    /// <summary>Chuyển động mạnh: thể thao, game, nhiễu hạt. Tốn byte nhất.</summary>
    BusyMotion = 4,
}

/// <summary>
/// Đặc trưng nội dung video theo ITU-T P.910, đo trên vài khung hình mẫu.
///
/// <para>Hai con số này là bản sao rẻ của "nội dung này khó tới đâu", và là thứ quyết
/// định codec nên dùng. Không cần giải mã toàn bộ tệp: ba điểm lấy mẫu, mỗi điểm hai
/// khung liên tiếp ở 320x180 thang xám, đo tốn khoảng nửa giây.</para>
/// </summary>
public sealed record ContentComplexity
{
    /// <summary>
    /// SI — Spatial Information. Độ lệch chuẩn không gian của độ lớn Sobel trên ảnh xám.
    /// Càng cao = càng nhiều cạnh sắc = càng nhiều chi tiết phải giữ.
    ///
    /// <para>Đo thật: quay màn hình 122.5; anime 72–100. Ngưỡng chẻ nằm giữa, lệch ~10–12
    /// so với cả hai nhóm.</para>
    /// </summary>
    public required double SpatialDetail { get; init; }

    /// <summary>
    /// TI — Temporal Information. Độ lệch chuẩn không gian của |F(n) − F(n−1)|, lấy
    /// giá trị lớn nhất trong các điểm lấy mẫu. Càng cao = càng nhiều chuyển động.
    ///
    /// <para>Đo thật: quay màn hình 0.02; anime 0.0–11.4. Lưu ý anime hạn chế chuyển động
    /// cũng ra TI ~ 0, nên <b>không dùng TI một mình</b> để kết luận là màn hình.</para>
    /// </summary>
    public required double TemporalActivity { get; init; }

    /// <summary>Số điểm lấy mẫu đọc được. 0 = probe hỏng, dùng <see cref="ContentProfile.Unknown"/>.</summary>
    public int Samples { get; init; }

    public ContentProfile Profile => Classify(SpatialDetail, TemporalActivity, Samples);

    /// <summary>Ngưỡng SI chẻ giữa nội dung màn hình và nội dung khác. Đo: 122.5 vs tối đa 100.</summary>
    public const double ScreenSpatialThreshold = 110.0;

    /// <summary>Ngưỡng TI coi như "gần như đứng yên". Đo: màn hình 0.02.</summary>
    public const double StillTemporalThreshold = 1.0;

    /// <summary>Ngưỡng TI coi như "chuyển động mạnh". Chưa có mẫu đo tới, nên để rộng.</summary>
    public const double BusyTemporalThreshold = 25.0;

    /// <summary>
    /// Phân loại nội dung từ SI/TI.
    ///
    /// <para>Cần <b>cả hai</b> điều kiện mới gọi là nội dung màn hình. Chỉ SI thì anime
    /// vẽ tay chi tiết cũng có thể vượt ngưỡng; chỉ TI thì anime đứng yên ra 0 và bị
    /// nhầm. Cả hai cùng đúng thì tín hiệu rất mạnh, và hậu quả khi nhầm cũng nhẹ: tệp
    /// màn hình thật bị đánh nhầm sang nội dung khác cũng chỉ mất hiệu quả nén HEVC.</para>
    /// </summary>
    public static ContentProfile Classify(double si, double ti, int samples)
    {
        if (samples <= 0) return ContentProfile.Unknown;

        if (si >= ScreenSpatialThreshold && ti < StillTemporalThreshold) return ContentProfile.ScreenContent;
        if (ti >= BusyTemporalThreshold) return ContentProfile.BusyMotion;
        if (ti < StillTemporalThreshold) return ContentProfile.FlatMotionless;
        return ContentProfile.ModerateMotion;
    }
}
