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
/// <para><b>Điều kiện để giữ bản gốc là "không chứng minh được", không phải "ước lượng
/// không đẹp".</b> Ứng viên chỉ bị bác khi ngay cả ở cách đọc <b>có lợi nhất có thể</b> —
/// tức biên dưới của khoảng quan sát — nó vẫn không nhỏ hơn nguồn đủ xa. Nghĩa là để bác ta
/// phải có lý do mạnh nhất; chỉ cần một ước lượng hơi lạc quan là không đủ để bỏ qua một
/// cơ hội tiết kiệm thật.</para>
///
/// <para>Hướng thiên lệch là cố ý và một chiều: <b>nghiêng về giữ bản gốc khi không chắc,
/// nghiêng về encode khi có dấu hiệu lợi ích</b>. Báo động đối lập — encode rồi hóa ra không
/// đáng — vẫn an toàn tuyệt đối, vì lưới 5A giữ bản gốc khi tệp đầu ra không đủ nhỏ. Cái ta
/// không thể để xảy ra là bỏ mất một khoản tiết kiệm thật chỉ vì ước lượng hơi lạc quan.
///
/// Vì vậy hàm này <b>không</b> phải nơi quyết định cuối: nó chỉ tránh một lần encode khi
/// bằng chứng đủ rõ. Mọi thứ sau đó vẫn thuộc lưới 5A.</para>
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

        if (confidence == MeasurementConfidence.Uncertain)
        {
            // Phép đo không chắc, nên "không chứng minh được lợi ích" ở đây chỉ có nghĩa là
            // TA CHƯA CHỨNG MINH ĐƯỢC — chứ không phải là không có lợi ích. Kết luận đó không
            // đủ chắc để bỏ một lần encode, nên đi đường an toàn: encode rồi để lưới 5A
            // quyết định bằng kích thước và số đo thật của tệp đầu ra.
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

        // Không ứng viên nào chứng minh được. Nói rõ đây là kết luận về BẰNG CHỨNG, không
        // phải tuyên bố tệp nguồn đã tối ưu — ta không có căn cứ để nói điều đó.
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

    private static string Format(long bytes) =>
        bytes.ToString("N0", CultureInfo.InvariantCulture);
}
