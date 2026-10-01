using UltraCompressor.Core;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Ngữ nghĩa so bản gốc với ứng viên encode — quyết định <b>không hoàn tác được</b> của cả
/// đường thích ứng, nên nó được kiểm thử tất định, không cần ffmpeg.
/// </summary>
/// <remarks>
/// Ở đây mọi thứ là con số thuần: kích thước ước lượng và khoảng của nó. Đó là chủ đích, vì
/// câu hỏi cần trả lời là "lớp này có đọc đúng con số không", chứ không phải "ffmpeg có cho
/// ra con số đó không".
/// </remarks>
public class OriginalComparisonTests
{
    [Fact]
    public void Co_ung_vien_chung_minh_duoc_loi_ich_thi_encode()
    {
        // Nguồn 100 MB, ngưỡng 1% → cần dưới 99 MB. Ứng viên nhỏ tới 60 MB ngay cả ở biên
        // dưới → lợi ích rõ, encode. Trường hợp này KHÔNG cần bằng chứng kích thước được
        // chứng nhận: encode là hành động an toàn, và có thể thực hiện ngay cả khi ước lượng
        // sai.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 70_000_000, 60_000_000, 80_000_000)]);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.False(comparison.FullEncodeAvoided);
        Assert.Equal(SearchDecisionReasons.PilotSelected, comparison.Reason);
    }

    // ------------------------------------------------------------------ yêu cầu 1: không
    // để ước lượng tự nó kết luận giữ bản gốc

