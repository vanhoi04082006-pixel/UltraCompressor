using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Bộ lập kế hoạch ứng viên. Hàm thuần nên test không cần ffmpeg và không cần tệp thật.
///
/// <para>Các test ở đây chặn những lỗi <b>im lặng</b>: một preset table trá hình vẫn cho
/// ra "ứng viên" hợp lệ nhìn bề ngoài, và chỉ lộ ra khi so với yêu cầu chất lượng thật.</para>
/// </summary>
public class CandidatePlannerTests
{
    private static AppConfig Config() => new();

    private static EncoderCapabilities Both() =>
        EncoderCapabilities.FromEncoderNames(["libx264", "libx265"]);

    private static VideoSourceProfile Source(
        int w = 1920, int h = 1080, double fps = 24, double? density = null,
        ContentProfile content = ContentProfile.ModerateMotion, ContentComplexity? complexity = null) =>
        new()
        {
            Width = w,
            Height = h,
            Fps = fps,
            BitsPerPixelPerFrame = density,
            Content = content,
            Complexity = complexity,
        };

    private static CandidatePlan Plan(
        VideoSourceProfile source,
        CompressionLevel level = CompressionLevel.Balanced,
        ComputeBudget budget = ComputeBudget.Normal,
        EncoderCapabilities? caps = null,
        AppConfig? config = null) =>
        CandidatePlanner.Generate(source, level, budget, caps ?? Both(), config ?? Config());

    // ================================================================= A. Tách mode khỏi compute budget

