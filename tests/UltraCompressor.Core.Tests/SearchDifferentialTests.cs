using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// So <c>BranchSearch</c> (nhị phân, có cắt) với một bộ đánh giá <b>tham chiếu</b> đo hết
/// mọi điểm. Mục tiêu không phải "ít encode hơn" — mà là: tối ưu hoá được phép bỏ bớt phép
/// đo, nhưng <b>không được</b> bỏ một phép đo mà kết luận có thể phụ thuộc vào nó.
/// </summary>
/// <remarks>
/// <para>Vì sao phải so với bộ tham chiếu: nhị phân có một giả định (chất lượng đơn điệu
/// theo <c>PointIndex</c>) và một loạt lệnh cắt dựa trên đó. Khi giả định đúng, mọi thứ
/// khớp; khi giả định sai, mọi thứ có thể sai <b>âm thầm</b> — không ném lỗi, chỉ chọn
/// nhầm. Test tham chiếu là thứ duy nhất bắt được loại sai lệch đó.</para>
///
/// <para>Trong một nhánh, chỉ số CAO hơn nghĩa là chất lượng THẤP hơn và tệp NHỎ hơn, nên ứng
/// viên nhỏ nhất còn đạt là chỉ số khả thi <b>lớn nhất</b>. Đó là quy tắc mà cả hai bên cùng
/// dùng, để chúng chỉ khác nhau ở chỗ có cắt bỏ phép đo hay không.</para>
///
/// <para>Ước lượng dung lượng cố tình <b>không</b> tham gia vào test này: nó chỉ xếp hạng
/// sau khi đã có phép đo, còn thứ đang bị kiểm tra ở đây là <i>những phép đo nào được bỏ</i>.
/// Xếp hạng sai thì ảnh hưởng thứ tự ưu tiên, không ảnh hưởng việc một ứng viên đạt có bị bỏ
/// sót hay không — và bỏ sót mới là lỗi không thể hoàn tác.</para>
/// </remarks>
public class SearchDifferentialTests
{
    private static QualityFloor Floor => QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Neg);

    [Theory]
    // Đơn điệu: chất lượng giảm dần theo chỉ số, đi qua ngưỡng ở giữa.
    [InlineData(new[] { 95.0, 92.0, 89.0, 86.0, 80.0 })]
    // Sát ngưỡng: biên nằm giữa hai điểm liền kề.
    [InlineData(new[] { 91.0, 90.5, 89.5, 88.5, 88.0 })]
    // Tất cả đạt.
    [InlineData(new[] { 95.0, 94.0, 93.0, 92.0, 91.0 })]
    // Tất cả rớt.
    [InlineData(new[] { 85.0, 84.0, 83.0, 82.0, 81.0 })]
    // Chỉ điểm giữa đạt — trường hợp bản cắt-một-phép-đo bỏ sót.
    [InlineData(new[] { 85.0, 85.5, 91.0, 85.5, 85.0 })]
    // Biên nằm ngay ở bên kia đầu.
    [InlineData(new[] { 95.0, 95.0, 94.0, 93.0, 92.0 })]
    // Rộng, nhiều điểm.
    [InlineData(new[] { 97.0, 96.0, 95.0, 94.0, 93.0, 92.0, 91.0, 90.0, 89.0, 88.0 })]
    public void Ket_qua_khop_bo_tham_chieu(double[] landscape)
    {
        var points = Points(landscape.Length);

        Assert.Equal(ExhaustiveWinner(landscape, Floor), OptimizedWinner(points, landscape, Floor, out _));
    }

    [Fact]
    public void Toi_uu_hoa_duoc_phep_do_it_hon()
    {
        // Điều kiện để việc tối ưu hoá có đáng làm: trên một nhánh đơn điệu, nhị phân không
        // bao giờ phải đo hết. Nếu đo hết thì việc cắt chỉ thêm rủi ro chứ không tiết kiệm gì.
        var landscape = new[] { 95.0, 92.0, 89.0, 86.0, 80.0, 75.0, 70.0 };
        var points = Points(landscape.Length);

        OptimizedWinner(points, landscape, Floor, out var measured);

        Assert.True(measured < landscape.Length, $"do {measured}/{landscape.Length} diem — khong tiet kiem gi");
    }

    [Theory]
    [InlineData(new[] { 85.0, 85.5, 91.0, 85.5, 85.0 })]
    [InlineData(new[] { 80.0, 88.0, 80.0, 88.0, 80.0 })]
    [InlineData(new[] { 80.0, 91.0, 82.0, 91.5, 80.0 })]
    [InlineData(new[] { 91.0, 80.0, 91.0, 80.0, 91.0 })]
    public void Phong_canh_khong_don_dieu_khong_duoc_tra_ket_luan_rot(double[] landscape)
    {
        // Giả định sai thì hệ quả được phép là chọn nhầm; điều KHÔNG được phép là kết luận
        // sai theo hướng nguy hiểm — trả về một ứng viên rớt ngưỡng.
        var points = Points(landscape.Length);
        var winner = OptimizedWinner(points, landscape, Floor, out _);

        if (winner is { } chosen)
        {
            Assert.True(
                Accepts(landscape[chosen], Floor),
                $"ung vien p{chosen} rot nguong nhung lai duoc chon: mean={landscape[chosen]}");

            // Nếu còn ứng viên khả thi nào bị bỏ sót, sự kiện bất đơn điệu phải được đánh dấu.
            // Bỏ sót mà không báo là im lặng giấu lỗi.
            var missedHigher = Enumerable.Range(chosen + 1, landscape.Length - chosen - 1)
                .Any(i => Accepts(landscape[i], Floor));

            if (missedHigher)
            {
                Assert.True(
                    NonMonotonicDetected(points, landscape, Floor),
                    "bo so ung vien dat o chi soc cao hon nhung khong danh dau NON_MONOTONIC_BRANCH_OBSERVED");
            }
        }
    }

    [Fact]
    public void Chi_diem_giua_dat_thi_phai_tim_duoc()
    {
        // Bỏ sót ứng viên ở giữa là lỗi thật, không phải chấp nhận sai số phép đo. Điểm đầu
        // rớt nhưng điểm cuối đạt ⇒ bằng chứng trái chiều ⇒ phải dò tiếp thay vì cắt.
        var landscape = new[] { 85.0, 86.0, 91.0, 86.0, 85.0 };
        var points = Points(landscape.Length);

        Assert.Equal(2, ExhaustiveWinner(landscape, Floor));
        Assert.Equal(2, OptimizedWinner(points, landscape, Floor, out _));
    }

    [Fact]
    public void Phat_hien_khong_don_dieu_phai_ghi_ma_va_thong_ke()
    {
        var landscape = new[] { 85.0, 85.5, 91.0, 85.5, 85.0 };
        var points = Points(landscape.Length);

        Assert.True(NonMonotonicDetected(points, landscape, Floor));
    }

    [Fact]
    public void Phong_canh_don_dieu_khong_bi_ghi_nham()
    {
        // Mã chẩn đoán mất ý nghĩa nếu bắn cả khi không có gì bất thường — lúc đó không ai
        // đếm nổi và cảnh báo trở thành tiếng ồn.
        var landscape = new[] { 95.0, 92.0, 89.0, 86.0, 80.0 };
        var points = Points(landscape.Length);

        Assert.False(NonMonotonicDetected(points, landscape, Floor));
    }

    // ---------------------------------------------------------------- bộ đánh giá tham chiếu

    /// <summary>
    /// Bộ tham chiếu: đo <b>mọi</b> điểm rồi chọn, không cắt gì cả. Chậm và tốn kém, nhưng
    /// không dựa vào bất kỳ giả định nào — nên nó là chuẩn để so.
    /// </summary>
    private static int? ExhaustiveWinner(double[] landscape, QualityFloor floor)
    {
        var feasible = Enumerable.Range(0, landscape.Length)
            .Where(i => Accepts(landscape[i], floor))
            .ToList();

        return feasible.Count == 0 ? null : feasible.Max();
    }

    // ---------------------------------------------------------------- bộ đánh giá tối ưu

    private static int? OptimizedWinner(
        List<VideoEncodeCandidate> points, double[] landscape, QualityFloor floor, out int measuredCount)
    {
        var search = new BranchSearch(points, floor);
        var measured = new List<int>();

        while (!search.IsClosed)
        {
            var (candidate, _) = search.Next();
            if (candidate is null)
            {
                break;
            }

            var index = IndexOf(points, candidate.Id);
            measured.Add(index);
            search.Observe(Evaluate(points[index], landscape[index], floor));
        }

        measuredCount = measured.Count;

        var feasible = measured.Where(i => Accepts(landscape[i], floor)).ToList();

        // Cùng quy tắc chọn thật: chỉ số cao hơn ⇒ tệp nhỏ hơn ⇒ ưu tiên chỉ số khả thi cao
        // nhất.
        return feasible.Count == 0 ? null : feasible.Max();
    }

    private static bool NonMonotonicDetected(
        List<VideoEncodeCandidate> points, double[] landscape, QualityFloor floor)
    {
        var search = new BranchSearch(points, floor);

        while (!search.IsClosed)
        {
            var (candidate, _) = search.Next();
            if (candidate is null)
            {
                break;
            }

            var index = IndexOf(points, candidate.Id);
            search.Observe(Evaluate(points[index], landscape[index], floor));
        }

        return search.NonMonotonicObserved;
    }

    // ---------------------------------------------------------------- dữ liệu dùng chung

    private static bool Accepts(double mean, QualityFloor floor) =>
        mean >= floor.VmafMean && mean - 3 >= floor.VmafP5;

    private static int IndexOf(List<VideoEncodeCandidate> points, string id)
    {
        var index = points.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        Assert.True(index >= 0, $"khong tim thay ung vien {id}");
        return index;
    }

    private static EvaluatedCandidate Evaluate(
        VideoEncodeCandidate candidate, double mean, QualityFloor floor)
    {
        var sample = new QualitySample(mean, mean - 3, mean - 20, 0.999, 72);
        var measurement = new WindowMeasurement(WindowRole.Typical, 0, 3, sample);

        return new EvaluatedCandidate
        {
            Candidate = candidate,
            Aggregate = QualityAggregator.Aggregate(candidate.Id, [measurement], floor),
            Estimate = new SizeEstimate
            {
                TotalBytes = 1_000_000,
                VideoBytes = 900_000,
                AudioBytes = 100_000,
                ContainerBytes = 0,
                DurationSeconds = 300,
                IsReliable = true,
                Assumptions = [],
            },
            Measurements = [measurement],
            MeasurementsTaken = 1,
            MeasurementsSkipped = 0,
            WindowsNotMeasured = 0,
            ComputeCostSeconds = 1,
            Stage = SearchStage.Bracket,
        };
    }

    private static List<VideoEncodeCandidate> Points(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new VideoEncodeCandidate($"b/p{i}")
        {
            Codec = VideoCodec.H264,
            EncoderName = "libx264",
            Quality = QualityOption.X26xCrf(16 + i * 4),
            Width = 1280,
            Height = 720,
            Fps = 24,
            Speed = SpeedOption.X26xPreset("medium"),
            PixelFormat = "yuv420p",
            Origin = i == 0 ? CandidateOrigin.CoarseProbe : CandidateOrigin.QualityAnchor,
            BranchId = "b",
            PointIndex = i,
            PointCount = count,
            Reason = "kiểm thử vi sai",
        })];
}