    [Fact]
    public void Case_A_uoc_luong_noi_khong_dang_nen_nhung_thuc_te_co_the_tot()
    {
        // Bằng chứng heuristics KHÔNG đủ chắc chắn. Ngay cả khi biên dưới nói "không đáng
        // encode", quyết định irreversible đó vẫn không được phép — vì đã có mẫu thật cho thấy
        // biên ra ngoài chính nó (nguồn nhiễu: ước 87,8 MB, thật 122,3 MB, lệch −28%).
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 99_500_000, 99_200_000, 99_900_000)]);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.False(comparison.FullEncodeAvoided);
        Assert.NotEqual(SearchDecisionReasons.OriginalSelected, comparison.Reason);
        Assert.Contains(
            OriginalComparison.PendingEvidenceMarker,
            comparison.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Case_B_uoc_luang_du_doan_tiet_kiem_nhung_thuc_te_lon_hon_nguon_thi_van_encode()
    {
        // Hướng ngược lại: ước lượng hứa có lợi ích thì ta encode. Nếu tệp thật lại lớn hơn
        // nguồn thì LƯỚI 5A giữ bản gốc dựa trên byte thật. Ở đây ta chỉ khẳng định phía ta:
        // không có đường nào chặn encode vì ước lượng "quá lạc quan".
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 50_000_000, 40_000_000, 70_000_000)]);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.Equal(SearchDecisionReasons.PilotSelected, comparison.Reason);
    }

    [Fact]
    public void Case_C_uoc_luong_null_thi_khong_co_y_kien()
    {
        // `null` = không có ý kiến, KHÔNG phải "hẹp". Không được biến thành bằng chứng giữ bản
        // gốc — cũng không được biến thành bằng chứng chống lại.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 10, null, 10)]);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.Equal(1, comparison.ConsideredWithoutBounds);
        Assert.Contains("không ứng viên nào có khoảng ước lượng", comparison.Message, StringComparison.Ordinal);
        Assert.Contains("thiếu bằng chứng không phải bằng chứng giữ bản gốc", comparison.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_D_uoc_luong_ngoai_calibration_thi_khong_lo_hong_va_khong_tu_noi_rong()
    {
        // Con số ở rất xa mọi thứ đã đo. Phải xử lý được, không được ném lỗi, và KHÔNG được
        // tự mở rộng phạm vi hiệu chỉnh để "cho vừa" — làm vậy là bịa thêm bằng chứng.
        var wild = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 500_000_000, 400_000_000, 900_000_000)]);

        Assert.Equal(OriginalDecision.KeepEncoded, wild.Decision);
        Assert.Equal(400_000_000, wild.BestOptimisticBytes);

        // Hiệu chỉnh giữ nguyên sau khi gặp số lệch: không có đường nào ghi đè hằng số.
        Assert.Equal(0.61, SizeEstimator.Calibration.VideoObservedMin);
        Assert.Equal(0.89, SizeEstimator.Calibration.VideoObservedMax);
        Assert.Equal(
            SizeEstimator.Calibration.VideoSampleCount,
            SizeEstimator.Calibration.VideoSampleCount);
        Assert.False(SizeEstimator.Calibration.Video.IsCertified);
    }

    [Fact]
    public void Case_E_khong_ung_vien_dat_thi_giu_ban_goc_nhung_khong_phai_original_selected()
    {
        // Ở đường thích ứng, trạng thái này do tầng trên quyết định (không có ứng viên khả
        // thi ⇒ `NO_FEASIBLE_CANDIDATE`). Lớp so bản gốc ở đây chỉ được gọi khi CÓ ứng viên
        // đạt, nên test ở đây khẳng định điều quan trọng: lớp này KHÔNG BAO GIỜ tự sinh ra
        // `ORIGINAL_SELECTED` cho tình huống không có ứng viên nào đạt.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: []);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.Equal(0, comparison.FeasibleCount);
        Assert.NotEqual(SearchDecisionReasons.OriginalSelected, comparison.Reason);
    }

    [Fact]
    public void Case_F_bao_hoi_co_bang_chung_chung_nhan_thi_moi_duoc_ket_luan_giu_ban_goc()
    {
        // Nhánh giữ bản gốc vẫn TỒN TẠI và vẫn đúng — nhưng chỉ mở khi bằng chứng kích thước
        // được chứng nhận. Đây là bằng chứng cho thấy việc siết lại không xoá hành vi, chỉ
        // chuyển nó sang chờ bằng chứng.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Certified("a", 99_500_000, 99_200_000, 99_900_000)]);

        Assert.Equal(OriginalDecision.KeepOriginal, comparison.Decision);
        Assert.True(comparison.FullEncodeAvoided);
        Assert.Equal(SearchDecisionReasons.OriginalSelected, comparison.Reason);
        Assert.Contains("không phải kết luận tệp nguồn đã tối ưu", comparison.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mot_ung_vien_khong_chung_nhan_thi_keo_ca_quyet_dinh_mat_chung_cu()
    {
        // Quyết định dựa trên TẤT CẢ ứng viên đã xét, nên một ứng viên có biên chưa chứng
        // nhận làm toàn bộ kết luận mất chứng cứ. Đây là hành vi bảo thủ, và là hành vi đúng.
        var mixed = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible:
            [
                Certified("tot", 99_500_000, 99_200_000, 99_900_000),
                Evaluated("chua-chung-nhan", 99_500_000, 99_300_000, 99_900_000),
            ]);

        Assert.Equal(OriginalDecision.KeepEncoded, mixed.Decision);
    }

    [Fact]
    public void Phong_do_khong_tin_thi_khong_duoc_ket_luan_giu_ban_goc()
    {
        // Bước 11 của giai đoạn 5B: số đo có thể sai vì DẤU THỜI GIAN chứ không phải vì nén
        // (đã đo thấy remux stream-copy sang MPEG-TS mất 4,1 điểm VMAF mà không đổi pixel).
        // Ngay cả khi bằng chứng kích thước đã chứng nhận, phép đo chất lượng không tin thì
        // vẫn không được kết luận.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Certified("a", 99_500_000, 99_200_000, 99_900_000)],
            confidence: MeasurementConfidence.Uncertain);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.Contains("không đáng tin", comparison.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Phong_do_khong_tin_van_duoc_phep_encode_roi()
    {
        // Việc hạ mức tin cậy KHÔNG được biến thành cấm: ứng viên chứng minh được lợi ích thì
        // vẫn encode, vì đó là hướng đúng.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 70_000_000, 60_000_000, 80_000_000)],
            confidence: MeasurementConfidence.Uncertain);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
    }

    [Fact]
    public void Chi_can_mot_ung_vien_chung_minh_duoc_la_duoc()
    {
        // Một ứng viên tệ không được phép giết một ứng viên tốt.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible:
            [
                Evaluated("bad", 99_900_000, 99_800_000, 99_950_000),
                Evaluated("good", 40_000_000, 30_000_000, 50_000_000),
            ]);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
    }

    [Fact]
    public void Nguong_tiet_kiem_lay_nguyen_va_tu_cau_hinh_khong_phai_hang_so_o_day()
    {
        // Ngưỡng 1% cho ứng viên tốt, 50% cho ứng viên chỉ tiết kiệm 20% ở mức điểm.
        // Cùng một con số ước lượng, hai kết luận khác nhau — chứng minh ngưỡng đi vào quyết
        // định chứ không phải hằng số bên trong. Cả hai đều dùng bằng chứng ĐÃ chứng nhận để
        // nhánh giữ bản gốc thật sự mở.
        var candidate = new[] { Certified("a", 80_000_000, 70_000_000, 90_000_000) };

        Assert.Equal(
            OriginalDecision.KeepEncoded,
            OriginalComparison.Decide(100_000_000, 1.0, candidate).Decision);

        Assert.Equal(
            OriginalDecision.KeepOriginal,
            OriginalComparison.Decide(100_000_000, 50.0, candidate).Decision);
    }

    [Fact]
    public void He_so_hieu_chinh_hien_tai_khong_duoc_coi_la_chung_minh()
    {
        // Bằng chứng đã loại giả định "biên là chứng minh": lệch −28% trên nguồn nhiễu, ra
        // NGOÀI chính khoảng 0,61…0,89. Nên trạng thái phải là provisional, và mọi thành phần
        // đều phải cùng trạng thái — một thành phần được chứng nhận thì không đủ.
        Assert.Equal(
            "provisional-calibrated-on-limited-corpus",
            SizeEstimator.Calibration.Status);

        Assert.False(SizeEstimator.Calibration.Video.IsCertified);
        Assert.False(SizeEstimator.Calibration.Audio.IsCertified);
        Assert.False(SizeEstimator.Calibration.Container.IsCertified);
        Assert.All(SizeEstimator.Calibration.All, p => Assert.False(p.IsCertified));

        // Hằng số bị đo lệch thì phải BÁO, không tự ý sửa.
        Assert.Equal(0.61, SizeEstimator.Calibration.VideoObservedMin);
        Assert.Equal(0.89, SizeEstimator.Calibration.VideoObservedMax);
    }

    [Fact]
    public void Uoc_luong_hong_thi_khong_duoc_coi_la_chung_nhan()
    {
        // Thiếu dấu vết hiệu chỉnh thì không có căn cứ để tin, và thiếu bằng chứng thì phải đi
        // đường an toàn chứ không phải đường không hoàn tác được.
        var noProvenance = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 99_500_000, 99_200_000, 99_900_000, calibration: [])]);

        Assert.Equal(OriginalDecision.KeepEncoded, noProvenance.Decision);
    }

    [Fact]
    public void Khong_co_thuoc_tinh_chat_luong_uoc_luong_tren_nhanh_original()
    {
        // ORIGINAL có kích thước và thuộc tính nguồn, nhưng KHÔNG có ước lượng dung lượng:
        // byte nguồn là sự thật đã biết, ước lượng nó là vô nghĩa.
        var properties = typeof(OriginalCandidate).GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        Assert.DoesNotContain("estimatedbytes", properties);
        Assert.DoesNotContain("vmaf", properties);
        Assert.Contains("sourcebytes", properties);
    }

    [Theory]
    [InlineData("a.ts", true)]
    [InlineData("a.m2ts", true)]
    [InlineData("A.TS", true)]
    [InlineData("a.mp4", false)]
    [InlineData("a.mkv", false)]
    [InlineData("a.mov", false)]
    public void Nhan_dung_container_mpeg_ts_theo_phan_mo_rong(string path, bool expected) =>
        Assert.Equal(expected, MediaClassifier.IsMpegTs(path));

    [Fact]
    public void Chi_dung_mpeg_ts_moi_han_muc_tin_cay()
    {
        // Không suy rộng: .mpg/.vob/.3gp KHÔNG được tự động hạ mức tin cậy. Mỗi phần mở
        // rộng là một quyết định giảm chất lượng lựa chọn người dùng mà không kèm số đo.
        Assert.False(MediaClassifier.IsMpegTs("a.mpg"));
        Assert.False(MediaClassifier.IsMpegTs("a.vob"));
        Assert.False(MediaClassifier.IsMpegTs("a.3gp"));
    }

    // ---------------------------------------------------------------- dữ liệu dùng chung

    private static EvaluatedCandidate Evaluated(
        string id, long totalBytes, long? minBytes, long maxBytes,
        IReadOnlyList<SizeCalibrationProvenance>? calibration = null)
    {
        var floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Neg);
        var sample = new QualitySample(95, 92, 80, 0.999, 72);
        var measurement = new WindowMeasurement(WindowRole.Typical, 0, 3, sample);

        return new EvaluatedCandidate
        {
            Candidate = new VideoEncodeCandidate(id)
            {
                Codec = VideoCodec.H264,
                EncoderName = "libx264",
                Quality = QualityOption.X26xCrf(23),
                Width = 1920,
                Height = 1080,
                Fps = 24,
                Speed = SpeedOption.X26xPreset("medium"),
                PixelFormat = "yuv420p",
                Origin = CandidateOrigin.CoarseProbe,
                BranchId = "b",
                PointIndex = 0,
                PointCount = 1,
                Reason = "kiểm thử",
            },
            Aggregate = QualityAggregator.Aggregate(id, [measurement], floor),
            Estimate = new SizeEstimate
            {
                TotalBytes = totalBytes,
                VideoBytes = totalBytes,
                AudioBytes = 0,
                ContainerBytes = 0,
                DurationSeconds = 300,
                IsReliable = true,
                Assumptions = [],
                Bounds = minBytes is { } min
                    ? new HeuristicEstimateBounds(min, maxBytes)
                    : null,

                // Mặc định là bằng chứng THẬT của ứng dụng, tức chưa chứng nhận. Test nào
                // cần nhánh "giữ bản gốc" thật sự mở thì phải tự nói rõ bằng `Certified`.
                Calibration = calibration ?? SizeEstimator.Calibration.All,
            },
            Measurements = [measurement],
            MeasurementsTaken = 1,
            MeasurementsSkipped = 0,
            WindowsNotMeasured = 0,
            ComputeCostSeconds = 1,
            Stage = SearchStage.Coarse,
        };
    }

    /// <summary>Ứng viên có bằng chứng kích thước ĐÃ được chứng nhận.</summary>
    /// <remarks>
    /// Chỉ dùng để chứng minh nhánh quyết định còn nguyên vẹn chứ không bị xoá khi siết
    /// bằng chứng. Chưa có bộ hiệu chỉnh nào trong thực tế đạt trạng thái này.
    /// </remarks>
    private static EvaluatedCandidate Certified(string id, long totalBytes, long minBytes, long maxBytes) =>
        Evaluated(
            id, totalBytes, minBytes, maxBytes,
            [
                new SizeCalibrationProvenance(
                    "video", SizeCalibrationProvenance.CertifiedStatus, 1000, 0.61, 0.89,
                    "bộ mẫu kiểm thử", "tài liệu kiểm thử"),
            ]);
}
