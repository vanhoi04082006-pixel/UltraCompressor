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
}

/// <summary>
/// Máy trạng thái tìm kiếm nhị phân cho MỘT nhánh (cùng codec × kích thước).
///
/// <para>Các điểm trong nhánh xếp từ chất lượng cao xuống thấp theo
/// <c>PointIndex</c>, và chất lượng đo được GIẢ ĐỊNH đơn điệu theo thứ tự đó: điểm cao
/// rớt thì điểm thấp hơn chắc chắn rớt, điểm thấp đạt thì điểm cao hơn chắc chắn đạt.
/// Mọi lần cắt (prune) trong class này đều dựa trên giả định đó — nếu nhiễu đo phá vỡ
/// tính đơn điệu ở một nguồn nào đó, cắt sẽ sai. Giả định được ghi ở đây để khi có bằng
/// chứng ngược thì biết phải sửa chỗ nào, thay vì đi tìm trong cả vòng lặp.</para>
///
/// <para>Thứ tự đo: điểm đầu (chất lượng cao nhất), rồi điểm cuối (sâu nhất), rồi chia
/// đôi khoảng còn lại cho tới khi tìm được cặp biên (đạt / rớt kề nhau). Mỗi lần đo đều
/// thu hẹp khoảng — không bao giờ đo điểm mà kết quả của nó không loại được khả năng
/// nào.</para>
/// </summary>
internal sealed class BranchSearch
{
    private readonly List<VideoEncodeCandidate> _points;
    private readonly QualityFloor _floor;
    private readonly CompressionLevel _level;
    private readonly HashSet<int> _evaluated = [];
    private readonly HashSet<int> _failed = [];
    private readonly List<RejectedCandidate> _pruned = [];

    private int? _loFeasible;
    private int? _hiInfeasible;
    private bool _topFeasible;
    private bool _linearMode;
    private bool _closed;

    public BranchSearch(
        IReadOnlyList<VideoEncodeCandidate> points, QualityFloor floor, CompressionLevel level)
    {
        // Sắp phòng thủ: người gọi đã xếp, nhưng thứ tự sai ở đây là sai toàn bộ chiến
        // lược mà không báo lỗi nào. Rẻ hơn là xếp lại chắc chắn.
        _points = [.. points.OrderBy(c => c.PointIndex)];
        _floor = floor;
        _level = level;
        _closed = _points.Count == 0;
    }

    public string BranchId => _points.Count > 0 ? _points[0].BranchId : string.Empty;

    public bool IsClosed => _closed;

    public IReadOnlyList<RejectedCandidate> Pruned => _pruned;

    /// <summary>
    /// Điểm cần đo kế tiếp, hoặc null khi nhánh đã đóng. Trả null cũng đồng nghĩa đóng —
    /// không có "mở mà hết việc", vì trạng thái đó chỉ tạo vòng lặp vô hạn cho người gọi.
    /// </summary>
    public (VideoEncodeCandidate? Candidate, SearchStage Stage) Next()
    {
        if (_closed)
        {
            return (null, SearchStage.Coarse);
        }

        var last = _points.Count - 1;

        if (!Attempted(0))
        {
            return (_points[0], SearchStage.Coarse);
        }

        if (!Attempted(last))
        {
            return (_points[last], SearchStage.Bracket);
        }

        if (!_linearMode && _loFeasible is { } lo && _hiInfeasible is { } hi)
        {
            if (hi - lo <= 1)
            {
                Close(
                    "ngoài biên khả thi: chắc chắn rớt",
                    "trong vùng đã đạt: không thêm biên mới");
                return (null, SearchStage.Coarse);
            }

            // Điểm giữa trước: đó mới là nhị phân. Lấy điểm nhỏ nhất còn lại thì suy biến
            // thành tuyến tính trong trường hợp xấu (biên nằm ở cuối khoảng), và toàn bộ
            // ý nghĩa của việc chia đôi mất hết.
            //
            // Điểm giữa hỏng hạ tầng thì lấy gần nó nhất — mọi điểm trong khoảng đều thu
            // hẹp được biên, nên lệch khỏi giữa một chút không sai, chỉ kém tối ưu một
            // chút. Hòa thì ưu tiên chỉ số lớn hơn (tệp nhỏ hơn) vì đó là hướng mục tiêu.
            var mid = (lo + hi) / 2;
            for (var d = 0; d < hi - lo; d++)
            {
                if (mid + d < hi && !Attempted(mid + d))
                {
                    return (_points[mid + d], SearchStage.Bracket);
                }

                if (d > 0 && mid - d > lo && !Attempted(mid - d))
                {
                    return (_points[mid - d], SearchStage.Bracket);
                }
            }

            Close(
                "ngoài biên khả thi: chắc chắn rớt",
                "trong vùng đã đạt: không thêm biên mới");
            return (null, SearchStage.Coarse);
        }

        // Không có neo đo được (điểm đầu hỏng hạ tầng hoặc thiếu số đo), hoặc bằng chứng
        // mâu thuẫn tính đơn điệu: không có gì để chia đôi, dò tuyến tính từ trên xuống.
        // Chậm hơn nhưng trung thực — không cắt khi chưa có bằng chứng.
        for (var i = 0; i <= last; i++)
        {
            if (!Attempted(i))
            {
                return (_points[i], SearchStage.Bracket);
            }
        }

        Close("mọi điểm đều đã thử hoặc hỏng hạ tầng", "mọi điểm đều đã thử hoặc hỏng hạ tầng");
        return (null, SearchStage.Coarse);
    }

