using System.Globalization;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Planning;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Test ở <b>tầng dựng lệnh cuối</b>: chỗ duy nhất có thể sinh ra một cặp công tắc/giá trị
/// sai cho ffmpeg.
///
/// <para>Lý do có riêng một tệp test: lỗi đã gặp <b>không nằm ở tầng lập kế hoạch</b>.
/// Miền tìm kiếm trả đúng giá trị <c>8</c>; chỗ hỏng là tầng ghép thành <c>-preset cpu-used=8</c>.
/// Test chỉ ở tầng trên sẽ xanh trong khi sản phẩm vẫn hỏng.</para>
///
/// <para>ffmpeg <b>không báo lỗi</b> với dạng sai: nó bỏ qua âm thầm và chạy ở mặc định.
/// Đo trên tệp 5 giây: <c>-cpu-used 8</c> mất 2,05 s, còn <c>-preset "cpu-used=8"</c> mất
/// 151,8 s và cho tệp giống hệt mặc định — chậm hơn 74 lần mà vẫn báo thành công.</para>
/// </summary>
public class EncoderCommandTests
{
    private static readonly ComputeBudget[] Budgets =
        [ComputeBudget.Fast, ComputeBudget.Normal, ComputeBudget.Thorough];

    private static readonly IEncoderSearchDomain[] Domains =
        [new X264SearchDomain(), new X265SearchDomain(), new LibaomAv1SearchDomain()];

    private static EncoderConfiguration Config(IEncoderSearchDomain domain, ComputeBudget budget, double? quality = null)
        => new()
        {
            EncoderName = domain.EncoderName,
            Quality = domain.Quality(quality ?? domain.DefaultQualityPoint),
            Speed = domain.Speed(budget),
            PixelFormat = "yuv420p",
        };

    // ---------------------------------------------------------------- A. Cặp công tắc/giá trị

    [Fact]
    public void X264_va_x265_dung_dung_va_preset_va_crf()
    {
        foreach (var domain in new IEncoderSearchDomain[] { new X264SearchDomain(), new X265SearchDomain() })
        {
            foreach (var budget in Budgets)
            {
                var configuration = Config(domain, budget);
                var args = configuration.ToArguments();

                AssertPair(args, "-c:v", domain.EncoderName);

                // Mỗi codec dùng mặc định của chính nó: x264 23, x265 28. Dùng chung một
                // con số ở đây là lỗi đã tồn tại ở mã cũ.
                AssertPair(args, "-crf", domain.DefaultQualityPoint.ToString(CultureInfo.InvariantCulture));
                AssertPair(args, "-preset", domain.Speed(budget).Text);
            }
        }
    }

    [Fact]
    public void Libaom_dung_cpu_used_chu_khong_dung_preset()
    {
        var domain = new LibaomAv1SearchDomain();

        foreach (var budget in Budgets)
        {
            var args = Config(domain, budget).ToArguments();

            AssertPair(args, "-c:v", "libaom-av1");
            AssertPair(args, "-cpu-used", domain.Speed(budget).Text);
            AssertPair(args, "-crf", "32");

            // Đây là điều mệnh đề quan trọng nhất của cả tệp test.
            Assert.DoesNotContain("-preset", args);
        }
    }

    [Fact]
    public void Khong_bao_gio_sinh_preset_theo_dang_cpu_used_bang()
    {
        // Duyệt mọi tổ hợp miền × ngân sách, ở tầng dựng lệnh thật.
        foreach (var domain in Domains)
        {
            foreach (var budget in Budgets)
            {
                var args = Config(domain, budget).ToArguments();

                for (var i = 0; i < args.Count - 1; i++)
                {
                    if (args[i] != "-preset")
                    {
                        continue;
                    }

                    var value = args[i + 1];
                    Assert.DoesNotContain("=", value);
                    Assert.DoesNotContain("cpu-used", value);
                    Assert.DoesNotContain(" ", value);
                }
            }
        }
    }

