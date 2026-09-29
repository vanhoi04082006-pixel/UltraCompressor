using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Kiểm thử quy tắc lập kế hoạch nén.
///
/// Đây là lớp test quan trọng nhất của dự án: nó thay thế việc "nén thật mọi tổ hợp để xem
/// ra sao", vốn chậm và không kiểm được các trường hợp lớn như 4K 120fps hay nguồn H.265.
/// Kế hoạch là hàm thuần nên mọi nhánh đều gọi được bằng số.
///
/// Số đo trong các test lấy từ probe thật trên máy, không phải bịa ra.
/// </summary>
public class CompressionPlannerTests
{
    // ---------------------------------------------------------------- trợ giúp dựng dữ kiện

    private static MediaInfo Video(
        int width,
        int height,
        double fps,
        double kbps,
        string? codec = "h264",
        bool audio = true,
        double? seconds = null) => new()
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateKbps = kbps,
            VideoCodec = codec,
            HasVideo = true,
            HasAudio = audio,
            Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(24),
        };

    // ---------------------------------------------------------------- ảnh chụp màn hình

    [Fact]
    public void Man_hinh_1918x1078_30fps_8Mbps_giu_nguyen_binh_va_dung_muc()
    {
        // Số đo thật từ E:\Ảnh\Recording 2026-09-28 221517.mp4
        var info = Video(1918, 1078, 30, 8001);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info);

        // 1918px dưới trần 1920px nên không được thu — thu 2px là vô nghĩa.
        Assert.Equal(1918, plan.TargetWidth);

        // Mức cân bằng không đụng fps: giảm khung hình là đánh đổi người dùng thấy ngay,
        // chỉ chấp nhận ở mức "Mạnh".
        Assert.Null(plan.TargetFps);

        // Mật độ ~0.098 bit/px/khung: hơi chật, nên CRF cao hơn nền 24 một chút.
        Assert.Equal(26, plan.Crf);
        Assert.Contains("hơi chật", plan.Reason);
        Assert.Equal(192, plan.AudioBitrateKbps);
    }

    // ---------------------------------------------------------------- quy tắc mật độ bit

    [Fact]
    public void Nguon_da_quet_hon_vao_phai_nang_crf_chu_khong_ha()
    {
        // 0.02 bit/px/khung là tệp đã bị nén tới mức mã hoá lại chỉ sinh hạt nhiễu.
        var depleted = Video(1920, 1080, 30, 0.02 * 1920 * 1080 * 30 / 1000);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, depleted);

        // Nền 24, nguồn cạn -> +4. Hạ đi thì tệp to hơn chứ không nhỏ hơn.
        Assert.Equal(28, plan.Crf);
        Assert.Contains("rất nén", plan.Reason);
    }

    [Fact]
    public void Nguon_c_con_du_thi_nen_crf()
    {
        // 0.8 bit/px/khung là nguồn rất dư.
        var roomy = Video(1920, 1080, 30, 0.8 * 1920 * 1080 * 30 / 1000);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, roomy);

        Assert.Equal(22, plan.Crf);
        Assert.Contains("còn dư", plan.Reason);
    }

    [Theory]
    [InlineData(0.02, 28)]
    [InlineData(0.15, 26)]
    [InlineData(0.30, 24)]
    [InlineData(0.80, 22)]
    public void Muc_crf_doi_chieu_voi_mat_do_bit(double density, int expectedCrf)
    {
        // Ở mức "Cân bằng" nền là 24; mỗi vùng mật độ lệch đúng 2.
        var info = Video(1920, 1080, 30, density * 1920 * 1080 * 30 / 1000);

        Assert.Equal(expectedCrf, CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info).Crf);
    }

    // ---------------------------------------------------------------- quy tắc codec nguồn

    [Theory]
    [InlineData("hevc")]
    [InlineData("h265")]
    [InlineData("vp9")]
    [InlineData("av1")]
    public void Nguon_codec_hieu_qua_hon_h264_phai_nang_crf_manh(string codec)
    {
        // 4 Mbps ở 1080p30 = 0,064 bit/px/khung, tức nằm dưới ngưỡng "rất nén" (+4).
        // Tổng cộng: nền 24 + 4 (cạn) + 6 (codec) = 34.
        var info = Video(1920, 1080, 30, 4000, codec);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info);

        // Mã hoá lại HEVC bằng H.264 ở CRF 24 sẽ cho tệp LỚN hơn bản gốc.
        Assert.Equal(34, plan.Crf);
        Assert.Contains(codec, plan.Reason);
    }

    [Fact]
    public void Nguon_h264_thi_khong_nang_crf_vi_codec()
    {
        var info = Video(1920, 1080, 30, 4000, "h264");

        Assert.DoesNotContain("hiệu quả hơn", CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info).Reason);
    }

    [Fact]
    public void Codec_hieu_qua_hon_ban_nang_crf_manh_hon_cho_muc_nhe()
    {
        // Ở mức "Nhẹ" người dùng vẫn mong tệp nhỏ đi, nên phải nâng CRF NHIỀU HƠN chứ không
        // phải ít hơn. Nếu nâng ít hơn thì tệp từ HEVC chuyển về H.264 sẽ phình to ra ở đúng
        // mức mà người dùng kỳ vọng nhất được giảm.
        var info = Video(1920, 1080, 30, 4000, "hevc");

        var quality = CompressionPlanner.PlanVideo(CompressionGoal.Quality, info);
        var balanced = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info);

        // Nền 20 + 4 (cạn) + 12 (codec) = 36, so với 24 + 4 + 6 = 34 ở mức cân bằng.
        Assert.Equal(36, quality.Crf);
        Assert.Equal(34, balanced.Crf);
        Assert.True(quality.Crf > balanced.Crf);
    }

    // ---------------------------------------------------------------- quy tắc bề rộng

    [Theory]
    [InlineData(CompressionGoal.Quality, 3840)]
    [InlineData(CompressionGoal.Balanced, 1920)]
    [InlineData(CompressionGoal.Size, 1920)]
    public void Tran_be_rong_video_theo_muc(CompressionGoal goal, int expected)
    {
        var info = Video(7680, 4320, 30, 40000);

        var plan = CompressionPlanner.PlanVideo(goal, info);

        Assert.Equal(expected, plan.TargetWidth);
        Assert.Equal(PlanDelta.Downscale, plan.Delta);
        Assert.Contains("thu", plan.Reason);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(1280)]
    [InlineData(640)]
    public void Khong_bao_gio_phong_to_tep_nho_hon_tran(int width)
    {
        var info = Video(width, width * 9 / 16, 30, 2000);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Size, info);

        // Nguồn nhỏ hơn trần thì giữ nguyên, không dùng min() để "trần" rồi ra trần.
        Assert.Equal(width, plan.TargetWidth);
        Assert.Equal(PlanDelta.None, plan.Delta);
    }

    [Fact]
    public void Thu_be_rong_thi_bu_trong_mot_bu_crf()
    {
        var wide = Video(3840, 2160, 30, 8000);
        var same = Video(1920, 1080, 30, 8000 / 4.0);

        var thuNho = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, wide);
        var giuNguyen = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, same);

        // Cùng mật độ bit, nhưng tệp bị thu nhỏ thì mất chi tiết theo cả không gian, nên
        // cần nâng (thành giảm) CRF một bước để bù.
        Assert.Equal(giuNguyen.Crf - 1, thuNho.Crf);
    }

    // ---------------------------------------------------------------- quy tắc số khung hình

    [Fact]
    public void Mu_man_hinh_60fps_bi_ha_xuong_30_chi_o_muc_manh()
    {
        var info = Video(1920, 1080, 60, 12000);

        Assert.Equal(30, CompressionPlanner.PlanVideo(CompressionGoal.Size, info).TargetFps);
        Assert.Null(CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info).TargetFps);
        Assert.Null(CompressionPlanner.PlanVideo(CompressionGoal.Quality, info).TargetFps);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    public void Khong_ha_fps_thap_hon_50_ke_ca_o_muc_manh(int fps)
    {
        // 30 -> 24 thì đã thấy rõ (chậm hơn, giật hơn). Không làm.
        var info = Video(1920, 1080, fps, 4000);

        Assert.Null(CompressionPlanner.PlanVideo(CompressionGoal.Size, info).TargetFps);
    }

    [Fact]
    public void Mien_tran_30fps_khi_nguon_60fps_va_muc_manh()
    {
        var info = Video(3840, 2160, 60, 60000);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Size, info);

        // Cả hai điều kiện cùng xảy ra; phải nêu cả hai, không giấu mất một.
        Assert.Equal(1920, plan.TargetWidth);
        Assert.Equal(30, plan.TargetFps);
        Assert.Contains("thu", plan.Reason);
        Assert.Contains("fps", plan.Reason);
    }

    // ---------------------------------------------------------------- âm thanh

    [Fact]
    public void Video_khong_tieng_thi_bo_luon_tieng()
    {
        var info = Video(1920, 1080, 30, 4000, audio: false);

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info);

        Assert.True(plan.DropAudio);
    }

    [Fact]
    public void Bitrate_am_thanh_giam_theo_muc()
    {
        var info = Video(1920, 1080, 30, 4000);

        Assert.Equal(320, CompressionPlanner.PlanVideo(CompressionGoal.Quality, info).AudioBitrateKbps);
        Assert.Equal(192, CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info).AudioBitrateKbps);
        Assert.Equal(128, CompressionPlanner.PlanVideo(CompressionGoal.Size, info).AudioBitrateKbps);
    }

    [Fact]
    public void Tep_mono_ghim_ban_mot_phan_nhu_stereo()
    {
        // Bitrate nguồn cao hơn mức đích để quy tắc "không nâng bitrate nguồn đã nhỏ hơn"
        // không chen vào giữa, làm che mất đúng thứ cần kiểm.
        var mono = new MediaInfo { HasAudio = true, AudioChannels = 1, BitrateKbps = 320 };
        var stereo = new MediaInfo { HasAudio = true, AudioChannels = 2, BitrateKbps = 320 };

        Assert.Equal(96, CompressionPlanner.PlanAudio(CompressionGoal.Size, mono).BitrateKbps);
        Assert.Equal(128, CompressionPlanner.PlanAudio(CompressionGoal.Size, stereo).BitrateKbps);
    }

    [Fact]
    public void Khong_nang_ban_so_am_thanh_da_nho_hon()
    {
        // Nguồn đã 64k mà mức đích 192k: nâng lên chỉ làm tệp to thêm.
        var low = new MediaInfo { HasAudio = true, AudioChannels = 2, BitrateKbps = 64 };

        var plan = CompressionPlanner.PlanAudio(CompressionGoal.Balanced, low);

        Assert.Equal(64, plan.BitrateKbps);
        Assert.Contains("giữ bitrate nguồn", plan.Reason);
    }

    // ---------------------------------------------------------------- ảnh

    [Fact]
    public void Anh_rong_hon_man_hinh_thi_khong_thu()
    {
        var info = new MediaInfo { Width = 1600, Height = 900, IsStillImage = true };

        Assert.Equal(1600, CompressionPlanner.PlanImage(CompressionGoal.Size, info).TargetWidth);
    }

    [Fact]
    public void Anh_8_bit_manh_hoi_chuat_vo_block()
    {
        var info = new MediaInfo { Width = 4000, Height = 3000, BitDepth = 8, IsStillImage = true };

        var plan = CompressionPlanner.PlanImage(CompressionGoal.Size, info);

        // Ảnh 8 bit vỡ block rõ ở -q:v cao, nên phải nới.
        Assert.Equal(12, plan.QScale);
        Assert.Contains("vỡ block", plan.Reason);
    }

    [Fact]
    public void Anh_rat_nho_bi_bo_qua_o_muc_manh()
    {
        // 100 KB: JPEG không cải thiện được nữa, nén thêm chỉ tổn chất lượng.
        var info = new MediaInfo { Width = 800, Height = 600, IsStillImage = true };

        var plan = CompressionPlanner.PlanImage(CompressionGoal.Size, info, sourceBytes: 100 * 1024);

        Assert.Equal(PlanDelta.NotWorthIt, plan.Delta);
    }

    [Theory]
    [InlineData(CompressionGoal.Quality, 3)]
    [InlineData(CompressionGoal.Balanced, 6)]
    [InlineData(CompressionGoal.Size, 10)]
    public void Chat_luong_anh_theo_muc(CompressionGoal goal, int expected)
    {
        // Ảnh lớn, 12 bit: không bị nới, nên -q:v đúng theo mức.
        var info = new MediaInfo { Width = 8000, Height = 6000, BitDepth = 12, IsStillImage = true };

        Assert.Equal(expected, CompressionPlanner.PlanImage(goal, info).QScale);
    }

    // ---------------------------------------------------------------- GIF

    [Fact]
    public void Gif_dai_va_nhanh_bi_ha_khung_hinh()
    {
        var info = new MediaInfo
        {
            Width = 800,
            Height = 600,
            Fps = 30,
            Duration = TimeSpan.FromSeconds(20),
        };

        var plan = CompressionPlanner.PlanGif(CompressionGoal.Size, info);

        Assert.Equal(12, plan.Fps);
        Assert.Contains("20", plan.Reason);
    }

    [Fact]
    public void Gif_ngan_va_cham_giu_nguyen_khung_hinh()
    {
        var info = new MediaInfo
        {
            Width = 400,
            Height = 300,
            Fps = 12,
            Duration = TimeSpan.FromSeconds(3),
        };

        Assert.Equal(12, CompressionPlanner.PlanGif(CompressionGoal.Balanced, info).Fps);
    }

    // ---------------------------------------------------------------- PDF

    [Theory]
    [InlineData(CompressionGoal.Quality, "/default")]
    [InlineData(CompressionGoal.Balanced, "/ebook")]
    [InlineData(CompressionGoal.Size, "/screen")]
    public void Preset_pdf_theo_muc(CompressionGoal goal, string expected)
        => Assert.Equal(expected, CompressionPlanner.PlanPdf(goal).Preset);

    // ---------------------------------------------------------------- độ bền

    [Fact]
    public void Khong_co_du_lieu_probe_van_ke_hoach_duoc()
    {
        // Probe thất bại (tệp đang mở, container lạ) không được làm job hỏng.
        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info: null);

        Assert.Equal(24, plan.Crf);
        Assert.Equal(0, plan.TargetWidth);
        Assert.Null(plan.TargetFps);
        Assert.Equal(string.Empty, plan.Reason);
    }

    [Fact]
    public void Crf_luon_nam_trong_khoang_x264_chap_nhan()
    {
        // x264 chỉ nhận 0..51; ngoài khoảng này ffmpeg báo lỗi và tệp không ra.
        var worst = Video(1920, 1080, 30, 0.01 * 1920 * 1080 * 30 / 1000, "av1");

        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Quality, worst);

        Assert.InRange(plan.Crf, 0, 51);
    }

    [Fact]
    public void Cung_muc_muc_tieu_va_cung_tep_cho_cung_ke_hoach()
    {
        // Tính lại phải cho ra y hệt — tham số nén phải tái lập được.
        var info = Video(1920, 1080, 30, 6000);

        var a = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info);
        var b = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, info);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Mac_dinh_dung_H264_khong_tu_bat_HEVC()
    {
        // HEVC tốn gấp 2-5 lần thời gian. Bản dựng không có libx265 thì lệnh chết và mất
        // tệp, nên mặc định phải là codec luôn chạy được.
        var plan = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, Video(1920, 1080, 30, 6000));

        Assert.Equal("libx264", plan.VideoEncoder);
    }

    [Fact]
    public void Chon_HEVC_thi_dung_libx265_va_bu_crf_cho_khong_bang_H264()
    {
        var h264 = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, Video(1920, 1080, 30, 6000));
        var hevc = CompressionPlanner.PlanVideo(CompressionGoal.Balanced, Video(1920, 1080, 30, 6000), preferHevc: true);

        Assert.Equal("libx265", hevc.VideoEncoder);

        // Thang CRF của HEVC khác H.264: cùng số thì HEVC cho tệp nhỏ hơn, nên phải cộng
        // thêm để chất lượng ngang. Đo trên tệp anime cho mức cộng này.
        Assert.Equal(h264.Crf + 2, hevc.Crf);
        Assert.Contains("HEVC", hevc.Reason);
    }

    [Fact]
    public void Dung_che_do_dinh_dang_theo_thu_tu()
    {
        Assert.Equal(CompressionGoal.Quality, CompressionLevel.Light.ToGoal());
        Assert.Equal(CompressionGoal.Balanced, CompressionLevel.Balanced.ToGoal());
        Assert.Equal(CompressionGoal.Size, CompressionLevel.Strong.ToGoal());
    }
}