    /// <summary>Ghi nhận kết quả đo của một điểm. Chỉ kết quả ĐO ĐƯỢC mới dịch chuyển biên.</summary>
    public void Observe(EvaluatedCandidate result)
    {
        var index = IndexOf(result.Candidate.Id);
        if (index < 0 || _closed)
        {
            return;
        }

        _evaluated.Add(index);

        // Chế độ tuyến tính: chỉ ghi nhận, không cắt gì thêm. Đã mất neo đơn điệu thì mọi
        // lần cắt đều là đoán — mà đoán thì không được ghi là "chắc chắn".
        if (_linearMode)
        {
            return;
        }

        // "Không đo được" khác "rớt": thiếu số đo thì không có thông tin chất lượng, nên
        // không được dịch chuyển biên và càng không được cắt nhánh. Đánh giá các điểm còn
        // lại tuyến tính — đúng yêu cầu "đo không được thì thử ứng viên khác".
        if (!result.IsFeasible && result.Aggregate.FailingWindow is null)
        {
            _linearMode = true;
            return;
        }

        if (!result.IsFeasible)
        {
            if (index == 0)
            {
                // Điểm cao nhất đã RỚT THẬT (có mẫu đo dưới ngưỡng) thì mọi điểm thấp hơn
                // chắc chắn rớt.
                _hiInfeasible = 0;
                Close("điểm chất lượng cao nhất đã rớt — các điểm thấp hơn chắc chắn rớt",
                    "điểm chất lượng cao nhất đã rớt — các điểm thấp hơn chắc chắn rớt");
                return;
            }

            // Điểm này rớt thật. Nếu nó phá vỡ thứ tự đơn điệu với neo đã có (nằm ngoài
            // khoảng hoặc đảo đầu), bằng chứng đã mâu thuẫn — chuyển tuyến tính.
            if ((_loFeasible is { } lo && index <= lo)
                || (_hiInfeasible is { } hi && index >= hi))
            {
                _linearMode = true;
                return;
            }

            _hiInfeasible = index;
            if (_loFeasible is { } loBound && index - loBound <= 1)
            {
                Close(
                    "ngoài biên khả thi: chắc chắn rớt",
                    "trong vùng đã đạt: không thêm biên mới");
            }

            return;
        }

        if (index == 0)
        {
            _topFeasible = true;
            _loFeasible = 0;

            // DỪNG SỚM. Điểm đầu vượt ngưỡng nhiều thì nhánh này "quá tốt": đào sâu thêm
            // chỉ để tìm tệp nhỏ hơn trong cùng nhánh. Với Light/Balanced thì dừng để tiết
            // kiệm encode; với Strong thì KHÔNG — Strong ưu tiên dung lượng nhỏ nhất nên
            // dừng ở đây là phản lại chính mode.
            if (_level != CompressionLevel.Strong
                && PilotSearch.PassesWithMargin(result.Aggregate, _floor))
            {
                Close(
                    "ngoài biên khả thi: chắc chắn rớt",
                    $"điểm đầu vượt ngưỡng nhiều ở mode {_level} — dừng nhánh để tiết kiệm encode");
            }

            return;
        }

        // Điểm này đạt thật. Nếu nó phá vỡ thứ tự với neo đã có, chuyển tuyến tính.
        if ((_loFeasible is { } existingLo && index <= existingLo)
            || (_hiInfeasible is { } existingHi && index >= existingHi))
        {
            _linearMode = true;
            return;
        }

        _loFeasible = index;

        if (_hiInfeasible is { } hiBound)
        {
            if (hiBound - index <= 1)
            {
                Close(
                    "ngoài biên khả thi: chắc chắn rớt",
                    "trong vùng đã đạt: không thêm biên mới");
            }

            return;
        }

        if (index == _points.Count - 1 && _topFeasible)
        {
            // Điểm sâu nhất đã đạt mà điểm đầu cũng đạt: mọi điểm đều đạt, và điểm nhỏ
            // nhất (cuối) đã có số đo — không còn gì để tìm.
            Close(
                "ngoài biên khả thi: chắc chắn rớt",
                "điểm sâu nhất đã đạt — mọi điểm đều đạt, điểm nhỏ nhất đã có số đo");
        }
    }

