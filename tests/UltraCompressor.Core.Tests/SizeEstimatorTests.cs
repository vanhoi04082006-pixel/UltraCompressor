using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>Ngữ nghĩa của <see cref="SizeEstimator"/>.</summary>
public class SizeEstimatorTests
{
    private static PilotArtifact Artifact(
        long bytes, double windowSeconds, WindowRole role = WindowRole.Typical, bool ok = true) =>
        new(
            CandidateId: "c",
            Role: role,
            ReferenceWindow: new TimeWindow(10, windowSeconds),
            CandidateWindow: new TimeWindow(0, windowSeconds),
            OutputPath: ok ? "x.mp4" : null,
            Bytes: ok ? bytes : 0,
            Target: new EncodeTarget(1920, 1080),
            FfmpegArguments: [],
            Elapsed: TimeSpan.FromSeconds(1),
            Outcome: new SearchOutcome(
                ok ? SearchDecisionReasons.PilotSelected : SearchDecisionReasons.PilotEncodeFailed, ""));

    [Fact]
    public void Video_duoc_tinh_theo_ti_le_thoi_luong_roi_nhan_he_so()
    {
        // 300.000 B trong 3 giây = 100.000 B/s. Nguồn 100 giây -> 10.000.000 B thô, rồi
        // nhân hệ số hiệu chỉnh đã đo (0,72). Test cũ khẳng định 10.000.000 — đúng với
        // hàm CHƯA hiệu chỉnh; từ khi có hệ số thì 10.000.000 là con số thô trung gian,
        // không phải đáp án.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)], fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        Assert.Equal(7_200_000, result.VideoBytes);
        Assert.Equal(0, result.AudioBytes);
        Assert.Equal(7_200_000 + result.ContainerBytes, result.TotalBytes);
    }

    [Fact]
    public void Khoang_heuristic_boc_duoc_ua_uoc_luong()
    {
        // 100.000 B/s × 100s = 10.000.000 B thô. Biên dưới = ×0,61, biên trên = ×0,89.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)], fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        var bounds = Assert.IsType<HeuristicEstimateBounds>(result.Bounds);
        Assert.Equal(6_100_000 + result.ContainerBytes, bounds.MinBytes);
        Assert.Equal(8_900_000 + result.ContainerBytes, bounds.MaxBytes);

        // Điểm ước lượng phải NẰM TRONG khoảng — hệ số trung bình phải là con số đo được,
        // không phải con số bịa ra ngoài khoảng.
        Assert.InRange(result.TotalBytes, bounds.MinBytes, bounds.MaxBytes);
        Assert.True(bounds.IsUsable);
    }

    [Fact]
    public void Khong_du_lieu_thi_khong_co_khoang()
    {
        // `null` nghĩa là "không có ý kiến", KHÔNG phải "hẹp". Khoảng rộng bằng 0 ở đây sẽ
        // biến thành khẳng định sai là tệp nhỏ hơn mọi thứ.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)], fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: true);

        Assert.Null(result.Bounds);
        Assert.False(result.IsReliable);
    }

