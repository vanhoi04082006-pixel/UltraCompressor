using UltraCompressor.Core.Media;
using Xunit;

namespace UltraCompressor.Core.Tests;

public class FFmpegOutputParserTests
{
    [Theory]
    [InlineData("  Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s", 83.45)]
    [InlineData("  Duration: 01:02:03.04, start: 0.000000, bitrate: 500 kb/s", 3723.04)]
    [InlineData("Duration: 00:00:10.00,", 10)]
    public void Doc_thoi_luong(string line, double expectedSeconds)
    {
        var duration = FFmpegOutputParser.ParseDuration(line);
        Assert.NotNull(duration);
        Assert.Equal(expectedSeconds, duration!.Value.TotalSeconds, 2);
    }

    [Fact]
    public void Duration_khong_co_thi_tra_null()
    {
        // ffmpeg in "Duration: N/A" với nhiều định dạng. Bản gốc xử lý sai chỗ này và
        // coi như thời lượng bằng 0, khiến tiến độ không bao giờ chạy.
        Assert.Null(FFmpegOutputParser.ParseDuration("  Duration: N/A, bitrate: N/A"));
        Assert.Null(FFmpegOutputParser.ParseDuration("vô nghĩa"));
        Assert.Null(FFmpegOutputParser.ParseDuration(null));
    }

    [Fact]
    public void Doc_thoi_luong_cho_phep_hon_99_gio()
    {
        // Regex của bản gốc là (\d{2}):(\d{2}):(\d{2}) — chỉ nhận đúng hai chữ số, nên
        // video trên 99 giờ không có tiến độ. Ở đây phải chấp nhận ba chữ số trở lên.
        var duration = FFmpegOutputParser.ParseDuration("Duration: 120:00:00.00, bitrate: 800 kb/s");
        Assert.NotNull(duration);
        Assert.Equal(120 * 3600, duration!.Value.TotalSeconds, 0);
    }

    [Theory]
    [InlineData("frame= 100 fps=25 q=28.0 size=100kB time=00:00:04.00 bitrate=200.0kbits/s", 4)]
    [InlineData("time=01:02:03.45", 3723.45)]
    public void Doc_moc_thoi_gian(string line, double expectedSeconds)
    {
        var position = FFmpegOutputParser.ParseTime(line);
        Assert.NotNull(position);
        Assert.Equal(expectedSeconds, position!.Value.TotalSeconds, 2);
    }

    [Theory]
    [InlineData(0.0, 10.0, 0)]
    [InlineData(5.0, 10.0, 50)]
    [InlineData(9.99, 10.0, 99)]
    [InlineData(10.0, 10.0, 99)]
    [InlineData(99.0, 10.0, 99)]
    public void Tinh_phan_tram(double position, double total, int expected)
        => Assert.Equal(expected, FFmpegOutputParser.ToPercent(
            TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(total))!.Value);

    [Fact]
    public void Thoi_luong_bang_khong_thi_khong_tinh_duoc_phan_tram()
    {
        Assert.Null(FFmpegOutputParser.ToPercent(TimeSpan.FromSeconds(5), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("out_time_us=0", 0)]
    [InlineData("out_time_us=5920000", 5.92)]
    [InlineData("out_time_us=1200000000", 1200)]
    public void Doc_thoi_diem_tu_dong_kenh_tien_bo(string line, double expectedSeconds)
    {
        // -progress pipe:1 là nguồn tiến độ duy nhất còn hoạt động khi pipeline chạy với
        // -loglevel error. Không có nó thì ffmpeg im lặng và thanh tiến độ đứng ở 0%.
        var position = FFmpegOutputParser.ParseProgressTime(line);
        Assert.NotNull(position);
        Assert.Equal(expectedSeconds, position!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void Khong_doc_nham_out_time_ms_thanh_micro_giay()
    {
        // ffmpeg ghi out_time_ms cũng bằng đơn vị micro giây. Đọc nhầm sang trường đó làm
        // phần trăm nhảy gấp 1000 lần, nên parser cố tình chỉ chấp nhận out_time_us.
        Assert.Null(FFmpegOutputParser.ParseProgressTime("out_time_ms=5920000"));
        Assert.Null(FFmpegOutputParser.ParseProgressTime("out_time=00:00:05.920000"));
        Assert.Null(FFmpegOutputParser.ParseProgressTime("progress=continue"));
        Assert.Null(FFmpegOutputParser.ParseProgressTime(null));
    }

    [Fact]
    public void Doc_bitrate()
    {
        var line = "frame=  100 fps=0.0 q=28.0 size=     256kB time=00:00:08.00 bitrate= 262.1kbits/s";
        Assert.Equal(262.1, FFmpegOutputParser.ParseBitrateKbps(line)!.Value, 1);
    }

    [Fact]
    public void Nhan_dien_luong_am_thanh()
    {
        var lines = new[]
        {
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'video.mp4':",
            "  Stream #0:0(eng): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1280x720",
            "  Stream #0:1(eng): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, stereo",
        };

        Assert.True(FFmpegOutputParser.HasVideoStream(lines));
        Assert.True(FFmpegOutputParser.HasAudioStream(lines));
    }

    [Fact]
    public void Video_khong_tieng_thi_khong_co_luong_am_thanh()
    {
        var lines = new[]
        {
            "  Stream #0:0(und): Video: h264 (High), yuv420p, 320x240",
        };

        Assert.True(FFmpegOutputParser.HasVideoStream(lines));
        Assert.False(FFmpegOutputParser.HasAudioStream(lines));
    }
}
