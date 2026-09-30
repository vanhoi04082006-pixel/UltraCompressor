using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>Ngữ nghĩa của <see cref="ParetoSelector"/>.</summary>
public class ParetoSelectorTests
{
    private static QualityAggregate Feasible(string id) => new()
    {
        CandidateId = id,
        IsFeasible = true,
        RequiredCount = 1,
        MeasuredCount = 1,
        Floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Default),
        Outcome = new SearchOutcome(SearchDecisionReasons.PilotSelected, "đạt"),
    };

    private static QualityAggregate Failing(string id) => new()
    {
        CandidateId = id,
        IsFeasible = false,
        RequiredCount = 1,
        MeasuredCount = 1,
        Floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Default),
        Outcome = new SearchOutcome(SearchDecisionReasons.PilotWindowQualityFailed, "rớt"),
    };

    private static SizeEstimate Estimate(long bytes) => new()
    {
        TotalBytes = bytes,
        VideoBytes = bytes,
        AudioBytes = 0,
        ContainerBytes = 0,
        DurationSeconds = 100,
        IsReliable = true,
        Assumptions = [],
    };

    private static ScoredCandidate C(
        string id, double quality, long bytes, VideoCodec codec = VideoCodec.H264,
        double cost = 1.0, bool feasible = true) =>
        new(id, codec, quality, bytes, cost,
            feasible ? Feasible(id) : Failing(id), Estimate(bytes));

    [Fact]
    public void Ung_vien_khong_che_can_truong_bi_lo()
    {
        // A: chất lượng 92, 10 MB. B: 91, 14 MB. A không kém ở chiều nào và nhỏ hơn 4 MB.
        var result = ParetoSelector.Select([
            C("a", 92, 10_000_000),
            C("b", 91, 14_000_000),
        ]);

        Assert.Equal(["a"], result.Frontier.Select(c => c.CandidateId));
        Assert.Single(result.Rejected);
        Assert.Equal("b", result.Rejected[0].CandidateId);
        Assert.Equal(SearchDecisionReasons.PilotDominated, result.Rejected[0].Reason);
    }

    [Fact]
    public void Hai_dinh_dang_khac_nhau_giu_ca_hai_tren_frontier()
    {
        // C: chất lượng cao nhưng nặng. D: nhẹ nhưng chất lượng thấp. Không áp đảo nhau.
        var result = ParetoSelector.Select([
            C("c", 95, 16_000_000),
            C("d", 90, 9_000_000),
        ]);

        Assert.Equal(2, result.Frontier.Count);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Chi_chat_luong_bang_nhau_thi_ung_vien_nho_hon_thang()
    {
        var result = ParetoSelector.Select([
            C("lon", 92, 12_000_000),
            C("nho", 92, 10_000_000),
        ]);

        Assert.Equal(["nho"], result.Frontier.Select(c => c.CandidateId));
    }

    [Fact]
    public void Chi_dung_luong_bang_nhau_thi_ung_vien_chat_luong_cao_hon_thang()
    {
        var result = ParetoSelector.Select([
            C("thap", 89, 10_000_000),
            C("cao", 93, 10_000_000),
        ]);

        Assert.Equal(["cao"], result.Frontier.Select(c => c.CandidateId));
    }

    [Fact]
    public void Chenh_lech_duoi_nguong_epsilon_khong_coi_la_tot_hon()
    {
        // VMAF lệch 0,1 là nhiễu đo, không phải chất lượng khác. Coi là khác thì xếp hạng
        // theo nhiễu.
        Assert.False(ParetoSelector.Dominates(C("a", 92.1, 10_000_000), C("b", 92.0, 10_000_000)));
        Assert.True(ParetoSelector.Dominates(C("a", 93.0, 10_000_000), C("b", 92.0, 10_000_000)));
    }

    [Fact]
    public void Hai_chieu_bang_nhau_khong_phai_la_ap_dao()
    {
        // Trùng nhau thì không ai áp đảo ai; nếu coi là áp đảo thì sẽ loánh một trong hai
        // ứng viên giống hệt nhau và giữ một cái vô nghĩa.
        var result = ParetoSelector.Select([
            C("a", 92.0, 10_000_000),
            C("b", 92.0, 10_000_000),
        ]);

        Assert.Equal(2, result.Frontier.Count);
    }

    [Fact]
    public void Ung_vien_khong_kha_thi_bi_lo_ngay_truoc_va_khong_vao_frontier()
    {
        // Ứng viên nhỏ nhất trong tập nhưng rớt ngưỡng: không được phép đi tiếp dù nhỏ đến đâu.
        var result = ParetoSelector.Select([
            C("nho_nhat", 80, 5_000_000, feasible: false),
            C("lon_hon", 93, 12_000_000),
        ]);

        Assert.Equal(["lon_hon"], result.Frontier.Select(c => c.CandidateId));
        Assert.Single(result.Infeasible);
        Assert.Equal("nho_nhat", result.Infeasible[0].CandidateId);
        Assert.Equal(SearchDecisionReasons.PilotWindowQualityFailed, result.Infeasible[0].Reason);
    }

    [Fact]
    public void Tham_so_encoder_cua_cac_codec_khong_duoc_dung_de_so()
    {
        // Ứng viên HEVC có "chất lượng" thấp hơn theo VMAF đo được nhưng tham số encoder
        // thì lớn hơn. Nếu ta lỡ dùng tham số thay VMAF thì sẽ loại nhầm. Test này chỉ đúng
        // khi chỉ VMAF mới vào phép so — nếu ai thêm tham số thô vào đây thì nó vẫn xanh
        // (vì hàm không dùng), nên nó tài liệu hoá ranh giới chứ không tự bắt lỗi.
        var hevc = new VideoEncodeCandidate("hevc/1920x1080/crf28")
        {
            Codec = VideoCodec.Hevc,
            EncoderName = "libx265",
            Quality = UltraCompressor.Core.Encoders.QualityOption.X26xCrf(28),
            Width = 1920,
            Height = 1080,
            Fps = 24,
            Speed = UltraCompressor.Core.Encoders.SpeedOption.X26xPreset("medium"),
            PixelFormat = "yuv420p",
            Origin = CandidateOrigin.CoarseProbe,
            BranchId = "Hevc/1920x1080",
            PointIndex = 0,
            PointCount = 3,
            Reason = "",
        };

        var av1 = new VideoEncodeCandidate("av1/1920x1080/crf32")
        {
            Codec = VideoCodec.Av1,
            EncoderName = "libaom-av1",
            Quality = UltraCompressor.Core.Encoders.QualityOption.LibaomCrf(32),
            Width = 1920,
            Height = 1080,
            Fps = 24,
            Speed = UltraCompressor.Core.Encoders.SpeedOption.AomCpuUsed(6),
            PixelFormat = "yuv420p",
            Origin = CandidateOrigin.CoarseProbe,
            BranchId = "Av1/1920x1080",
            PointIndex = 0,
            PointCount = 3,
            Reason = "",
        };

        // Tham số thô: AV1 "lớn hơn" (32 > 28) nên lẽ ra bị coi là tệ hơn. Nhưng VMAF đo
        // được của nó cao hơn, nên nó phải thắng.
        Assert.True(av1.QualityValue > hevc.QualityValue);

        var result = ParetoSelector.Select([
            new ScoredCandidate(hevc.Id, VideoCodec.Hevc, 90.0, 10_000_000, 1, Feasible(hevc.Id), Estimate(10_000_000)),
            new ScoredCandidate(av1.Id, VideoCodec.Av1, 93.0, 10_000_000, 1, Feasible(av1.Id), Estimate(10_000_000)),
        ]);

        Assert.Equal([av1.Id], result.Frontier.Select(c => c.CandidateId));
    }

    [Fact]
    public void Thu_tu_khong_doi_giua_cac_lan_chay()
    {
        // Các ứng viên được giao chạy song song nên không có thứ tự nào cố định. Nếu thứ tự
        // ảnh hưởng tới kết quả thì lần chạy sau cho ra lựa chọn khác.
        var candidates = new[]
        {
            C("a", 93, 10_000_000, VideoCodec.H264, cost: 2.0),
            C("b", 91, 9_000_000, VideoCodec.Hevc, cost: 3.0),
            C("c", 95, 14_000_000, VideoCodec.Av1, cost: 1.0),
        };

        var baseline = ParetoSelector.Select(candidates).Frontier.Select(c => c.CandidateId).ToList();

        foreach (var shuffle in new[] { candidates.Reverse().ToArray() })
        {
            var again = ParetoSelector.Select(shuffle).Frontier.Select(c => c.CandidateId).ToList();
            Assert.Equal(baseline, again);
        }

        // Thứ tự cố định: chất lượng giảm dần.
        Assert.Equal(["c", "a", "b"], baseline);
    }

    [Fact]
    public void Danh_sach_rong_thi_khong_co_frontier()
    {
        var result = ParetoSelector.Select([]);

        Assert.Empty(result.Frontier);
        Assert.Empty(result.Rejected);
        Assert.Empty(result.Infeasible);
    }
}
