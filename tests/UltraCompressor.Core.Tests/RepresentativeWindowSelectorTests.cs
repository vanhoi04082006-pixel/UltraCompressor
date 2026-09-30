using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Bộ chọn đoạn đại diện. Thuần toán, nên test bằng đặc trưng tổng hợp — không cần ffmpeg.
///
/// <para>Số trong fixture bám theo thang thật của ffmpeg: <c>YDIF</c> là chênh luma
// giữa hai khung liên tiếp trên thang 0–255, đo được 0,00 (cảnh tĩnh) tới 23,54 (cảnh
/// bận) trên thư viện người dùng. Fixture đặt số 0,05 thay vì ~6 sẽ khiến mọi thứ trông
/// "gần như tĩnh" và che mất đúng cái lỗi mà các test này sinh ra để bắt.</para>
/// </summary>
public class RepresentativeWindowSelectorTests
{
    private static AppConfig Config() => new()
    {
        TargetWindowCount = 3,
        MaxWindowCount = 5,
        WindowDurationSeconds = 3,
        MinWindowSeparationSeconds = 30,
        AnalysisSampleSeconds = 2,
    };

    private static ScanStats Stats() => new(8, 8, TimeSpan.FromSeconds(2), false);

    private static List<WindowFeatures> Samples(params (double spatial, double motion, double scene, double blur)[] rows)
    {
        var list = new List<WindowFeatures>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var (spatial, motion, scene, blur) = rows[i];
            list.Add(new WindowFeatures(
                StartSeconds: i * 10.0, SampleSeconds: 2.0,
                Spatial: spatial, Motion: motion, SceneScore: scene, Blur: blur,
                LumaAverage: 100, Frames: 8));
        }

