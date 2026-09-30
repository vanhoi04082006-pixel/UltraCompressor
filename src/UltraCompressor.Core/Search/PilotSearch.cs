using System.Diagnostics;
using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Search;

/// <summary>Kết quả cuối của một lần tìm kiếm.</summary>
public enum SearchStatus
{
    /// <summary>Có ứng viên đạt chất lượng, sẵn sàng encode toàn tệp.</summary>
    SelectedCandidate,

    /// <summary>
    /// Tìm kiếm chạy đúng nhưng không ứng viên nào đạt. <b>Đây là kết quả hợp lệ</b>, không
    /// phải lỗi, và <b>không</b> phải lý do để chạy lại bằng đường legacy.
    /// </summary>
    NoFeasibleCandidate,

    /// <summary>
    /// Hệ thống không đủ khả năng đưa ra quyết định: thiếu encoder, đo lỗi hạ tầng, hoặc
    /// thành phần bên trong hỏng. Đây mới là trường hợp được phép rơi về đường legacy.
    /// </summary>
    SearchInfrastructureFailure,
}

/// <summary>Giai đoạn của chiến lược tìm kiếm đã đánh giá một ứng viên.</summary>
public enum SearchStage
{
    /// <summary>Dò rộng để biết nhánh nào còn dư chất lượng.</summary>
    Coarse,

    /// <summary>Dò quanh ranh giới giữa ứng viên đạt và ứng viên rớt.</summary>
    Bracket,

    /// <summary>Mịn quanh biên đã tìm được, với số lượng cố định.</summary>
    Refine,
}

/// <summary>Một ứng viên đã được đánh giá, kèm mọi thứ cần để quyết định.</summary>
public sealed record EvaluatedCandidate
{
    public required VideoEncodeCandidate Candidate { get; init; }

    public required QualityAggregate Aggregate { get; init; }

    public required SizeEstimate Estimate { get; init; }

    public required IReadOnlyList<WindowMeasurement> Measurements { get; init; }

    /// <summary>Số phép đo VMAF thực sự đã chạy cho ứng viên này.</summary>
    public required int MeasurementsTaken { get; init; }

    /// <summary>
    /// Số phép đo <b>không</b> chạy vì một đoạn trước đó đã rớt ngưỡng.
    /// </summary>
    public required int MeasurementsSkipped { get; init; }

    /// <summary>Số đoạn bị bỏ qua hoàn toàn (không encode được, hoặc không có tham chiếu).</summary>
    public required int WindowsNotMeasured { get; init; }

    public required double ComputeCostSeconds { get; init; }

    public required SearchStage Stage { get; init; }

    public bool IsFeasible => Aggregate.IsFeasible;
}

/// <summary>Số liệu của một lần tìm kiếm, đủ để chẩn đoán mà không cần xem media.</summary>
public sealed record SearchStatistics
{
    public required int CandidatesPlanned { get; init; }

    public required int CandidatesEvaluated { get; init; }

    public required int PilotEncodes { get; init; }

    public required int QualityMeasurements { get; init; }

    public required int MeasurementsIfNoEarlyReject { get; init; }

    public required int CandidatesRejectedInfeasible { get; init; }

    public required int CandidatesDominated { get; init; }

    public required int CandidatesOnFrontier { get; init; }

    public required TimeSpan TotalElapsed { get; init; }

    public required TimeSpan EncodeElapsed { get; init; }

    public required TimeSpan MeasureElapsed { get; init; }

    public int MeasurementsSavedByEarlyReject =>
        Math.Max(0, MeasurementsIfNoEarlyReject - QualityMeasurements);

    public double EarlyRejectSavingPercent =>
        MeasurementsIfNoEarlyReject == 0
            ? 0
            : MeasurementsSavedByEarlyReject * 100.0 / MeasurementsIfNoEarlyReject;
}

/// <summary>Kết quả tìm kiếm: ứng viên được chọn, hoặc lý do không có.</summary>
public sealed record SearchResult
{
    public required SearchStatus Status { get; init; }

    public required SearchOutcome Outcome { get; init; }

    public required IReadOnlyList<EvaluatedCandidate> Evaluated { get; init; }

