using System.Diagnostics;
using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Search;

/// <summary>Kết quả cuối của một lần tìm kiếm.</summary>
public enum SearchStatus
{
    /// <summary>
    /// Có ứng viên đạt chất lượng <b>và chứng minh được lợi ích dung lượng</b> so với bản
    /// gốc — sẵn sàng encode toàn tệp.
    /// </summary>
    SelectedCandidate,

    /// <summary>
    /// Có ứng viên đạt chất lượng, nhưng không ứng viên nào chứng minh được lợi ích dung
    /// lượng đủ ý nghĩa so với bản gốc. Giữ nguyên bản gốc và <b>bỏ qua</b> một lần encode
    /// toàn tệp.
    /// </summary>
    /// <remarks>
    /// <b>Khác <see cref="NoFeasibleCandidate"/>, và phải khác.</b> Ở kia ta thử rồi không
    /// ứng viên nào đạt chất lượng — nói về <i>những ứng viên encode</i>. Ở đây có ứng viên
    /// đạt, ta chỉ không thấy lý do để làm tốn công mã hoá lại — nói về <i>tệp đầu ra</i>.
    /// Gộp hai thứ lại thì người đọc không phân biệt được "không có gì tốt hơn bản gốc"
    /// với "bản gốc thắng", và hai trường hợp đó cần hành xử khác nhau khi báo cáo.
    ///
    /// <para><b>Hiện chưa xảy ra, và đó là chủ đích.</b> Kết luận giữ bản gốc đòi hỏi bằng
    /// chứng kích thước <b>được chứng nhận</b>; bộ hiệu chỉnh hiện tại là
    /// <c>provisional</c> nên chưa đạt. Xem <see cref="OriginalComparison"/>. Nhánh này được
    /// giữ nguyên trong bảng quyết định để khi bằng chứng tới, không phải sửa lại kiến trúc.</para>
    /// </remarks>
    OriginalSelected,

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
/// Máy trạng thái tìm kiếm nhị phân cho MỘT nhánh — nhánh là nhóm ứng viên có <b>cùng
/// dấu vân tay phép biến đổi</b> (xem <see cref="TransformFingerprint"/>), không phải
/// "cùng tên".
///
/// <para>Các điểm trong nhánh xếp từ chất lượng cao xuống thấp theo <c>PointIndex</c>, và
/// chất lượng đo được <b>giả định</b> đơn điệu theo thứ tự đó: điểm cao rớt thì điểm thấp
/// hơn chắc chắn rớt, điểm thấp đạt thì điểm cao hơn chắc chắn đạt. Mọi lần cắt trong
/// class này đều dựa trên giả định đó, nên nó chỉ chạy trên một nhánh đã khoá phép biến
/// đổi — đổi codec, đổi kích thước, đổi FPS, đổi định dạng pixel hay đổi chuỗi bộ lọc
/// giữa hai điểm thì chất lượng không còn đơn điệu và mọi lần cắt sẽ sai.</para>
///
/// <para><b>Mọi lần cắt phải có hai chứng cứ, không phải một.</b> Bản trước cắt ngay khi
/// điểm đầu rớt — tức dựa vào <i>một</i> phép đo để khẳng định cả nhánh rớt. Điều đó sai
/// với phong cảnh chất lượng không đơn điệu: một clip có thể rớt ở đoạn đầu trong khi điểm
/// giữa vẫn đạt, và bản trước <b>không tìm ra</b> ứng viên dù đó. Nay điểm đầu rớt chỉ đặt
/// biên, còn phải dò điểm cuối để xác nhận; hai đầu cùng rớt mới cắt được, và nếu điểm cuối
/// lại đạt thì đó là bằng chứng trái chiều — chuyển dò tuyến tính, không cắt gì.</para>
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
    private readonly HashSet<int> _evaluated = [];
    private readonly HashSet<int> _failed = [];
    private readonly List<RejectedCandidate> _pruned = [];

    private int? _loFeasible;
    private int? _hiInfeasible;
    private bool _topFeasible;
    private bool _linearMode;
    private bool _closed;
    private bool _awaitCorroboration;
    private string? _nonMonotonicReason;

    public BranchSearch(IReadOnlyList<VideoEncodeCandidate> points, QualityFloor floor)
    {
        // Sắp phòng thủ: người gọi đã xếp, nhưng thứ tự sai ở đây là sai toàn bộ chiến
        // lược mà không báo lỗi nào. Rẻ hơn là xếp lại chắc chắn.
        _points = [.. points.OrderBy(c => c.PointIndex)];
        _floor = floor;
        _closed = _points.Count == 0;
    }