        return list;
    }

    private static WindowSelection Select(List<WindowFeatures> samples, AppConfig? config = null, int minutes = 10)
        => RepresentativeWindowSelector.Select(samples, TimeSpan.FromMinutes(minutes), config ?? Config(), Stats());

    /// <summary>Sáu mẫu gần như tĩnh: chuyển động tuyệt đối dưới mốc 1,0.</summary>
    private static List<WindowFeatures> StaticSamples() => Samples(
        (0.50, 0.10, 0.0, 3.0), (0.52, 0.18, 0.0, 3.1), (0.49, 0.05, 0.0, 2.9),
        (0.51, 0.12, 0.0, 3.0), (0.50, 0.20, 0.0, 3.0), (0.53, 0.02, 0.0, 2.8));

    // ---------------------------------------------------------------- tất định

    [Fact]
    public void Chay_nhieu_lan_cho_cung_ket_qua()
    {
        var samples = MixedSamples();
        var first = Select(samples);

        for (var i = 0; i < 5; i++)
        {
            var again = Select(samples);
            Assert.Equal(
                first.Windows.Select(w => (w.Role, w.StartSeconds, w.OverallComplexity)),
                again.Windows.Select(w => (w.Role, w.StartSeconds, w.OverallComplexity)));
        }
    }

    [Fact]
    public void Diem_bang_nhau_thi_chon_mau_som_hon()
    {
        // ThenBy theo chỉ số mẫu ở mọi nhánh: nếu không, kết quả phụ thuộc thứ tự LINQ
        // trả về chứ không phụ thuộc dữ liệu.
        var selection = Select(Samples(
            (0.5, 5.0, 0.0, 3.0), (0.5, 5.0, 0.0, 3.0), (0.5, 5.0, 0.0, 3.0)));

        Assert.All(selection.Windows, w => Assert.Equal(0, w.SampleIndex));
    }

    // ---------------------------------------------------------------- tĩnh

    [Fact]
    public void Tep_gan_nhu_tinh_khong_bi_tao_doan_chuyen_dong()
    {
        // Các đoạn tĩnh thật của clip người dùng có YDIF 0,00–1,29. Trước khi có mốc
        // tuyệt đối, chuẩn hoá tương đối thấy "biến thiên toàn phạm vi" và vẫn sinh ra
        // một đoạn HIGH_MOTION cho tệp không hề có chuyển động.
        var selection = Select(StaticSamples());

        Assert.DoesNotContain(selection.Windows, w => w.Role == WindowRole.HighMotion);
        Assert.Contains(selection.Windows, w => w.Role == WindowRole.Typical);
    }

    [Fact]
    public void Bien_thien_manh_nhung_tuyet_doi_nho_van_khong_duoc_coi_la_chuyen_dong_cao()
    {
        // Cùng dải tương đối (0,02 → 0,20) nhưng nằm hoàn toàn dưới mốc. Đây đúng là cái
        // bẫy của chuẩn hoá min-max: khuếch đại nhiễu thành [0,1].
        var selection = Select(Samples(
            (0.50, 0.02, 0.0, 3.0), (0.50, 0.20, 0.0, 3.0), (0.50, 0.09, 0.0, 3.0),
            (0.50, 0.14, 0.0, 3.0), (0.50, 0.05, 0.0, 3.0), (0.50, 0.17, 0.0, 3.0)));

        Assert.DoesNotContain(selection.Windows, w => w.Role == WindowRole.HighMotion);
    }

    [Fact]
    public void Chuyen_dong_that_dung_moc_thi_duoc_coi()
    {
        // YDIF ~23 là cảnh bận của clip anime thật, vượt mốc 1,0.
        var selection = Select(Samples(
            (0.50, 8.0, 0.0, 4.0), (0.50, 8.5, 0.0, 4.0), (0.50, 7.8, 0.0, 4.0),
            (0.50, 23.0, 5.0, 4.0), (0.50, 8.2, 0.0, 4.0), (0.50, 7.5, 0.0, 4.0)));

        Assert.Contains(selection.Windows, w => w.Role == WindowRole.HighMotion);
    }

    [Fact]
    public void Tep_tinh_van_chon_duoc_vai_tro_dien_hinh()
    {
        var selection = Select(StaticSamples());

        Assert.NotEmpty(selection.Windows);
        Assert.All(selection.Windows, w => Assert.False(string.IsNullOrWhiteSpace(w.Reason)));
    }

    // ---------------------------------------------------------------- chuyển động / chi tiết

    [Fact]
    public void Tim_duoc_doan_chuyen_dong_cao()
    {
        // Cảnh cháy ở mẫu 3, cảnh nhiều chi tiết khác ở mẫu 5. Đặt cả hai cực đại ở
        // cùng một mẫu là bịa — nội dung thật không vậy.
        var selection = Select(Samples(
            (0.50, 8.0, 0.0, 4.0), (0.50, 8.5, 0.0, 4.0), (0.50, 7.8, 0.0, 4.0),
            (0.50, 23.0, 5.0, 4.0),
            (0.50, 8.2, 0.0, 4.0), (0.90, 7.5, 0.0, 0.5)));

        var motion = selection.Windows.SingleOrDefault(w => w.Role == WindowRole.HighMotion);

        Assert.NotNull(motion);
        Assert.Equal(3, motion!.SampleIndex);
    }

    [Fact]
    public void Khong_chi_lay_mot_diem_median()
    {
        // Nếu chỉ sắp theo một điểm tổng rồi lấy top-N, cả ba đoạn sẽ nằm trong vùng
        // dễ và cảnh khó sẽ không bao giờ được đo.
        var selection = Select(Samples(
            (0.20, 6.0, 0.0, 9.0), (0.20, 6.0, 0.0, 9.0), (0.20, 6.0, 0.0, 9.0),
            (0.30, 22.0, 8.0, 6.0),
            (0.95, 6.0, 0.5, 0.5),
            (0.20, 6.0, 0.0, 9.0), (0.20, 6.0, 0.0, 9.0)));

        Assert.Contains(selection.Windows, w => w.Role == WindowRole.HighSpatial);
        Assert.Contains(selection.Windows, w => w.Role == WindowRole.HighMotion);
    }

    // ---------------------------------------------------------------- nội dung trộn

    /// <summary>
    /// tĩnh → chuyển động → nhiều chi tiết → tĩnh.
    ///
    /// <para>Bảy mẫu cách nhau 10 giây nên trải trong 60 giây, và các test gọi nó đều khai
    /// báo thời lượng <b>1 phút</b>. Khai báo 10 phút trong khi mẫu chỉ nằm trong phút đầu
    /// là dữ liệu tự mâu thuẫn: bộ chọn dùng thời lượng để tính khoảng cách tối thiểu, nên
    /// nó sẽ hành xử đúng theo một tệp dài trong khi dữ liệu chỉ có của một tệp ngắn.</para>
    /// </summary>
    private static List<WindowFeatures> MixedSamples() => Samples(
        (0.20, 6.0, 0.0, 8.0), (0.20, 6.0, 0.0, 8.0),
        (0.25, 20.0, 4.0, 4.0), (0.25, 18.0, 3.0, 4.0),
        (0.95, 6.0, 0.5, 0.5),
        (0.20, 6.0, 0.0, 8.0), (0.20, 6.0, 0.0, 8.0));

    /// <summary>Thời lượng khớp với <see cref="MixedSamples"/>.</summary>
    private static WindowSelection SelectMixed(AppConfig? config = null) =>
        Select(MixedSamples(), config, minutes: 1);

    [Fact]
    public void Khoang_cach_toi_thieu_co_lai_theo_thoi_luong()
    {
        // Tệp 60 giây không thể chia ra ba đoạn cách nhau 30 giây. Mốc cấu hình phải tự co
        // để đủ số đoạn, thay vì âm thầm trả về ít hơn mà không kèm lý do.
        Assert.Equal(3, SelectMixed().Windows.Count);
    }

    [Fact]
    public void Noi_dung_tron_co_dua_dai_dien_khac_nhau()
    {
        var roles = SelectMixed().Windows.Select(w => w.Role).ToHashSet();

        Assert.Contains(WindowRole.Typical, roles);
        Assert.Contains(WindowRole.HighMotion, roles);
        Assert.Contains(WindowRole.HighSpatial, roles);
    }

    [Fact]
    public void Cac_doan_duoc_tach_ra_theo_thoi_gian()
    {
        var selection = SelectMixed();

        // Cấu hình cách nhau 30 giây, mẫu cách nhau 10 giây.
        for (var i = 0; i < selection.Windows.Count; i++)
        {
            for (var j = i + 1; j < selection.Windows.Count; j++)
            {
                var gap = Math.Abs(selection.Windows[i].StartSeconds - selection.Windows[j].StartSeconds);
                Assert.True(gap >= 15, $"hai doan {i} va {j} cach nhau {gap}s");
            }
        }
    }

    // ---------------------------------------------------------------- đổi cảnh

    [Fact]
    public void Khong_chon_nhieu_doan_quanh_cung_mot_diem_doi_canh()
    {
        // Ba mẫu liên tiếp đều đổi cảnh mạnh, cách nhau 10 giây: cùng một chỗ.
        var selection = Select(Samples(
            (0.60, 7.0, 9.0, 2.0),
            (0.60, 7.0, 9.0, 2.0),
            (0.60, 7.0, 9.0, 2.0),
            (0.30, 6.0, 0.0, 6.0),
            (0.30, 6.0, 0.0, 6.0),
            (0.30, 6.0, 0.0, 6.0)));

        for (var i = 0; i < selection.Windows.Count; i++)
        {
            for (var j = i + 1; j < selection.Windows.Count; j++)
            {
                Assert.NotEqual(selection.Windows[i].SampleIndex, selection.Windows[j].SampleIndex);
            }
        }
    }

    // ---------------------------------------------------------------- tệp ngắn và giới hạn

    [Fact]
    public void Tep_ngan_khong_co_start_am()
    {
        var selection = Select(Samples((0.5, 5.0, 0.0, 3.0), (0.6, 6.0, 1.0, 2.5)), minutes: 0);

        Assert.NotEmpty(selection.Windows);
        Assert.All(selection.Windows, w => Assert.True(w.StartSeconds >= 0, $"start = {w.StartSeconds}"));
    }

    [Fact]
    public void Doan_khong_duoc_vuot_ra_ngoai_tep()
    {
        var selection = Select(Samples((0.5, 5.0, 0.0, 3.0), (0.6, 6.0, 1.0, 2.5), (0.4, 5.0, 0.0, 4.0)), minutes: 1);

        Assert.All(selection.Windows, w => Assert.True(w.EndSeconds <= 60.5, $"end = {w.EndSeconds}"));
    }

    [Fact]
    public void So_doan_khong_vuot_qua_giu_toi_da_dat()
    {
        var config = Config();
        config.TargetWindowCount = 2;

        var selection = Select(Samples(
            (0.2, 6.0, 0.0, 8.0), (0.9, 22.0, 5.0, 0.5), (0.5, 8.0, 1.0, 3.0), (0.7, 7.0, 0.0, 2.0)), config);

        Assert.True(selection.Windows.Count <= 2, $"chon {selection.Windows.Count} doan");
    }

    [Fact]
    public void Tran_cau_hinh_dem_cuong_vao()
    {
        // "Trần" là trần: mở rộng cửa sổ cho phép tăng mục tiêu, không phải bỏ trần.
        var config = Config();
        config.MaxWindowCount = 2;
        config.TargetWindowCount = 10;

        var selection = Select(MixedSamples(), config);

        Assert.True(selection.Windows.Count <= 2, $"chon {selection.Windows.Count} doan");
    }

    // ---------------------------------------------------------------- hỏng và thiếu dữ liệu

    [Fact]
    public void Khong_co_mau_nao_thi_rot_ve_phong()
    {
        var selection = RepresentativeWindowSelector.Select([], TimeSpan.FromMinutes(5), Config(), Stats());

        Assert.True(selection.UsedFallback);
        Assert.NotEmpty(selection.Windows);
    }

    [Fact]
    public void Mau_khong_doc_duoc_khong_duoc_dung()
    {
        var samples = Samples((0.5, 5.0, 0.0, 3.0));
        samples.Add(new WindowFeatures(99, 2, 0.9, 0.9, 9, 0.1, 100, Frames: 0));

        var selection = RepresentativeWindowSelector.Select(samples, TimeSpan.FromMinutes(5), Config(), Stats());

        Assert.All(selection.Windows, w => Assert.NotEqual(99, w.StartSeconds));
    }

    [Fact]
    public void Gia_tri_khong_hu_lien_khong_lam_hong_bo_chon()
    {
        var samples = Samples((0.5, 5.0, 0.0, 3.0), (double.NaN, 5.0, 0.0, 3.0), (0.9, 22.0, 2.0, 1.0));
        var selection = RepresentativeWindowSelector.Select(samples, TimeSpan.FromMinutes(5), Config(), Stats());

        Assert.NotEmpty(selection.Windows);
        Assert.All(selection.Windows, w => Assert.InRange(w.OverallComplexity, 0, 1));
    }

    // ---------------------------------------------------------------- giải thích và điểm số

    [Fact]
    public void Moi_doan_deu_co_ly_do_doc_duoc()
    {
        var labels = new Dictionary<WindowRole, string>
        {
            [WindowRole.Typical] = "điển hình",
            [WindowRole.HighSpatial] = "nhiều chi tiết",
            [WindowRole.HighMotion] = "chuyển động mạnh",
            [WindowRole.LowComplexity] = "đơn giản nhất",
        };

        foreach (var w in Select(MixedSamples()).Windows)
        {
            Assert.False(string.IsNullOrWhiteSpace(w.Reason));
            Assert.Contains(labels[w.Role], w.Reason);
            Assert.Contains("độ khó tổng", w.Reason);
        }
    }

    [Fact]
    public void Diem_deu_trong_khoang_0_den_1()
    {
        var selection = Select(Samples(
            (0.0, 6.0, 0.0, 100.0), (1.0, 24.0, 50.0, 0.0), (0.5, 8.0, 1.0, 50.0)));

        Assert.All(selection.Windows, w =>
        {
            Assert.InRange(w.SpatialScore, 0, 1);
            Assert.InRange(w.MotionScore, 0, 1);
            Assert.InRange(w.SceneScore, 0, 1);
            Assert.InRange(w.SharpnessScore, 0, 1);
            Assert.InRange(w.OverallComplexity, 0, 1);
        });
    }

    [Fact]
    public void Mau_giong_het_thi_giu_o_giua_khong_dong_vi_tri()
    {
        // Không có gì để phân biệt thì trả 0,5. Trả 0 sẽ làm mọi mẫu dính "đơn giản nhất",
        // trả 1 sẽ làm mọi mẫu dính "nhiều chi tiết" — cả hai đều bịa.
        var selection = Select(Samples((0.4, 4.0, 0.0, 5.0), (0.4, 4.0, 0.0, 5.0), (0.4, 4.0, 0.0, 5.0)));

        Assert.All(selection.Windows, w => Assert.InRange(w.OverallComplexity, 0.49, 0.51));
    }

    // ---------------------------------------------------------------- số mẫu

    [Fact]
    public void So_mau_tang_theo_log_thoi_luong()
    {
        var config = Config();
        var ngan = TimelineScanner.SampleCountFor(TimeSpan.FromMinutes(1), config);
        var vua = TimelineScanner.SampleCountFor(TimeSpan.FromMinutes(10), config);
        var dai = TimelineScanner.SampleCountFor(TimeSpan.FromMinutes(30), config);

        Assert.True(ngan < vua, $"{ngan} !< {vua}");
        Assert.True(vua < dai, $"{vua} !< {dai}");
    }

    [Fact]
    public void So_mau_luon_nam_trong_gioi_han()
    {
        var config = Config();

        foreach (var minutes in new[] { 0.01, 0.5, 1, 5, 30, 600, 100000 })
        {
            var n = TimelineScanner.SampleCountFor(TimeSpan.FromMinutes(minutes), config);
            Assert.InRange(n, config.AnalysisMinSamples, config.AnalysisMaxSamples);
        }
    }

    [Fact]
    public void Khong_biet_thoi_luong_van_co_so_mau_hop_le()
    {
        var n = TimelineScanner.SampleCountFor(null, Config());
        Assert.InRange(n, Config().AnalysisMinSamples, Config().AnalysisMaxSamples);
    }

    [Fact]
    public void Cau_hinh_min_lon_hon_max_khong_lam_no()
    {
        // Math.Clamp ném ArgumentException khi min > max. Người dùng gõ sai cấu hình
        // không được làm hỏng giữa lúc nén.
        var config = Config();
        config.AnalysisMinSamples = 100;
        config.AnalysisMaxSamples = 2;

        var n = TimelineScanner.SampleCountFor(TimeSpan.FromMinutes(30), config);
        Assert.True(n >= 1, $"so mau = {n}");
    }

    [Fact]
    public void Cau_hinh_am_khong_lam_no()
    {
        var config = Config();
        config.AnalysisMinSamples = -5;
        config.AnalysisMaxSamples = -1;

        Assert.True(TimelineScanner.SampleCountFor(TimeSpan.FromMinutes(30), config) >= 1);
    }
}
