namespace UltraCompressor.Core.Search;

/// <summary>
/// Mã lý do của giai đoạn tìm kiếm, tách khỏi câu chữ tiếng Việt dành cho người dùng.
///
/// <para>Tách hai thứ này là bắt buộc, không phải sở thích: câu chữ sẽ được viết lại khi
/// cần, còn mã thì được đếm, lọc và so sánh giữa các lần chạy. Nếu gộp làm một, thì mọi
/// lần sửa câu chữ đều phá vỡ thống kê, và người ta sẽ dần ngừng dùng mã vì không tin nó.</para>
///
/// <para>Mã ở đây <b>không dùng chung</b> với mã của lưới chất lượng cuối
/// (<c>QualityGate.DecisionReasons</c>). Hai tầng khác nhau: tầng này nói về một
/// <i>ứng viên</i> trong lúc tìm, tầng kia nói về <i>tệp đầu ra cuối cùng</i>. Gộp chúng
/// sẽ không phân biệt được "ứng viên này bị loại" với "tệp cuối bị giữ nguyên".</para>
///
/// <para>Riêng <c>SOURCE_ALREADY_EFFICIENT</c> thuộc giai đoạn 5B — nó nói "tệp này đã ở
/// dạng tốt rồi, đừng nén", tức là một kết luận về chính tệp nguồn chứ không phải về một
/// ứng viên. Giai đoạn tìm kiếm không được dùng lại mã đó, vì như vậy nó sẽ biến một
/// ứng viên đơn lẻ thất bại thành kết luận về toàn bộ tệp.</para>
/// </summary>
public static class SearchDecisionReasons
{
    /// <summary>ffmpeg không encode được đoạn này (lỗi tiến trình, hết giờ, hủy).</summary>
    public const string PilotEncodeFailed = "PILOT_ENCODE_FAILED";

    /// <summary>
    /// Không đo được chất lượng. <b>Không được coi là đạt.</b>
    ///
    /// <para>Khác hẳn lưới chất lượng cuối, vốn cố tình fail-open: nếu công cụ hỏng mà loại
    /// thì người dùng không nén được gì. Ở đây ngược lại — ứng viên mà không đo được thì
    /// <b>không biết</b> nó có an toàn không, nên không được phép đi tiếp.</para>
    /// </summary>
    public const string PilotMeasurementUnavailable = "PILOT_MEASUREMENT_UNAVAILABLE";

    /// <summary>
    /// Có đoạn đo được nhưng rớt dưới ngưỡng của mode.
    ///
    /// <para>Tách khỏi <see cref="PilotQualityFloorNotMet"/> để phân biệt "rớt ở một đoạn"
    /// với "rớt ở điểm tổng hợp". Lượt A chỉ dùng mã thứ nhất; mã thứ hai để sẵn cho lượt B
    /// khi có quy tắc tổng hợp nhiều tầng.</para>
    /// </summary>
    public const string PilotWindowQualityFailed = "PILOT_WINDOW_QUALITY_FAILED";

    /// <summary>Điểm tổng hợp rớt dưới ngưỡng. Chưa dùng ở lượt A.</summary>
    public const string PilotQualityFloorNotMet = "PILOT_QUALITY_FLOOR_NOT_MET";

    /// <summary>
    /// Bị ứng viên khác áp đảo: có ứng viên vừa chất lượng không thấp hơn vừa nhỏ hơn, và ít
    /// nhất một chiều tốt hơn đáng kể.
    /// </summary>
    public const string PilotDominated = "PILOT_DOMINATED";

    /// <summary>Bị cắt khỏi vùng tìm trước khi encode. Chỉ dùng khi chắc chắn không bỏ sót nghiệm.</summary>
    public const string PilotPruned = "PILOT_PRUNED";

    /// <summary>Chất lượng tốt nhưng ước lượng kích thước không đủ cạnh tranh.</summary>
    public const string PilotSizeNotCompetitive = "PILOT_SIZE_NOT_COMPETITIVE";

    /// <summary>Được chọn để encode toàn tệp.</summary>
    public const string PilotSelected = "PILOT_SELECTED";

    /// <summary>Không ứng viên nào đạt được chất lượng. Kết quả hợp lệ, không phải lỗi.</summary>
    public const string PilotAllCandidatesRejected = "PILOT_ALL_CANDIDATES_REJECTED";

    /// <summary>Không có ứng viên nào để thử (planner rỗng, hoặc không encoder nào khả dụng).</summary>
    public const string PilotNoCandidates = "PILOT_NO_CANDIDATES";

    /// <summary>
    /// Dùng đường lập kế hoạch cũ. Để giai đoạn B dùng; lượt A chưa nối nên chưa sinh mã này.
    /// </summary>
    public const string LegacyFallbackUsed = "LEGACY_FALLBACK_USED";

    /// <summary>Mọi mã, để kiểm tra không trùng nhau và không trùng mã của lưới cuối.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PilotEncodeFailed,
        PilotMeasurementUnavailable,
        PilotWindowQualityFailed,
        PilotQualityFloorNotMet,
        PilotDominated,
        PilotPruned,
        PilotSizeNotCompetitive,
        PilotSelected,
        PilotAllCandidatesRejected,
        PilotNoCandidates,
        LegacyFallbackUsed,
    ];
}

/// <summary>Một kết luận gồm mã máy đọc được và câu chữ cho người đọc.</summary>
/// <param name="Reason">Mã ở <see cref="SearchDecisionReasons"/>, dùng để lọc và đếm.</param>
/// <param name="Message">Câu tiếng Việt cho log và giao diện. Không dùng để quyết định.</param>
public readonly record struct SearchOutcome(string Reason, string Message)
{
    /// <summary>Ứng viên có dùng được hay không, theo đúng mã chứ không theo câu chữ.</summary>
    public bool IsUsable => Reason == SearchDecisionReasons.PilotSelected;
}
