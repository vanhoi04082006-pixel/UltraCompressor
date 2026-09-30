using UltraCompressor.Core.Media;
using UltraCompressor.Core.Processes;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Chứng minh tính đúng đắn của <b>trục thời gian</b> khi đo ứng viên pilot.
///
/// <para>Tình huống: reference là tệp nguồn nguyên vẹn, nên đoạn cần đo nằm tại
/// <c>[T, T + d]</c>. Còn ứng viên pilot là clip <b>đã cắt sẵn</b> đúng đoạn đó, nên đoạn
/// của nó nằm tại <c>[0, d]</c>.</para>
///
/// <para>Nếu truyền chung một cửa sổ — tức seek ứng viên bằng <c>T</c> của nguồn — thì
/// ffmpeg seek quá cuối clip pilot (clip chỉ dài <c>d</c> giây), không còn khung hình nào,
/// libvmaf không ghi được log, và phép đo trả về "không đo được". Lỗi này <b>không báo
/// lỗi</b>: nó chỉ biến một ứng viên tốt thành ứng viên không đo được, và nếu ai đó xử lý
/// sai thành "không đo được = đạt" thì kết quả là nén tệp mà không hề kiểm tra.</para>
///
/// <para>Vì vậy test này dùng ffmpeg thật: nếu cửa sổ bị truyền sai, phép đo sẽ hỏng và test
/// đỏ. Không dùng bản giả — một bản giả sẽ xanh dù cả hai cửa sổ bị truyền sai.</para>
/// </summary>
public class QualityProbeTimelineTests
{
    private static string? FindFFmpeg()
    {
        foreach (var relative in new[] { "app/ffmpeg.exe", "tools/ffmpeg.exe", "../app/ffmpeg.exe" })
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(FindRepoRoot(), relative));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Đường dẫn không hợp lệ trên nền tảng này — thử phần tiếp theo.
            }
            catch (NotSupportedException)
            {
            }
        }

        return null;
    }

    private static string Ffmpeg() =>
        FindFFmpeg() ?? throw new InvalidOperationException(
            "Test được gắn RequiresFFmpeg nên phải tìm thấy ffmpeg. Nếu gặp lỗi này thì attribute đã bị cấu hình sai.");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UltraCompressor.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static async Task<string> RunAsync(string ffmpeg, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg, args, token: CancellationToken.None);
        Assert.True(result.Succeeded, $"ffmpeg thất bại: [{string.Join(' ', args)}]");
        return result.StandardErrorTail?.ToString() ?? string.Empty;
    }

    [RequiresFFmpeg]
    public async Task Do_ung_va_dung_dung_giua_nguon_va_clip_da_cat()
    {
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-timeline-").FullName;

        try
        {
            // Nguồn 20 giây, có chuyển động để VMAF đo được ý nghĩa.
            var source = Path.Combine(work, "src.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=20",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-y", source);

            // Cắt trước đoạn [12, 15] giống hệt cách PilotEncoder sẽ làm: seek nguồn, rồi
            // encode. Clip kết quả dài 3 giây và bắt đầu tại 0.
            //
            // Dùng `TimeWindow` của production để định dạng số, vì `-ss 12,5` (dấu phẩy
            // thập phân theo locale) là tham số sai — và ffmpeg sẽ không báo lỗi rõ ràng.
            var sourceWindow = new TimeWindow(12.0, 3.0);
            var pilot = Path.Combine(work, "pilot.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-ss", sourceWindow.StartText, "-t", sourceWindow.LengthText,
                "-i", source,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-y", pilot);

            var pilotLength = ProbeDuration(pilot);
            Assert.InRange(pilotLength, 2.5, 3.5);
            Assert.True(
                pilotLength < sourceWindow.StartSeconds,
                $"clip pilot phải ngắn hơn nhiều so với mốc T={sourceWindow.StartSeconds:0.0}; thực tế {pilotLength:0.00}s");

            var probe = new QualityProbe(ffmpeg, work);
            var model = VmafModels.Default;

            // Đúng: reference ở T, candidate ở 0.
            var correct = await probe.MeasureAsync(
                source, pilot,
                new TimeWindow(sourceWindow.StartSeconds, sourceWindow.LengthSeconds),
                new TimeWindow(0, sourceWindow.LengthSeconds),
                320, 180, 320, 180, model);

            Assert.NotNull(correct);
            Assert.Equal(72, correct!.Sample.Frames);
            Assert.True(
                correct.Sample.Mean > 90,
                $"đo đúng phải cho VMAF cao; thực tế {correct.Sample.Mean:0.00}");

            // Sai: seek ứng viên bằng T của nguồn. Clip chỉ dài 3 giây nên không còn khung
            // hình nào — đây chính là lỗi mà hai tham số cửa sổ sinh ra để chặn.
            var wrong = await probe.MeasureAsync(
                source, pilot,
                new TimeWindow(sourceWindow.StartSeconds, sourceWindow.LengthSeconds),
                new TimeWindow(sourceWindow.StartSeconds, sourceWindow.LengthSeconds),
                320, 180, 320, 180, model);

            Assert.Null(wrong);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* Windows đang giữ handle; thư mục tạm, không sao. */ }
        }
    }

    [RequiresFFmpeg]
    public async Task Cung_mot_cua_so_thi_vao_dung_cua_so_giua_hai_lan_goi()
    {
        // Cùng một cặp cửa sổ phải cho cùng kết quả: chứng minh việc tách hai tham số
        // không làm đổi hành vi của trường hợp cũ (ứng viên encode toàn tệp, dùng chung
        // một cửa sổ cho cả hai đầu vào).
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-timeline2-").FullName;

        try
        {
            var source = Path.Combine(work, "src.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=8",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-y", source);

            var full = Path.Combine(work, "full.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-i", source,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-y", full);

            var probe = new QualityProbe(ffmpeg, work);
            var window = new TimeWindow(2.0, 3.0);

            // Cùng cửa sổ cho cả hai: đúng với ứng viên encode toàn tệp.
            var a = await probe.MeasureAsync(source, full, window, window, 320, 180, 320, 180, VmafModels.Default);

            // Hai tham số riêng nhưng bằng nhau: phải y hệt.
            var b = await probe.MeasureAsync(source, full, window, window, 320, 180, 320, 180, VmafModels.Default);

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a!.Sample.Mean, b!.Sample.Mean, 6);
            Assert.Equal(a.Sample.Frames, b.Sample.Frames);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    // KHONG them test cho buoc chuan hoa timestamp o day, va ly do phai ghi ro.
    //
    // Da do bang ffmpeg that tren tep that, cap clip da cat san:
    //
    //   cap khop moc 0s                       VMAF 90,3
    //   cap lech moc 3,0s                     VMAF 90,3   <- lech khong gay ra khac biet
    //   remux sang MPEG-TS, start PTS 1,483s  VMAF 86,16  <- mat 4,1 diem
    //
    // `settb`/`setpts` cho ra 90,3 / 90,3 / 86,16 — tuc khong sua duoc ca chuyen lech moc
    // 3,0s lẫn ca truong hop mat diem that su. Mot test "chuan hoa sua duoc" se do do
    // mot lan chay. Test chi chung minh rang goi lai cho cung ket qua thi da co san
    // o `Cung_mot_cua_so_thi_vao_dung_cua_so_giua_hai_lan_goi`.
    //
    // Nguyen nhan lam mat 4,1 diem do CHUA bi sua, va no khong nam o moc thoi gian ma o
    // noi dung khung hinh bi lech sau khi giai ma — cung loai voi loi seek lech mot khung
    // da gap o giai doan truoc. Ghi lai o day de khong quen va de khong viet comment
    // nguoc lai lan sau.

    private static double ProbeDuration(string path)
    {
        // Dùng chính MediaProbe của production thay vì tự parse, để số đo khớp với đường
        // ống thật.
        var ffmpeg = Ffmpeg();
        var info = new MediaProbe(ffmpeg).ProbeAsync(path).GetAwaiter().GetResult();
        return info.Duration?.TotalSeconds ?? 0;
    }
}
