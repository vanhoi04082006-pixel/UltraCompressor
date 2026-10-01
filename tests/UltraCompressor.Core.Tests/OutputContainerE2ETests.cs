using UltraCompressor.Core;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Search;
using UltraCompressor.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Hồ sơ của lỗi "container đầu ra kế thừa phần mở rộng tệp nguồn", chạy trên ffmpeg thật.
///
/// <para>Lỗi: <c>TempWorkspace.CreatePath</c> sao chép phần mở rộng của nguồn sang tệp tạm,
/// trong khi lệnh encode mang <c>-movflags +faststart</c> — một tuỳ chọn của muxer mov/mp4. Đo
/// được trước khi sửa: cùng một lệnh, chỉ khác phần mở rộng đầu ra, thì <c>.mkv</c> ra
/// Matroska và <c>.ts</c> ra MPEG-TS, còn tuỳ chọn đó bị nuốt lặng lẽ. Nguồn <c>.webm</c> có
/// tiếng còn hỏng hẳn vì lệnh dùng <c>-c:a aac</c> mà WebM không nhận.</para>
///
/// <para>Các test ở đây không kiểm bằng cách tin rằng <c>OutputContainer</c> trả về
/// <c>".mp4"</c> — chúng chạy encode thật rồi <b>đọc chữ ký container</b> của tệp ra.</para>
/// </remarks>
public class OutputContainerE2ETests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static string Ffmpeg() => TestFFmpeg.Require();

    /// <summary>Đọc 16 byte đầu tệp, đủ để nhận ra loại container.</summary>
    private static string Signature(string path)
    {
        var head = new byte[16];
        using var fs = File.OpenRead(path);
        var read = fs.Read(head, 0, 16);
        return BitConverter.ToString(head, 0, read).Replace('-', ' ');
    }

    /// <summary>Có phải hộp MP4/MOV (bắt đầu bằng kích thước rồi tới <c>ftyp</c>) không.</summary>
    private static bool IsMp4Family(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12) return false;

        // byte 4..7 phải là "ftyp" — đó là hộp đầu tiên của mọi tệp MP4/MOV.
        return bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p';
    }

    /// <summary>Có phải byte đồng bộ MPEG-TS (0x47) không.</summary>
    private static bool IsMpegTs(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[1];
        _ = fs.Read(head, 0, 1);
        return head[0] == 0x47;
    }

    /// <summary>
    /// Nguồn MPEG-TS, Matroska và MP4 đều phải ra MP4 — kiểm bằng byte thật của tệp ra.
    /// </summary>
    /// <remarks>
    /// <para>Dùng một <c>[Fact]</c> quét nhiều phần mở rộng thay vì <c>[Theory]</c>: điều kiện
    /// "có ffmpeg hay không" là của môi trường, không phụ thuộc tham số, mà
    /// <c>[RequiresFFmpeg]</c> kế thừa <c>[Fact]</c> nên không gắn được cùng
    /// <c>[Theory]</c>.</para>
    ///
    /// <para>Bao gồm cả nguồn MP4 để chắc việc sửa không làm hỏng trường hợp vốn đã đúng.</para>
    /// </remarks>
    [RequiresFFmpeg]
    public async Task Tep_dau_ra_phai_la_mp4_bat_ke_nguon_la_gi()
    {
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-container-").FullName;

        try
        {
            foreach (var sourceExtension in new[] { ".ts", ".mkv", ".mp4" })
            {
                var source = Path.Combine(work, $"nguon{sourceExtension}");
                var muxer = sourceExtension == ".ts" ? "mpegts" : sourceExtension == ".mkv" ? "matroska" : "mp4";

                await UltraCompressor.Core.Processes.ProcessRunner.RunAsync(
                    ffmpeg,
                    [
                        "-hide_banner", "-loglevel", "error", "-nostdin",
                        "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3",
                        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p",
                        "-f", muxer, "-y", source,
                    ],
                    TimeSpan.FromMinutes(5),
                    CancellationToken.None);

                using var workspace = new TempWorkspace(work);

                // Đúng cách đường chạy thật tạo đường dẫn tạm: container do OutputContainer
                // quyết, KHÔNG sao chép từ nguồn.
                var temp = workspace.CreatePath(OutputContainer.ExtensionFor(MediaKind.Video, source));
                var target = new EncodeTarget(640, 360);

                // Cùng lệnh mà cả đường cũ lẫn đường thích ứng đều dùng cho encode toàn tệp.
                var args = EncodeTransform.BuildFullArguments(
                    new EncoderConfiguration
                    {
                        EncoderName = "libx264",
                        Quality = QualityOption.X26xCrf(28),
                        Speed = SpeedOption.X26xPreset("veryfast"),
                        PixelFormat = "yuv420p",
                    },
                    EncodeTransform.BuildFilter(target, 640, 360),
                    source,
                    temp,
                    audioBitrateKbps: null);

                var result = await UltraCompressor.Core.Processes.ProcessRunner.RunAsync(
                    ffmpeg, [.. args], TimeSpan.FromMinutes(5), CancellationToken.None);

                Assert.True(result.Succeeded, result.StandardErrorText);
                Assert.True(File.Exists(temp));

                var signature = Signature(temp);
                _output.WriteLine($"nguon {sourceExtension} -> tep tam {Path.GetExtension(temp)} : {signature}");

                // Container THẬT của tệp ra, đọc từ byte — không phải từ tên tệp.
                Assert.True(IsMp4Family(temp), $"tep dau ra khong phai MP4: {signature}");
                Assert.False(IsMpegTs(temp), "tep dau ra dang la MPEG-TS");

                // Phần mở rộng tạm cũng phải đúng, vì nó là thứ quyết định muxer.
                Assert.Equal(OutputContainer.Mp4, Path.GetExtension(temp));
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [RequiresFFmpeg]
    public async Task Nguon_webm_co_tieng_truoc_day_hong_han_bang_mp4()
    {
        // Lỗi nặng nhất của nhóm này: nguồn `.webm` có tiếng bị ffmpeg từ chối, vì lệnh encode
        // của ta dùng `-c:a aac` còn WebM chỉ nhận Vorbis/Opus. Chỉ cần container đầu ra không
        // còn bám theo nguồn là lỗi biến mất.
        var ffmpeg = Ffmpeg();
        var work = Directory.CreateTempSubdirectory("uc-container-audio-").FullName;

        try
        {
            var source = Path.Combine(work, "nguon.webm");
            await UltraCompressor.Core.Processes.ProcessRunner.RunAsync(
                ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                    "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=3",
                    "-map", "0:a", "-map", "1:v", "-b:a", "128k", "-c:v", "libvpx",
                    "-pix_fmt", "yuv420p", "-f", "webm", "-y", source,
                ],
                TimeSpan.FromMinutes(5),
                CancellationToken.None);

            using var workspace = new TempWorkspace(work);
            var temp = workspace.CreatePath(OutputContainer.ExtensionFor(MediaKind.Video, source));

            var args = EncodeTransform.BuildFullArguments(
                new EncoderConfiguration
                {
                    EncoderName = "libx264",
                    Quality = QualityOption.X26xCrf(28),
                    Speed = SpeedOption.X26xPreset("veryfast"),
                    PixelFormat = "yuv420p",
                },
                string.Empty,
                source,
                temp,
                audioBitrateKbps: 128);

            var result = await UltraCompressor.Core.Processes.ProcessRunner.RunAsync(
                ffmpeg, [.. args], TimeSpan.FromMinutes(5), CancellationToken.None);

            Assert.True(result.Succeeded, result.StandardErrorText);
            Assert.True(File.Exists(temp));
            Assert.True(IsMp4Family(temp), $"tep dau ra khong phai MP4: {Signature(temp)}");

            // Tiếng vẫn phải còn trong tệp ra — sửa container không được làm mất nội dung.
            var probe = await new MediaProbe(ffmpeg).ProbeAsync(temp);
            Assert.True(probe.HasAudio, "phai giu duoc am thanh");
            Assert.True(probe.HasVideo, "phai giu duoc video");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
