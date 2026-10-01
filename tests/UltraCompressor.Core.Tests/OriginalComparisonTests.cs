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
        // dưới → lợi ích rõ, encode.
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 70_000_000, 60_000_000, 80_000_000)]);

        Assert.Equal(OriginalDecision.KeepEncoded, comparison.Decision);
        Assert.False(comparison.FullEncodeAvoided);
        Assert.Equal(SearchDecisionReasons.PilotSelected, comparison.Reason);
    }

    [Fact]
    public void Khong_ung_vien_nao_chung_minh_duoc_thi_giu_ban_goc()
    {
        // Ứng viên ước 99,5 MB trên nguồn 100 MB: chỉ tiết kiệm 0,5%, dưới ngưỡng 1%. Nhưng
        // ngay cả ở biên dưới (99,5 × 0,89 ≈ 88,5 MB) nó vẫn nhỏ hơn 99 MB — nên ở đây vẫn
        // chứng minh được lợi ích. Đổi biên dưới lên trên mức yêu cầu thì mới thua.
        var stillProven = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 99_500_000, 88_500_000, 110_000_000)]);

        Assert.Equal(OriginalDecision.KeepEncoded, stillProven.Decision);

        var notProven = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 99_500_000, 99_200_000, 99_900_000)]);

        Assert.Equal(OriginalDecision.KeepOriginal, notProven.Decision);
        Assert.True(notProven.FullEncodeAvoided);
        Assert.Equal(SearchDecisionReasons.OriginalSelected, notProven.Reason);
        Assert.Equal(99_000_000, notProven.RequiredBytes);
        Assert.Equal(99_200_000, notProven.BestOptimisticBytes);
    }

    [Fact]
    public void Ket_luan_giu_ban_goc_phai_noi_day_la_thieu_bang_chung()
    {
        var comparison = OriginalComparison.Decide(
            sourceBytes: 100_000_000,
            minSavingPercent: 1.0,
            feasible: [Evaluated("a", 99_500_000, 99_200_000, 99_900_000)]);

        // TUYỆT ĐỐI không được tuyên bố tệp nguồn đã tối ưu — ta không có căn cứ, và người đọc
        // sẽ tin câu đó.
        Assert.Contains("không phải kết luận tệp nguồn đã tối ưu", comparison.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("đã tối ưu", comparison.Message.Replace(
            "không phải kết luận tệp nguồn đã tối ưu", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Khong_co_ung_vien_khung_gi_thi_khong_duoc_bac_vao_chung_minh()
    {
        // Ứng viên không có khoảng thì không có cách nào chứng minh là không đáng encode, và
        // đó là lý do để KHÔNG giữ bản gốc.
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
    public void Chi_can_mot_ung_vien_chung_minh_duoc_la_duoc()
    {
        // Một ứng viên tệ không được phép giết một ứng viên tốt. Ứng viên hàng 1 thua, hàng 2
        // thắt — kết luận phải là encode.
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
    public void Phong_do_khong_tin_thi_khong_duoc_ket_luan_giu_ban_goc()
    {
        // Bước 11 của giai đoạn: số đo có thể sai vì DẤU THỜI GIAN chứ không phải vì nén
        // (đã đo thấy remux stream-copy sang MPEG-TS mất 4,1 điểm VMAF mà không đổi pixel).
        // "Không chứng minh được lợi ích" khi đó chỉ có nghĩa là TA CHƯA CHỨNG MINH ĐƯỢC, chứ
        // không phải là không có lợi ích — nên không đủ để bỏ một lần encode.
        var args = (
            SourceBytes: 100_000_000,
            MinSavingPercent: 1.0,
            Feasible: new[] { Evaluated("a", 99_500_000, 99_200_000, 99_900_000) });

        var trusted = OriginalComparison.Decide(
            args.SourceBytes, args.MinSavingPercent, args.Feasible, MeasurementConfidence.Trusted);

        var uncertain = OriginalComparison.Decide(
            args.SourceBytes, args.MinSavingPercent, args.Feasible, MeasurementConfidence.Uncertain);

        Assert.Equal(OriginalDecision.KeepOriginal, trusted.Decision);
        Assert.Equal(OriginalDecision.KeepEncoded, uncertain.Decision);
        Assert.Equal(MeasurementConfidence.Uncertain, uncertain.Confidence);
        Assert.Contains("không đáng tin", uncertain.Message, StringComparison.Ordinal);
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
    public void Nguong_tiet_kiem_lay_nguyen_va_tu_cau_hinh_khong_phai_hang_so_o_day()
    {
        // Ngưỡng 1% cho ứng viên tốt, 50% cho ứng viên chỉ tiết kiệm 20% ở mức điểm.
        // Cùng một con số ước lượng, hai kết luận khác nhau — chứng minh ngưỡng đi vào quyết
        // định chứ không phải hằng số bên trong.
        var candidate = new[] { Evaluated("a", 80_000_000, 70_000_000, 90_000_000) };

        Assert.Equal(
            OriginalDecision.KeepEncoded,
            OriginalComparison.Decide(100_000_000, 1.0, candidate).Decision);

        Assert.Equal(
            OriginalDecision.KeepOriginal,
            OriginalComparison.Decide(100_000_000, 50.0, candidate).Decision);
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
        string id, long totalBytes, long? minBytes, long maxBytes)
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
            },
            Measurements = [measurement],
            MeasurementsTaken = 1,
            MeasurementsSkipped = 0,
            WindowsNotMeasured = 0,
            ComputeCostSeconds = 1,
            Stage = SearchStage.Coarse,
        };
    }
}