    /// <summary>
    /// Ghi nhận điểm encode hỏng. Không dịch chuyển biên (không có thông tin chất lượng),
    /// chỉ để không thử lại lệnh y hệt — ffmpeg là xác định, hỏng lần một thì lần hai
    /// cũng hỏng.
    /// </summary>
    public void NoteFailed(VideoEncodeCandidate candidate)
    {
        var index = IndexOf(candidate.Id);
        if (index >= 0)
        {
            _failed.Add(index);
        }
    }

    private bool Attempted(int index) => _evaluated.Contains(index) || _failed.Contains(index);

    private int IndexOf(string id)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            if (string.Equals(_points[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Cắt mọi điểm chưa thử. Điểm ngoài biên rớt (chỉ số lớn hơn biên rớt) thì "chắc chắn
    /// rớt"; điểm còn lại là "không thêm biên mới". Hai lý do khác nhau vì một cái là kết
    /// luận chất lượng, một cái chỉ là tiết kiệm encode — gộp chung là nói dối một trong hai.
    /// </summary>
    private void Close(string worseReason, string redundantReason)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            if (!Attempted(i))
            {
                var reason = _hiInfeasible is { } hi && i > hi ? worseReason : redundantReason;
                _pruned.Add(new RejectedCandidate(
                    _points[i].Id, SearchDecisionReasons.PilotPruned, reason));
            }
        }

        _closed = true;
    }
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

    /// <summary>Số ứng viên bị chiến lược cắt mà chưa tốn một lần encode nào.</summary>
    public required int CandidatesPruned { get; init; }

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
    /// Biên "vượt nhiều" cho dừng sớm: điểm đầu phải qua ngưỡng mean lẫn P5 với dư ít
    /// nhất bấy nhiêu điểm VMAF.
    ///
    /// <para>Con số này <b>chưa hiệu chỉnh</b> — nó là giới hạn chi phí (bao nhiêu dư thì
    /// đáng để bỏ qua phần còn lại của nhánh), không phải ngưỡng chất lượng. Đặt gấp nhiều
    /// lần epsilon nhiễu đo (0,5) để "vượt nhiều" không thể là nhiễu.</para>
    /// </summary>
    public const double EarlyStopMargin = 3.0;

    /// <summary>
    /// Điểm đầu có vượt ngưỡng "nhiều" không: ngay cả đoạn tệ nhất cũng qua cả hai ngưỡng
    /// với dư ít nhất <see cref="EarlyStopMargin"/>.
    /// </summary>
    internal static bool PassesWithMargin(QualityAggregate aggregate, QualityFloor floor)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(floor);

        return aggregate.IsFeasible
            && aggregate.WorstWindow?.Sample is { } worst
            && worst.Mean >= floor.VmafMean + EarlyStopMargin
            && worst.P5 >= floor.VmafP5 + EarlyStopMargin;
    }

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
        var failed = new List<RejectedCandidate>();
        var pruned = new List<RejectedCandidate>();
        var encodeAttempts = 0;

        var branches = request.Candidates
            .GroupBy(c => c.BranchId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BranchSearch([.. g], floor, request.Level))
            .ToList();

