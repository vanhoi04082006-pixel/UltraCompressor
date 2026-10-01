using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Chạy trọn đường thích ứng trên tệp thật: <c>CandidatePlanner</c> → <c>PilotSearch</c> →
/// encode toàn tệp.
///
/// <para>Phần quyết định đã được kiểm bằng bảng trạng thái và bằng bản giả; ở đây kiểm
/// phần mà bản giả không chứng minh được: rằng <c>ffmpeg</c> thật chấp nhận lệnh dựng ra,
/// rằng tệp đầu ra thật sự nhỏ hơn, và rằng tệp tạm được dọn sạch.</para>
///
/// <para>Nguồn được TỔNG HỢP bằng ffmpeg thay vì để sẵn một tệp thật trong kho. Lý do: tệp
/// thật nặng vài trăm MB, không nên nằm trong git; còn nguồn tổng hợp cho lại đúng loại nội
/// dung cần kiểm — nhiều chuyển động và nhiều chi tiết, tức là trường hợp mà lỗi lệch khung
/// hình hay lộ ra nhất.</para>
public class AdaptiveVideoPipelineE2ETests
{
    private static string Ffmpeg() => TestFFmpeg.Require();

    private static async Task<string> MakeSourceAsync(string work, string ffmpeg, bool compressible)
    {
        var path = Path.Combine(work, "src.mp4");

        // `testsrc2` có chuyển động, hình học và vân — đủ để phân biệt ứng viên.
        // Nguồn "không nén được" mã hoá ở crf 40: đã bị bóp tới mức mã hoá lại chỉ thêm
        // nhiễu, nên đây là tình huống thật mà đường cũ hay "nén" theo bảng tra cứu CRF.
        var crf = compressible ? "14" : "40";

        await RunAsync(ffmpeg,
            "-v", "error",
            "-f", "lavfi",
            "-i", "testsrc2=size=1280x720:rate=25:duration=20",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", crf,
            "-pix_fmt", "yuv420p",
            "-y", path);

        return path;
    }

    private static async Task RunAsync(string tool, params string[] args)
    {
        var result = await UltraCompressor.Core.Processes.ProcessRunner.RunAsync(
            tool, args, TimeSpan.FromMinutes(10), CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffmpeg thất bại ({result.ExitCode}): {result.StandardErrorText}");
        }
    }

    /// <summary>
    /// Dựng ngữ cảnh như engine thật, GỒM cả probe.
    /// </summary>
    /// <remarks>
    /// Bản đầu bỏ trống <c>Probe</c> và đường thích ứng rơi về đường cũ với lý do
    /// "probe không thấy luồng video". Đó là hành vi ĐÚNG — không có thông tin nguồn thì
    /// không dựng được bộ lọc thu nhỏ — nhưng nó là lý do fallback, không phải lý do để
    /// test này đi đường cũ. Engine thật luôn cấp probe, nên test cũng phải cấp.
    /// </remarks>
    private static async Task<PipelineContext> ContextAsync(
        AppConfig config, string source, string temp)
    {
        var ffmpeg = Ffmpeg();
        var probe = await new MediaProbe(ffmpeg).ProbeAsync(source);

        return new PipelineContext
        {
            Item = new JobItem
            {
                FilePath = source,
                Kind = MediaKind.Video,
                OldSize = new FileInfo(source).Length,
                SourceWidth = probe.Width,
                SourceHeight = probe.Height,
                SourceBitrateKbps = probe.BitrateKbps,
                HasAudio = probe.HasAudio,
                DurationSeconds = probe.Duration?.TotalSeconds ?? 0,
            },
            TempPath = temp,
            Level = CompressionLevel.Balanced,
            Config = config,
            Tools = new ToolResolution(ffmpeg, null, null, null),
            Probe = probe,
        };
    }