    [Fact]
    public void Cung_duoc_dinh_nghia_mot_dang_xau_khong_duoc_dung()
    {
        // Dạng xau vẫn còn biểu diễn được ở tầng API — người gọi vẫn có thể thử. Nó
        // phải bị chặn bởi kiểm tra, chứ không âm thầm đi tiếp.
        var bad = new EncoderConfiguration
        {
            EncoderName = "libx264",
            Quality = QualityOption.X26xCrf(23),
            Speed = SpeedOption.X26xPreset("cpu-used=8"),
            PixelFormat = "yuv420p",
        };

        Assert.False(bad.Validate(out var failure));

        // Lỗi phải nói rõ giá trị bị chặn và chỉ ra tập hợp lệ, chứ không chỉ "sai".
        Assert.Contains("cpu-used=8", failure);
        Assert.Contains("medium", failure);
    }

    [Fact]
    public void Gia_tri_toc_do_theo_ten_phai_khong_co_dau_bang()
    {
        // Danh sách tên preset đóng: giá trị lạ chặn được ngay, kể cả khi người gọi cố
        // nhét chuỗi giống tuỳ chọn của encoder khác vào.
        foreach (var hostile in new[] { "cpu-used=8", "8", "cpu-used", "", " ", "medium " })
        {
            var configuration = new EncoderConfiguration
            {
                EncoderName = "libx264",
                Quality = QualityOption.X26xCrf(23),
                Speed = SpeedOption.X26xPreset(hostile),
                PixelFormat = "yuv420p",
            };

            if (hostile == "8")
            {
                // "8" cũng không phải tên preset hợp lệ của x264.
                Assert.False(configuration.Validate(out _), $"\"{hostile}\" phải bị chặn");
            }
            else
            {
                Assert.False(configuration.Validate(out _), $"\"{hostile}\" phải bị chặn");
            }
        }
    }

    // ---------------------------------------------------------------- B. Miền riêng từng encoder

    [Fact]
    public void Miền_crf_khac_nhau_giua_x26x_va_libaom()
    {
        // Cùng tên công tắc -crf nhưng hai thang khác nhau. Đây là lý do không được dùng
        // chung một lớp cho cả hai.
        Assert.True(QualityOption.X26xCrf(51).IsInRange);
        Assert.False(QualityOption.X26xCrf(52).IsInRange);

        Assert.True(QualityOption.LibaomCrf(52).IsInRange);
        Assert.True(QualityOption.LibaomCrf(63).IsInRange);
        Assert.False(QualityOption.LibaomCrf(64).IsInRange);
    }

    [Fact]
    public void Cpu_used_libaom_chan_9()
    {
        // Test trước đây cho phép tới 9 theo thói quen tài liệu SVT-AV1. Bản ffmpeg đi kèm
        // chỉ nhận 0–8: "Value 9.000000 for parameter 'cpu-used' out of range [0 - 8]".
        Assert.True(SpeedOption.AomCpuUsed(8).AcceptsValue());
        Assert.False(SpeedOption.AomCpuUsed(9).AcceptsValue());
    }

    [Fact]
    public void Khong_lay_mien_cua_svt_av1_cho_libaom()
    {
        // SVT-AV1 dùng QP chứ không dùng CRF, và miền preset riêng. Miền của nó không
        // được dính sang libaom.
        Assert.NotEqual(
            QualityOption.SvtAv1Qp(30).Switch,
            QualityOption.LibaomCrf(30).Switch);

        // QP của SVT-AV1 không dùng được cho libaom, và ngược lại.
        var svtOnLibaom = new EncoderConfiguration
        {
            EncoderName = "libaom-av1",
            Quality = QualityOption.SvtAv1Qp(30),
            Speed = SpeedOption.AomCpuUsed(6),
            PixelFormat = "yuv420p",
        };
        Assert.False(svtOnLibaom.Validate(out _), "QP khong hop le cho libaom");

        var crfOnSvt = new EncoderConfiguration
        {
            EncoderName = "libsvtav1",
            Quality = QualityOption.LibaomCrf(30),
            Speed = SpeedOption.SvtAv1Preset(8),
            PixelFormat = "yuv420p",
        };
        Assert.False(crfOnSvt.Validate(out _), "CRF khong hop le cho SVT-AV1");
    }