        try
        {
            // Round-robin giữa các nhánh: mỗi vòng mỗi nhánh còn mở được thử một điểm.
            // Nhị phân trong nhánh quyết định điểm kế tiếp; nhánh nào xong thì thôi.
            while (branches.Any(b => !b.IsClosed))
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

                var progressed = false;
                foreach (var branch in branches)
                {
                    token.ThrowIfCancellationRequested();

                    if (evaluated.Count >= request.MaxEvaluations)
                    {
                        break;
                    }

                    var (candidate, stage) = branch.Next();
                    if (candidate is null || !done.Add(candidate.Id))
                    {
                        continue;
                    }

                    encodeAttempts++;
                    var (result, failure) = await EvaluateAsync(
                        request, candidate, stage, ordered, referenceByRole, floor,
                        encodeWatch, measureWatch, token).ConfigureAwait(false);

                    if (result is not null)
                    {
                        evaluated.Add(result);
                        branch.Observe(result);
                        progressed = true;
                    }
                    else
                    {
                        branch.NoteFailed(candidate);
                        if (failure is not null)
                        {
                            infrastructure.Add(failure);
                            failed.Add(new RejectedCandidate(
                                candidate.Id, SearchDecisionReasons.PilotEncodeFailed, failure));
                        }

                        progressed = true;
                    }
                }

                if (!progressed)
                {
                    break;
                }
            }

            foreach (var branch in branches)
            {
                pruned.AddRange(branch.Pruned);
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
                pruned, failed,
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
                pruned, failed,
                SearchStatus.NoFeasibleCandidate,
                new SearchOutcome(
                    SearchDecisionReasons.PilotAllCandidatesRejected,
                    feasibleCount == 0
                        ? $"không ứng viên nào đạt chất lượng ({evaluated.Count}/{request.Candidates.Count} đã thử) — giữ bản gốc"
                        : $"{feasibleCount} ứng viên đạt chất lượng nhưng ước lượng không đủ tin để chọn"));
        }

        return Build(request, watch, encodeWatch, measureWatch, evaluated, pareto, encodeAttempts,
            pruned, failed,
            SearchStatus.SelectedCandidate,
            new SearchOutcome(SearchDecisionReasons.PilotSelected,
                $"chọn {selected.CandidateId}: chất lượng {selected.Quality.ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"ước lượng {selected.EstimatedBytes} B")) with
        {
            Selected = evaluated.First(e => string.Equals(e.Candidate.Id, selected.CandidateId, StringComparison.Ordinal)),
        };
    }

    // ---------------------------------------------------------------- chiến lược tìm kiếm
    //
    // Thứ tự đánh giá do `BranchSearch` lái theo từng nhánh (nhị phân trên thang điểm).
    // Round-robin ở vòng lặp chính giữ cho mọi nhánh đều có cơ hội trước khi ngân sách
    // cạn — xong nhánh nào thì thôi nhánh đó, thay vì dồn hết ngân sách vào nhánh đầu.

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
                    //
                    // Offset căn là (0,0) tường minh: cả hai clip đều từ cùng một tệp nguồn
                    // bằng cùng một lệnh cắt, nên khung đầu đã trùng nhau theo cách xây dựng.
                    // Việc căn ±1 khung chỉ cần ở lưới cuối, nơi hai clip đến từ hai tệp
                    // có timebase khác nhau.
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
                        candidateStartFrame: 0,
                        referenceStartFrame: 0,
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
        List<RejectedCandidate> pruned,
        List<RejectedCandidate> failed,
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

            // Mọi ứng viên không đi tiếp đều phải có mặt với mã lý do: bị Pareto loại, đo
            // rớt, bị chiến lược cắt, hay encode hỏng. Thiếu một nhóm là báo cáo tự dối
            // mình rằng nhóm đó không tồn tại.
            Rejected = [.. pareto.Rejected, .. pareto.Infeasible, .. pruned, .. failed],
            Statistics = new SearchStatistics
            {
                CandidatesPlanned = request.Candidates.Count,

                // Đếm cả ứng viên encode hỏng: chúng đã tốn thời gian thật, và giấu đi số
                // đó là khiến báo cáo chi phí tự dối mình rằng tìm kiếm rẻ.
                CandidatesEvaluated = evaluated.Count,
                CandidatesPruned = pruned.Count,
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
            ParetoSelector.Select(ToScored(evaluated)), evaluated.Count, [], [], status, outcome);
    }
}
