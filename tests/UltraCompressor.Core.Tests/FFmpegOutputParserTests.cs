using UltraCompressor.Core.Media;
using Xunit;

namespace UltraCompressor.Core.Tests;

public class FFmpegOutputParserTests
{
    /// <summary>
    /// Bug thật: chiều cao bị báo bằng chiều rộng.
    ///
    /// <para>Hàm cũ chỉ trả về chiều rộng, rồi cả <c>Width</c> lẫn <c>Height</c> cùng
    /// nhận giá trị đó. Người dùng thấy video 1918×1078 bị báo 1918×1918 trong hộp so
    /// sánh.</para>
    ///
    /// <para>Nguy hiểm hơn nhiều so với con số sai trên màn hình: mật độ bit/px/khung
    /// dùng W×H, nên báo vuông làm mật độ thấp hơn thật, planner cho rằng nguồn còn dư
    /// chất lượng, và nâng CRF nhiều hơn cần thiết — nén quá mạnh mà không ai biết.</para>
    /// </summary>
    [Fact]
    public void Doc_dung_ca_chieu_rong_va_chieu_cao()
    {
        // Nguyên văn dòng ffmpeg cho tệp thật của người dùng.
        const string line =
            "  Stream #0:0[0x1](und): Video: h264 (Main) (avc1 / 0x31637661), yuv420p(progressive), " +
            "1918x1078 [SAR 1:1 DAR 137:77], 8001 kb/s, 30 fps, 30 tbr, 30k tbn (default)";

        var (w, h) = FFmpegOutputParser.ParseDimensions(line);

        Assert.Equal(1918, w);
        Assert.Equal(1078, h);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(1280, 720)]
    [InlineData(1918, 1078)]
    public void Khong_bao_khung_hinh_vuong(int expectedW, int expectedH)
    {
        var (w, h) = FFmpegOutputParser.ParseDimensions($"Video: h264, {expectedW}x{expectedH}, 30 fps");

        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Fact]
    public void Khong_co_khung_hinh_thi_tra_null_ca_hai()
    {
        // Không có cặp số nào thì phải trả null cho cả hai. Trả null lệch phía thì mật độ
        // bit/px/khung có thể được tính bằng 0 và ra số vô nghĩa.
        var (w, h) = FFmpegOutputParser.ParseDimensions("Stream #0:0: Audio: aac (LC), 48000 Hz, stereo");
        Assert.Null(w);
        Assert.Null(h);

        var (w2, h2) = FFmpegOutputParser.ParseDimensions(null);
        Assert.Null(w2);
        Assert.Null(h2);
    }

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

    /// <summary>
    /// Bug thật gặp khi test trên tệp thật: bản gốc đọc bitrate ở <c>lines[^1]</c>, tức dòng
    /// cuối của stderr. Nhưng dòng đó là "At least one output file must be specified" —
    /// vì lệnh <c>ffmpeg -i</c> không có tệp đầu ra. Bitrate luôn null, mật độ
    /// bit/px/khung mất trắng, và planner rơi về tham số nền cho MỌI tệp.
    ///
    /// Job vẫn chạy và vẫn ra tệp, nên lỗi này gần như vô hình cho tới khi ta đo ra tệp
    /// 19 phút chỉ giảm được 9,7%.
    /// </summary>
    [Fact]
    public void Doc_bitrate_tu_dong_ffmpeg_that_khong_phai_vao_dong_cuoi()
    {
        // Nguyên văn stderr của ffmpeg cho một tệp thật, đã cắt gọn còn đúng thứ tự.
        var stderr = new[]
        {
            "ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers",
            "  built with gcc 14.2.0",
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'Mama x Holic - 01.mp4':",
            "  Metadata:",
            "    major_brand     : isom",
            "  Duration: 00:19:34.49, start: 0.000000, bitrate: 1629 kb/s",
            "  Stream #0:0[0x1](und): Video: h264 (Main) (avc1 / 0x31637661), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 1374 kb/s, 23.98 fps, 23.98 tbr, 90k tbn (default)",
            "  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 249 kb/s (default)",
            "      handler_name    : SoundHandler",
            "At least one output file must be specified",
        };

        // 1629 là bitrate TỔNG của tệp; 1374 là riêng luồng video và luôn thấp hơn.
        // Phải lấy 1629: mật độ bit/px/khung cần trả lời "cả tệp này còn dư bao nhiêu bit".
        Assert.Equal(1629, FFmpegOutputParser.FindInputBitrateKbps(stderr));
        Assert.Equal(1374, FFmpegOutputParser.FindVideoStreamBitrateKbps(stderr));
    }