    [Fact]
    public void Cpu_used_khong_dung_duoc_cho_x264()
    {
        // Đúng công tắc, sai encoder: x264 không có tuỳ chọn -cpu-used. Nếu lọt qua, ffmpeg
        // sẽ báo lỗi tuỳ chọn không tồn tại — hoặc tệ hơn, bỏ qua.
        var mismatched = new EncoderConfiguration
        {
            EncoderName = "libx264",
            Quality = QualityOption.X26xCrf(23),
            Speed = SpeedOption.AomCpuUsed(6),
            PixelFormat = "yuv420p",
        };

        Assert.False(mismatched.Validate(out var failure));

        // Thông báo phải nêu rõ chỉ dành cho họ nào, để người đọc biết sửa ở đâu.
        Assert.Contains("X264", failure);
        Assert.Contains("LibaomAv1", failure);
    }

    [Fact]
    public void Encoder_l_a_can_co_bien_loi()
    {
        // Encoder lạ thì không có miền để kiểm, nên phải báo thay vì im lặng cho qua.
        var unknown = new EncoderConfiguration
        {
            EncoderName = "libsomethingmadeup",
            Quality = QualityOption.X26xCrf(23),
            Speed = SpeedOption.X26xPreset("medium"),
            PixelFormat = "yuv420p",
        };

        Assert.False(unknown.Validate(out var failure));
        Assert.Contains("không nhận ra", failure);
    }

    // ---------------------------------------------------------------- C. Đường dựng lệnh thật của pipeline

    [Fact]
    public void VideoPipeline_dung_bo_dung_luong_co_kieu()
    {
        // Chỗ nối giữa planner cũ và mô hình mới. Nếu chỗ này gọi lại chuỗi trần thì mọi
        // bảo đảm ở trên đều vô nghĩa.
        foreach (var encoder in new[] { "libx264", "libx265" })
        {
            var plan = LegacyPlan(encoder, 26, "medium");

            var configuration = VideoPipeline.EncoderConfigurationFor(plan);
            Assert.True(configuration.Validate(out var failure), $"{encoder}: {failure}");

            var args = configuration.ToArguments();
            AssertPair(args, "-c:v", encoder);
            AssertPair(args, "-crf", "26");
            AssertPair(args, "-preset", "medium");
        }
    }

    [Fact]
    public void VideoPipeline_dung_libaom_theo_cong_tac_cpu_used()
    {
        // Nếu kế hoạch cũ trả về tên encoder AV1 thì phải ra -cpu-used, không phải
        // -preset. Đường legacy hiện chưa sinh ra trường hợp này, nhưng khi nó xảy ra thì
        // đây là chỗ quyết định.
        var plan = LegacyPlan("libaom-av1", 40, "medium");

        var args = VideoPipeline.EncoderConfigurationFor(plan).ToArguments();

        AssertPair(args, "-c:v", "libaom-av1");
        Assert.Contains("-cpu-used", args);
        Assert.DoesNotContain("-preset", args);
    }

    [Fact]
    public void VideoPipeline_giu_nguyen_preset_cua_ke_hoach_cho_x26x()
    {
        // -preset của x26x là tên trong danh sách đóng, nên phải đi từ kế hoạch sang và
        // được kiểm lại; không phải tự chế đặt tên preset.
        foreach (var preset in new[] { "veryfast", "fast", "medium", "slow", "slower" })
        {
            var plan = LegacyPlan("libx264", 26, preset);

            var configuration = VideoPipeline.EncoderConfigurationFor(plan);
            Assert.True(configuration.Validate(out var failure), $"{preset}: {failure}");
            AssertPair(configuration.ToArguments(), "-preset", preset);
        }
    }

