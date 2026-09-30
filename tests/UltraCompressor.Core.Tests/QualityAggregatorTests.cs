using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Ngữ nghĩa của <see cref="QualityAggregator"/>.
///
/// <para>Đây là nơi quyết định ứng viên nào được phép đi tiếp, nên test tập trung vào câu hỏi
/// "chuyện gì xảy ra khi một đoạn rớt" chứ không phải "hàm trả về đúng kiểu gì".</para>
/// </summary>
public class QualityAggregatorTests
{
    private static QualityFloor Balanced => QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Default);

    /// <summary>Hai giá trị P5 thật của hai đoạn; trung bình của chúng không thuộc về đoạn nào.</summary>
    private static readonly double[] ExpectedP5s = [91.0, 86.0];

    private static WindowMeasurement M(WindowRole role, double start, double mean, double p5) =>
        new(role, start, 3.0, new QualitySample(Mean: mean, P5: p5, Min: p5 - 20, SsimMean: 0.997, Frames: 72));

    private static WindowMeasurement Missing(WindowRole role, double start) => new(role, start, 3.0, null);

    [Fact]
    public void Moi_doan_dat_nguong_thi_kha_thi()
    {
        // Ngưỡng Balanced = mean 89 / P5 85.
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.HighMotion, 6, 95, 91),
            M(WindowRole.HighSpatial, 65, 92, 88),
            M(WindowRole.Typical, 104, 90, 87),
        ], Balanced);

        Assert.True(result.IsFeasible);
        Assert.Equal(3, result.MeasuredCount);
        Assert.Equal(3, result.RequiredCount);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Mot_doan_giua_rot_nguong_thi_ca_ung_vien_bi_lo()
    {
        // A đạt, B rớt, C đạt. Trung bình của cả ba là 92,3 — đẹp. Nhưng B là cảnh khó,
        // và người dùng sẽ thấy đúng cảnh đó bị hỏng. Không được cho qua.
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.HighMotion, 6, 94, 91),
            M(WindowRole.Typical, 65, 88, 84),
            M(WindowRole.HighSpatial, 104, 95, 92),
        ], Balanced);

        Assert.False(result.IsFeasible);
        Assert.Equal(SearchDecisionReasons.PilotWindowQualityFailed, result.Outcome.Reason);
        Assert.Equal(WindowRole.Typical, result.FailingWindow!.Role);
    }

    [Fact]
    public void Doan_de_khong_bu_duoc_doan_kho()
    {
        // Cùng dữ liệu, chỉ đổi thứ tự. Kết luận phải y hệt — thứ tự đoạn không được đổi
        // được kết quả, vì các đoạn chạy song song.
        var windows = new[]
        {
            M(WindowRole.HighMotion, 6, 95, 91),
            M(WindowRole.Typical, 65, 88, 84),
            M(WindowRole.HighSpatial, 104, 95, 92),
        };

        foreach (var order in new[] { windows, windows.Reverse().ToArray() })
        {
            var result = QualityAggregator.Aggregate("c", order, Balanced);
            Assert.False(result.IsFeasible);
            Assert.Equal(WindowRole.Typical, result.FailingWindow!.Role);
        }
    }

    [Fact]
    public void P5_that_ben_duoc_tinh_theo_nguong_khong_phai_theo_mean()
    {
        // mean 95 nhưng P5 84: dưới ngưỡng P5 = 85. Đây là lý do có P5: trung bình đẹp
        // nhưng đuôi chất lượng rớt.
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.Typical, 6, 95, 84),
        ], Balanced);

        Assert.False(result.IsFeasible);
        Assert.Equal(95, result.FailingWindow!.Sample!.Mean);
    }

    [Fact]
    public void Khong_do_duoc_thi_khong_phai_dat()
    {
        // Đây là khác biệt lớn nhất so với lưới chất lượng cuối. Lưới cuối cố tình
        // fail-open; ở đây không được, vì ứng viên chưa đo thì chưa biết nó an toàn hay không.
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.HighMotion, 6, 95, 91),
            Missing(WindowRole.Typical, 65),
            M(WindowRole.HighSpatial, 104, 95, 92),
        ], Balanced);

        Assert.False(result.IsFeasible);
        Assert.Equal(SearchDecisionReasons.PilotMeasurementUnavailable, result.Outcome.Reason);
        Assert.Single(result.Missing);
        Assert.Contains("Typical", result.Missing[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Tat_ca_doan_deu_khong_do_duoc_thi_van_khong_phai_dat()
    {
        var result = QualityAggregator.Aggregate("c", [
            Missing(WindowRole.HighMotion, 6),
            Missing(WindowRole.Typical, 65),
        ], Balanced);

        Assert.False(result.IsFeasible);
        Assert.Equal(SearchDecisionReasons.PilotMeasurementUnavailable, result.Outcome.Reason);
        Assert.Equal(2, result.Missing.Count);
    }

    [Fact]
    public void Khong_co_doan_nao_thi_bao_khong_co_ung_vien()
    {
        var result = QualityAggregator.Aggregate("c", [], Balanced);

        Assert.False(result.IsFeasible);
        Assert.Equal(SearchDecisionReasons.PilotNoCandidates, result.Outcome.Reason);
    }

    [Fact]
    public void Ssim_khong_cuu_duoc_ung_vien_rot_nguong()
    {
        // Đo được trường hợp VMAF ~72 trong khi SSIM vẫn ~0,9887: SSIM cao không chứng minh
        // được gì về chất lượng cảm nhận. Nên SSIM thấp hay cao đều không đổi kết luận.
        var highSsim = new QualitySample(Mean: 80, P5: 76, Min: 40, SsimMean: 0.9999, Frames: 72);
        var lowSsim = new QualitySample(Mean: 80, P5: 76, Min: 40, SsimMean: 0.9000, Frames: 72);

        foreach (var sample in new[] { highSsim, lowSsim })
        {
            var result = QualityAggregator.Aggregate("c", [
                new WindowMeasurement(WindowRole.Typical, 6, 3.0, sample),
            ], Balanced);

            Assert.False(result.IsFeasible);
            Assert.Equal(SearchDecisionReasons.PilotWindowQualityFailed, result.Outcome.Reason);
        }
    }

    [Fact]
    public void Doan_te_nhat_duoc_giu_dung_theo_mean_thap_nhat()
    {
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.HighMotion, 6, 95, 91),
            M(WindowRole.Typical, 65, 90, 88),
            M(WindowRole.HighSpatial, 104, 93, 90),
        ], Balanced);

        Assert.Equal(WindowRole.Typical, result.WorstWindow!.Role);
        Assert.Equal(90, result.WorstWindow.Sample!.Mean);
    }

    [Fact]
    public void Diem_xep_hang_theo_doan_te_nhat_khong_phai_trung_binh()
    {
        // Nếu chấm theo trung bình thì ứng viên này (92,3) có vẻ ngang ứng viên kia dù
        // chúng khác nhau đúng ở chỗ quyết định đã dùng để loại.
        var good = QualityAggregator.Aggregate("a", [
            M(WindowRole.Typical, 6, 92, 89),
        ], Balanced);

        var uneven = QualityAggregator.Aggregate("b", [
            M(WindowRole.HighMotion, 6, 95, 92),
            M(WindowRole.Typical, 65, 90, 87),
        ], Balanced);

        Assert.Equal(92, QualityAggregator.RankingQuality(good));
        Assert.Equal(90, QualityAggregator.RankingQuality(uneven));
        Assert.True(
            QualityAggregator.RankingQuality(good) > QualityAggregator.RankingQuality(uneven),
            "ung vien deu dat phai cham theo doan te nhat cua chinh no");
    }

    [Fact]
    public void Nguong_dung_theo_mode()
    {
        // Cùng một số đo: đạt với Mạnh, không đạt với Nhẹ. Ngưỡng phải đến từ QualityPolicy
        // chứ không gộp cứng trong bộ gộp.
        var sample = M(WindowRole.Typical, 6, 86, 82);

        Assert.False(QualityAggregator.Aggregate("c", [sample],
            QualityPolicy.For(CompressionLevel.Light, VmafModels.Default)).IsFeasible);

        Assert.True(QualityAggregator.Aggregate("c", [sample],
            QualityPolicy.For(CompressionLevel.Strong, VmafModels.Default)).IsFeasible);
    }

    [Fact]
    public void Khong_he_giua_p5_cua_cac_doan()
    {
        // Hai đoạn: P5 lần lượt 91 và 86. Trung bình là 88,5 — con số không thuộc về đoạn
        // nào và không có nghĩa gì. Bộ gộp phải giữ P5 của một đoạn cụ thể.
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.HighMotion, 6, 95, 91),
            M(WindowRole.Typical, 65, 90, 86),
        ], Balanced);

        // Đoạn tệ nhất chọn theo mean thấp nhất, tức `Typical` (90), không phải đoạn có
        // P5 cao nhất. Nhất quán với việc quyết định khả thi cũng theo từng đoạn.
        Assert.Equal(WindowRole.Typical, result.WorstWindow!.Role);
        Assert.Equal(86, result.WorstWindow.Sample!.P5);
        Assert.Equal(86, QualityAggregator.RankingP5(result));

        // 88,5 là con số của cách tính bị cấm; không được xuất hiện ở bất kỳ đâu.
        Assert.NotEqual(88.5, result.WorstWindow.Sample.P5);
        Assert.NotEqual(88.5, QualityAggregator.RankingP5(result));
        Assert.All(result.Measurements, m => Assert.Contains(m.Sample!.P5, ExpectedP5s));
    }

    [Fact]
    public void Doi_loi_va_giua_noi_dung_dung_nguong_da_dung()
    {
        var result = QualityAggregator.Aggregate("c", [
            M(WindowRole.Typical, 6, 90, 87),
        ], Balanced);

        Assert.Contains("89", result.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("85", result.Outcome.Message, StringComparison.Ordinal);
    }
}