    [Fact]
    public void Doc_bitrate_fallback_khi_khong_co_dong_input()
    {
        // Một số bản ffmpeg / một số container không in dòng Input #0. Khi đó dùng bitrate
        // của riêng luồng video thay vì bỏ trống.
        var stderr = new[]
        {
            "  Duration: 00:19:34.49, start: 0.000000, bitrate: 1629 kb/s",
            "  Stream #0:0: Video: h264 (Main), 1920x1080, 1374 kb/s, 23.98 fps",
        };

        Assert.Null(FFmpegOutputParser.FindInputBitrateKbps(stderr));
        Assert.Equal(1374, FFmpegOutputParser.FindVideoStreamBitrateKbps(stderr));
    }

    [Fact]
    public void Doc_bitrate_tra_null_khi_khong_co_du_lieu()
    {
        // Không có gì thì trả null, không trả 0 — 0 sẽ bị planner hiểu là bitrate 0 và
        // kích hoạt nhầm nhánh "nguồn đã cạn".
        var stderr = new[] { "ffmpeg version 7.1", "At least one output file must be specified" };

        Assert.Null(FFmpegOutputParser.FindInputBitrateKbps(stderr));
        Assert.Null(FFmpegOutputParser.FindVideoStreamBitrateKbps(stderr));
    }

    [Fact]
    public void Doc_bitrate_am_thanh_phai_la_rieng_luong_am_thanh()
    {
        // Ba con so khac nhau, va hay nham sai nhat la tong voi am thanh:
        //   1629 kb/s  = TONG ca tep
        //   1374 kb/s  = rieng luong VIDEO
        //   128  kb/s  = rieng luong AUDIO
        //
        // Dung 1629 lam bitrate am thanh tren tep 300s cong them 83 MB vao uoc luong, du
        // de loai ca ung vien tot. Day la ly do phai doc rieng, khong suy ra tu tong.
        var stderr = new[]
        {
            "  Duration: 00:05:00.00, start: 0.000000, bitrate: 1629 kb/s",
            "  Stream #0:0: Video: h264 (Main), 1920x1080, 1374 kb/s, 23.98 fps",
            "  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp, 128 kb/s",
        };

        Assert.Equal(128, FFmpegOutputParser.FindAudioStreamBitrateKbps(stderr));
        Assert.NotEqual(FFmpegOutputParser.FindInputBitrateKbps(stderr),
            FFmpegOutputParser.FindAudioStreamBitrateKbps(stderr));
    }

    [Fact]
    public void Doc_bitrate_am_thanh_tra_null_khi_ffmpeg_khong_in()
    {
        // ffmpeg thuong BO TRONG truong do voi AAC trong MP4. Khong co so do la 0 — 0 se
        // bi hieu la "khong co am thanh" va uoc luong se mat het phan am thanh.
        var stderr = new[]
        {
            "  Duration: 00:05:00.00, start: 0.000000, bitrate: 1629 kb/s",
            "  Stream #0:0: Video: h264 (Main), 1920x1080, 1374 kb/s, 23.98 fps",
            "  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp",
        };

        Assert.Null(FFmpegOutputParser.FindAudioStreamBitrateKbps(stderr));
    }

    [Fact]
    public void Doc_bitrate_am_thanh_chi_doc_dung_luong_am_thanh()
    {
        // Regex phai khop dung loai luong. Neu no chap nhan ca hai, thi "audio" se lam
        // chay bang chieu video — va phan am thanh uoc ra cong bang phan video.
        var stderr = new[]
        {
            "  Stream #0:0: Video: h264 (Main), 1920x1080, 1374 kb/s, 23.98 fps",
            "  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp, 128 kb/s",
        };

        Assert.Equal(1374, FFmpegOutputParser.FindVideoStreamBitrateKbps(stderr));
        Assert.Equal(128, FFmpegOutputParser.FindAudioStreamBitrateKbps(stderr));
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
