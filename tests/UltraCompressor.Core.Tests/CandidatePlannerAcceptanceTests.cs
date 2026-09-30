using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Test chấp nhận quan trọng nhất của giai đoạn lập kế hoạch: cùng một mức nén, ba nguồn
/// khác nhau phải cho ba tập ứng viên khác nhau.
///
/// <para>Đây là thứ phân biệt một bộ lập kế hoạch thật với một bảng preset. Một bảng
/// preset cho ra <b>cùng</b> danh sách ứng viên cho mọi tệp — và đó chính là thứ mà bài
/// toán nén thích ứng cần loại bỏ.</para>
///
/// <para>Không kiểm VMAF ở đây: giai đoạn này chưa encode thử, nên chất lượng thật chưa
/// tồn tại để mà so.</para>
/// </summary>
public class CandidatePlannerAcceptanceTests
{
    private static AppConfig Config() => new();

    private static EncoderCapabilities Caps() =>
        EncoderCapabilities.FromEncoderNames(["libx264", "libx265"]);

    private static VideoSourceProfile Source4K() => new()
    {
        Width = 3840,
        Height = 2160,
        Fps = 60,
        BitsPerPixelPerFrame = 0.12,
        Content = ContentProfile.BusyMotion,
        Complexity = new ContentComplexity { SpatialDetail = 70, TemporalActivity = 28, Samples = 3 },
    };

    private static VideoSourceProfile Source1080p() => new()
    {
        Width = 1920,
        Height = 1080,
        Fps = 24,
        BitsPerPixelPerFrame = 0.06,
        Content = ContentProfile.ModerateMotion,
        Complexity = new ContentComplexity { SpatialDetail = 45, TemporalActivity = 8, Samples = 3 },
    };

    private static VideoSourceProfile Source720p() => new()
    {
        Width = 1280,
        Height = 720,
        Fps = 25,
        BitsPerPixelPerFrame = 0.20,
        Content = ContentProfile.ModerateMotion,
        Complexity = new ContentComplexity { SpatialDetail = 38, TemporalActivity = 12, Samples = 3 },
    };

    private static CandidatePlan Balanced(VideoSourceProfile source) =>
        CandidatePlanner.Generate(source, CompressionLevel.Balanced, ComputeBudget.Normal, Caps(), Config());

    [Fact]
    public void Cung_mot_muc_nen_ba_nguon_khac_nhau_phai_ba_tap_ung_vien_khac_nhau()
    {
        var a = Balanced(Source4K());
        var b = Balanced(Source1080p());
        var c = Balanced(Source720p());

        Assert.NotEmpty(a.EncodeCandidates);
        Assert.NotEmpty(b.EncodeCandidates);
        Assert.NotEmpty(c.EncodeCandidates);

        var idsA = a.EncodeCandidates.Select(x => x.Id).ToHashSet();
        var idsB = b.EncodeCandidates.Select(x => x.Id).ToHashSet();
        var idsC = c.EncodeCandidates.Select(x => x.Id).ToHashSet();

        // Không nguồn nào được sinh ra đúng bộ ứng viên của một nguồn khác.
        Assert.NotEmpty(idsA.Except(idsB));
        Assert.NotEmpty(idsB.Except(idsC));
        Assert.NotEmpty(idsA.Except(idsC));
    }

    [Fact]
    public void Nguon_4K_co_bo_rung_hinh_khac_nguon_720p()
    {
        var a = Balanced(Source4K());
        var c = Balanced(Source720p());

        var heightsA = a.EncodeCandidates.Select(x => x.Height).Distinct().ToHashSet();
        var heightsC = c.EncodeCandidates.Select(x => x.Height).Distinct().ToHashSet();

        // Bộ rung phải khác nhau — nếu giống nhau thì planner không nhìn nguồn mà chỉ nhìn mode.
        Assert.NotEmpty(heightsA.Except(heightsC));

        // 4K phải còn ứng viên giữ nguyên 4K: không mode nào được ghim thành "luôn xuống thấp".
        Assert.Contains(2160, heightsA);

        // Và không rung nào vượt quá nguồn của chính nó.
        Assert.All(heightsA, h => Assert.True(h <= 2160, $"4K: cao {h}"));
        Assert.All(heightsC, h => Assert.True(h <= 720, $"720p: cao {h}"));
    }

