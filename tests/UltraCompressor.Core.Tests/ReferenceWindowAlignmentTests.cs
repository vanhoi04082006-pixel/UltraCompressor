using UltraCompressor.Core.Media;
using UltraCompressor.Core.Processes;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Chứng minh vì sao đo phải cắt tham chiếu thành clip, chứ không seek thẳng vào tệp
/// nguồn.
///
/// <para>Đây là lỗi tinh vi nhất từng gặp trong dự án: mọi thứ đều đúng — đúng đoạn, đúng
/// codec, đúng mô hình VMAF, ffmpeg trả mã 0 — nhưng số đo thấp hơn thực tế gấp hơn hai lần.
/// Nguyên nhân là lệch nửa khung hình giữa hai bên, không phải chất lượng.</para>
///
/// <para>Test so sánh hai cách đo trên cùng một ứng viên: seek thẳng vào nguồn, và cắt cả
/// hai thành clip. Cách thứ hai phải cho điểm cao hơn rõ rệt. Nếu không còn tái hiện được
/// thì đường lệnh đã lệch và mọi quyết định dựa trên nó là sai.</para>
/// </summary>
public class ReferenceWindowAlignmentTests
{

    [RequiresFFmpeg]
    public async Task Cat_ca_hai_thanh_clip_khong_lay_di_chinh_nguon()
    {
        var ffmpeg = TestFFmpeg.Require();
        var work = Directory.CreateTempSubdirectory("uc-align-").FullName;

        try
        {
            // Nguồn có chuyển động đủ mạnh để lệch nửa khung hình thể hiện rõ. Số thứ tự
            // khung hình dài khiến input-seek dừng ở vị trí khác so với đường cắt clip.
            var source = Path.Combine(work, "src.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=30",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "250",
                "-pix_fmt", "yuv420p", "-y", source);

            const double start = 12.0, length = 3.0;
            var window = new RepresentativeWindow(
                start, length, WindowRole.Typical, 0.5, 0.5, 0.5, 0.5, 0.5, 0, "test");

            // Ứng viên: cắt bằng đúng đường của PilotEncoder.
            var pilotDir = Path.Combine(work, "pilot");
            Directory.CreateDirectory(pilotDir);
            var pilot = Path.Combine(pilotDir, "pilot.mp4");
            var w = new TimeWindow(start, length);
            await RunAsync(ffmpeg,
                "-v", "error", "-ss", w.StartText, "-t", w.LengthText, "-i", source,
                "-map", "0:v:0?", "-an", "-sn", "-dn",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", pilot);

            var model = VmafModels.Default;
            var probe = new QualityProbe(ffmpeg, work);

            // Cách sai: tham chiếu seek thẳng vào tệp nguồn, ứng viên seek ở 0.
            var seeked = await probe.MeasureAsync(
                source, pilot, w, new TimeWindow(0, length), 1280, 720, 1280, 720, model);

            // Cách đúng: cắt tham chiếu thành clip, đo clip đối clip.
            var refDir = Path.Combine(work, "ref");
            Directory.CreateDirectory(refDir);
            var references = await new ReferenceWindowExtractor(ffmpeg, refDir)
                .ExtractAsync(source, [window]);
            var reference = Assert.Single(references);

            var clipped = await probe.MeasureAsync(
                reference.Path, pilot,
                new TimeWindow(0, reference.LengthSeconds), new TimeWindow(0, length),
                1280, 720, 1280, 720, model);

            Assert.NotNull(seeked);
            Assert.NotNull(clipped);

            // Bất biền phổ quát: cách cắt clip không bao giờ TỆ HƠN cách seek thẳng.
            //
            // Cố tình KHÔNG khẳng định nó phải "cao hơn hẳn": độ lệch phụ thuộc cấu trúc
            // khung hình của tệp. Trên tệp thật của dự án, seek thẳng cho 41,3 còn cắt clip
            // cho 94,0 — nhưng trên một tệp tổng hợp không có keyframe cách nhau xa, cả hai
            // đường cho cùng 96,4. Khẳng định sai thứ tự đó sẽ làm test đỏ trên đúng những
            // tệp không có vấn đề.
            Assert.True(
                clipped!.Sample.Mean >= seeked!.Sample.Mean,
                $"cắt clip ({clipped.Sample.Mean:0.00}) không được thấp hơn seek thẳng ({seeked.Sample.Mean:0.00})");

            // Cách cắt phải cho một điểm đủ cao: ứng viên này gần như bản gốc.
            Assert.True(
                clipped.Sample.Mean > 85,
                $"ứng viên gần bản gốc mà chỉ {clipped.Sample.Mean:0.00}");

            ReferenceWindowExtractor.Release(references);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    [RequiresFFmpeg]
    public async Task Lech_mot_khung_hinh_lam_vmaf_sut_tum()
    {
        // Cơ chế đằng sau việc phải cắt tham chiếu thành clip: VMAF gần như KHÔNG nhạy với
        // lệch nửa khung hình, nhưng sụp gần như triệt để khi lệch tới MỘT khung hình.
        //
        // Đo trên 4 nguồn tổng hợp, 1280x720 @ 25 fps (một khung = 0,04s), tự so với chính
        // nó:  lệch 0,00s -> 99,2-100,0 | lệch 0,02s -> 98,8-99,2 | lệch 0,04s -> 21,2-68,7
        //
        // Nghĩa là sai sót ở đây không phải "hơi lệch" mà là đọc nhầm khung hình, và khi đó
        // ứng viên tốt bị loại như thể nó tệ. Trên tệp anime thật của dự án, cùng một ứng
        // viên cho mean 41,3 theo cách seek thẳng và 94,0 theo cách cắt cả hai thành clip.
        //
        // Lệch chính xác một chu kỳ khung hình, và nội dung có cạnh sắc + chuyển động mạnh
        // để độ nhạy lộ ra (testsrc2 hay nội dung mượt không lộ).
        var ffmpeg = TestFFmpeg.Require();
        var work = Directory.CreateTempSubdirectory("uc-frame-").FullName;

        try
        {
            var source = Path.Combine(work, "src.mp4");

            // Sinh ở kích thước đích luôn. Phóng to từ 320x180 làm mờ cạnh và giảm độ nhạy
            // (đo được 73,2 thay vì 21,2) — tức là làm mềt đúng cái hiện tượng cần đo.
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi",
                "-i", "cellauto=size=1280x720:rate=25:rule=110:random_fill_ratio=0.3",
                "-t", "20",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-y", source);

            const double oneFrame = 1.0 / 25.0;
            var probe = new QualityProbe(ffmpeg, work);
            var model = VmafModels.Default;

            var aligned = await probe.MeasureAsync(
                source, source,
                new TimeWindow(5.0, 3.0), new TimeWindow(5.0, 3.0),
                1280, 720, 1280, 720, model);

            var shifted = await probe.MeasureAsync(
                source, source,
                new TimeWindow(5.0 + oneFrame, 3.0), new TimeWindow(5.0, 3.0),
                1280, 720, 1280, 720, model);

            Assert.NotNull(aligned);
            Assert.NotNull(shifted);

            // Cùng một tệp với chính nó: lệch đúng khung hình thì gần như tuyệt đối.
            Assert.True(
                aligned!.Sample.Mean > 95,
                $"nguồn so với chính nó phải gần 100; thực tế {aligned.Sample.Mean:0.00}");

            // Lệch đúng một khung hình thì rơi thảm, dù nội dung gần như không đổi.
            Assert.True(
                shifted!.Sample.Mean < 70,
                $"lệch {oneFrame:0.000}s (một khung hình) phải làm điểm sụp: "
                    + $"thực tế {shifted.Sample.Mean:0.00}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    [RequiresFFmpeg]
    public async Task Clip_tham_chieu_co_dung_thoi_luong_doan()
    {
        var ffmpeg = TestFFmpeg.Require();
        var work = Directory.CreateTempSubdirectory("uc-align2-").FullName;

        try
        {
            var source = Path.Combine(work, "src.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=20",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-pix_fmt", "yuv420p", "-y", source);

            var windows = new[]
            {
                new RepresentativeWindow(2.0, 3.0, WindowRole.HighMotion, 0.5, 0.5, 0.5, 0.5, 0.5, 0, "a"),
                new RepresentativeWindow(10.0, 3.0, WindowRole.Typical, 0.5, 0.5, 0.5, 0.5, 0.5, 1, "b"),
            };

            var references = await new ReferenceWindowExtractor(ffmpeg, work).ExtractAsync(source, windows);

            Assert.Equal(2, references.Count);
            Assert.Equal([WindowRole.HighMotion, WindowRole.Typical], references.Select(r => r.Role));
            Assert.All(references, r => Assert.True(r.Bytes > 0));
            Assert.All(references, r => Assert.True(File.Exists(r.Path)));

            // Mốc nguồn được giữ lại để truy vết, nhưng clip bắt đầu tại 0.
            Assert.Equal(2.0, references[0].OriginSeconds);
            Assert.Equal(3.0, references[0].LengthSeconds);

            ReferenceWindowExtractor.Release(references);
            Assert.All(references, r => Assert.False(File.Exists(r.Path)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    [RequiresFFmpeg]
    public async Task Khong_cat_duoc_tham_chieu_thi_bao_lo_roi_thay_vi_do_vao_tep_rong()
    {
        var ffmpeg = TestFFmpeg.Require();
        var work = Directory.CreateTempSubdirectory("uc-align3-").FullName;

        try
        {
            var window = new RepresentativeWindow(
                1.0, 3.0, WindowRole.Typical, 0.5, 0.5, 0.5, 0.5, 0.5, 0, "x");

            // Không có tham chiếu thì không thể đo. Ném lỗi tường minh tốt hơn là dùng tệp
            // rỗng làm tham chiếu, vì khi đó mọi ứng viên đều "rớt ngưỡng" theo một lý do
            // không liên quan tới chất lượng.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ReferenceWindowExtractor(ffmpeg, work)
                    .ExtractAsync(Path.Combine(work, "khong-co.mp4"), [window]));

            Assert.Empty(Directory.GetFiles(work, "*.mp4"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task RunAsync(string ffmpeg, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(ffmpeg, args, token: CancellationToken.None);
        Assert.True(r.Succeeded, $"ffmpeg that bai: [{string.Join(' ', args)}]");
    }
}