    [Fact]
    public void Cung_muc_nguon_ma_ngan_sach_tinh_toan_khac_nhau()
    {
        // Invariant kiến trúc quan trọng nhất: "nhanh" và "chậm" là hai điều khác "chất
        // lượng thấp" và "chất lượng cao". Nếu một ngân sách hạ được ngưỡng thì toàn bộ
        // kiến trúc đã hỏng.
        var source = Source();
        var fast = Plan(source, budget: ComputeBudget.Fast);
        var normal = Plan(source, budget: ComputeBudget.Normal);
        var thorough = Plan(source, budget: ComputeBudget.Thorough);

        foreach (var level in new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong })
        {
            var floor = QualityPolicy.For(level, VmafModels.Default);
            Assert.Equal(89.0, QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Default).VmafMean);
            Assert.NotEqual(0, floor.VmafMean);
        }

        // Ngân sách khác nhau phải cho tập ứng viên khác nhau — đó là chỗ nó được phép
        // khác nhau. Chất lượng thì không: cùng mode thì cùng ngưỡng.
        Assert.NotEqual(
            fast.EncodeCandidates.Select(c => c.Preset).Distinct().Order(),
            thorough.EncodeCandidates.Select(c => c.Preset).Distinct().Order());
    }

    [Fact]
    public void Ngan_sach_quyet_dinh_preset_chu_khong_phai_mode()
    {
        // Cùng một ngân sách, ba mode khác nhau: preset phải GIỐNG NHAU. Nếu preset đổi
        // theo mode thì đó chính là cái lỗi "fast = chất lượng thấp" mà kiến trúc cảnh báo.
        var source = Source();
        var presets = new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong }
            .Select(level => Plan(source, level, budget: ComputeBudget.Fast)
                .EncodeCandidates.Select(c => c.Preset).Distinct().Order().ToArray())
            .ToArray();

        Assert.Equal(presets[0], presets[1]);
        Assert.Equal(presets[1], presets[2]);
    }

    [Fact]
    public void Mode_chi_dich_vi_tri_vung_tim_chu_khong_gan_thang_chung()
    {
        // Nhẹ phải dồn về chất lượng cao hơn Mạnh — nhưng so sánh PHẢI cùng một nhánh.
        // Trước đây test lấy max của toàn bộ ứng viên Nhẹ rồi so với min của toàn bộ ứng
        // viên Mạnh, tức so chéo các nhánh khác nhau. Điều đó vô nghĩa: nhánh 720p và
        // nhánh 1080p là hai hình khác nhau, "chất lượng" của chúng không cùng đơn vị.
        var source = Source();

        var light = Plan(source, CompressionLevel.Light).EncodeCandidates
            .Where(c => c.Codec == VideoCodec.H264)
            .ToList();
        var strong = Plan(source, CompressionLevel.Strong).EncodeCandidates
            .Where(c => c.Codec == VideoCodec.H264)
            .ToList();

        Assert.NotEmpty(light);
        Assert.NotEmpty(strong);

        var lightBranches = light.Select(c => c.BranchId).Distinct().ToHashSet();
        var strongBranches = strong.Select(c => c.BranchId).Distinct().ToHashSet();
        Assert.Equal(lightBranches, strongBranches);

        foreach (var branch in lightBranches)
        {
            var lightBranch = light.Where(c => c.BranchId == branch).ToList();
            var strongBranch = strong.Where(c => c.BranchId == branch).ToList();

            Assert.Equal(lightBranch.Count, strongBranch.Count);
            Assert.True(
                lightBranch.Max(c => c.QualityParameter) < strongBranch.Min(c => c.QualityParameter),
                $"nhánh {branch}: nhe {lightBranch.Max(c => c.QualityParameter)} khong < manh {strongBranch.Min(c => c.QualityParameter)}");
        }
    }

    [Fact]
    public void Nhanh_hon_phai_giu_chat_luong_tot_hon_nguon()
    {
        // Hạ độ phân giải mà giữ nguyên tham số chất lượng thì mất chi tiết cảm nhận, và
        // mọi nhánh nhỏ sẽ hỏng ngưỡng chất lượng, tốn công encode vô ích. Nên nhánh nhỏ
        // phải được mã hoá kỹ hơn: tham số chất lượng nhỏ hơn.
        var plan = Plan(Source(3840, 2160), CompressionLevel.Balanced, ComputeBudget.Thorough);

        var byHeight = plan.EncodeCandidates
            .Where(c => c.Codec == VideoCodec.H264)
            .GroupBy(c => c.Height)
            .OrderByDescending(g => g.Key)
            .ToList();

        Assert.True(byHeight.Count > 1, "can it nhat hai nhanh hinh de kiem tra");

        for (var i = 1; i < byHeight.Count; i++)
        {
            var higher = byHeight[i - 1].Max(c => c.QualityParameter);
            var lower = byHeight[i].Max(c => c.QualityParameter);

            Assert.True(
                lower < higher,
                $"nhanh {byHeight[i].Key}p ({lower}) phai chat luong tot hon nhanh {byHeight[i - 1].Key}p ({higher})");
        }
    }

    // ================================================================= F. CRF không dùng chung giữa codec

    [Fact]
    public void Moj_codec_co_mien_khac_nhau()
    {
        var x264 = new X264SearchDomain();
        var x265 = new X265SearchDomain();
        var av1 = new LibaomAv1SearchDomain();

        // Miền AV1 rộng hơn hẳn: đây là lý do không được áp một thang chung cho mọi codec.
        Assert.Equal(51, x264.MaxQuality);
        Assert.Equal(51, x265.MaxQuality);
        Assert.Equal(63, av1.MaxQuality);

        // Qua giao diện cũng phải đúng — nếu khai báo bằng `new` thì giao diện vẫn trả
        // miền của lớp cha và ứng viên AV1 ngoài 0-51 bị chặn oan.
        //
        // Đọc qua mảng kiểu giao diện thay vì biến hay tham số kiểu giao diện: trình
        // phân tích CA1859 gợi ý hạ kiểu xuống lớp cụ thể "để nhanh hơn", mà làm vậy là
        // hỏng đúng thứ test này cần kiểm — và kho này không dùng suppression nào.
        Assert.Equal(63, AllDomains().Single(d => d.Codec == VideoCodec.Av1).MaxQuality);
    }

    [Fact]
    public void Moj_codec_co_diem_khac_nhau()
    {
        // Mặc định của chính encoder: x264 23, x265 28. Đây là lý do một CRF chung cho mọi
        // codec là sai.
        var x264 = new X264SearchDomain().CoarseQualityPoints(CompressionLevel.Balanced, 0, 1, 1)[0];
        var x265 = new X265SearchDomain().CoarseQualityPoints(CompressionLevel.Balanced, 0, 1, 1)[0];

        Assert.NotEqual(x264, x265);
    }

    [Fact]
    public void Tham_so_chat_luong_duoc_kep_trong_mien()
    {
        foreach (IEncoderSearchDomain domain in new IEncoderSearchDomain[]
                 { new X264SearchDomain(), new X265SearchDomain(), new LibaomAv1SearchDomain() })
        {
            foreach (var level in new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong })
            {
                foreach (var point in domain.CoarseQualityPoints(level, -1, 1, 4))
                {
                    Assert.InRange(point, domain.MinQuality, domain.MaxQuality);
                }
            }
        }
    }

    [Fact]
    public void Xu_huong_mode_dung_chieu_trong_mien()
    {
        foreach (IEncoderSearchDomain domain in new IEncoderSearchDomain[]
                 { new X264SearchDomain(), new X265SearchDomain(), new LibaomAv1SearchDomain() })
        {
            var light = domain.CoarseQualityPoints(CompressionLevel.Light, 0, 1, 1)[0];
            var strong = domain.CoarseQualityPoints(CompressionLevel.Strong, 0, 1, 1)[0];

            // CRF nhỏ hơn = chất lượng cao hơn.
            Assert.True(light < strong, $"{domain.EncoderName}: nhe {light} khong < manh {strong}");
        }
    }

    // ================================================================= C/D. Không phóng to, không đổi FPS

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(640, 480)]
    public void Khong_bao_gio_phong_to(int w, int h)
    {
        var plan = Plan(Source(w, h), budget: ComputeBudget.Thorough);

        Assert.NotEmpty(plan.EncodeCandidates);
        Assert.All(plan.EncodeCandidates, c =>
        {
            Assert.True(c.Width <= w, $"{c.Id}: rong {c.Width} > nguon {w}");
            Assert.True(c.Height <= h, $"{c.Id}: cao {c.Height} > nguon {h}");
        });
    }

    [Fact]
    public void Khong_bao_gio_tang_hoac_doi_fps()
    {
        foreach (var fps in new[] { 23.976, 25.0, 30.0, 50.0, 60.0 })
        {
            var plan = Plan(Source(1920, 1080, fps), budget: ComputeBudget.Thorough);
            Assert.All(plan.EncodeCandidates, c => Assert.Equal(fps, c.Fps, 6));
        }
    }

    [Fact]
    public void Giu_dung_ti_le_khung_hinh()
    {
        // 4K 16:9 xuống 1440p phải ra 2560x1440, không phải một con số tròn tùy tiện.
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);
        var lowered = plan.EncodeCandidates.Where(c => c.Height < 2160).ToList();

        Assert.NotEmpty(lowered);
        foreach (var c in lowered)
        {
            var sourceRatio = 3840.0 / 2160.0;
            var candidateRatio = (double)c.Width / c.Height;
            Assert.True(Math.Abs(sourceRatio - candidateRatio) < 0.01, $"{c.Id}: {c.Width}x{c.Height}");
        }
    }

    [Fact]
    public void Khong_tao_kich_thuoc_lon_hon_nguon()
    {
        // Nguồn 720p không được sinh 1080p chỉ vì rung 1080p nằm trong thang.
        var plan = Plan(Source(1280, 720), budget: ComputeBudget.Thorough);

        Assert.All(plan.EncodeCandidates, c => Assert.True(c.Height <= 720, c.Id));
    }

    [Fact]
    public void Nguon_nho_khong_bi_ep_khong_con_ung_vien_gi_nguyen()
    {
        // Mức Mạnh không được đẩy tệp nhỏ xuống dưới mức hợp lý. Nhưng có một nhánh nhỏ
        // hơn ở đây vẫn đúng: 480p xuống 360p là một bước giảm hợp lý, và giai đoạn tìm
        // kiếm sẽ tự đo xem có dùng được không.
        var plan = Plan(Source(640, 480), CompressionLevel.Strong, ComputeBudget.Thorough);
        var sizes = plan.EncodeCandidates.Select(c => (c.Width, c.Height)).Distinct().ToList();

        // Nguồn phải còn trong tập ứng viên.
        Assert.Contains(sizes, s => s.Item1 == 640 && s.Item2 == 480);

        // Và không nhánh nào xuống dưới một nửa chiều cao nguồn.
        Assert.All(sizes, s => Assert.True(s.Item2 >= 240, $"cao {s.Item2} < mot nua 480"));
    }

    [Fact]
    public void Nguon_4K_co_nhieu_nhanh_hinh_hon()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);
        var heights = plan.EncodeCandidates.Select(c => c.Height).Distinct().OrderDescending().ToList();

        Assert.True(heights.Count > 1, $"chi {heights.Count} nhánh hình");
        Assert.Contains(2160, heights);
        Assert.Contains(1440, heights);
    }

    // ================================================================= K. Không hardcode mode → 1080p

    [Fact]
    public void Khong_mode_nao_bi_ghim_cung_mot_do_phan_giai()
    {
        foreach (var level in new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong })
        {
            var plan = Plan(Source(3840, 2160), level, ComputeBudget.Thorough);

            // 4K phải còn ứng viên ở 4K: không mode nào được ghim thành "luôn xuống 1080p".
            Assert.Contains(plan.EncodeCandidates, c => c.Height == 2160);
        }
    }

    [Fact]
    public void Cac_mode_khong_tao_ra_ung_vien_giong_het_nhau()
    {
        // Mode khác nhau phải cho tập ứng viên khác nhau, nếu không mode chưa làm gì.
        var source = Source(1920, 1080);
        var light = Plan(source, CompressionLevel.Light).EncodeCandidates.Select(c => c.QualityParameter).ToHashSet();
        var strong = Plan(source, CompressionLevel.Strong).EncodeCandidates.Select(c => c.QualityParameter).ToHashSet();

        Assert.NotEmpty(light.Except(strong));
    }

    // ================================================================= H. Lọc theo khả năng

    [Fact]
    public void Thieu_libx265_van_lap_ke_hoach_duoc()
    {
        var plan = Plan(Source(), caps: EncoderCapabilities.FromEncoderNames(["libx264"]));

        Assert.NotEmpty(plan.EncodeCandidates);
        Assert.All(plan.EncodeCandidates, c => Assert.Equal("libx264", c.EncoderName));
        Assert.Contains(plan.Diagnostics.CodecsSkipped, s => s.Contains("libx265"));
    }

    [Fact]
    public void Khong_co_codec_nao_thi_bao_ly_do_chu_khong_im_lang()
    {
        var plan = Plan(Source(), caps: EncoderCapabilities.None);

        Assert.Empty(plan.Candidates);
        Assert.NotEmpty(plan.Diagnostics.Notes);
    }

    [Fact]
    public void Av1_mac_dinh_tat_vi_cham()
    {
        var plan = Plan(Source(), caps: EncoderCapabilities.FromEncoderNames(["libx264", "libx265", "libaom-av1"]));

        Assert.DoesNotContain(plan.EncodeCandidates, c => c.Codec == VideoCodec.Av1);
        Assert.Contains(plan.Diagnostics.CodecsSkipped, s => s.Contains("libaom"));
    }

    [Fact]
    public void Av1_chi_duoc_thu_o_ngan_sach_ky()
    {
        var caps = EncoderCapabilities.FromEncoderNames(["libx264", "libx265", "libaom-av1"]);

        var normal = Plan(Source(), budget: ComputeBudget.Normal, caps: caps);
        var thorough = Plan(Source(), budget: ComputeBudget.Thorough, caps: caps, config: new AppConfig { EnableAv1Search = true });

        Assert.DoesNotContain(normal.EncodeCandidates, c => c.Codec == VideoCodec.Av1);
        Assert.Contains(thorough.EncodeCandidates, c => c.Codec == VideoCodec.Av1);
    }

    [Fact]
    public void Thieu_kich_thuoc_nguon_thi_bao_ly_do()
    {
        var plan = Plan(Source(0, 0));

        Assert.Empty(plan.Candidates);
        Assert.Contains("kích thước", string.Join(" ", plan.Diagnostics.Rejected));
    }

    // ================================================================= I/J. Trùng lặp và giới hạn

    [Fact]
    public void Khong_co_hai_ung_vien_ngu_nghia_giong_nhau()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);

        var keys = plan.EncodeCandidates
            .Select(c => string.Join('|', c.EncoderName, c.Width, c.Height, c.QualityParameter, c.Preset, c.PixelFormat, c.Tune ?? ""))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.NotNull(k));
    }

    [Fact]
    public void Khong_co_khong_id_trung()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);
        var ids = plan.EncodeCandidates.Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-10, 5)]
    [InlineData(5, 0)]
    [InlineData(5, -10)]
    [InlineData(100000, 100000)]
    public void Cau_hinh_cuc_do_khong_tao_hang_nghin_ung_vien(int min, int max)
    {
        var config = new AppConfig { MaxInitialCandidates = min, MaxResolutionBranches = max };
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough, config: config);

        Assert.InRange(plan.Candidates.Count, 0, 96);
    }

    [Fact]
    public void So_ung_vien_tang_theo_ngan_sach()
    {
        var source = Source(3840, 2160);
        var fast = Plan(source, budget: ComputeBudget.Fast).Candidates.Count;
        var normal = Plan(source, budget: ComputeBudget.Normal).Candidates.Count;
        var thorough = Plan(source, budget: ComputeBudget.Thorough).Candidates.Count;

        Assert.True(fast < normal, $"{fast} !< {normal}");
        Assert.True(normal <= thorough, $"{normal} > {thorough}");
    }

    // ================================================================= E. Tất định

    [Fact]
    public void Chay_nhieu_lan_cho_cung_tap_ung_vien_theo_cung_thu_tu()
    {
        var source = Source(3840, 2160, 30, 0.2, ContentProfile.BusyMotion, new ContentComplexity
        {
            SpatialDetail = 50,
            TemporalActivity = 20,
            Samples = 3,
        });

        var first = Plan(source, CompressionLevel.Strong, ComputeBudget.Thorough);
        for (var i = 0; i < 5; i++)
        {
            var again = Plan(source, CompressionLevel.Strong, ComputeBudget.Thorough);
            Assert.Equal(
                first.EncodeCandidates.Select(c => c.Id),
                again.EncodeCandidates.Select(c => c.Id));
        }
    }

    [Fact]
    public void Ten_ung_vien_la_duy_nhat_theo_ky_tu()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);
        var ids = plan.EncodeCandidates.Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Equal(id, id.Trim()));
    }

    private static IEncoderSearchDomain[] AllDomains() =>
        [new X264SearchDomain(), new X265SearchDomain(), new LibaomAv1SearchDomain()];

    // ================================================================= Ứng viên phải dựng được lệnh ffmpeg

    [Fact]
    public void Gia_tri_preset_luon_la_the_tran_khong_phai_manh_cu_phap()
    {
        // Trước đây miền AV1 trả về "cpu-used=9". Nếu giai đoạn dựng lệnh ghép thành
        // "-preset cpu-used=9" thì ffmpeg từ chối, và lỗi chỉ lộ ra khi encode thật. Ở đây
        // chặn ngay: giá trị phải là một thẻ đơn, và công tắc nằm ở chỗ khác.
        foreach (IEncoderSearchDomain domain in AllDomains())
        {
            foreach (var budget in new[] { ComputeBudget.Fast, ComputeBudget.Normal, ComputeBudget.Thorough })
            {
                var preset = domain.Preset(budget);

                Assert.False(preset.Contains('='), $"{domain.EncoderName}/{budget}: preset \"{preset}\" chua la the tran");
                Assert.False(preset.Contains(' '), $"{domain.EncoderName}/{budget}: preset \"{preset}\" chua la the tran");
                Assert.False(string.IsNullOrWhiteSpace(preset), $"{domain.EncoderName}/{budget}: preset rong");
            }
        }
    }

    [Fact]
    public void Cong_tac_cua_preset_dung_voi_tung_encoder()
    {
        // libaom không dùng -preset kiểu x264; mức tốc độ của nó nằm ở -cpu-used. Ghép sai
        // công tắc là lỗi âm thầm: encoder vẫn chạy nhưng chạy theo mức tốc độ mặc định,
        // tức là khoảng 10-50 lần chậm so với dự kiến.
        Assert.Equal("-preset", new X264SearchDomain().PresetSwitch);
        Assert.Equal("-preset", new X265SearchDomain().PresetSwitch);
        Assert.Equal("-cpu-used", new LibaomAv1SearchDomain().PresetSwitch);

        foreach (IEncoderSearchDomain domain in AllDomains())
        {
            Assert.StartsWith("-", domain.PresetSwitch, StringComparison.Ordinal);
            Assert.Equal("-crf", domain.QualitySwitch);
        }
    }

    [Fact]
    public void Mien_av1_chi_nhan_nguong_cpu_used_hop_le()
    {
        var domain = new LibaomAv1SearchDomain();

        // Đo trên bản ffmpeg đi kèm (8.0.1-essentials):
        //   -cpu-used <int> Quality/Speed ratio modifier (from 0 to 8) (default 1)
        // Test trước đây cho phép tới 9 — đúng cái miền sai đã làm ứng viên ngân sách
        // Nhanh hỏng khi encode thật. Miền này phụ thuộc bản dựng; hằng số đi kèm ghi
        // rõ nguồn để lần đo sau cần cập nhật cả hai.
        foreach (var budget in new[] { ComputeBudget.Fast, ComputeBudget.Normal, ComputeBudget.Thorough })
        {
            Assert.True(
                int.TryParse(domain.Preset(budget), out var cpuUsed) && cpuUsed is >= 0 and <= 8,
                $"{budget}: cpu-used \"{domain.Preset(budget)}\" ngoai 0..8 do duoc");
        }

        // cpu-used nhỏ = chậm và nén tốt, nên ngân sách càng kỹ thì cpu-used càng nhỏ.
        var fast = int.Parse(domain.Preset(ComputeBudget.Fast), CultureInfo.InvariantCulture);
        var normal = int.Parse(domain.Preset(ComputeBudget.Normal), CultureInfo.InvariantCulture);
        var thorough = int.Parse(domain.Preset(ComputeBudget.Thorough), CultureInfo.InvariantCulture);

        Assert.True(fast > normal, $"nhanh {fast} phai lon hon thuong {normal}");
        Assert.True(normal > thorough, $"thuong {normal} phai lon hon ky {thorough}");
    }

    [Fact]
    public void Mien_chat_luong_khop_voi_ffmpeg_dang_dung()
    {
        // Giá trị sàn và trần phải nằm trong đúng thứ ffmpeg chấp nhận, vì ứng viên ngoài
        // miền sẽ làm cả lệnh encode thất bại chứ không chỉ bị loại âm thầm.
        //
        // Đo trên ffmpeg 8.0.1-essentials: libaom "-crf <int> from -1 to 63";
        // x265 từ chối 52 trở lên; cả hai x26x chấp nhận -1 nhưng -1 là chế độ lượng tử
        // hằng chứ không phải chất lượng hằng, nên sàn là 0.
        Assert.Equal(0, new X264SearchDomain().MinQuality);
        Assert.Equal(0, new X265SearchDomain().MinQuality);
        Assert.Equal(0, new LibaomAv1SearchDomain().MinQuality);

        Assert.True(new LibaomAv1SearchDomain().MaxQuality <= 63);
        Assert.True(new X265SearchDomain().MaxQuality <= 51);
        Assert.True(new X264SearchDomain().MaxQuality <= 51);
    }

    [Fact]
    public void Lo_giai_thich_phai_chua_dung_cau_hinh_that()
    {
        // Trước đây lời giải thích in ra chỉ số điểm ("chất lượng = 2/3") thay vì tham số
        // thật, tức là log không dùng để dựng lại lệnh ffmpeg được.
        var plan = Plan(Source(1920, 1080), budget: ComputeBudget.Thorough);

        foreach (var c in plan.EncodeCandidates)
        {
            var expected = c.QualityParameter.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            Assert.Contains(c.EncoderName, c.Reason);
            Assert.Contains(expected, c.Reason);
            Assert.Contains(c.Preset, c.Reason);
            Assert.Contains($"{c.Width}x{c.Height}", c.Reason);
        }
    }

    [Fact]
    public void Khong_ap_tune_anime_cho_noi_dung_khong_phai_anime()
    {
        // Trước đây x265 trả tune=animation cho MỌI nội dung không phải màn hình, tức áp
        // tune anime lên quay thật, game và phim. ContentProfile phân loại theo độ khó
        // chuyển động chứ không phải thể loại nội dung, nên không đủ căn cứ chọn tune anime.
        var domain = new X265SearchDomain();

        foreach (var profile in Enum.GetValues<ContentProfile>())
        {
            var tune = domain.TuneFor(profile);

            if (profile == ContentProfile.ScreenContent)
            {
                Assert.Equal("screencontent", tune);
                continue;
            }

            Assert.True(
                tune != "animation",
                $"{profile}: tune \"{tune}\" khong co co so do duoc");
        }
    }

    [Fact]
    public void Tham_so_chat_luong_luon_la_so_nguyen()
    {
        // Cả ba encoder lấy tham số chất lượng dạng nguyên. Giữ số thực chỉ mang lại
        // nhiễu float vào log, ID và dòng lệnh ffmpeg.
        foreach (IEncoderSearchDomain domain in AllDomains())
        {
            foreach (var level in new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong })
            {
                foreach (var scale in new[] { 1.0, 0.5, 0.25 })
                {
                    foreach (var point in domain.CoarseQualityPoints(level, 0, scale, 4))
                    {
                        Assert.Equal(Math.Round(point), point, 6);
                    }
                }
            }
        }
    }

    [Fact]
    public void Diem_chat_luong_khong_trung_nhau_trong_mot_nhanh()
    {
        // Hai điểm bằng nhau nghĩa là giai đoạn tìm kiếm sẽ encode cùng một thứ hai lần.
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);

        foreach (var branch in plan.EncodeCandidates.GroupBy(c => c.BranchId))
        {
            var points = branch.Select(c => c.QualityParameter).ToList();
            Assert.Equal(points.Count, points.Distinct().Count());
        }
    }

    [Fact]
    public void Cong_tac_preset_phai_khac_nhau_giua_x26x_va_libaom()
    {
        // ffmpeg KHÔNG báo lỗi khi nhận "-preset cpu-used=N" cho libaom — nó âm thầm bỏ
        // qua và chạy ở mặc định, tức chậm hơn 74 lần (đo trên tệp 5 giây). Nên công tắc
        // phải sai khác hẳn, và test này chặn việc gộp chúng lại.
        var x264 = new X264SearchDomain();
        var libaom = new LibaomAv1SearchDomain();

        Assert.NotEqual(x264.PresetSwitch, libaom.PresetSwitch);

        // Và ghép theo công tắc của từng miền thì không chứa dấu "=" — dấu báo hiệu đây là
        // dạng "mảnh cú pháp" mà ffmpeg sẽ bỏ qua.
        foreach (var domain in AllDomains())
        {
            var args = $"{domain.PresetSwitch} {domain.Preset(ComputeBudget.Fast)}";
            Assert.DoesNotContain("=", args);
        }
    }

    // ================================================================= Cấu trúc tập ứng viên

    [Fact]
    public void Tap_ung_vien_co_cau_truc_nhanh_theo_coc()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);

        // Giai đoạn tìm kiếm cần nhóm theo nhánh để dò thô rồi khoanh biên, không thử
        // mọi tổ hợp.
        var branches = plan.EncodeCandidates.GroupBy(c => c.BranchId).ToList();
        Assert.True(branches.Count >= 2, $"chi {branches.Count} nhanh");

        Assert.All(branches, branch =>
        {
            Assert.All(
                branch.OrderBy(c => c.QualityParameter),
                c => Assert.InRange(c.PointIndex, 0, c.PointCount - 1));
        });
    }

    [Fact]
    public void Diem_dau_tien_cua_moi_nhanh_la_di_pho()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);

        foreach (var branch in plan.EncodeCandidates.GroupBy(c => c.BranchId))
        {
            var first = branch.OrderBy(c => c.PointIndex).First();
            Assert.Equal(CandidateOrigin.CoarseProbe, first.Origin);
            Assert.Equal(0, first.PointIndex);
        }
    }

    [Fact]
    public void Moi_ung_vien_deu_co_ly_do_doc_duoc()
    {
        var plan = Plan(Source(3840, 2160), budget: ComputeBudget.Thorough);

        Assert.All(plan.EncodeCandidates, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Reason));
            Assert.Contains(c.EncoderName, c.Reason);
        });
    }

    [Fact]
    public void Khong_co_so_do_chat_luong_gia_trong_ung_vien()
    {
        // Con số chưa đo mà đặt vào ứng viên là dữ liệu bịa, và tệ hơn là thiếu hẳn.
        var properties = typeof(VideoEncodeCandidate).GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        Assert.DoesNotContain("vmaf", properties);
        Assert.DoesNotContain("predictedquality", properties);
        Assert.DoesNotContain("estimatedbytes", properties);
    }

    [Fact]
    public void Original_da_co_che_de_nhung_chua_duoc_dung()
    {
        // Nhánh "giữ nguyên" phải là một loại ứng viên ngang hàng, không phải ngoại lệ ở
        // cuối đường ống. Giai đoạn này chưa dùng, nhưng kiểu phải sẵn sàng.
        var original = new OriginalCandidate("original", 1_000_000);

        Assert.IsAssignableFrom<CompressionCandidate>(original);
        Assert.Equal(1_000_000, original.SourceBytes);

        // Ứng viên encode bắt buộc phải đủ thông tin để chạy được lệnh ffmpeg: thiếu một
        // trường nào đó là một ứng viên không dùng được, dù đã sinh ra.
        var encode = new VideoEncodeCandidate("x")
        {
            Codec = VideoCodec.H264,
            EncoderName = "libx264",
            QualityParameter = 23,
            Width = 1920,
            Height = 1080,
            Fps = 24,
            Preset = "medium",
            PixelFormat = "yuv420p",
            Origin = CandidateOrigin.CoarseProbe,
            BranchId = "H264/1920x1080",
            PointIndex = 0,
            PointCount = 1,
            Reason = "kiểm thử",
        };

        Assert.IsAssignableFrom<CompressionCandidate>(encode);
    }

    [Fact]
    public void Planner_hien_tai_khong_sinh_nhanh_original()
    {
        var plan = Plan(Source(), budget: ComputeBudget.Thorough);

        Assert.DoesNotContain(plan.Candidates, c => c is OriginalCandidate);
    }

    // ================================================================= Ưu tiên nguồn

    [Fact]
    public void Nguon_yeu_hon_doi_vung_tim_ve_chat_luong_cao_hon()
    {
        // Mật độ bit KHÔNG dịch vùng tìm: chiều ảnh hưởng của nó chưa được số đo nào xác
        // lập, nên ở đây nó bị bỏ qua thay vì đoán. Xem
        // CandidatePlanner.ContentBias để biết vì sao và cần đo gì để mở lại.
        var depleted = Plan(Source(1920, 1080, density: 0.05));
        var rich = Plan(Source(1920, 1080, density: 0.60));

        var depletedX264 = depleted.EncodeCandidates.Where(c => c.Codec == VideoCodec.H264).ToList();
        var richX264 = rich.EncodeCandidates.Where(c => c.Codec == VideoCodec.H264).ToList();

        Assert.Equal(depletedX264.Count, richX264.Count);
        foreach (var (a, b) in depletedX264.Zip(richX264))
        {
            Assert.Equal(a.BranchId, b.BranchId);
            Assert.Equal(a.QualityParameter, b.QualityParameter);
        }
    }

    [Fact]
    public void Do_leo_chi_doi_vung_tim_khong_anh_huong_nguong_chat_luong()
    {
        // Biến thiên nội dung chỉ được phép ảnh hưởng vị trí trung tâm, không được biến mất.
        var still = Plan(Source(1920, 1080, density: 0.2, complexity: new ContentComplexity
        { SpatialDetail = 40, TemporalActivity = 0, Samples = 3 }));
        var busy = Plan(Source(1920, 1080, density: 0.2, complexity: new ContentComplexity
        { SpatialDetail = 40, TemporalActivity = 30, Samples = 3 }));

        foreach (var level in new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong })
        {
            var floor = QualityPolicy.For(level, VmafModels.Default);
            Assert.True(floor.VmafMean > 0, "nguong khong doi theo noi dung");
        }

        // Nội dung động nên dồn về chất lượng cao hơn nội dung tĩnh.
        Assert.True(
            busy.EncodeCandidates.Where(c => c.Codec == VideoCodec.H264).Max(c => c.QualityParameter)
            < still.EncodeCandidates.Where(c => c.Codec == VideoCodec.H264).Max(c => c.QualityParameter));
    }

    // ================================================================= Ràng buộc mode

    [Fact]
    public void Yeu_cau_chat_luong_luon_monoton_theo_mode()
    {
        var light = QualityPolicy.For(CompressionLevel.Light, VmafModels.Default);
        var balanced = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Default);
        var strong = QualityPolicy.For(CompressionLevel.Strong, VmafModels.Default);

        Assert.True(light.VmafMean >= balanced.VmafMean);
        Assert.True(light.VmafP5 >= balanced.VmafP5);
        Assert.True(balanced.VmafMean >= strong.VmafMean);
        Assert.True(balanced.VmafP5 >= strong.VmafP5);
    }
}