    [RequiresFFmpeg]
    public async Task Nguon_nen_duoc_thi_chon_ung_vien_va_tao_tepdau_ra_nho_hon()
    {
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-e2e-ok-").FullName;

        try
        {
            var source = await MakeSourceAsync(work, ffmpeg, compressible: true);
            var temp = Path.Combine(work, "out.mp4");
            var sourceSize = new FileInfo(source).Length;

            var pipeline = new AdaptiveVideoPipeline(new VideoPipeline());
            var result = await pipeline.RunAsync(
                await ContextAsync(new AppConfig { EnableAdaptiveSearch = true }, source, temp),
                _ => { },
                CancellationToken.None);

            // KHÔNG được rơi về đường cũ: nếu rơi thì bài này chỉ chứng minh được đường
            // cũ vẫn chạy, chứ không chứng minh được đường mới.
            Assert.DoesNotContain(AdaptiveVideoPipeline.LegacyFallbackMarker, result.Message ?? string.Empty);
            Assert.True(result.Success, result.Message);

            Assert.True(File.Exists(temp), "phải tạo ra tệp đầu ra");
            Assert.Equal(new FileInfo(temp).Length, result.NewSize);
            Assert.True(result.NewSize < sourceSize,
                $"tệp đầu ra phải nhỏ hơn nguồn: {result.NewSize} so với {sourceSize}");

            // Báo cáo phải cho biết đã đo gì, chứ không chỉ "xong".
            Assert.Contains("tìm kiếm:", result.Message);
            Assert.Contains("VMAF", result.Message);
            Assert.Contains("chọn", result.Message);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Khong_duoc_phat_sinh_rac_cong_trong_khi_do()
    {
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-e2e-clean-").FullName;

        try
        {
            var source = await MakeSourceAsync(work, ffmpeg, compressible: true);
            var temp = Path.Combine(work, "out.mp4");

            var pipeline = new AdaptiveVideoPipeline(new VideoPipeline());
            await pipeline.RunAsync(
                await ContextAsync(new AppConfig { EnableAdaptiveSearch = true }, source, temp),
                _ => { },
                CancellationToken.None);

            // Clip thử nghiệm, clip tham chiếu, log scanner và thư mục search phải được dọn.
            // Hai tệp còn lại là hợp lệ theo đúng thiết kế bài test: nguồn tạo riêng và
            // tệp đầu ra được kiểm tra.
            var leftovers = Directory
                .GetFiles(work, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(f, source, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(f, temp, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(leftovers.Count == 0,
                "còn sót tệp tạm: " + string.Join(", ", leftovers.Select(Path.GetFileName)));
            Assert.Empty(Directory.GetDirectories(work, "uc-adaptive-*"));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Kich_thuoc_dau_ra_khop_ung_vien_duoc_chon()
    {
        // Nếu encode toàn tệp dùng bộ lọc khác phép thử thì tệp ra sẽ khác kích thước so
        // với thứ đã đo. Kiểm trên tệp thật, không kiểm bằng cách tin rằng hai hàm được
        // gọi với cùng đối số.
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-e2e-size-").FullName;

        try
        {
            var source = await MakeSourceAsync(work, ffmpeg, compressible: true);
            var temp = Path.Combine(work, "out.mp4");

            var pipeline = new AdaptiveVideoPipeline(new VideoPipeline());
            var result = await pipeline.RunAsync(
                await ContextAsync(new AppConfig { EnableAdaptiveSearch = true }, source, temp),
                _ => { },
                CancellationToken.None);

            Assert.True(result.Success, result.Message);

            var output = await new MediaProbe(ffmpeg).ProbeAsync(temp);
            var sourceInfo = await new MediaProbe(ffmpeg).ProbeAsync(source);

            Assert.NotNull(output.Width);
            Assert.NotNull(output.Height);
            Assert.NotNull(sourceInfo.Width);
            Assert.NotNull(sourceInfo.Height);

            // Không bao giờ phóng to, và không vượt quá nguồn.
            Assert.True(output.Width <= sourceInfo.Width, "không được phóng to");
            Assert.True(output.Height <= sourceInfo.Height, "không được phóng to");

            // Cùng tỉ lệ khung hình: hạ một chiều mà giữ chiều kia sẽ méo hình.
            //
            // `.Value` là bắt buộc, không phải cho đẹp: ép `int?` sang `double` sinh ra
            // `double?`, và `Math.Abs(double?)` không khớp chồng `double` nên trở thành lỗi
            // biên dịch. Ở đây đã khẳng định khác null ở trên.
            var sourceRatio = (double)sourceInfo.Width.Value / sourceInfo.Height.Value;
            var outputRatio = (double)output.Width.Value / output.Height.Value;
            Assert.True(Math.Abs(sourceRatio - outputRatio) < 0.01,
                $"tỉ lệ khung hình bị méo: nguồn {sourceRatio.ToString("0.0000", CultureInfo.InvariantCulture)}, "
                + $"đầu ra {outputRatio.ToString("0.0000", CultureInfo.InvariantCulture)}");

            // Không được ép nhịp khung hình: ứng viên không yêu cầu đổi FPS.
            Assert.NotNull(output.Fps);
            Assert.NotNull(sourceInfo.Fps);
            Assert.Equal(sourceInfo.Fps!.Value, output.Fps!.Value, 2);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Khong_duoc_rua_noi_vao_ban_ghi_khong_bao_gi()
    {
        // Một lỗi mà không kiểm được ở đâu khác: encode thành công nhưng tệp rỗng. `Interpret`
        // đã kiểm tệp tồn tại, nhưng KHÔNG kiểm tệp có nội dung.
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-e2e-size0-").FullName;

        try
        {
            var source = await MakeSourceAsync(work, ffmpeg, compressible: true);
            var temp = Path.Combine(work, "out.mp4");

            var pipeline = new AdaptiveVideoPipeline(new VideoPipeline());
            var result = await pipeline.RunAsync(
                await ContextAsync(new AppConfig { EnableAdaptiveSearch = true }, source, temp),
                _ => { },
                CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.True(result.NewSize > 0, "tệp đầu ra phải có nội dung");
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
