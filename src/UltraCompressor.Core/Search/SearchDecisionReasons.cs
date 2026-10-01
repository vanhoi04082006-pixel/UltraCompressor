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
/// <para>Riêng <c>SOURCE_ALREADY_EFFICIENT</c> của lưới chất lượng cuối thuộc một tầng khác
/// và giai đoạn tìm kiếm <b>không được dùng lại</b> mã đó: nó nói "tệp này đã ở dạng tốt
/// rồi, đừng nén", tức là một kết luận về chính tệp nguồn. Nếu một ứng viên đơn lẻ thất bại
/// mà được dịch thành mã đó thì ta sẽ biến thất bại của <i>một</i> phương án thành kết
/// luận về toàn bộ tệp. Giữ nguyên bản gốc ở giai đoạn 5B dùng mã
/// <see cref="OriginalSelected"/>, và luôn kèm lý do cụ thể trong phần <c>Message</c>.</para>
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

    /// <summary>
    /// Không có ứng viên nào để thử (planner rỗng, hoặc không encoder nào khả dụng).
    /// </summary>
    public const string PilotNoCandidates = "PILOT_NO_CANDIDATES";

    /// <summary>
    /// Nhánh đo được số đo <b>phá vỡ giả định đơn điệu</b>: có điểm chỉ số thấp rớt trong
    /// khi điểm chỉ số cao đạt, hoặc ngược lại.
    ///
    /// <para>Mã này nói về <b>tính hợp lệ của phép đo</b>, không nói về chất lượng tệp.
    /// Khi nó xuất hiện thì nhánh đó chuyển sang dò tuyến tính và <b>không cắt gì thêm</b>
    /// theo giả định cũ — vì giả định đó vừa bị dữ liệu bác bỏ.</para>
    ///
    /// <para>Cần máy đọc được vì đây là tín hiệu đúng nhất cho biết phép đo VMAF có đang
    /// lệch so với điều ta nghĩ không — thứ mà nếu chỉ ghi trong log tiếng Việt thì không
    /// ai đếm được.</para>
    /// </summary>
    public const string NonMonotonicBranchObserved = "NON_MONOTONIC_BRANCH_OBSERVED";

    /// <summary>
    /// Giữ nguyên bản gốc vì ứng viên encode <b>không chứng minh được lợi ích đủ ý nghĩa</b>
    /// so với chính nguồn — hoặc vì không ứng viên nào đạt chất lượng.
    ///
    /// <para>Khác <see cref="PilotAllCandidatesRejected"/> ở chỗ: mã đó là kết luận về
    /// <i>những ứng viên encode</i>, mã này là kết luống về <i>tệp đầu ra cuối cùng</i>.
    /// Người đọc báo cáo cần biết ta đã bỏ qua một lần encode toàn tệp hay không, và vì
    /// sao.</para>
    ///
    /// <para><b>Không đồng nghĩa "nguồn đã tối ưu".</b> Phần <c>Message</c> luôn nêu rõ điều
    /// tìm được: ứng viên có đạt chất lượng không, và lợi ích dung lượng có đạt ngưỡng của
    /// cấu hình không. Ta chưa có bằng chứng nào để tuyên bố tệp nguồn là tối ưu.</para>
    /// </summary>
    public const string OriginalSelected = "ORIGINAL_SELECTED";

    /// <summary>
    /// Tìm kiếm chạy thành công nhưng không ứng viên encode nào đạt chất lượng.
    /// </summary>
    /// <remarks>
    /// Khác <see cref="PilotAllCandidatesRejected"/> ở tầng: mã đó là <i>lý do trong giai
    /// đoạn tìm</i>, còn mã này là <b>kết cục của đường chạy</b> — thứ người đọc báo cáo cần.
    /// Hai kết cục giữ bản gốc đều hợp lệ, nhưng một cái là "không có gì đạt", cái kia là
    /// "có gì đạt mà vẫn không đáng làm", và chúng phải đếm tách bạch.
    /// </remarks>
    public const string NoFeasibleCandidate = "NO_FEASIBLE_CANDIDATE";

    /// <summary>
    /// Dùng đường lập kế hoạch cũ. Chỉ dùng cho <b>lỗi hạ tầng</b>: khi search chạy được
    /// nhưng không đo được ứng viên nào, hoặc khi thiếu công cụ. Đây là nhánh duy nhất được
    /// phép rơi về đường cũ.
    /// </summary>
    public const string LegacyFallbackUsed = "LEGACY_FALLBACK_USED";

    /// <summary>
    /// Bốn kết cục của đường chạy thích ứng, theo đúng thứ tự quyết định.
    /// </summary>
    /// <remarks>
    /// Tách khỏi <see cref="All"/> vì đây là tầng khác: <c>All</c> là mã của <i>một quyết
    /// định trong tìm kiếm</i>, còn đây là <i>kết cục của cả đường chạy</i>. Trộn hai tầng
    /// làm mất đúng cái ta cần: biết một lần nén kết thúc thế nào.
    /// </remarks>
    public static IReadOnlyList<string> Outcomes { get; } =
    [
        PilotSelected,
        OriginalSelected,
        NoFeasibleCandidate,
        LegacyFallbackUsed,
    ];

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
        NonMonotonicBranchObserved,
        OriginalSelected,
        NoFeasibleCandidate,
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