    public string BranchId => _points.Count > 0 ? _points[0].BranchId : string.Empty;

    public bool IsClosed => _closed;

    public IReadOnlyList<RejectedCandidate> Pruned => _pruned;

    /// <summary>
    /// Có bằng chứng trái chiều với giả định đơn điệu trong nhánh này không.
    /// </summary>
    /// <remarks>
    /// <para>Đây là thứ người đọc nhật ký cần để biết phép đo VMAF có đáng tin ở nguồn này
    /// hay không. Giả định bị bác bỏ không phải lỗi của thuật toán — thuật toán đã phản ứng
    /// đúng bằng cách bỏ cắt — nhưng nó là tín hiệu đáng điều tra về phía phép đo.</para>
    /// </remarks>
    public bool NonMonotonicObserved => _nonMonotonicReason is not null;

    /// <summary>Lý do vi phạm, bằng tiếng Việt để ghép vào báo cáo.</summary>
    public string? NonMonotonicReason => _nonMonotonicReason;

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

        // Đã có hai đầu rớt và đang chờ một điểm xác nhận thứ ba trước khi cắt cả nhánh.
        if (_awaitCorroboration)
        {
            var mid = last / 2;
            for (var d = 0; d <= last; d++)
            {
                if (mid + d <= last && !Attempted(mid + d))
                {
                    return (_points[mid + d], SearchStage.Bracket);
                }

                if (d > 0 && mid - d >= 0 && !Attempted(mid - d))
                {
                    return (_points[mid - d], SearchStage.Bracket);
                }
            }

            _awaitCorroboration = false;
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
            NoteLinear("không đo được chất lượng ở điểm này — không có thông tin để dịch chuyển biên");
            return;
        }

        var lastIndex = _points.Count - 1;
        var confirming = _awaitCorroboration;
        _awaitCorroboration = false;

