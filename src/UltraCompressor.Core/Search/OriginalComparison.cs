using System.Globalization;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Search;

/// <summary>Hai cách kết thúc so sánh giữa bản gốc và các ứng viên encode.</summary>
public enum OriginalDecision
{
    /// <summary>Có ứng viên chứng minh được lợi ích đủ ý nghĩa — encode.</summary>
    KeepEncoded,

    /// <summary>Không ứng viên nào chứng minh được — giữ bản gốc, không encode toàn tệp.</summary>
    KeepOriginal,
}

/// <summary>
/// Mức độ tin cậy của phép đo chất lượng trên tệp này — vì nó quyết định phép đo có được
/// phép làm bằng chứng <b>không hoàn tác được</b> hay không.
/// </summary>
public enum MeasurementConfidence
{
    /// <summary>
    /// Phép đo đáng tin. Kết luận giữ bản gốc được phép dựa vào số đo này.
    /// </summary>
    Trusted,

    /// <summary>
    /// Phép đo có thể lệch vì lý do <b>không liên quan tới nén</b> — ví dụ container có dấu
    /// thời gian đáng ngờ. Số đo vẫn dùng để xếp hạng, nhưng không được dùng để kết luận giữ
    /// bản gốc.
    /// </summary>
    /// <remarks>
    /// Hệ quả là cố ý và một chiều: khi không chắc, ta <b>encode</b> rồi để lưới 5A quyết
    /// định bằng kích thước và số đo thật của tệp đầu ra. Tốn công hơn, nhưng không bỏ mất một
    /// khả năng nén thật chỉ vì phép đo có thể sai.
    /// </remarks>
    Uncertain,
}

