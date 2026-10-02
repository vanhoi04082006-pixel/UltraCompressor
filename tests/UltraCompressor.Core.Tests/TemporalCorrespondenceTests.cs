using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Processes;
using UltraCompressor.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Chứng minh rằng hai clip được cắt bằng đường thật tương ứng theo <b>khung hình</b>, và
/// rằng phép đo không cần lệch khung để khớp.
///
/// <para>Đây là câu hỏi mà "điểm VMAF có đạt không" không trả lời được. Nếu hai clip không
/// cùng số khung, hoặc bắt đầu ở hai nội dung khác nhau, thì điểm đo được nói về <b>đường cắt
/// clip</b>, không nói về chất lượng nén. Và khi điểm đó thấp, đường cứu hợp lệ duy nhất mà
/// mã hiện có là "bỏ một khung cho khớp" — tức tự sửa số liệu thay vì đi tìm nguyên nhân.</para>
///
/// <para>Test này vì vậy kiểm tra <b>cơ chế trước</b>: đếm khung thật của từng clip, và đo ở
/// offset 0. Chỉ khi cơ chế đúng mới nói chuyện điểm số.</para>
/// </summary>
public class TemporalCorrespondenceTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Doc_dich_vay_hieu_va_va_dong_khung()
    {
        // Log THẬT của ffmpeg đi kèm, chép nguyên văn. Các mẫu này từng viết theo trí nhớ
        // và đọc sai: dòng stream KHÔNG chứa mốc bắt đầu, nên regex cũ lấy nhầm số khung
        // hình làm start_time. Ghim log thật vào test để lỗi loại này chết ngay.
        const string header = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'src.mp4':
              Metadata:
                major_brand     : isom
                encoder         : Lavf62.3.100
              Duration: 00:00:03.00, start: 0.000000, bitrate: 594 kb/s
              Stream #0:0[0x1](und): Video: h264 (Constrained Baseline) (avc1 / 0x31637661), yuv420p(progressive), 320x180 [SAR 1:1 DAR 16:9], 591 kb/s, 24 fps, 24 tbr, 12288 tbn (default)
                Metadata:
                  encoder         : Lavc62.11.100 libx264
            """;

        var (start, duration) = TemporalProbe.ParseContainer(header);

        Assert.Equal(0.0, start!.Value, 6);
        Assert.Equal(3.0, duration!.Value, 6);

        // Không có dòng `start_time:` trong log dạng văn bản — nếu ai đó viết regex theo
        // tên trường JSON thì sẽ không bao giờ khớp và âm thầm trả "không biết".
        var (noStart, noDuration) = TemporalProbe.ParseContainer("Duration: 00:00:03.00, start_time: 0.021");
        Assert.Null(noStart);
        Assert.Null(noDuration);
    }

    [Fact]
    public void PTS_khung_dau_va_tong_khong_va_timebase_duoc_doc_dung()
    {
        const string showinfo = """
            [Parsed_showinfo_0 @ 000001d8ea1ce440] config in time_base: 1/12288, frame_rate: 24/1
            [Parsed_showinfo_0 @ 000001d8ea1ce440] n: 0 pts: 0 pts_time:0 duration: 512 duration_time:0.0416667 fmt:yuv420p s:320x180 i:P iskey:1 type:I
            [Parsed_showinfo_0 @ 000001d8ea1ce440] n: 1 pts: 512 pts_time:0.0416667 duration: 512 duration_time:0.0416667 fmt:yuv420p s:320x180 i:P iskey:0 type:P
            frame=    1 fps=0.0 q=-0.0 Lsize=N/A time=00:00:00.04 bitrate=N/A
            """;

        var (timeBase, frameRate) = TemporalProbe.ParseStreamConfig(showinfo);
        Assert.Equal("1/12288", timeBase);
        Assert.Equal("24/1", frameRate);

        // Chỉ dòng n:0 mới là khung đầu; n:1 là khung thứ hai và phải bị bỏ qua.
        Assert.Equal(0.0, TemporalProbe.ParseFirstPts(showinfo)!.Value, 6);
        Assert.Equal(1, TemporalProbe.ParseFrameTotal(showinfo));

        // frame= xuất hiện nhiều lần trong log tiến trình; lấy lần cuối, tức tổng thật.
        Assert.Equal(72, TemporalProbe.ParseFrameTotal("frame=   10\nframe=   72 fps=24"));

        // Không đọc được thì phải là "không biết", KHÔNG phải 0 — 0 là một khẳng định sai.
        Assert.Null(TemporalProbe.ParseFirstPts("khong co gi"));
        Assert.Equal(0, TemporalProbe.ParseFrameTotal("khong co gi"));
    }

    [RequiresFFmpeg]
    public async Task Thu_1080_xuong_720p_hai_clip_tuong_ung_theo_khung()
    {
        var ffmpeg = TestFFmpeg.Require();
        var work = Directory.CreateTempSubdirectory("uc-temporal-").FullName;

        try
        {
            // Nguồn 1080p thật, có chuyển động. GOP dài (250) để input-seek có việc phải làm,
            // đúng như tệp thật — nếu mọi thứ đều keyframe thì lỗi căn không bao giờ lộ ra.
            var source = Path.Combine(work, "src.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi",
                "-i", "testsrc2=size=1920x1080:rate=24:duration=8",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-g", "250",
                "-pix_fmt", "yuv420p", "-y", source);

            // Ứng viên: đúng hình dạng việc nén thật — thu 1080p xuống 720p rồi mã hoá lại.
            var candidate = Path.Combine(work, "cand.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-i", source,
                "-vf", "scale=1280:720:flags=lanczos",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "22",
                "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", candidate);

            const double start = 3.0, length = 3.0;
            var window = new RepresentativeWindow(
                start, length, WindowRole.Typical, 0.5, 0.5, 0.5, 0.5, 0.5, 0, "temporal");

            // Cắt clip BẰNG ĐÚNG đường sản xuất dùng (ReferenceWindowExtractor), không tự
            // chép lệnh: chép lệnh thì test xanh trong khi đường thật đã hỏng.
            var extractor = new ReferenceWindowExtractor(ffmpeg, work);
            var sourceClips = await extractor.ExtractAsync(source, [window]);
            var candidateClips = await extractor.ExtractAsync(candidate, [window]);

            var sourceClip = sourceClips[0].Path;
            var candidateClip = candidateClips[0].Path;

            var sourceFacts = await TemporalProbe.ProbeAsync(ffmpeg, sourceClip);
            var candidateFacts = await TemporalProbe.ProbeAsync(ffmpeg, candidateClip);

            Assert.NotNull(sourceFacts);
            Assert.NotNull(candidateFacts);
            Assert.True(sourceFacts!.IsUsable, $"clip tham chiếu không đọc được trục thời gian: {sourceFacts}");
            Assert.True(candidateFacts!.IsUsable, $"clip ứng viên không đọc được trục thời gian: {candidateFacts}");

            _output.WriteLine($"source   clip: {sourceFacts}");
            _output.WriteLine($"candidate clip: {candidateFacts}");

            // Cơ chế: hai clip phải chứa CÙNG số khung. Lệch số khung thì mọi điểm VMAF đo
            // được đang so hai đoạn nội dung khác nhau, và "bỏ một khung cho khớp" chỉ là
            // cách lách số liệu, không phải cách sửa lỗi.
            Assert.Equal(
                sourceFacts.DecodedFrames, candidateFacts.DecodedFrames);
            Assert.True(
                sourceFacts.DecodedFrames >= (long)(length * 23),
                $"clip tham chiếu phải có gần đủ {length:0.#}s*24fps khung, thực tế {sourceFacts}");

            // Và đo ở offset 0 phải khớp — không cần dùng tới cách "bỏ một khung".
            var probe = new QualityProbe(ffmpeg, work);
            var zero = await probe.MeasureAsync(
                sourceClip, candidateClip,
                new TimeWindow(0, length), new TimeWindow(0, length),
                1280, 720, 1280, 720, VmafModels.Default);

            Assert.NotNull(zero);
            _output.WriteLine($"VMAF @ offset 0 = {zero!.Sample.Mean:0.00} (P5 {zero.Sample.P5:0.00}, {zero.Sample.Frames} khung)");

            // Ngưỡng lỏng: việc ở đây là phát hiện lệch khung, không phải đòi chất lượng cao.
            // 720p thu từ 1080p mất chiều không gian thật, nên điểm thấp là chuyện bình thường;
            // điểm tụt dốc thì mới là dấu hiệu hai bên không tương ứng.
            Assert.True(
                zero.Sample.Mean > 40,
                $"offset 0 phải cho điểm khả dĩ; {zero.Sample.Mean:0.00} là dấu hiệu lệch khung, "
                + $"xem clip: {sourceFacts} | {candidateFacts}");

            ReferenceWindowExtractor.Release(sourceClips);
            ReferenceWindowExtractor.Release(candidateClips);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// Chứng minh điều mà <c>align=1/2</c> trong lưới cuối đang bù: hai tệp có **lưới keyframe
    /// khác nhau** thì lệnh <c>-ss</c> trước <c>-i</c> dừng ở hai nội dung khác nhau.
    /// </summary>
    /// <remarks>
    /// <para>Test <see cref="Thu_1080_xuong_720p_hai_clip_tuong_ung_theo_khung"/> cho cả hai tệp
    /// cùng <c>-g 250</c>, nên lưới keyframe trùng nhau và offset 0 khớp tuyệt đối. Nhưng tệp
    /// nguồn thật có GOP tuỳ ý, còn bản encode toàn tệp dùng GOP mặc định của x264 — hai lưới
    /// đó hiếm khi trùng. Đây là lý do lưới cuối phải thử lệch ±1 khung.</para>
    ///
    /// <para>Test này dựng đúng tình huống đó và đo lại, thay vì chỉ suy đoán.</para>
    /// </remarks>
    [RequiresFFmpeg]
    public async Task Hai_tep_khac_luoi_keyframe_se_llech_khung_dau()
    {
        var ffmpeg = TestFFmpeg.Require();
        var work = Directory.CreateTempSubdirectory("uc-gop-").FullName;

        try
        {
            // Nguồn có GOP rất ngắn — kiểu của video quay màn hình.
            var source = Path.Combine(work, "src.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-f", "lavfi",
                "-i", "testsrc2=size=1920x1080:rate=24:duration=8",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-g", "12",
                "-pix_fmt", "yuv420p", "-y", source);

            // Ứng viên: thu 720p rồi mã hoá lại, KHÔNG ép GOP — đúng như đường chạy thật.
            var candidate = Path.Combine(work, "cand.mp4");
            await RunAsync(ffmpeg,
                "-v", "error", "-i", source,
                "-vf", "scale=1280:720:flags=lanczos",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "22",
                "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", candidate);

            const double start = 3.0, length = 3.0;
            var window = new RepresentativeWindow(
                start, length, WindowRole.Typical, 0.5, 0.5, 0.5, 0.5, 0.5, 0, "gop");

            var extractor = new ReferenceWindowExtractor(ffmpeg, work);
            var sourceClips = await extractor.ExtractAsync(source, [window]);
            var candidateClips = await extractor.ExtractAsync(candidate, [window]);

            var sourceClip = sourceClips[0].Path;
            var candidateClip = candidateClips[0].Path;

            var sourceFacts = await TemporalProbe.ProbeAsync(ffmpeg, sourceClip);
            var candidateFacts = await TemporalProbe.ProbeAsync(ffmpeg, candidateClip);

            Assert.NotNull(sourceFacts);
            Assert.NotNull(candidateFacts);
            Assert.True(sourceFacts!.IsUsable, sourceFacts.ToString());
            Assert.True(candidateFacts!.IsUsable, candidateFacts.ToString());

            _output.WriteLine($"nguon (g=12)  clip: {sourceFacts}");
            _output.WriteLine($"ung vien (mac dinh) clip: {candidateFacts}");

            var probe = new QualityProbe(ffmpeg, work);
            var zero = await probe.MeasureAsync(
                sourceClip, candidateClip,
                new TimeWindow(0, length), new TimeWindow(0, length),
                1280, 720, 1280, 720, VmafModels.Default);
            var shifted = await probe.MeasureAsync(
                sourceClip, candidateClip,
                new TimeWindow(0, length), new TimeWindow(0, length),
                1280, 720, 1280, 720, VmafModels.Default,
                candidateStartFrame: 1);

            Assert.NotNull(zero);
            Assert.NotNull(shifted);
            _output.WriteLine($"VMAF offset 0 = {zero!.Sample.Mean:0.00} | bỏ 1 khung = {shifted!.Sample.Mean:0.00}");

            // Kết quả đo được, và nó phủ nhận giả thuyết đẹp nhất: lệch lưới keyframe KHÔNG
            // phải lý do lưới cuối phải thử ±1 khung. Hai clip vẫn khớp tuyệt đối ở offset 0.
            //
            // Điều này còn quan trọng hơn: bỏ một khung làm điểm rơi từ ~94 xuống ~32 — đó là
            // VÁC chứ không phải cải thiện nhẹ. Nghĩa là khi lưới cuối chọn "lệch 1 khung",
            // nó đang bám một cách giải thích sai, và điểm nó trả về có thể đang đo tệp nén
            // dở chứ không phải lệch khung. Biên căn chỉ 3 giá trị là cố ý — mở rộng nó là biến
            // phép đo thành bộ dò offset để nâng điểm.
            Assert.True(
                zero.Sample.Mean > 90,
                $"offset 0 phải khớp dù lưới keyframe khác nhau; {zero.Sample.Mean:0.00} là dấu hiệu lệch khung");

            Assert.True(
                shifted!.Sample.Mean < zero.Sample.Mean - 30,
                $"bỏ 1 khung phải làm điểm tụt mạnh (vác, không phải cải thiện nhẹ); "
                + $"thực tế {shifted.Sample.Mean:0.00} so với {zero.Sample.Mean:0.00}");

            ReferenceWindowExtractor.Release(sourceClips);
            ReferenceWindowExtractor.Release(candidateClips);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static async Task RunAsync(string tool, params string[] args)
    {
        var result = await ProcessRunner.RunAsync(
            tool, args, TimeSpan.FromMinutes(10), CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffmpeg thất bại ({result.ExitCode}): {result.StandardErrorText}");
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