        if (!result.IsFeasible)
        {
            // Điểm xác nhận thứ ba cũng rớt: ba điểm trải khắp nhánh đều không đạt. Đây là
            // chứng cứ mạnh nhất mà ta mua được với giá rẻ — và vẫn không phải chứng minh.
            // Xem ghi chú ở `HasUnattemptedInterior` về giới hạn của nó.
            if (confirming)
            {
                Close(
                    "ba điểm trải khắp nhánh đều rớt — mọi điểm giữa chúng nằm dưới ngưỡng",
                    "ba điểm trải khắp nhánh đều rớt — mọi điểm giữa chúng nằm dưới ngưỡng");
                return;
            }

            // Hai đầu cùng rớt. Chưa cắt ngay: cần điểm xác nhận thứ ba ở giữa, vì hai đầu
            // rớt KHÔNG chứng minh các điểm giữa cũng rớt khi phong cảnh không đơn điệu.
            if (_hiInfeasible == 0 && index == lastIndex)
            {
                if (HasUnattemptedInterior(lastIndex))
                {
                    _awaitCorroboration = true;
                    return;
                }

                Close(
                    "cả hai đầu nhánh đều rớt — đoạn giữa nằm giữa nên không thể đạt",
                    "cả hai đầu nhánh đều rớt — đoạn giữa nằm giữa nên không thể đạt");
                return;
            }

            if (index == 0)
            {
                // Chỉ đặt biên, KHÔNG cắt. Cần chứng cứ thứ hai ở khối trên.
                _hiInfeasible = 0;
                return;
            }

            // Điểm này rớt thật. Nếu việc ghi nhận sẽ khiến một biên lùi sai hướng (lùi
            // biên rớt lên trên, lùi biên đạt xuống dưới) thì mọi lần cắt sau đó sẽ loại
            // nhầm. Không phải mọi trường hợp ở đây đều là "trái chiều" — chỉ một cách mới
            // thật sự là trá chiều, và cách đó mới được đánh dấu.
            if (_loFeasible is { } lo && index <= lo)
            {
                NoteLinear($"điểm {index} rớt trong khi điểm {lo} đã đạt — không dòng được biên nữa");
                return;
            }

            if (_hiInfeasible is { } hi && index >= hi)
            {
                NoteLinear($"điểm rớt {index} nằm trên biên rớt đã biết {hi} — biên sẽ lùi sai hướng");
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
            return;
        }

        // Điểm này đạt thật. Điểm đã đạt nằm DƯỚI biên rớt đã biết thì đó là bằng chứng trái
        // chiều thật sự: theo giả định, chất lượng không tăng theo chỉ số, nên không thể có
        // điểm sau mà lại đạt. Đây là thứ duy nhất được đánh dấu là phi đơn điệu.
        if (_hiInfeasible is { } existingHi && index >= existingHi)
        {
            NoteLinear(
                $"điểm {index} đạt trong khi điểm {existingHi} đã rớt — chất lượng không đơn điệu theo chỉ số",
                nonMonotonic: true);
            return;
        }

        if (_loFeasible is { } existingLo && index <= existingLo)
        {
            NoteLinear($"điểm đạt {index} nằm dưới biên đạt đã biết {existingLo} — biên sẽ lùi sai hướng");
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

    /// <summary>
    /// Còn điểm nào ở khoảng giữa chưa thử không?
    /// </summary>
    /// <remarks>
    /// <para>Đây là chỗ mua chứng cứ thứ ba trước khi cắt cả nhánh, và cũng là <b>giới hạn</b>
    /// thật của thuật toán: ba điểm trải khắp nhánh vẫn không phải chứng minh. Một "đảo ngọc"
    /// hẹp — chỉ vài điểm ở giữa đạt — vẫn có thể lọt. Chấp nhận được là vì:
    /// <c>QualityGate</c> ở giai đoạn 5A vẫn kiểm tra tệp đầu ra thật, nên hệ quả tệ nhất là
    /// bỏ sót một cơ hội, không bao giờ là giao cho người dùng một tệp tệ hơn nguồn.</para>
    ///
    /// <para>Muốn có bảo đảm tuyệt đối thì phải đo hết mọi điểm — tức bỏ nhị phân. Đó là
    /// đánh đổi chi phí, và việc quyết định nó thuộc về chính sách tính toán, không thuộc
    /// phần sửa lỗi này.</para>
    /// </remarks>
    private bool HasUnattemptedInterior(int lastIndex)
    {
        for (var i = 1; i < lastIndex; i++)
        {
            if (!Attempted(i))
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// Bỏ giả định đơn điệu của nhánh này: từ đây chỉ ghi nhận, không cắt gì nữa.
    /// </summary>
    /// <param name="reason">Lý do, bằng tiếng Việt để ghép vào báo cáo.</param>
    /// <param name="nonMonotonic">
    /// <c>true</c> khi số đo thật sự <b>trái</b> với giả định đơn điệu. Chỉ khi đó mới đánh
    /// dấu phi đơn điệu — còn "biên lùi sai hướng" thì vẫn là giả định đúng, chỉ là ta
    /// không dòng được nữa. Gộp hai thứ đó làm mã chẩn đoán mất ý nghĩa.
    /// </param>
    private void NoteLinear(string reason, bool nonMonotonic = false)
    {
        _linearMode = true;

        if (nonMonotonic)
        {
            _nonMonotonicReason ??= reason;
        }
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

    /// <summary>
    /// Số nhánh tìm kiếm đã dựng. Một nhánh là một <b>dấu vân tay phép biến đổi</b>, không
    /// phải một "tên nhánh" — xem <see cref="TransformFingerprint"/>.
    /// </summary>
    public required int BranchesTotal { get; init; }

    /// <summary>
    /// Số nhánh có số đo <b>trái chiều</b> với giả định đơn điệu, tức mã
    /// <see cref="SearchDecisionReasons.NonMonotonicBranchObserved"/>.
    /// </summary>
    /// <remarks>
    /// Không phải lỗi: những nhánh đó đã dò tuyến tính và không bị cắt theo giả định.
    /// Nhưng đây là thứ duy nhất cho biết phép đo VMAF có đang ra thứ ngoài dự kiến hay
    /// không, nên phải đếm được chứ không chỉ ghi trong log tiếng Việt.
    /// </remarks>
    public required int BranchesNonMonotonic { get; init; }

    /// <summary>Nhánh nào bị đánh dấu, theo thứ tự phát hiện.</summary>
    public required IReadOnlyList<string> NonMonotonicBranches { get; init; }

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

    /// <summary>
    /// Kết luận so bản gốc với ứng viên encode, nếu đã so.
    /// </summary>
    /// <remarks>
    /// Giữ lại để người đọc báo cáo thấy <i>vì sao</i> bản gốc thắng hoặc thua — kể cả khi
    /// thắng, vì đó là thông tin thú vị nhất của một lần nén mà không nén.
    /// </remarks>
    public OriginalComparison? OriginalComparison { get; init; }

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

    /// <summary>
    /// Ngưỡng tiết kiệm tối thiểu của cấu hình, phần trăm — <b>cùng</b> con số mà lưới 5A
    /// dùng khi kiểm tra tệp đầu ra.
    /// </summary>
    /// <remarks>
    /// Truyền vào đây để so bản gốc với ứng viên encode bằng <b>đúng</b> tiêu chuẩn người
    /// dùng đã đặt, thay vì một ngưỡng "hợp lý" riêng do lớp này tự chế ra. Hai ngưỡng cùng
    /// nghĩa mà lệch nhau là cách rất tinh vi để bỏ qua cấu hình mà không ai nhận ra.
    /// </remarks>
    public required double MinSavingPercent { get; init; }

    /// <summary>
    /// Mức độ tin cậy của phép đo trên tệp này.
    /// </summary>
    /// <remarks>
    /// Khi <see cref="MeasurementConfidence.Uncertain"/>, tìm kiếm vẫn chọn ứng viên và encode
    /// như bình thường, nhưng <b>không</b> kết luận giữ bản gốc từ số đo đáng ngờ — vì một
    /// số đo sai vì lý do không liên quan tới nén thì không phải bằng chứng cho việc
    /// "không đáng nén". Xem <see cref="OriginalComparison"/>.
    /// </remarks>
    public MeasurementConfidence Confidence { get; init; } = MeasurementConfidence.Trusted;
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
    /// Biên "vượt nhiều" của phương án <b>dừng sớm theo nhánh</b>: điểm đầu phải qua ngưỡng mean
    /// lẫn P5 với dư ít nhất bấy nhiêu điểm VMAF.
    /// </summary>
    /// <remarks>
    /// <para><b>ĐÃ BỎ KHỎI ĐƯỜNG CHẠY — giữ lại để ghi nhận, không dùng để quyết định.</b> Lý do
    /// không phải kỹ thuật mà là ngữ nghĩa: điểm đầu là điểm <b>chất lượng cao nhất, tệp
    /// lớn nhất</b> của nhánh. Mọi điểm chưa đo còn lại đều chất lượng thấp hơn và tệp
    /// <b>nhỏ hơn</b>. Vì bộ chọn ưu tiên tệp nhỏ nhất, bỏ qua chúng đồng nghĩa với việc bỏ qua
    /// ứng viên có thể thắng — dừng sớm làm ta chọn ra tệp <b>lớn hơn</b>, và không có cách nào
    /// thu hẹp điều kiện bật/tắt để sửa, vì ứng viên bị bỏ qua luôn là ứng viên nhỏ hơn.</para>
    ///
    /// <para>Vì vậy nó <b>không phải</b> heuristic tối ưu tính toán trung tính với kết quả: nó
    /// đánh đổi chất lượng lựa chọn lấy thời gian. Muốn có heuristic như vậy thì phải đổi chính
    /// sách chọn ứng viên sang ưu tiên chất lượng trước dung lượng — một quyết định khác hẳn,
    /// và không thuộc phạm vi giai đoạn này.</para>
    ///
    /// <para>Giá trị <b>chưa được hiệu chỉnh</b> và không có quyền quyết định nào: nó là
    /// giới hạn chi phí, không phải ngưỡng chất lượng. Việc chấp nhận chất lượng vẫn thuộc
    /// hoàn toàn về <see cref="QualityPolicy"/>.</para>
    /// </remarks>
    public const double EarlyStopMargin = 3.0;

    /// <summary>
    /// Điểm đầu có vượt ngưỡng "nhiều" không: ngay cả đoạn tệ nhất cũng qua cả hai ngưỡng
    /// với dư ít nhất <see cref="EarlyStopMargin"/>.
    /// </summary>
    /// <remarks>
    /// Giữ lại cùng lý do với <see cref="EarlyStopMargin"/>: dùng để <b>kiểm chứng</b> rằng
    /// phương án dừng sớm đã bị bỏ có đúng tiêu chí không, chứ không phải để bật lại.
    /// </remarks>
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
        var nonMonotonic = new List<BranchSearch>();
        var encodeAttempts = 0;

        // Nhánh = NHÓM THEO DẤU VÂN TAY PHÉP BIẾN ĐỔI, không theo tên. Xem
        // `TransformFingerprint`: nhị phân chỉ hợp lệ khi codec, kích thước, FPS, định
        // dạng pixel, họ điều khiển tốc độ và chuỗi bộ lọc cùng cố định. Nhóm theo
        // `BranchId` ("h264/1280x720") là một thoả thuận miệng không có gì chặn vi phạm.
        var branches = request.Candidates
            .GroupBy(
                c => TransformFingerprint.Of(c, request.SourceWidth, request.SourceHeight).Value,
                StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BranchSearch([.. g], floor))
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

                if (branch.NonMonotonicObserved)
                {
                    nonMonotonic.Add(branch);
                }
            }
        }
        finally
        {
            references.Release(referenceClips);
        }

        watch.Stop();

        // Số liệu về nhánh. Gom vào một kiểu riêng thay vì thêm tham số vào `Build` — hàm
        // đó đã dài, và đây là nhóm thông tin luôn đi cùng nhau.
        var diagnostics = SearchDiagnostics.From(branches, nonMonotonic);

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
                pruned, failed, diagnostics,
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
                pruned, failed, diagnostics,
                SearchStatus.NoFeasibleCandidate,
                new SearchOutcome(
                    SearchDecisionReasons.PilotAllCandidatesRejected,
                    (feasibleCount == 0
                        ? $"không ứng viên nào đạt chất lượng ({evaluated.Count}/{request.Candidates.Count} đã thử) — giữ bản gốc"
                        : $"{feasibleCount} ứng viên đạt chất lượng nhưng ước lượng không đủ tin để chọn")
                    + diagnostics.NonMonotonicNote));
        }

        // Bản gốc là một ứng viên ngang hàng, nên nó được so ở ĐÂY — sau khi đã đo, không phải
        // trước. Metadata của nguồn không được động vào quyết định này (xem `OriginalComparison`).
        var comparison = OriginalComparison.Decide(
            request.SourceSizeBytes,
            request.MinSavingPercent,
            [.. evaluated.Where(e => e.IsFeasible)],
            request.Confidence);

        if (comparison.Decision == OriginalDecision.KeepOriginal)
        {
            return Build(request, watch, encodeWatch, measureWatch, evaluated, pareto, encodeAttempts,
                pruned, failed, diagnostics,
                SearchStatus.OriginalSelected,
                new SearchOutcome(comparison.Reason, comparison.Message + diagnostics.NonMonotonicNote)) with
            {
                Selected = null,
                OriginalComparison = comparison,
            };
        }

        return Build(request, watch, encodeWatch, measureWatch, evaluated, pareto, encodeAttempts,
            pruned, failed, diagnostics,
            SearchStatus.SelectedCandidate,
            new SearchOutcome(SearchDecisionReasons.PilotSelected,
                $"chọn {selected.CandidateId}: chất lượng {selected.Quality.ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"ước lượng {selected.EstimatedBytes} B"
                + diagnostics.NonMonotonicNote)) with
        {
            Selected = evaluated.First(e => string.Equals(e.Candidate.Id, selected.CandidateId, StringComparison.Ordinal)),
            OriginalComparison = comparison,
        };
    }