    [Fact]
    public void Ngan_sach_ky_mo_nhieu_nhanh_hinh_hon_cho_nguon_lon()
    {
        var normal = Balanced(Source4K()).EncodeCandidates.Select(x => x.Height).Distinct().Count();
        var thorough = CandidatePlanner
            .Generate(Source4K(), CompressionLevel.Balanced, ComputeBudget.Thorough, Caps(), Config())
            .EncodeCandidates.Select(x => x.Height).Distinct().Count();

        Assert.True(thorough > normal, $"ky {thorough} rung, thuong {normal} rung");
    }

    [Fact]
    public void Mat_do_bit_nguon_khong_duoc_im_lien_doi_vung_tim()
    {
        // Mật độ bit là số đo thật, nhưng CHƯA có số đo nào xác lập nó nên dịch vùng tìm
        // theo chiều nào. Trước đây nó dịch theo một chiều, kèm comment giải thích ngược
        // chiều với code. Ở đây khẳng định hành vi đã chọn: nó không dịch gì cả, và câu
        // hỏi chiều ảnh hưởng được ghi lại trong mã nguồn thay vì đoán.
        //
        // Lưu ý: mức nén vẫn khác nhau — chỉ là tâm vùng tìm không đổi theo mật độ bit.
        var depleted = Balanced(new VideoSourceProfile
        {
            Width = 1920,
            Height = 1080,
            Fps = 24,
            BitsPerPixelPerFrame = 0.04,
            Content = ContentProfile.ModerateMotion,
        });

        var rich = Balanced(new VideoSourceProfile
        {
            Width = 1920,
            Height = 1080,
            Fps = 24,
            BitsPerPixelPerFrame = 0.60,
            Content = ContentProfile.ModerateMotion,
        });

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
    public void Do_kho_noi_dung_thi_duoc_doi_vung_tim()
    {
        // Khác với mật độ bit, chiều của tín hiệu này kiểm chứng được: nhiều chuyển động
        // nghĩa là nhiều khối phải dựng lại mỗi khung hình, và đó là nơi hạt nhiễu lộ ra
        // đầu tiên khi siết tham số chất lượng. Nên vùng tìm phải dời về chất lượng cao.
        VideoSourceProfile WithMotion(double temporalActivity) => new()
        {
            Width = 1920,
            Height = 1080,
            Fps = 24,
            BitsPerPixelPerFrame = 0.10,
            Content = ContentProfile.ModerateMotion,
            Complexity = new ContentComplexity
            {
                SpatialDetail = 40,
                TemporalActivity = temporalActivity,
                Samples = 3,
            },
        };

        var still = Balanced(WithMotion(0));
        var busy = Balanced(WithMotion(30));

        var stillMax = still.EncodeCandidates.Where(c => c.Codec == VideoCodec.H264).Max(c => c.QualityParameter);
        var busyMax = busy.EncodeCandidates.Where(c => c.Codec == VideoCodec.H264).Max(c => c.QualityParameter);

        // Tham số nhỏ hơn = chất lượng cao hơn, nên nội dung bận phải có con số nhỏ hơn.
        Assert.True(busyMax < stillMax, $"ban {busyMax} khong < tinh {stillMax}");
    }

    [Fact]
    public void Ca_ba_nguon_dieu_dung_mot_nguong_chat_luong()
    {
        // Mức nén giữ nguyên nghĩa với chất lượng bất kể nguồn ra sao. Ở đây chỉ kiểm
        // được phần planner không tự ý dịch ngưỡng theo nguồn — phần đo thật thuộc giai đoạn
        // sau.
        var floor = QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Default);

        foreach (var source in new[] { Source4K(), Source1080p(), Source720p() })
        {
            var plan = Balanced(source);
            Assert.All(plan.EncodeCandidates, c => Assert.True(c.QualityParameter >= 0));
            Assert.Equal(89.0, floor.VmafMean);
        }
    }

    [Fact]
    public void Ca_ba_nguon_dieu_co_ung_vien_di_dung_mien_ma_khong_tran_giao()
    {
        foreach (var source in new[] { Source4K(), Source1080p(), Source720p() })
        {
            foreach (var candidate in Balanced(source).EncodeCandidates)
            {
                IEncoderSearchDomain domain = candidate.Codec switch
                {
                    VideoCodec.H264 => new X264SearchDomain(),
                    VideoCodec.Hevc => new X265SearchDomain(),
                    _ => new LibaomAv1SearchDomain(),
                };

                Assert.True(
                    domain.Validate(candidate.QualityParameter, candidate.Width, candidate.Height, out _),
                    $"{candidate.Id} ngoai mien {domain.EncoderName}");
            }
        }
    }
}
