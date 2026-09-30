using System.Globalization;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Search;

/// <summary>Một ứng viên đã đo xong, đủ dữ liệu để xếp hạng.</summary>
/// <param name="CandidateId">Định danh ứng viên.</param>
/// <param name="Codec">Họ codec, để cảnh báo khi so nhầm thang số.</param>
/// <param name="Quality">Số đo chất lượng dùng xếp hạng (mean của đoạn tệ nhất).</param>
/// <param name="EstimatedBytes">Ước lượng dung lượng tệp cuối. Chỉ để xếp hạng.</param>
/// <param name="ComputeCostSeconds">Chi phí tính toán đã bỏ ra; dùng để phá thế hoà.</param>
/// <param name="QualityAggregate">Kết quả tổng hợp, giữ để truy vết.</param>
/// <param name="Estimate">Ước lượng kèm giả định, giữ để báo cáo.</param>
public sealed record ScoredCandidate(
    string CandidateId,
    VideoCodec Codec,
    double Quality,
    long EstimatedBytes,
    double ComputeCostSeconds,
    QualityAggregate QualityAggregate,
    SizeEstimate Estimate);

/// <summary>Một ứng viên cùng lý do đã bị loại khỏi frontier.</summary>
public sealed record RejectedCandidate(string CandidateId, string Reason, string Message);

/// <summary>Kết quả lọc frontier.</summary>
public sealed record ParetoResult
{
    public required IReadOnlyList<ScoredCandidate> Frontier { get; init; }

    public required IReadOnlyList<RejectedCandidate> Rejected { get; init; }

    /// <summary>Ứng viên không khả thi về chất lượng, đã bị loại trước cả bước xếp hạng.</summary>
    public required IReadOnlyList<RejectedCandidate> Infeasible { get; init; }
}

/// <summary>
/// Loại ứng viên bị áp đảo, giữ lại những ứng viên còn lựa chọn thật sự.
///
/// <para><b>Ứng viên A áp đảo B</b> khi A không kém hơn B ở chiều nào <i>và</i> tốt hơn ở ít
/// nhất một chiều. Hai chiều là chất lượng (cao hơn tốt) và dung lượng (nhỏ hơn tốt).</para>
///
/// <para><b>Không so tham số encoder giữa các codec.</b> CRF 30 của x264, CRF 30 của
/// libaom và QP 30 của SVT-AV1 là ba mức chất lượng không liên quan. Nếu đưa thang số
/// thô vào đây để "tiết kiệm công so sánh", ta sẽ loại nhầm một ứng viên HEVC 28-bitrate
/// chỉ vì con số của nó trông lớn hơn con số của x264. Chỉ số được so ở đây là
/// <b>VMAF đã đo</b> — cùng một đơn vị cho mọi codec, vì mọi ứng viên đều được đo bằng
/// cùng một mô hình trên cùng một điều kiện hiển thị.</para>
///
/// <para><b>Chỉ ứng viên khả thi mới vào frontier.</b> Ứng viên rớt ngưỡng đã bị loại ở
/// bước đo; đưa nó vào đây chỉ để so sánh với những ứng viên khác là vô nghĩa, vì nó không
/// được phép đi tiếp dù có nhỏ đến đâu.</para>
///
/// <para>Thứ tự kết quả là <b>tất định</b>: cùng đầu vào thì cùng thứ tự, không phụ thuộc
/// thứ tự ban đầu. Lý do: một bộ ứng viên chạy song song sẽ không theo thứ tự nào, nên nếu
/// thứ tự ảnh hưởng tới việc ứng viên nào được chọn thì kết quả sẽ không lặp lại được.</para>
/// </summary>
public static class ParetoSelector
{
    /// <summary>
    /// Chênh lệch tối thiểu để coi là "tốt hơn đáng kể".
    ///
    /// <para>Không so bằng phép bằng tuyệt đối trên số thực: hai phép đo VMAF lệch nhau vài
    /// phần nghìn là chuyện bình thường, và coi đó là "tốt hơn" sẽ xếp hạng theo nhiễu.
    /// Ngưỡng này <b>chưa được hiệu chỉnh</b> — nó giữ cho chênh lệch có ý nghĩa, chứ không
    /// phải ngưỡng chất lượng.</para>
    /// </summary>
    public const double QualityEpsilon = 0.5;