    /// <summary>
    /// Số liệu về các nhánh tìm kiếm, gom lại để không phải thêm tham số vào <c>Build</c>.
    /// </summary>
    /// <remarks>
    /// Riêng phần "phi đơn điệu" thì không phải chi tiết trang trí: đó là tín hiệu đúng nhất
    /// cho biết phép đo VMAF có đang cho ra thứ mà thuật toán không lường trước được. Một
    /// nhánh bị đánh dấu này vẫn cho kết quả hợp lệ (ta chỉ dò tuyến tính, không cắt), nhưng
    /// người đọc nhật ký cần biết nó xảy ra ở tỉ lệ nào.
    /// </remarks>
    private readonly record struct SearchDiagnostics(
        int BranchCount,
        int NonMonotonicCount,
        IReadOnlyList<string> NonMonotonicBranches,
        string NonMonotonicNote)
    {
        public static SearchDiagnostics None { get; } = new(0, 0, [], string.Empty);

        public static SearchDiagnostics From(
            List<BranchSearch> branches, List<BranchSearch> nonMonotonic)
        {
            if (nonMonotonic.Count == 0)
            {
                return new SearchDiagnostics(branches.Count, 0, [], string.Empty);
            }

            var detail = string.Join(
                "; ", nonMonotonic.Select(b => $"{b.BranchId}: {b.NonMonotonicReason}"));

            return new SearchDiagnostics(
                branches.Count,
                nonMonotonic.Count,
                [.. nonMonotonic.Select(b => b.BranchId)],
                $" — cảnh báo {SearchDecisionReasons.NonMonotonicBranchObserved}: "
                    + $"{nonMonotonic.Count}/{branches.Count} nhánh có số đo trái chiều với giả định đơn điệu "
                    + $"({detail}); những nhánh đó đã chuyển sang dò tuyến tính và không bị cắt theo giả định nữa");
        }
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
        SearchDiagnostics diagnostics,
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
                BranchesTotal = diagnostics.BranchCount,
                BranchesNonMonotonic = diagnostics.NonMonotonicCount,
                NonMonotonicBranches = diagnostics.NonMonotonicBranches,
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
            ParetoSelector.Select(ToScored(evaluated)), evaluated.Count, [], [],
            SearchDiagnostics.None, status, outcome);
    }
}