/// <summary>
/// Kết luận của phép so bản gốc với các ứng viên encode, đủ để giải thích cho người đọc và
/// đủ để đếm bằng máy.
/// </summary>
/// <param name="Decision">Hướng đi.</param>
/// <param name="Reason">Mã ở <see cref="SearchDecisionReasons"/>.</param>
/// <param name="Message">Câu tiếng Việt, nêu rõ điều gì đã đo và điều gì chưa đo.</param>
/// <param name="SourceBytes">Byte thật của nguồn.</param>
/// <param name="RequiredBytes">
/// Byte tối đa mà một ứng viên được phép có để vẫn đạt ngưỡng tiết kiệm của cấu hình.
/// </param>
/// <param name="BestOptimisticBytes">
/// Kích thước <b>nhỏ nhất có thể** theo khoảng quan sát của ứng viên đáng chú ý nhất. Đây là
/// con số quyết định, không phải ước lượng điểm.
/// </param>
/// <param name="BestEstimatedBytes">Ước lượng điểm của cùng ứng viên đó, để đối chiếu.</param>
/// <param name="RequiredSavingPercent">Ngưỡng lấy từ cấu hình, không phải hằng số ở đây.</param>
/// <param name="FeasibleCount">Số ứng viên đạt chất lượng đã xét.</param>
/// <param name="ConsideredWithoutBounds">
/// Số ứng viên không có khoảng để so — những ứng viên này <b>không</b> được dùng để kết luận
/// giữ bản gốc, vì không có khoảng thì không có cách nào chứng minh là không đáng encode.
/// </param>
/// <param name="Confidence">
/// Mức độ tin cậy của phép đo. Khi không chắc thì lớp này <b>không</b> kết luận giữ bản gốc.
/// </param>
/// <param name="SizeEvidenceCertified">
/// Bộ hiệu chỉnh kích thước có đủ căn cứ để ra kết luận không hoàn tác được không. Chỉ khi
/// <c>true</c> thì lớp này mới cho phép kết luận giữ bản gốc; xem
/// <see cref="Decision"/>.
/// </param>
/// <remarks>
/// <para><b>Không có điểm số tổng hợp.</b> Không có dạng <c>VMAF × a + tiết kiệm × b</c>, vì
/// trọng số <c>a</c> và <c>b</c> chưa được chứng minh bằng gì: tra một tỉ số tuỳ ý vào
/// không gian nơi hai thứ đó không cùng đơn vị là tạo ra một con số trông khoa học mà không
/// có nghĩa. Thứ tự kiểm tra là thứ tự ràng buộc:</para>
///
/// <list type="number">
/// <item><description><b>Khả thi về chất lượng</b> — do <c>QualityPolicy</c> quyết định,
/// không phải việc của hàm này (ứng viên không khả thi thì không được vào đây).</description></item>
/// <item><description><b>Lợi ích dung lượng có ý nghĩa</b> — theo đúng
/// <see cref="Models.AppConfig.MinSavingPercent"/>, cùng ngưỡng mà lưới 5A dùng. Không tạo
/// thêm một ngưỡng "hợp lý" riêng ở đây, vì hai ngưỡng cùng nghĩa mà lệch nhau là một cách
/// rất tinh vi để bỏ qua cấu hình.</description></item>
/// <item><description><b>Quan hệ Pareto</b> — do <see cref="ParetoSelector"/> lo trước;
/// ORIGINAL không tham gia vào đó vì nó không có điểm chất lượng để so.</description></item>
/// <item><description><b>Chi phí tính toán</b> — chỉ để phá thế hoà, sau cùng.</description></item>
/// </list>
///
/// <para><b>Ước lượng KHÔNG BAO GIỜ tự nó kết luận giữ bản gốc.</b> Đây là điều chỉnh quan
/// trọng nhất của lớp này. `HeuristicEstimateBounds` là biên dựng từ độ lan tỉa quan sát
/// trên 7 tệp, không phải chứng minh toán học — và đã có bằng chứng nó <b>không bao trọn thực
/// tế</b>: nguồn nhiễu 720p crf 32 cho ước lượng 87,8 MB với tệp thật 122,3 MB, lệch −28%,
/// ra ngoài chính khoảng 0,61…0,89.</para>
///
/// <para>Nên quy tắc là: chỉ khi bộ hiệu chỉnh được chứng nhận (status = <c>certified</c>)
/// thì "không ứng viên nào chứng minh được lợi ích" mới đủ để bỏ full encode. Chưa chứng
/// nhận thì kết quả là <b>thiếu bằng chứng → encode</b>: encode rồi để lưới 5A quyết định
/// bằng <b>byte thật</b> của tệp đầu ra. Tốn công hơn, nhưng đó là cách duy nhất không dùng một
/// con số chưa chứng minh để quyết định không hoàn tác được.</para>
///
/// <para><b>Hướng thiên lệch là cố ý và một chiều</b>: báo động đối lập — encode rồi hóa ra
/// không đáng — vẫn an toàn tuyệt đối, vì lưới 5A giữ bản gốc khi tệp đầu ra không đủ nhỏ. Cái
/// ta không thể để xảy ra là bỏ mất một khoản tiết kiệm thật chỉ vì ước lượng sai.</para>
///
/// <para><b>Thiếu khoảng thì không kết luận.</b> Ứng viên không dựng được
/// <see cref="SizeEstimate.Bounds"/> thì không dùng để bác được: không có khoảng nghĩa là
/// không có cách nào chứng minh là không đáng encode, và đó là lý do để <b>không</b> giữ
/// bản gốc.</para>
/// </remarks>
public sealed record OriginalComparison(
    OriginalDecision Decision,
    string Reason,
    string Message,
    long SourceBytes,
    long RequiredBytes,
    long BestOptimisticBytes,
    long BestEstimatedBytes,
    double RequiredSavingPercent,
    int FeasibleCount,
    int ConsideredWithoutBounds,
    MeasurementConfidence Confidence = MeasurementConfidence.Trusted)
{
    /// <summary>Có bỏ được một lần encode toàn tệp không.</summary>
    public bool FullEncodeAvoided => Decision == OriginalDecision.KeepOriginal;

    /// <summary>Mô tả ngắn cho báo cáo một dòng.</summary>
    public string Summary =>
        $"{Decision}: {Message}";

    /// <summary>
    /// Cờ báo cáo cho biết nhánh giữ bản gốc sớm đang bị tắt vì chưa có bằng chứng kích
    /// thước đủ mạnh.
    /// </summary>
    /// <remarks>
    /// <para>Đây không phải hồi quy correctness — đây là hậu quả có chủ đích của việc thừa nhận
    /// rằng biên ước lượng chưa được chứng nhận. Nó phải nằm trong báo cáo, vì nếu im lặng thì
    /// người đọc sẽ tưởng tính năng chưa bao giờ có, thay vì là "có nhưng bị tạm khoá cho
    /// tới khi có bằng chứng".</para>
    /// </remarks>
    public const string PendingEvidenceMarker =
        "EARLY ORIGINAL OPTIMIZATION DISABLED PENDING STRONGER SIZE EVIDENCE";

    /// <summary>
    /// So bản gốc với các ứng viên đã đo và đạt chất lượng.
    /// </summary>
    /// <param name="sourceBytes">Byte thật của nguồn.</param>
    /// <param name="minSavingPercent">
    /// Ngưỡng tiết kiệm tối thiểu của cấu hình — cùng ngưỡng lưới 5A dùng.
    /// </param>
    /// <param name="feasible">Ứng viên đã đo và đạt chất lượng.</param>
    /// <param name="confidence">
    /// Mức độ tin cậy của phép đo. <see cref="MeasurementConfidence.Uncertain"/> khiến hàm
    /// này <b>không</b> bao giờ kết luận giữ bản gốc — vì một số đo có thể sai vì lý do
    /// không liên quan tới nén thì không phải bằng chứng cho việc "không đáng nén".
    /// </param>
    public static OriginalComparison Decide(
        long sourceBytes,
        double minSavingPercent,
        IReadOnlyList<EvaluatedCandidate> feasible,
        MeasurementConfidence confidence = MeasurementConfidence.Trusted)
    {
        ArgumentNullException.ThrowIfNull(feasible);

        // Ngưỡng dùng lại đúng của cấu hình. Bằng 0 hoặc âm thì mọi thứ đều "đủ", và lúc đó
        // hàm này không có ý nghĩa gì nên nói rõ thay vì âm thầm cho đi.
        var savingPercent = Math.Max(0, minSavingPercent);
        var requiredBytes = sourceBytes <= 0
            ? 0
            : (long)Math.Round(
                sourceBytes * (1.0 - savingPercent / 100.0), MidpointRounding.AwayFromZero);

        var withBounds = 0;
        var certified = true;
        var bestOptimistic = long.MaxValue;
        var bestEstimated = long.MaxValue;
        var bestId = string.Empty;

        foreach (var candidate in feasible)
        {
            if (candidate.Estimate.Bounds is not { IsUsable: true } bounds)
            {
                continue;
            }

            withBounds++;

            // Biên của ứng viên này có đủ căn cứ để ra kết luận không hoàn tác được không?
            // Quyết định giữ bản gốc dựa trên TẤT CẢ ứng viên đã xét, nên một ứng viên có
            // biên chưa chứng nhận làm toàn bộ kết luận mất chứng cứ.
            certified &= IsCertified(candidate.Estimate);

            // Nhỏ nhất theo khoảng quan sát — đây là cách đọc có lợi nhất cho ứng viên, nên
            // dùng nó để bác cũng là cách khó bác nhất.
            if (bounds.MinBytes < bestOptimistic)
            {
                bestOptimistic = bounds.MinBytes;
                bestEstimated = candidate.Estimate.TotalBytes;
                bestId = candidate.Candidate.Id;
            }
        }

        if (withBounds == 0)
        {
            return new OriginalComparison(
                OriginalDecision.KeepEncoded,
                SearchDecisionReasons.PilotSelected,
                $"{feasible.Count} ứng viên đạt chất lượng nhưng không ứng viên nào có khoảng ước lượng "
                    + "để so — thiếu bằng chứng không phải bằng chứng giữ bản gốc, nên vẫn encode",
                sourceBytes,
                requiredBytes,
                0,
                0,
                savingPercent,
                feasible.Count,
                feasible.Count);
        }

        if (bestOptimistic < requiredBytes)
        {
            return new OriginalComparison(
                OriginalDecision.KeepEncoded,
                SearchDecisionReasons.PilotSelected,
                $"{bestId} nhỏ tới {Format(bestOptimistic)} B ngay cả ở biên nhỏ nhất, "
                    + $"thấp hơn mức cần có ({Format(requiredBytes)} B) — chứng minh được lợi ích, encode",
                sourceBytes,
                requiredBytes,
                bestOptimistic,
                bestEstimated,
                savingPercent,
                feasible.Count,
                feasible.Count - withBounds,
                confidence);
        }

        // Không ứng viên nào chứng minh được lợi ích. HAI trường hợp phải phân biệt, và cả hai
        // đều không được dùng ước lượng để kết luận.
        if (!certified)
        {
            // Bộ hiệu chỉnh kích thước chưa được chứng nhận. Biên heuristics chỉ là độ lan tỉa
            // quan sát trên một bộ mẫu nhỏ, và đã có bằng chứng nó ra ngoài chính biên (nguồn
            // nhiễu: ước 87,8 MB, thật 122,3 MB). "Không chứng minh được lợi ích" khi đó chỉ
            // có nghĩa là TA CHƯA CHỨNG MINH ĐƯỢC — chứ không phải là không có lợi ích — nên đi
            // đường an toàn: encode rồi để lưới 5A quyết định bằng byte thật.
            return new OriginalComparison(
                OriginalDecision.KeepEncoded,
                SearchDecisionReasons.PilotSelected,
                $"không ứng viên nào chứng minh được lợi ích: kể cả {bestId} "
                    + $"(nhỏ nhất có thể {Format(bestOptimistic)} B) vẫn không nhỏ hơn mức cần có "
                    + $"({Format(requiredBytes)} B). Nhưng bộ hiệu chỉnh kích thước chưa được chứng nhận "
                    + $"(trạng thái \"{SizeEstimator.Calibration.Status}\"), và biên ước lượng KHÔNG phải "
                    + "chứng minh — đã đo thấy một nguồn lệch −28%, ra ngoài chính khoảng đã hiệu "
                    + $"chỉnh. Nên không dùng nó để kết luận giữ bản gốc: {PendingEvidenceMarker} — "
                    + "encode và để lưới chất lượng cuối quyết định bằng kích thước thật",
                sourceBytes,
                requiredBytes,
                bestOptimistic,
                bestEstimated,
                savingPercent,
                feasible.Count,
                feasible.Count - withBounds,
                confidence);
        }

        if (confidence == MeasurementConfidence.Uncertain)
        {
            // Phép đo chất lượng không chắc (container có dấu thời gian đáng ngờ) thì "không
            // chứng minh được" cũng không đủ. Đi đường an toàn.
            return new OriginalComparison(
                OriginalDecision.KeepEncoded,
                SearchDecisionReasons.PilotSelected,
                $"không ứng viên nào chứng minh được lợi ích, nhưng phép đo trên nguồn này "
                    + "không đáng tin (container có dấu thời gian đáng ngờ) nên không dùng nó để "
                    + "kết luận giữ bản gốc — encode và để lưới chất lượng cuối quyết định",
                sourceBytes,
                requiredBytes,
                bestOptimistic,
                bestEstimated,
                savingPercent,
                feasible.Count,
                feasible.Count - withBounds,
                confidence);
        }

        // Đã tới đây thì mới thật sự đủ: biên được chứng nhận VÀ phép đo đáng tin. Nói rõ đây
        // là kết luận về BẰNG CHỨNG, không phải tuyên bố tệp nguồn đã tối ưu — ta không có
        // căn cứ để nói điều đó.
        return new OriginalComparison(
            OriginalDecision.KeepOriginal,
            SearchDecisionReasons.OriginalSelected,
            $"{feasible.Count} ứng viên đạt chất lượng, nhưng kể cả {bestId} "
                + $"(nhỏ nhất có thể {Format(bestOptimistic)} B, ước lượng {Format(bestEstimated)} B) "
                + $"vẫn không nhỏ hơn nguồn đủ xa: cần dưới {Format(requiredBytes)} B cho ngưỡng "
                + $"tiết kiệm {savingPercent.ToString("0.##", CultureInfo.InvariantCulture)}% "
                + $"trên nguồn {Format(sourceBytes)} B — giữ bản gốc, bỏ qua một lần encode toàn tệp. "
                + "Đây là kết luận thiếu bằng chứng lợi ích, không phải kết luận tệp nguồn đã tối ưu",
            sourceBytes,
            requiredBytes,
            bestOptimistic,
            bestEstimated,
            savingPercent,
            feasible.Count,
            feasible.Count - withBounds,
            confidence);
    }

    /// <summary>
    /// Bộ hiệu chỉnh của một ước lượng đã được chứng nhận chưa.
    /// </summary>
    /// <remarks>
    /// Thiếu danh sách nguồn gốc thì coi như <b>chưa</b> chứng nhận. Ước lượng không kèm dấu
    /// vết hiệu chỉnh không cho ta bất kỳ căn cứ nào để tin, và thiếu bằng chứng thì phải đi
    /// đường an toàn chứ không phải đường không hoàn tác được.
    /// </remarks>
    private static bool IsCertified(SizeEstimate estimate) =>
        estimate.Calibration.Count > 0 && estimate.Calibration.All(p => p.IsCertified);

    private static string Format(long bytes) =>
        bytes.ToString("N0", CultureInfo.InvariantCulture);
}