    public required IReadOnlyList<ScoredCandidate> Frontier { get; init; }

    public required IReadOnlyList<RejectedCandidate> Rejected { get; init; }

    public required SearchStatistics Statistics { get; init; }

    public EvaluatedCandidate? Selected { get; init; }

    public bool HasSelection => Selected is not null;
}

/// <summary>Đầu vào của một lần tìm kiếm.</summary>
public sealed record SearchRequest
{
    public required string SourcePath { get; init; }

    public required long SourceSizeBytes { get; init; }

    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    public required double SourceDurationSeconds { get; init; }

    public required double? SourceAudioBitrateKbps { get; init; }

    public required bool HasAudio { get; init; }

    /// <summary>Các đoạn đại diện, đã có độ khó để sắp xếp.</summary>
    public required IReadOnlyList<RepresentativeWindow> Windows { get; init; }

    /// <summary>
    /// Ứng viên từ <c>CandidatePlanner</c>, đã nhóm theo nhánh và sắp từ chất lượng cao xuống
    /// thấp trong mỗi nhánh. Lớp tìm kiếm không sinh thêm ứng viên nào ngoài tập này.
    /// </summary>
    public required IReadOnlyList<VideoEncodeCandidate> Candidates { get; init; }

    public required CompressionLevel Level { get; init; }

    public required VmafModel Model { get; init; }

    /// <summary>
    /// Trần số ứng viên được đánh giá. Không có trần thì tìm kiếm có thể thử hết tập ứng
    /// viên và mất thời gian không kiểm soát.
    /// </summary>
    public required int MaxEvaluations { get; init; }
}

