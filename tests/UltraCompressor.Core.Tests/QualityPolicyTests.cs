using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Cổng chất lượng. Đa số test ở đây bảo vệ những chỗ sai <b>âm thầm</b>: đo sai thứ tự,
/// chọn nhầm bảng ngưỡng theo model, hoặc tin SSIM. Cả ba đều không ném lỗi — chỉ cho
/// kết quả sai, nên test phải chặn trước khi chúng lọt vào bản phát hành.
/// </summary>
public class QualityPolicyTests
{
    private static QualitySample Sample(double mean, double p5, double ssim = 0.999)
        => new(mean, p5, Min: 0, ssim, Frames: 100);

    [Fact]
    public void Dung_bang_nguong_v0_khi_dung_model_v0()
    {
        var floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Neg);

        // Bảng v0 thấp hơn bảng v1 có chủ đích: v0.6.1neg cho điểm thấp hơn ở cùng mức
        // chất lượng, nên dùng số của v1 sẽ siết nén quá tay.
        Assert.Equal(89.0, floor.VmafMean);
        Assert.Equal(85.0, floor.VmafP5);
    }

    [Fact]
    public void Doi_model_sang_v1_thi_dung_bang_v1()
    {
        var fakeV1 = new VmafModel("vmaf_v1.0.16_3d0h", "VMAF v1", 0, 100, "test");
        var floor = QualityPolicy.For(CompressionLevel.Balanced, fakeV1);

        Assert.Equal(92.0, floor.VmafMean);
        Assert.Equal(86.0, floor.VmafP5);
    }

    [Theory]
    [InlineData(CompressionLevel.Light, 93.0, 89.0)]
    [InlineData(CompressionLevel.Balanced, 89.0, 85.0)]
    [InlineData(CompressionLevel.Strong, 84.0, 80.0)]
    public void Moi_mode_co_nguong_rieng(CompressionLevel level, double mean, double p5)
    {
        var floor = QualityPolicy.For(level, VmafModels.Neg);
        Assert.Equal(mean, floor.VmafMean);
        Assert.Equal(p5, floor.VmafP5);
    }

    [Fact]
    public void Mode_nhe_vao_hon_vao_vao_manh()
    {
        double Mean(CompressionLevel level) => QualityPolicy.For(level, VmafModels.Neg).VmafMean;

        Assert.True(Mean(CompressionLevel.Light) > Mean(CompressionLevel.Balanced));
        Assert.True(Mean(CompressionLevel.Balanced) > Mean(CompressionLevel.Strong));
    }

    [Fact]
    public void Khoang_cach_P5_luon_nho_hon_mean()
    {
        foreach (var level in new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong })
        {
            var floor = QualityPolicy.For(level, VmafModels.Neg);
            Assert.InRange(floor.P5Gap, 3.0, 5.0);
        }
    }

    [Fact]
    public void Mean_dat_nhung_P5_ruot_thi_va_lo()
    {
        var floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Neg);

        // 90,0 mean vượt ngưỡng 89, nhưng P5 80 thì không. Đây là lý do P5 phải có:
        // vài cảnh hỏng bị che bởi phần lớn khung đẹp.
        Assert.False(floor.Accepts(Sample(mean: 90.0, p5: 80.0)));
    }

    [Fact]
    public void Dat_ca_hai_nguong_thi_qua()
    {
        var floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Neg);
        Assert.True(floor.Accepts(Sample(mean: 91.0, p5: 87.0)));
    }

    [Fact]
    public void Ssim_cao_khong_cu_duoc_ung_vien_hong()
    {
        var floor = QualityPolicy.For(CompressionLevel.Strong, VmafModels.Neg);

        // Số liệu thật trên clip của người dùng: VMAF 72,08 / SSIM 0,9887.
        // Nếu SSIM làm cổng thì ứng viên này lọt. Phải loại.
        Assert.False(floor.Accepts(Sample(mean: 72.08, p5: 65.47, ssim: 0.9887)));
    }

    [Fact]
    public void Model_mac_dinh_la_bien_the_neg()
    {
        // Bản neg dành cho tối ưu encoder; dùng bản gốc thì nén quá tay mà vẫn trông "đạt".
        Assert.Equal("vmaf_v0.6.1neg", VmafModels.Default.Id);
    }

    [Fact]
    public void Tim_model_khong_phan_biet_hoa_thuong()
    {
        Assert.Same(VmafModels.Neg, VmafModels.ById("VMAF_V0.6.1NEG"));
        Assert.Null(VmafModels.ById("khong-ton-tai"));
        Assert.Null(VmafModels.ById(null));
    }

    [Fact]
    public void Model_co_mien_diem_khai_bao()
    {
        // Một số model v1 chạy trên [0,110]. Ngưỡng viết cho [0,100] mà áp vào đó tương
        // đương nới lỏng mà không ai hề biết.
        Assert.Equal(0, VmafModels.Neg.MinScore);
        Assert.Equal(100, VmafModels.Neg.Ceiling);
    }
}