    [Fact]
    public void Uoc_luong_hong_khong_co_khoang()
    {
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0, ok: false)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: 128, hasAudio: true);

        Assert.Null(result.Bounds);
        Assert.Empty(result.Calibration);
    }

    [Fact]
    public void Khoang_khong_phai_khoang_tin_cay_thong_ke()
    {
        // Biên phải bao quanh cả hai đầu quan sát được, đúng hệ số đã đo.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)], fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        var bounds = Assert.IsType<HeuristicEstimateBounds>(result.Bounds);
        var raw = 10_000_000d;

        Assert.Equal(raw * SizeEstimator.Calibration.VideoObservedMin + result.ContainerBytes, bounds.MinBytes, 0);
        Assert.Equal(raw * SizeEstimator.Calibration.VideoObservedMax + result.ContainerBytes, bounds.MaxBytes, 0);

        // Và phải nói rõ mình chỉ có N mẫu, chưa hiệu chỉnh rộng.
        Assert.Contains(
            SizeEstimator.Calibration.Status,
            result.Calibration.Select(c => c.Status),
            StringComparer.Ordinal);
        Assert.Equal(SizeEstimator.Calibration.VideoSampleCount, result.Calibration[0].SampleCount);
    }

    [Fact]
    public void Nguon_goc_hieu_chinh_phai_ghi_so_mau_va_trang_thai()
    {
        // Con số mà không kèm số mẫu thì y như không có con số: người đọc không biết đó là
        // trung bình 7 tệp hay 7 tệp của cùng một người quay.
        var video = SizeEstimator.Calibration.Video;

        Assert.Equal(SizeEstimator.Calibration.Status, video.Status);
        Assert.Equal(SizeEstimator.Calibration.VideoSampleCount, video.SampleCount);
        Assert.True(video.ObservedMin < video.ObservedMax);
        Assert.Contains("provisional", video.Status, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(video.Corpus));
        Assert.False(string.IsNullOrWhiteSpace(video.Documentation));
    }

    [Fact]
    public void He_so_hieu_chinh_phai_boc_trong_do_lang_quan_sat_duoc()
    {
        // Hệ số dùng để tính phải nằm trong khoảng đo được — nếu không thì "biên" được dựng
        // từ độ lan tỉa mà điểm ước lượng lại nằm ngoài, tức tự mâu thuẫn.
        Assert.InRange(
            SizeEstimator.VideoBytesCalibrationFactor,
            SizeEstimator.Calibration.VideoObservedMin,
            SizeEstimator.Calibration.VideoObservedMax);

        Assert.InRange(
            SizeEstimator.AudioBytesCalibrationFactor,
            SizeEstimator.Calibration.AudioObservedMin,
            SizeEstimator.Calibration.AudioObservedMax);

        Assert.Equal(SizeEstimator.Calibration.VideoObservedMin, 0.61);
        Assert.Equal(SizeEstimator.Calibration.VideoObservedMax, 0.89);
        Assert.Equal(7, SizeEstimator.Calibration.VideoSampleCount);
    }

    [Fact]
    public void Lay_doan_dat_nhat_chu_phai_trung_binh()
    {
        // Một đoạn rẻ bất thường (cảnh tĩnh) kéo trung bình xuống, làm ứng viên trông
        // nhỏ hơn thực tế. Dùng đoạn đắc nhất là ước lượng thận trọng.
        var result = SizeEstimator.Estimate(
            [
                Artifact(600_000, 3.0, WindowRole.Typical),
                Artifact(30_000, 3.0, WindowRole.LowComplexity),
            ],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        // 600.000 / 3 = 200.000 B/s, không phải (600.000 + 30.000) / 6 = 105.000 B/s —
        // rồi nhân hệ số hiệu chỉnh 0,72.
        Assert.Equal(14_400_000, result.VideoBytes);
    }

    [Fact]
    public void Phan_am_thanh_duoc_tinh_rieng()
    {
        // 128 kb/s = 16.000 B/s. 100 giây -> 1.600.000 B thô, nhân hệ số audio 0,98.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: 128, hasAudio: true);

        Assert.Equal(1_568_000, result.AudioBytes);
        Assert.Equal(7_200_000, result.VideoBytes);
        Assert.Equal(7_200_000 + 1_568_000 + result.ContainerBytes, result.TotalBytes);
        Assert.Contains(result.Assumptions, a => a.Contains("audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Khong_biet_bitrate_am_thanh_thi_bao_khong_dang_tin()
    {
        // Không biết phần âm thanh thì tổng ước lượng chắc chắn THẤP hơn thực tế. Phải nói
        // ra chứ không trả một số trông chắc chắn.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: true);

        Assert.Equal(0, result.AudioBytes);
        Assert.False(result.IsReliable);
        Assert.Contains(result.Assumptions, a => a.Contains("KHÔNG biết bitrate", StringComparison.Ordinal));
    }

    [Fact]
    public void Clip_thu_nghiem_khong_co_am_thanh()
    {
        // Clip thử nghiệm bỏ âm thanh có chủ đích, nên phần này chỉ được suy ra từ metadata
        // nguồn. Khi nguồn không có âm thanh thì bằng 0 là đúng.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: 128, hasAudio: false);

        Assert.Equal(0, result.AudioBytes);
        Assert.True(result.IsReliable);
    }

    [Fact]
    public void Clip_hoan_chinh_va_clip_hong_khong_duoc_dem()
    {
        var result = SizeEstimator.Estimate(
            [
                Artifact(300_000, 3.0, WindowRole.HighMotion),
                Artifact(0, 3.0, WindowRole.Typical, ok: false),
            ],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        Assert.Equal(7_200_000, result.VideoBytes);
        Assert.Contains("1 đoạn", string.Join(' ', result.Assumptions), StringComparison.Ordinal);
    }

    [Fact]
    public void Khong_co_clip_nao_thanh_cong_thi_tra_ve_khong()
    {
        var result = SizeEstimator.Estimate(
            [Artifact(0, 3.0, ok: false)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: 128, hasAudio: true);

        Assert.Equal(0, result.TotalBytes);
        Assert.False(result.IsReliable);
        Assert.Contains(result.Assumptions, a => a.Contains("không có clip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Danh_sach_rong_thi_khong_duoc_a_loi()
    {
        var result = SizeEstimator.Estimate(
            [], fullDurationSeconds: 100, sourceAudioBitrateKbps: 128, hasAudio: true);

        Assert.Equal(0, result.TotalBytes);
        Assert.False(result.IsReliable);
    }

    [Fact]
    public void Thoi_luong_am_thu_can_bang_ba_thi_bao_khong_dang_tin()
    {
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)],
            fullDurationSeconds: 0, sourceAudioBitrateKbps: null, hasAudio: false);

        Assert.False(result.IsReliable);
        Assert.Contains(result.Assumptions, a => a.Contains("thời lượng", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Thoi_luong_am_thu_hoac_ban_ma_khong_tao_so_am()
    {
        foreach (var duration in new[] { -5.0, 0.0 })
        {
            var result = SizeEstimator.Estimate(
                [Artifact(300_000, 3.0)],
                fullDurationSeconds: duration, sourceAudioBitrateKbps: 128, hasAudio: true);

            Assert.True(result.TotalBytes >= 0, $"tổng âm: {result.TotalBytes}");
            Assert.True(result.VideoBytes >= 0);
            Assert.True(result.AudioBytes >= 0);
            Assert.True(result.ContainerBytes >= 0);
        }
    }

    [Fact]
    public void Bitrate_am_thanh_am_thi_buoc_phai_bang_khong()
    {
        // 0 kb/s là dữ liệu hỏng, không phải "không có âm thanh". Phải bỏ qua chứ không
        // nhân thành 0 một cách âm thầm rồi báo là đáng tin.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: 0, hasAudio: true);

        Assert.Equal(0, result.AudioBytes);
        Assert.False(result.IsReliable);
    }

    [Fact]
    public void Chi_mot_doan_thi_ghi_ro_la_kiem_chua_duoc()
    {
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        Assert.Contains(result.Assumptions, a => a.Contains("1 đoạn", StringComparison.Ordinal));
    }

    [Fact]
    public void Gia_dinh_ve_container_duoc_ghi_ro_la_so_bo()
    {
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0), Artifact(300_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        Assert.True(result.ContainerBytes > 0);
        Assert.Contains(result.Assumptions, a => a.Contains("Mp4TrackSizes", StringComparison.Ordinal));
    }

    [Fact]
    public void Pho_v_container_khong_duoc_nhan_voi_so_doan_thu_nghiem()
    {
        // Tệp đầu ra là MỘT tệp. Thử nghiệm ba ứng viên hay một ứng viên không làm phần vỏ
        // container của tệp đó nặng thêm. Bản trước nhân với số đoạn dựa trên suy luận rằng
        // vỏ tính theo từng đoạn ghép — điều không có cơ sở nào ở đây.
        var one = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)], 100, null, false);
        var many = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0), Artifact(300_000, 3.0), Artifact(300_000, 3.0)],
            100, null, false);

        Assert.Equal(one.ContainerBytes, many.ContainerBytes);
        Assert.Equal(
            SizeEstimator.ContainerBaseBytes + SizeEstimator.ContainerBytesPerSecond * 100,
            one.ContainerBytes);
    }

    [Fact]
    public void Phan_tram_tiet_kiem_am_nghia_la_uoc_lon_hon_nguon()
    {
        var result = SizeEstimator.Estimate(
            [Artifact(600_000, 3.0)],
            fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        // 20 MB thô × 0,72 = 14,4 MB ước lượng so với nguồn 10 MB -> tăng 44%.
        Assert.True(result.SavingPercent(10_000_000) < 0);
        Assert.Equal(0, result.SavingPercent(0));
    }

    [Fact]
    public void He_so_hieu_chinh_video_duoc_ap_dung()
    {
        // 300.000 B / 3s = 100.000 B/s × 100s = 10.000.000 thô. Con số thô thừa có hệ
        // thống trên encode thật, nên phải nhân hệ số hiệu chỉnh đã đo — không phải để
        // "đẹp số", mà để báo cáo gần sự thật hơn khi so với dung lượng thật.
        var result = SizeEstimator.Estimate(
            [Artifact(300_000, 3.0)], fullDurationSeconds: 100, sourceAudioBitrateKbps: null, hasAudio: false);

        var expected = (long)Math.Round(
            10_000_000 * SizeEstimator.VideoBytesCalibrationFactor, MidpointRounding.AwayFromZero);

        Assert.Equal(expected, result.VideoBytes);
        Assert.True(
            SizeEstimator.VideoBytesCalibrationFactor > 0 && SizeEstimator.VideoBytesCalibrationFactor <= 1,
            "hệ số hiệu chỉnh chỉ được thu nhỏ ước lượng thừa, không được phóng to hay đảo dấu");
    }

    [Fact]
    public void He_so_hieu_chinh_khong_doi_thu_tu_xep_hang()
    {
        // Hệ số là một phép nhân đơn điệu: ứng viên nào lớn hơn trước thì vẫn lớn hơn sau.
        // Nếu thứ tự đổi thì hệ số đang bóp méo chứ không phải hiệu chỉnh.
        var small = SizeEstimator.Estimate([Artifact(300_000, 3.0)], 100, null, false);
        var big = SizeEstimator.Estimate([Artifact(600_000, 3.0)], 100, null, false);

        Assert.True(small.VideoBytes < big.VideoBytes);
        Assert.True(small.TotalBytes < big.TotalBytes);
    }

    [Fact]
    public void He_so_am_thanh_la_gia_tri_do_duoc_khong_phai_mot()
    {
        // Ban đầu giữ bằng 1 với lập luận "đã dùng mục tiêu nên không cần hiệu chỉnh".
        // Đo 4 mẫu cho thấy encoder undershoot có hệ thống ~2% (0,975…0,989) — nhỏ nhưng
        // thật và khoảng hẹp, nên hiệu chỉnh được. Giữ 1 lúc này mới là bỏ qua số đo.
        Assert.Equal(0.98, SizeEstimator.AudioBytesCalibrationFactor);
    }

    [Fact]
    public void Bitrate_am_thanh_khong_hop_le_thi_khong_tao_so_am()
    {
        foreach (var kbps in new double?[] { null, 0, -5 })
        {
            var result = SizeEstimator.Estimate(
                [Artifact(300_000, 3.0)],
                fullDurationSeconds: 100, sourceAudioBitrateKbps: kbps, hasAudio: true);

            Assert.True(result.AudioBytes >= 0);
            Assert.True(result.TotalBytes >= 0);
            Assert.False(result.IsReliable);
        }
    }

    [Fact]
    public void Ket_qua_tat_dinh_giua_hai_lan_goi()
    {
        var artifacts = new[] { Artifact(300_000, 3.0, WindowRole.HighMotion), Artifact(450_000, 3.0) };

        var a = SizeEstimator.Estimate(artifacts, 100, 128, true);
        var b = SizeEstimator.Estimate(artifacts, 100, 128, true);

        Assert.Equal(a.TotalBytes, b.TotalBytes);
        Assert.Equal(a.VideoBytes, b.VideoBytes);
        Assert.Equal(a.AudioBytes, b.AudioBytes);
        Assert.Equal(a.Assumptions, b.Assumptions);
    }
}