    /// <summary>Tương ứng với <see cref="QualityEpsilon"/> cho dung lượng, tính theo tỉ lệ.</summary>
    public const double SizeRatioEpsilon = 0.01;

    public static ParetoResult Select(IReadOnlyList<ScoredCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var feasible = new List<ScoredCandidate>();
        var infeasible = new List<RejectedCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate.QualityAggregate.IsFeasible)
            {
                feasible.Add(candidate);
            }
            else
            {
                infeasible.Add(new RejectedCandidate(
                    candidate.CandidateId,
                    candidate.QualityAggregate.Outcome.Reason,
                    candidate.QualityAggregate.Outcome.Message));
            }
        }

        if (feasible.Count == 0)
        {
            return new ParetoResult { Frontier = [], Rejected = [], Infeasible = infeasible };
        }

        var rejected = new List<RejectedCandidate>();
        var frontier = new List<ScoredCandidate>(feasible.Count);

        foreach (var candidate in feasible)
        {
            var dominator = feasible
                .FirstOrDefault(other =>
                    !ReferenceEquals(other, candidate) && Dominates(other, candidate));

            if (dominator is null)
            {
                frontier.Add(candidate);
                continue;
            }

            rejected.Add(new RejectedCandidate(
                candidate.CandidateId,
                SearchDecisionReasons.PilotDominated,
                $"{candidate.CandidateId} bị {dominator.CandidateId} áp đảo: "
                    + $"chất lượng {candidate.Quality.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"/{dominator.Quality.ToString("0.0", CultureInfo.InvariantCulture)}, "
                    + $"dung lượng {candidate.EstimatedBytes} B/{dominator.EstimatedBytes} B"));
        }

        return new ParetoResult
        {
            Frontier = Order(frontier),
            Rejected = rejected,
            Infeasible = infeasible,
        };
    }

    /// <summary>
    /// Có <paramref name="a"/> áp đảo <paramref name="b"/> không: không kém ở chiều nào và
    /// tốt hơn đủ ở ít nhất một chiều.
    /// </summary>
    public static bool Dominates(ScoredCandidate a, ScoredCandidate b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var qualityBetter = a.Quality >= b.Quality + QualityEpsilon;
        var qualityTied = Math.Abs(a.Quality - b.Quality) < QualityEpsilon;
        var sizeBetter = a.EstimatedBytes <= b.EstimatedBytes * (1.0 - SizeRatioEpsilon);
        var sizeTied = Math.Abs(a.EstimatedBytes - b.EstimatedBytes)
            <= b.EstimatedBytes * SizeRatioEpsilon;

        // Không kém: mỗi chiều hoặc tốt hơn, hoặc ngang trong sai số.
        if (!qualityTied && !qualityBetter) return false;
        if (!sizeTied && !sizeBetter) return false;

        // Tốt hơn đáng kể ở ít nhất một chiều — nếu không thì hai chiều ngang nhau, tức
        // trùng nhau, không phải áp đảo.
        return qualityBetter || sizeBetter;
    }

    /// <summary>
    /// Thứ tự tất định: chất lượng cao trước, rồi dung lượng nhỏ, rồi chi phí thấp, rồi ID.
    /// </summary>
    /// <remarks>
    /// <para>ID chỉ để chốt trường hợp bằng nhau hoàn toàn; nó không phải tiêu chí chất
    /// lượng. Nhờ vậy cùng một tập ứng viên luôn cho cùng thứ tự, kể cả khi được giao chạy
    /// song song.</para>
    /// </remarks>
    private static IReadOnlyList<ScoredCandidate> Order(IEnumerable<ScoredCandidate> candidates) =>
        [.. candidates
            .OrderByDescending(c => c.Quality)
            .ThenBy(c => c.EstimatedBytes)
            .ThenBy(c => c.ComputeCostSeconds)
            .ThenBy(c => c.CandidateId, StringComparer.Ordinal)];
}