/// <summary>
/// Điều phối tìm kiếm: chọn ứng viên nào để encode toàn tệp, dựa trên <b>số đo thật</b>.
///
/// <para>Nguyên tắc: <b>planner đề xuất, measurement quyết định</b>. Lớp này không sinh ứng
/// viên, không dựng lệnh encode, không tính chất lượng và không ước lượng dung lượng — nó
/// chỉ quyết định <b>thứ tự thử</b> và <b>dừng khi nào</b>.</para>
///
/// <para><b>Không dùng ngoại lệ làm kết quả kinh doanh.</b> "Không ứng viên nào đạt" xảy ra
/// thường xuyên với tệp đã nén hiệu quả; ném ngoại lệ cho trường hợp đó khiến người gọi không
/// phân biệt được với lỗi hạ tầng — và chỉ lỗi hạ tầng mới được phép rơi về đường legacy.</para>
///
/// <para><b>Ranh giới giữa "loại ứng viên" và "hỏng hạ tầng".</b> ffmpeg từ chối một tổ hợp
/// tham số là sự thật về ứng viên <b>đó</b>: ứng viên bị loại khỏi vòng chơi, còn các ứng viên
/// khác vẫn được đo. Chỉ khi <b>không ứng viên nào</b> cho ra số đo mới là hạ tầng — vì khi đó
/// ta không có căn cứ nào để kết luận, và việc rơi về đường legacy là hành động tử tế chứ
/// không phải che lỗi. Cơ chế này là lý do <c>EvaluateAsync</c> trả về cặp
/// (ứng viên, lý do hạ tầng) thay vì <c>null</c>: nếu trả <c>null</c> thì "hỏng" và "loại"
/// trông giống nhau, và lỗi hạ tầng bị nuốt mất.</para>
/// </summary>
public sealed class PilotSearch(
    IPilotEncodeRunner encoder,
    IReferenceWindowSource references,
    IQualityMeasure measurer)
{
    /// <summary>
    /// Số ứng viên dò thêm ở giai đoạn refine. Hằng số <b>giới hạn chi phí</b>, không phải
    /// tham số chất lượng.
    /// </summary>
    public const int RefineSamples = 2;

    /// <summary>
    /// Ngưỡng tiết kiệm để chấp nhận dừng sớm. Lấy cùng ngưỡng với lưới chất lượng cuối, để
    /// giai đoạn tìm không chấp nhận thứ mà giai đoạn cuối sẽ loại.
    /// </summary>
    public const double MinSavingForEarlyStopPercent = 1.0;

    public async Task<SearchResult> RunAsync(SearchRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var watch = Stopwatch.StartNew();
        var encodeWatch = new Stopwatch();
        var measureWatch = new Stopwatch();

        if (request.Candidates.Count == 0)
        {
            return Finish(request, watch, encodeWatch, measureWatch, [], SearchStatus.NoFeasibleCandidate,
                new SearchOutcome(SearchDecisionReasons.PilotNoCandidates,
                    "planner không sinh ứng viên nào — khả năng là bản ffmpeg này không có encoder phù hợp"));
        }

        if (request.Windows.Count == 0)
        {
            return Finish(request, watch, encodeWatch, measureWatch, [], SearchStatus.NoFeasibleCandidate,
                new SearchOutcome(SearchDecisionReasons.PilotNoCandidates,
                    "không có đoạn đại diện nào để đo"));
        }

        var ordered = OrderWindows(request.Windows);
        var floor = QualityPolicy.For(request.Level, request.Model);

        // Tham chiếu cắt MỘT LẦN, dùng chung cho mọi ứng viên. Cắt lại cho từng ứng viên vừa
        // tốn công vừa tạo thêm một nguồn lệch khung hình.
        IReadOnlyList<WindowReference> referenceClips;
        try
        {
            referenceClips = await references.ExtractAsync(request.SourcePath, ordered, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Finish(request, watch, encodeWatch, measureWatch, [], SearchStatus.SearchInfrastructureFailure,
                new SearchOutcome(SearchDecisionReasons.PilotEncodeFailed,
                    $"không cắt được đoạn tham chiếu để đo chất lượng: {ex.Message}"));
        }

        var referenceByRole = referenceClips.ToDictionary(r => r.Role);
        var evaluated = new List<EvaluatedCandidate>();
        var done = new HashSet<string>(StringComparer.Ordinal);

        // Lỗi hạ tầng phải được GIỮ LẠI, không được bỏ qua cùng ứng viên. Bỏ qua nó thì
        // một lần tìm kiếm mà mọi ứng viên đều hỏng sẽ trông giống hệt một lần tìm kiếm mà
        // mọi ứng viên đều rớt chất lượng — và chỉ vế thứ nhất mới được phép rơi về legacy.
        var infrastructure = new List<string>();
        var encodeAttempts = 0;

        try
        {
            // Giai đoạn 1: coarse trên mọi nhánh. Nhánh rớt ngay ở điểm chất lượng cao nhất thì
            // bỏ: nhịp chất lượng đi xuống, không quay lại được.
            foreach (var candidate in CoarseCandidates(request.Candidates))
            {
                // Hủy không phải là một kết quả. `OperationCanceledException` là câu trả lời
                // chuẩn của .NET và giữ cho người gọi không nhầm hủy với "không có ứng viên
                // nào đạt" — vì cả hai đều dẫn tới việc dừng, nhưng chỉ một cái là do người
                // dùng yêu cầu.
                token.ThrowIfCancellationRequested();

                if (evaluated.Count >= request.MaxEvaluations)
                {
                    break;
                }

                if (!done.Add(candidate.Id))
                {
                    continue;
                }

                encodeAttempts++;
                var (result, failure) = await EvaluateAsync(
                    request, candidate, SearchStage.Coarse, ordered, referenceByRole, floor,
                    encodeWatch, measureWatch, token).ConfigureAwait(false);

                if (result is not null)
                {
                    evaluated.Add(result);
                }
                else if (failure is not null)
                {
                    infrastructure.Add(failure);
                }
            }

            // Giai đoạn 2 và 3: dò thêm các điểm còn lại trong nhánh đã từng đạt, quanh biên.
            for (var round = 0; round < 2; round++)
            {
                token.ThrowIfCancellationRequested();

                var stage = round == 0 ? SearchStage.Bracket : SearchStage.Refine;
                var next = NextCandidates(request, evaluated, done, stage);
                if (next.Count == 0)
                {
                    break;
                }

                foreach (var candidate in next)
                {
                    token.ThrowIfCancellationRequested();

                    if (evaluated.Count >= request.MaxEvaluations)
                    {
                        break;
                    }

                    done.Add(candidate.Id);

                    encodeAttempts++;
                    var (result, failure) = await EvaluateAsync(
                        request, candidate, stage, ordered, referenceByRole, floor,
                        encodeWatch, measureWatch, token).ConfigureAwait(false);

                    if (result is not null)
                    {
                        evaluated.Add(result);
                    }
                    else if (failure is not null)
                    {
                        infrastructure.Add(failure);
                    }
                }
            }
        }
        finally
        {
            references.Release(referenceClips);
        }

        watch.Stop();

        // Không đo được ứng viên nào: chưa có quyết định nào để báo cáo. Nếu nguyên nhân là
        // hạ tầng thì đó là `SearchInfrastructureFailure` — vì chỉ trường hợp này được phép
        // rơi về đường legacy. Ngân sách bằng 0 cũng thuộc nhóm này: không thử gì thì không
        // có căn cứ để kết luận.
        if (evaluated.Count == 0)
        {
            var detail = infrastructure.Count > 0
                ? string.Join("; ", infrastructure.Take(3))
                : $"ngân sách đánh giá bằng 0 (MaxEvaluations = {request.MaxEvaluations})";

            return Build(request, watch, encodeWatch, measureWatch, evaluated, EmptyPareto, encodeAttempts,
                SearchStatus.SearchInfrastructureFailure,
                new SearchOutcome(SearchDecisionReasons.PilotEncodeFailed,
                    $"không đánh giá được ứng viên nào: {detail}"));
        }

        var pareto = ParetoSelector.Select(ToScored(evaluated));
        var selected = SelectFromFrontier(pareto);

        if (selected is null)
        {
            var feasibleCount = evaluated.Count(e => e.IsFeasible);
            return Build(request, watch, encodeWatch, measureWatch, evaluated, pareto, encodeAttempts,
                SearchStatus.NoFeasibleCandidate,
                new SearchOutcome(
                    SearchDecisionReasons.PilotAllCandidatesRejected,
                    feasibleCount == 0
                        ? $"không ứng viên nào đạt chất lượng ({evaluated.Count}/{request.Candidates.Count} đã thử) — giữ bản gốc"
                        : $"{feasibleCount} ứng viên đạt chất lượng nhưng ước lượng không đủ tin để chọn"));
        }

        return Build(request, watch, encodeWatch, measureWatch, evaluated, pareto, encodeAttempts,
            SearchStatus.SelectedCandidate,
            new SearchOutcome(SearchDecisionReasons.PilotSelected,
                $"chọn {selected.CandidateId}: chất lượng {selected.Quality.ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"ước lượng {selected.EstimatedBytes} B")) with
        {
            Selected = evaluated.First(e => string.Equals(e.Candidate.Id, selected.CandidateId, StringComparison.Ordinal)),
        };
    }

    // ---------------------------------------------------------------- chiến lược: coarse

    /// <summary>
    /// Ứng viên dò thô: mỗi nhánh lấy điểm chất lượng cao nhất và điểm sâu nhất.
    /// </summary>
    /// <remarks>
    /// <para>Hai đầu đủ để biết nhánh nào còn dư chất lượng và nhánh nào đã hết ngay. Dò
    /// một điểm giữa thì tốn thêm một lần encode + VMAF mà chưa chắc thêm thông tin: nếu
    /// <c>CoarseProbe</c> đã rớt thì điểm giữa chắc chắn cũng rớt.</para>
    /// </remarks>
    internal static IReadOnlyList<VideoEncodeCandidate> CoarseCandidates(
        IReadOnlyList<VideoEncodeCandidate> candidates) =>
    [
        .. candidates
            .GroupBy(c => c.BranchId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .SelectMany(g =>
            {
                var points = g.OrderBy(c => c.PointIndex).ToList();
                return points.Count <= 1 ? points : [points[0], points[^1]];
            })
    ];

    /// <summary>
    /// Ứng viên cho vòng kế tiếp: các điểm còn lại trong nhánh đã từng vượt ngưỡng, lấy từ
    /// chất lượng cao xuống thấp để tìm biên khả thi.
    /// </summary>
    /// <remarks>
    /// <para>Chỉ nhánh <b>đã từng đạt</b> mới được dò tiếp. Nhánh rớt ở <c>CoarseProbe</c> thì
    /// mọi điểm thấp hơn cũng rớt, nên bỏ hẳn — đây là loại cắt bảo thủ: nó chỉ bỏ ứng viên
    /// mà đã có bằng chứng không thể đạt.</para>
    /// </remarks>
    internal static IReadOnlyList<VideoEncodeCandidate> NextCandidates(
        SearchRequest request,
        IReadOnlyList<EvaluatedCandidate> evaluated,
        HashSet<string> done,
        SearchStage stage)
    {
        var passingBranches = evaluated
            .Where(e => e.IsFeasible)
            .Select(e => e.Candidate.BranchId)
            .ToHashSet(StringComparer.Ordinal);

        if (passingBranches.Count == 0)
        {
            return [];
        }

        // Vòng 0 dò mọi điểm còn lại của nhánh đạt; vòng 1 chỉ dò thêm có giới hạn.
        var limit = stage == SearchStage.Bracket ? int.MaxValue : RefineSamples;

        return
        [
            .. request.Candidates
                .Where(c => passingBranches.Contains(c.BranchId)
                            && !done.Contains(c.Id))
                .OrderBy(c => c.BranchId, StringComparer.Ordinal)
                .ThenBy(c => c.PointIndex)
                .Take(limit)
        ];
    }

    // ---------------------------------------------------------------- đánh giá một ứng viên

    /// <summary>
    /// Đánh giá một ứng viên.
    /// </summary>
    /// <returns>
    /// Đúng một trong hai thứ: ứng viên đã có số đo, hoặc lý do hạ tầng. Không bao giờ
    /// trả "không có gì" — vì người gọi phải phân biệt được "chưa đo được" với "hỏng", và
    /// nhầm hai thứ đó là nguồn lỗi nguy hiểm nhất của cả lớp tìm kiếm này.
    /// </returns>
    private async Task<(EvaluatedCandidate? Candidate, string? InfrastructureFailure)> EvaluateAsync(
        SearchRequest request,
        VideoEncodeCandidate candidate,
        SearchStage stage,
        IReadOnlyList<RepresentativeWindow> orderedWindows,
        Dictionary<WindowRole, WindowReference> referenceByRole,
        QualityFloor floor,
        Stopwatch encodeWatch,
        Stopwatch measureWatch,
        CancellationToken token)
    {
        IReadOnlyList<PilotArtifact> artifacts;
        encodeWatch.Start();
        try
        {
            artifacts = await encoder.EncodeAsync(
                candidate, request.SourcePath, request.SourceWidth, request.SourceHeight,
                orderedWindows, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"{candidate.Id}: encode ném ngoại lệ: {ex.Message}");
        }
        finally
        {
            encodeWatch.Stop();
        }

        // Không encode được đoạn nào: hạ tầng lỗi, không phải chất lượng.
        if (artifacts.Count == 0)
        {
            return (null, $"{candidate.Id}: không tạo được clip thử nghiệm nào");
        }

        var usable = artifacts.Where(a => a.Success).ToList();
        if (usable.Count == 0)
        {
            var why = artifacts[0].Outcome.Message;
            return (null, $"{candidate.Id}: mọi clip thử nghiệm đều hỏng — {why}");
        }

        var measurements = new List<WindowMeasurement>(orderedWindows.Count);
        var taken = 0;
        var skipped = 0;
        var notMeasured = 0;
        var stoppedEarly = false;

        try
        {
            foreach (var window in orderedWindows)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!referenceByRole.TryGetValue(window.Role, out var reference))
                {
                    notMeasured++;
                    measurements.Add(Blank(window));
                    continue;
                }

                // `Success` đã đảm bảo `OutputPath` khác null, nhưng trình biên dịch không
                // suy ra được điều đó từ một thuộc tính. Ràng `OutputPath` vào lúc lấy clip
                // để kiểu đúng được bảo đảm bằng chính cấu trúc, không bằng kỷ luật.
                if (artifacts.FirstOrDefault(a => a.Success && a.Role == window.Role)
                    is not { OutputPath: { } candidatePath, Target: { } target } artifact)
                {
                    notMeasured++;
                    measurements.Add(Blank(window));
                    continue;
                }

                QualitySample? sample;
                measureWatch.Start();
                try
                {
                    // Cả hai cửa sổ đều là `[0, d]` vì cả tham chiếu lẫn clip ứng viên đều
                    // đã được cắt riêng. Truyền cửa sổ của nguồn vào đây là lỗi đã mắc phải:
                    // nó seek quá cuối clip ứng viên và đo ra rỗng.
                    var measured = await measurer.MeasureAsync(
                        reference.Path,
                        candidatePath,
                        new TimeWindow(0, reference.LengthSeconds),
                        artifact.CandidateWindow,
                        request.SourceWidth,
                        request.SourceHeight,
                        target.Width,
                        target.Height,
                        request.Model,
                        token).ConfigureAwait(false);

                    sample = measured?.Sample;
                }
                finally
                {
                    measureWatch.Stop();
                }

                taken++;
                measurements.Add(new WindowMeasurement(
                    window.Role, window.StartSeconds, window.DurationSeconds, sample));

                // LOẠI SỚM. Một đoạn đo được mà rớt thì ứng viên đã rớt; các đoạn sau không
                // thể cứu được, và bộ gộp bảo thủ vẫn cho cùng kết luận. Dừng ở đây tiết
                // kiệm encode + VMAF mà không đổi quyết định.
                //
                // `sample is null` (không đo được) KHÔNG dừng: "không đo được" khác "rớt", và
                // người đọc báo cáo cần thấy nó.
                if (sample is not null && !floor.Accepts(sample))
                {
                    stoppedEarly = true;
                    break;
                }
            }
        }
        finally
        {
            // Giữ clip tới khi đo xong mọi đoạn của ứng viên này, rồi mới giải phóng.
            encoder.Release(artifacts);
        }

        if (stoppedEarly)
        {
            // Các đoạn chưa kịp đo chưa từng được đo, nên không được ghi là "không đo được":
            // số 0 đó là do chúng ta chủ động bỏ qua, không phải hỏng hạ tầng.
            for (var i = taken; i < orderedWindows.Count; i++)
            {
                skipped++;
            }
        }

        var aggregate = QualityAggregator.Aggregate(candidate.Id, measurements, floor);

        // Ước lượng chỉ dùng clip thành công. Clip hỏng có `Bytes = 0` nên đưa vào sẽ làm
        // ước lượng nhỏ đi và khiến ứng viên hỏng trông hấp dẫn giả.
        var estimate = SizeEstimator.Estimate(
            usable, request.SourceDurationSeconds, request.SourceAudioBitrateKbps, request.HasAudio);

        return (new EvaluatedCandidate
        {
            Candidate = candidate,
            Aggregate = aggregate,
            Estimate = estimate,
            Measurements = measurements,
            MeasurementsTaken = taken,
            MeasurementsSkipped = skipped,
            WindowsNotMeasured = notMeasured,
            ComputeCostSeconds = artifacts.Sum(a => a.Elapsed.TotalSeconds),
            Stage = stage,
        }, null);
    }

    private static WindowMeasurement Blank(RepresentativeWindow window) =>
        new(window.Role, window.StartSeconds, window.DurationSeconds, null);

    // ---------------------------------------------------------------- chọn ứng viên

    /// <summary>
    /// Chọn từ frontier: ứng viên <b>nhỏ nhất</b> trong số những ứng viên đạt chất lượng.
    /// </summary>
    /// <remarks>
    /// <para>Ở đây không có chỗ nào ghi "Cân bằng = CRF 26" hay "Mạnh = nhỏ nhất". Cả ba mode
    /// dùng cùng quy tắc, và khác nhau ở chỗ chúng dùng ngưỡng chất lượng nào — vốn do
    /// <c>QualityPolicy</c> quyết định. Sự khác biệt giữa các mode là ở ngưỡng, không ở
    /// thuật toán chọn.</para>
    ///
    /// <para><b>Ước lượng chỉ để xếp hạng, không để loại cứng.</b> Chưa encode tệp nào thì
    /// không biết kích thước thật; loại ứng viên chỉ vì ước lượng lớn là dùng con số chưa
    /// được kiểm chứng để quyết định không thể hoàn tác.</para>
    /// </remarks>
    internal static ScoredCandidate? SelectFromFrontier(ParetoResult pareto) =>
        pareto.Frontier
            .Where(c => c.Estimate.TotalBytes > 0)
            .OrderBy(c => c.EstimatedBytes)
            .ThenByDescending(c => c.Quality)
            .ThenBy(c => c.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault();

    // ---------------------------------------------------------------- sắp xếp đoạn

    /// <summary>
    /// Sắp đoạn theo khả năng làm ứng viên rớt, giảm dần.
    /// </summary>
    /// <remarks>
    /// <para>Đo đoạn khó trước vì nếu ứng viên rớt ở đó thì không cần đo đoạn còn lại. Thứ tự
    /// <b>không</b> dựa vào tên vai trò: có nguồn mà đoạn <c>HighSpatial</c> khó hơn hẳn đoạn
    /// <c>HighMotion</c>, và đo sai thứ tự sẽ tốn công mà không đổi kết luận. Tiêu chí là
    /// <c>OverallComplexity</c> mà bộ chọn đoạn đã tính.</para>
    /// </remarks>
    internal static IReadOnlyList<RepresentativeWindow> OrderWindows(
        IReadOnlyList<RepresentativeWindow> windows) =>
        [.. windows.OrderByDescending(w => w.OverallComplexity)
                    .ThenBy(w => w.StartSeconds)
                    .ThenBy(w => w.Role)];

    // ---------------------------------------------------------------- kết quả

    /// <summary>Frontier rỗng, dùng khi chưa có ứng viên nào để xếp hạng.</summary>
    private static ParetoResult EmptyPareto => new() { Frontier = [], Rejected = [], Infeasible = [] };

    private static IReadOnlyList<ScoredCandidate> ToScored(IReadOnlyList<EvaluatedCandidate> evaluated) =>
        [.. evaluated.Select(e => new ScoredCandidate(
            e.Candidate.Id, e.Candidate.Codec,
            QualityAggregator.RankingQuality(e.Aggregate), e.Estimate.TotalBytes,
            e.ComputeCostSeconds, e.Aggregate, e.Estimate))];

    private static SearchResult Build(
        SearchRequest request,
        Stopwatch watch,
        Stopwatch encodeWatch,
        Stopwatch measureWatch,
        IReadOnlyList<EvaluatedCandidate> evaluated,
        ParetoResult pareto,
        int encodeAttempts,
        SearchStatus status,
        SearchOutcome outcome)
    {
        var windowCount = request.Windows.Count;
        var measured = evaluated.Sum(e => e.MeasurementsTaken);

        return new SearchResult
        {
            Status = status,
            Outcome = outcome,
            Evaluated = evaluated,
            Frontier = pareto.Frontier,
            Rejected = [.. pareto.Rejected, .. pareto.Infeasible],
            Statistics = new SearchStatistics
            {
                CandidatesPlanned = request.Candidates.Count,

                // Đếm cả ứng viên encode hỏng: chúng đã tốn thời gian thật, và giấu đi số
                // đó là khiến báo cáo chi phí tự dối mình rằng tìm kiếm rẻ.
                CandidatesEvaluated = evaluated.Count,
                PilotEncodes = encodeAttempts,
                QualityMeasurements = measured,
                MeasurementsIfNoEarlyReject = evaluated.Count * windowCount,
                CandidatesRejectedInfeasible = pareto.Infeasible.Count,
                CandidatesDominated = pareto.Rejected.Count,
                CandidatesOnFrontier = pareto.Frontier.Count,
                TotalElapsed = watch.Elapsed,
                EncodeElapsed = encodeWatch.Elapsed,
                MeasureElapsed = measureWatch.Elapsed,
            },
        };
    }

    private static SearchResult Finish(
        SearchRequest request,
        Stopwatch watch,
        Stopwatch encodeWatch,
        Stopwatch measureWatch,
        IReadOnlyList<EvaluatedCandidate> evaluated,
        SearchStatus status,
        SearchOutcome outcome)
    {
        watch.Stop();

        return Build(
            request, watch, encodeWatch, measureWatch, evaluated,
            ParetoSelector.Select(ToScored(evaluated)), evaluated.Count, status, outcome);
    }
}