public class QualityLogTests
{
    [Fact]
    public void Khong_co_khung_VMAF_thi_khong_co_mau()
    {
        Assert.False(QualityLog.TryParse("""{"frames":[]}""", out _));
        Assert.False(QualityLog.TryParse("""{"frames":[{"frameNum":0}]}""", out _));
        Assert.False(QualityLog.TryParse("", out _));
    }

    [Fact]
    public void Log_khong_phai_JSON_thi_khong_no()
    {
        // Không có log_fmt=json thì libvmaf xuất XML cho tên đuôi .json. Phải bỏ qua chứ
        // không được ném lỗi làm hỏng cả job.
        Assert.False(QualityLog.TryParse("<VMAF version=\"e0d9b82d\"></VMAF>", out _));
    }

    [Fact]
    public void Tinh_dung_mean_P5_min()
    {
        Assert.True(QualityLog.TryParse(Log([("60", null), ("70", null), ("80", null), ("90", null), ("100", null)]), out var s));

        Assert.Equal(80, s.Mean, 3);
        Assert.Equal(60, s.Min, 3);
        Assert.Equal(5, s.Frames);
    }

    [Fact]
    public void P5_la_phan_vi_5_phan_tram()
    {
        // 5 khung đã sắp: chỉ số = floor(0.05 * 4) = 0, tức phần tử nhỏ nhất.
        Assert.True(QualityLog.TryParse(Log([("60", null), ("70", null), ("80", null), ("90", null), ("100", null)]), out var s));
        Assert.Equal(60, s.P5, 3);

        // 100 khung: chỉ số = floor(0.05 * 99) = 4, tức phần tử thứ 5.
        var many = Enumerable.Range(0, 100).Select(i => (Vmaf: (string?)(i + 1).ToString(CultureInfo.InvariantCulture), Ssim: (string?)null));
        Assert.True(QualityLog.TryParse(Log(many), out var s100));
        Assert.Equal(5, s100.P5, 3);
    }

    [Fact]
    public void Khong_co_SSIM_thi_phan_gi_tri_ve_ma_van_dung_duoc()
    {
        Assert.True(QualityLog.TryParse(Log([("80", null), ("90", null)]), out var s));
        Assert.Equal(85, s.Mean, 3);
        Assert.Equal(0, s.SsimMean);
    }

    [Fact]
    public void Co_SSIM_thi_tinh_ca_trung_binh()
    {
        Assert.True(QualityLog.TryParse(Log([("80", "0.99"), ("90", "0.98")]), out var s));
        Assert.Equal(0.985, s.SsimMean, 4);
    }

    [Fact]
    public void Khung_thieu_VMAF_thi_bang_0_khong_thanh_khong_do()
    {
        // Chỉ có SSIM, không có VMAF thì không có mẫu — dùng SSIM để quyết định chất
        // lượng đã bị loại vì lý do riêng.
        Assert.False(QualityLog.TryParse(Log([(null, "0.99"), (null, "0.98")]), out _));
    }

    // Chỉ một hàm nhận IEnumerable. Trước đây có thêm bản `params` và nó tự gọi lại
    // chính nó khi truyền mảng: mảng khớp cả hai bản, bản `params` thắng, gọi vô hạn,
    // đổ test host thành stack overflow.
    private static string Log(IEnumerable<(string? Vmaf, string? Ssim)> frames)
    {
        var parts = frames.Select(f =>
        {
            var metrics = new List<string>();
            if (f.Vmaf is { } v) metrics.Add($"\"vmaf\":{v}");
            if (f.Ssim is { } s) metrics.Add($"\"float_ssim\":{s}");
            return $"{{\"frameNum\":0,\"metrics\":{{{string.Join(",", metrics)}}}}}";
        });

        return $"{{\"frames\":[{string.Join(",", parts)}]}}";
    }
}
