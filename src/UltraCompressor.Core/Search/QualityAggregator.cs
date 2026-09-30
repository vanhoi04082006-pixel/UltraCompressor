using System.Globalization;
using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Search;

/// <summary>Số đo của một đoạn đại diện, cho một ứng viên.</summary>
/// <param name="Role">Vai trò đoạn: chuyển động cao, chi tiết cao, điển hình...</param>
/// <param name="Start">Mốc bắt đầu trên tệp nguồn.</param>
/// <param name="Duration">Thời lượng đoạn.</param>
/// <param name="Sample">Số đo, hoặc null nếu <b>không đo được</b>.</param>
public sealed record WindowMeasurement(
    WindowRole Role,
    double Start,
    double Duration,
    QualitySample? Sample)
{
    public bool Measured => Sample is not null;
}

/// <summary>Kết quả tổng hợp chất lượng cho một ứng viên.</summary>
public sealed record QualityAggregate
{
    public required string CandidateId { get; init; }

    /// <summary>Ứng viên có dùng được để encode toàn tệp không.</summary>
    public required bool IsFeasible { get; init; }

    public required SearchOutcome Outcome { get; init; }

    /// <summary>Số đoạn đo được trên tổng số đoạn cần đo.</summary>
    public required int MeasuredCount { get; init; }

    public required int RequiredCount { get; init; }

    /// <summary>Đoạn rớt ngưỡng đầu tiên, nếu có.</summary>
    public WindowMeasurement? FailingWindow { get; init; }

    /// <summary>Đoạn có mean thấp nhất — ràng buộc gắn nhất, dùng để hiển thị và chẩn đoán.</summary>
    public WindowMeasurement? WorstWindow { get; init; }

    /// <summary>Ngưỡng đã áp dụng, để báo cáo lại đúng ngưỡng đã dùng.</summary>
    public required QualityFloor Floor { get; init; }

    public IReadOnlyList<WindowMeasurement> Measurements { get; init; } = [];

    /// <summary>Danh sách đoạn không đo được, để truy vết vì sao thiếu số đo.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
}

/// <summary>
/// Gộp số đo của nhiều đoạn thành một kết luận về ứng viên — theo hướng bảo thủ.
///
/// <para><b>Quy tắc duy nhất quyết định:</b> ứng viên chỉ khả thi khi <b>mọi</b> đoạn đo
/// được đều đạt cả ngưỡng mean lẫn ngưỡng P5 của mode. Không lấy trung bình các đoạn, không
/// để đoạn dễ bù cho đoạn khó.</para>
///
/// <para>Lý do: trung bình là cách nhanh nhất để che một sự cố. Một ứng viên hỏng ở cảnh
/// cháy nhưng giữ tốt ở cảnh tĩnh sẽ có mean trung bình đẹp, và nếu ta cho qua thì người
/// dùng thấy đúng những khung hình đó bị hỏng. Ngưỡng P5 vốn sinh ra để bắt đuôi chất
/// lượng, nên lấy trung bình lại trên các đoạn là phá đúng thứ mà nó bảo vệ.</para>
///
/// <para><b>Không đo được thì không phải đạt.</b> Ở lưới chất lượng cuối, công cụ hỏng thì
/// cố ý fail-open để người dùng vẫn nén được. Ở đây ngược lại: ứng viên mà không đo được
/// thì không biết nó có an toàn không, nên không được đi tiếp. Sự khác biệt này là có
/// chủ ý, không phải bất nhất.</para>
///
/// <para><b>Không phát minh ngưỡng mới.</b> <see cref="QualityFloor"/> lấy thẳng từ
/// <see cref="QualityPolicy"/>. <c>Min</c> và đoạn tệ nhất được <b>lưu lại để chẩn đoán</b>
/// nhưng không tham gia quyết định — chưa có số đo nào đủ rộng để biết ngưỡng cho chúng.</para>
/// </summary>
public static class QualityAggregator
{
    public static QualityAggregate Aggregate(
        string candidateId,
        IReadOnlyList<WindowMeasurement> measurements,
        QualityFloor floor)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(floor);

        var required = measurements.Count;
        var missing = new List<string>();
        WindowMeasurement? worst = null;
        WindowMeasurement? failing = null;