    [Fact]
    public void VideoPipeline_bao_loi_voi_preset_khong_hop_le()
    {
        // Planner cũ chỉ phát ra các preset hợp lệ, nhưng nếu bảng preset và bảng miền
        // lệch nhau thì phải báo chứ không dựng lệnh hỏng.
        var plan = LegacyPlan("libx264", 26, "khong-ton-tai");

        Assert.False(VideoPipeline.EncoderConfigurationFor(plan).Validate(out _));
    }

    // ---------------------------------------------------------------- D. Ứng viên từ planner

    [Fact]
    public void Moi_ung_vien_tu_planner_da_dung_lenh_hop_le()
    {
        // Nối liền cuối cùng: ứng viên sinh ra từ planner phải dựng được lệnh mà ffmpeg nhận.
        var plan = CandidatePlanner.Generate(
            new VideoSourceProfile
            {
                Width = 1920,
                Height = 1080,
                Fps = 24,
                BitsPerPixelPerFrame = 0.08,
                Content = ContentProfile.ModerateMotion,
            },
            CompressionLevel.Balanced,
            ComputeBudget.Thorough,
            EncoderCapabilities.FromEncoderNames(["libx264", "libx265", "libaom-av1"]),
            new AppConfig { EnableAv1Search = true });

        Assert.NotEmpty(plan.EncodeCandidates);

        foreach (var candidate in plan.EncodeCandidates)
        {
            Assert.True(candidate.Validate(out var failure), $"{candidate.Id}: {failure}");

            var args = candidate.ToEncoderConfiguration().ToArguments();
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == "-preset")
                {
                    Assert.DoesNotContain("=", args[i + 1]);
                }
            }
        }
    }

    [Fact]
    public void Id_ung_vien_chua_tham_so_chat_luong_that()
    {
        // ID đi vào log và vào đường dẫn tệp tạm, nên phải chứa tham số chất lượng thật
        // chứ không phải chỉ số điểm. Trước đây nó là ".../q21.32" — kèm cả phần thập
        // nhiễu của số thực.
        var plan = CandidatePlanner.Generate(
            new VideoSourceProfile { Width = 1280, Height = 720, Fps = 25 },
            CompressionLevel.Balanced,
            ComputeBudget.Normal,
            EncoderCapabilities.FromEncoderNames(["libx264"]),
            new AppConfig());

        foreach (var candidate in plan.EncodeCandidates)
        {
            Assert.Contains(candidate.Quality.Text, candidate.Id);
            Assert.DoesNotContain(".", candidate.Quality.Text, StringComparison.Ordinal);

            // ID phải phân biệt được các nhánh hình khác nhau.
            Assert.Contains($"{candidate.Width}x{candidate.Height}", candidate.Id);
        }
    }

    /// <summary>Kế hoạch của planner cũ, dựng đủ các trường bắt buộc.</summary>
    private static VideoPlan LegacyPlan(string encoder, int crf, string preset) => new()
    {
        TargetWidth = 1920,
        Crf = crf,
        Preset = preset,
        AudioBitrateKbps = 192,
        DropAudio = false,
        Delta = PlanDelta.None,
        Reason = "kiểm thử",
        VideoEncoder = encoder,
    };

    private static void AssertPair(IReadOnlyList<string> args, string option, string expected)
    {
        var list = args.ToList();
        var index = list.IndexOf(option);

        Assert.True(index >= 0, $"thiếu {option} trong [{string.Join(' ', args)}]");
        Assert.True(index + 1 < list.Count, $"{option} không có giá trị đi kèm");

        // Giá trị phải nằm ngay sau, và không được tự nó là một option khác — nếu không thì
        // lệnh dựng ra đã lệch cặp.
        Assert.Equal(expected, list[index + 1]);
        Assert.False(
            list[index + 1].StartsWith('-') && list[index + 1].Length > 1,
            $"giá trị của {option} lại là một option khác: {list[index + 1]}");

        // Mỗi option chỉ xuất hiện đúng một lần: trùng nghĩa là có tuỳ chọn bị cộng hai lần.
        Assert.Single(list.FindAll(a => a == option));
    }
}