        foreach (var measurement in measurements)
        {
            if (!measurement.Measured)
            {
                missing.Add(
                    $"{measurement.Role} lúc {measurement.Start.ToString("0", CultureInfo.InvariantCulture)}s");
                continue;
            }

            // "Tệ nhất" là theo mean: mean là con số cùng đơn vị với ngưỡng đang áp, còn
            // P5 của từng đoạn thì không so được với P5 của đoạn khác.
            if (worst is null || measurement.Sample!.Mean < worst.Sample!.Mean)
            {
                worst = measurement;
            }

            if (failing is not null)
            {
                continue;
            }

            if (!floor.Accepts(measurement.Sample!))
            {
                failing = measurement;
            }
        }

        var measuredCount = required - missing.Count;

        if (required == 0)
        {
            return new QualityAggregate
            {
                CandidateId = candidateId,
                IsFeasible = false,
                RequiredCount = 0,
                MeasuredCount = 0,
                Floor = floor,
                Measurements = measurements,
                Outcome = new SearchOutcome(
                    SearchDecisionReasons.PilotNoCandidates,
                    "không có đoạn đại diện nào để đo"),
            };
        }

        // Thiếu số đo: chưa biết ứng viên có an toàn không.
        if (missing.Count > 0)
        {
            return new QualityAggregate
            {
                CandidateId = candidateId,
                IsFeasible = false,
                RequiredCount = required,
                MeasuredCount = measuredCount,
                Floor = floor,
                Measurements = measurements,
                Missing = missing,
                WorstWindow = worst,
                Outcome = new SearchOutcome(
                    SearchDecisionReasons.PilotMeasurementUnavailable,
                    $"không đo được {missing.Count}/{required} đoạn "
                        + $"({string.Join(", ", missing)}) — không coi là đạt"),
            };
        }

        // Đủ số đo nhưng có đoạn rớt ngưỡng.
        if (failing is not null)
        {
            return new QualityAggregate
            {
                CandidateId = candidateId,
                IsFeasible = false,
                RequiredCount = required,
                MeasuredCount = measuredCount,
                Floor = floor,
                Measurements = measurements,
                FailingWindow = failing,
                WorstWindow = worst,
                Outcome = new SearchOutcome(
                    SearchDecisionReasons.PilotWindowQualityFailed,
                    $"đoạn {failing.Role} lúc {failing.Start.ToString("0", CultureInfo.InvariantCulture)}s "
                        + $"đạt VMAF {failing.Sample!.Mean.ToString("0.0", CultureInfo.InvariantCulture)} "
                        + $"(P5 {failing.Sample.P5.ToString("0.0", CultureInfo.InvariantCulture)}) "
                        + $"so với ngưỡng {floor}"),
            };
        }

        var worstText = worst is null
            ? string.Empty
            : $" đoạn tệ nhất {worst.Role} mean {worst.Sample!.Mean.ToString("0.0", CultureInfo.InvariantCulture)}"
                + $"/P5 {worst.Sample.P5.ToString("0.0", CultureInfo.InvariantCulture)}";

        return new QualityAggregate
        {
            CandidateId = candidateId,
            IsFeasible = true,
            RequiredCount = required,
            MeasuredCount = measuredCount,
            Floor = floor,
            Measurements = measurements,
            WorstWindow = worst,
            Outcome = new SearchOutcome(
                SearchDecisionReasons.PilotSelected,
                $"mọi đoạn đạt ngưỡng {floor}{worstText}"),
        };
    }

    /// <summary>
    /// Chất lượng dùng để xếp hạng: mean của đoạn tệ nhất, không phải trung bình các đoạn.
    /// </summary>
    /// <remarks>
    /// <para>Tương thích với quyết định khả thi: ứng viên bị loại bởi đoạn nào thì bị loại bởi
    /// đoạn đó, nên chấm điểm cũng nên theo đoạn đó. Nếu chấm theo trung bình thì hai ứng
    /// viên có cùng điểm lại khác nhau ở đúng chỗ quyết định đã dùng để loại.</para>
    ///
    /// <para>Chỉ dùng cho xếp hạng và frontier. Không dùng thay cho ngưỡng.</para>
    /// </remarks>
    public static double RankingQuality(QualityAggregate aggregate) =>
        aggregate.WorstWindow?.Sample?.Mean ?? 0;

    /// <summary>P5 của đoạn tệ nhất theo mean, để chẩn đoán.</summary>
    public static double RankingP5(QualityAggregate aggregate) =>
        aggregate.WorstWindow?.Sample?.P5 ?? 0;
}
