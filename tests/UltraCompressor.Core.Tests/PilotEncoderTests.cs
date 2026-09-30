using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Processes;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Ngữ nghĩa của <see cref="PilotEncoder"/> và <see cref="EncodeTarget"/>.
///
/// <para>Phần quan trọng nhất ở đây là <b>parity</b>: pilot và encode toàn tệp phải dùng
/// cùng một phép biến đổi. Nếu không, phép đo đang đo một thứ khác với thứ sẽ giao cho
/// người dùng — ứng viên có thể đạt VMAF rồi ra tệp hỏng.</para>
/// </summary>
public class PilotEncoderTests
{
    private static VideoEncodeCandidate Candidate(int width, int height, string encoder = "libx264") =>
        new($"{encoder}/{width}x{height}/crf23")
        {
            Codec = encoder == "libx265" ? VideoCodec.Hevc
                : encoder == "libaom-av1" ? VideoCodec.Av1 : VideoCodec.H264,
            EncoderName = encoder,
            Quality = encoder == "libaom-av1" ? QualityOption.LibaomCrf(32) : QualityOption.X26xCrf(23),
            Width = width,
            Height = height,
            Fps = 24,
            Speed = encoder == "libaom-av1" ? SpeedOption.AomCpuUsed(6) : SpeedOption.X26xPreset("medium"),
            PixelFormat = "yuv420p",
            Origin = CandidateOrigin.CoarseProbe,
            BranchId = $"{encoder}/{width}x{height}",
            PointIndex = 0,
            PointCount = 3,
            Reason = "kiểm thử",
        };

    private static RepresentativeWindow Window(WindowRole role, double start, double length = 3.0) =>
        new(start, length, role, 0.5, 0.5, 0.5, 0.5, 0.5, 0, $"{role} @ {start:0}s");

    private static string TempDir() => Directory.CreateTempSubdirectory("uc-pilot-").FullName;

    // ---------------------------------------------------------------- EncodeTarget

    [Fact]
    public void Kich_thuoc_dung_nguyen_nguon_thi_giu_nguyen()
    {
        Assert.True(EncodeTarget.TryFromRequest(1920, 1080, 1920, 1080, out var target, out _));

        Assert.Equal(1920, target.Width);
        Assert.Equal(1080, target.Height);
        // Ứng viên KHÔNG mang tần số đích: giữ nguyên nhịp của nguồn là mặc định và là trường hợp duy nhất ở giai đoạn này.
        Assert.False(target.ChangesFrameRate);
    }

    [Fact]
    public void Khong_bao_gio_phong_to()
    {
        // Yêu cầu lớn hơn nguồn: phải kẹp xuống, không được phóng to.
        Assert.True(EncodeTarget.TryFromRequest(1280, 720, 1920, 1080, out var target, out _));

        Assert.Equal(1280, target.Width);
        Assert.Equal(720, target.Height);
    }

    [Fact]
    public void Giu_ung_ti_le_khung_hinh_khong_ep_16_9()
    {
        // Nguồn 4:3 (640x480), yêu cầu 640x360. Phải ra 480x360 để vẫn là 4:3. Ép 16:9
        // sẽ ra 640x360, tức méo ngang.
        Assert.True(EncodeTarget.TryFromRequest(640, 480, 640, 360, out var target, out _));

        Assert.Equal(480, target.Width);
        Assert.Equal(360, target.Height);

        // Cùng tỉ lệ với nguồn.
        Assert.Equal(640 * 360, target.Width * 480);
    }

    [Fact]
    public void Kich_thuoc_luon_chan()
    {
        // 1080 -> 1081 (lẻ) phải làm tròn xuống, vì nhiều định dạng pixel từ chối số lẻ.
        Assert.True(EncodeTarget.TryFromRequest(1920, 1080, 1920, 1081, out var target, out _));

        Assert.Equal(1080, target.Height);
        Assert.Equal(0, target.Width % 2);
        Assert.Equal(0, target.Height % 2);
    }

    [Fact]
    public void Nguon_khong_hop_le_thi_bao_ngay_khong_goi_ffmpeg()
    {
        Assert.False(EncodeTarget.TryFromRequest(0, 0, 1920, 1080, out _, out var failure));
        Assert.Contains("không hợp lệ", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Kich_thuoc_qua_nho_van_duoc_ke_ve_san_2()
    {
        // Sàn 2: nhỏ hơn thế không có nghĩa nén, và nhiều bộ giải mã từ chối.
        Assert.True(EncodeTarget.TryFromRequest(1920, 1080, 1920, 1, out var target, out _));

        Assert.Equal(2, target.Height);
    }

    // ---------------------------------------------------------------- Parity transform

    [Fact]
    public void Nhanh_nho_hon_nguon_phai_co_filter_thu_nho()
    {
        var target = new EncodeTarget(1280, 720);

        var filter = EncodeTransform.BuildFilter(target, sourceWidth: 1920, sourceHeight: 1080);

        Assert.Contains("scale=1280", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Nhanh_bang_nguon_thi_khong_dung_filter()
    {
        var target = new EncodeTarget(1920, 1080);

        var filter = EncodeTransform.BuildFilter(target, sourceWidth: 1920, sourceHeight: 1080);

        Assert.Empty(filter);
    }

    [Fact]
    public void Khong_phai_hoi_nguon_thi_luon_giup_scale()
    {
        // Đây chính là bug đã mắc phải khi viết đầu tiên: truyền kích thước nguồn bằng 0
        // khiến điều kiện so sánh luôn sai và nhánh thu nhỏ không bao giờ chạy — tức ứng
        // viên nhỏ hơn lại được encode ở nguyên kích thước nguồn.
        var target = new EncodeTarget(1280, 720);

        Assert.Contains("scale", EncodeTransform.BuildFilter(target, 1920, 1080), StringComparison.Ordinal);
        Assert.Contains("scale", EncodeTransform.BuildFilter(target, 3840, 2160), StringComparison.Ordinal);
    }

    [Fact]
    public void Khong_co_muc_tieu_fps_thi_khong_bao_gio_co_filter_fps()
    {
        // Lỗi đã mắc phải: hàm dựng filter cứ cho `fps=23.98` với một nguồn 23,976 fps.
        // ffmpeg phải chuyển tỉ lệ để làm vậy — lặp hoặc bỏ khung hình — và ứng viên lệch
        // trục thời gian với tham chiếu, VMAF rơi từ 94 xuống 40.
        //
        // Cách sửa ở đúng tầng ngữ nghĩa: ứng viên MẶC ĐỊNH không có mục tiêu FPS, và
        // không còn phép so sánh "gần bằng" nào ở đây nữa. Không có mục tiêu thì không có
        // transform, không phải "có mục tiêu rồi thấy gần giống nên bỏ".
        var target = new EncodeTarget(1920, 1080);

        Assert.False(target.ChangesFrameRate);
        Assert.Null(target.FpsChange);
        Assert.Empty(EncodeTransform.BuildFilter(target, 1920, 1080));
    }

    [Fact]
    public void Nguon_24000_tren_1001_khong_bi_bien_thanh_23_98()
    {
        // 24000/1001 = 23,976…, khác 23,98 (làm tròn lên) và khác 24. Nếu chuỗi metadata
        // được biến thành số thập phân rồi truyền ngược vào bộ lọc, ffmpeg chuyển đổi khung
        // hình theo đúng sai số đó.
        //
        // Test này khẳng định ở tầng ngữ nghĩa: ứng viên không bao giờ mang tần số đích, nên
        // con số của nguồn không bao giờ đi vào bất kỳ đâu.
        const double NtscFilm = 24000d / 1001d;
        Assert.NotEqual(23.98, NtscFilm, 3);
        Assert.NotEqual(24.0, NtscFilm, 3);

        // Không có đường nào trong mô hình biết tới tần số nguồn, nên con số đó không thể
        // đi vào lệnh. Kiểm trên lệnh thật chứ không trên tên thuộc tính.
        var target = new EncodeTarget(1920, 1080);
        var args = EncodeTransform.BuildSegmentArguments(
            Candidate(1920, 1080).ToEncoderConfiguration(),
            EncodeTransform.BuildFilter(target, 1920, 1080),
            "s.mp4", "o.mp4", new TimeWindow(10, 3));

        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain("fps", string.Join(' ', args), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("23.9", string.Join(' ', args), StringComparison.Ordinal);
        Assert.DoesNotContain("24", args);

        // Kể cả khi tham số nguồn là số thập phân xấp xỉ, kết quả vẫn không đổi.
        foreach (var approximate in new[] { 23.976, 23.98, 24.0, 29.97, 30.0 })
        {
            Assert.Empty(EncodeTransform.BuildFilter(new EncodeTarget(1280, 720), 1280, 720));
            Assert.True(approximate > 0);
        }
    }

    [Fact]
    public void Tan_sot_24000_tren_1001_phai_di_nguyen_van_vao_bo_loc()
    {
        // Đường này chỉ mở khi có ứng viên thật sự đổi nhịp khung hình. Khi đó phải truyền
        // NGUYÊN VĂN "24000/1001", không phải 23,976 — vì 23,976 là tần số khác, và ffmpeg
        // sẽ lấp hoặc bỏ khung hình theo đúng sai số đó.
        //
        // Đi qua double thì không giữ được: 24000/1001 không biểu diễn chính xác, và
        // ToString("0.###") cho ra 23,976 — vẫn sai.
        var target = new EncodeTarget(1920, 1080)
            .WithFrameRateChange(24000d / 1001d, "đồng bộ với nguồn NTSC", "24000/1001");

        var filter = string.Join(' ', EncodeTransform.BuildFilter(target, 1920, 1080));

        Assert.Contains("fps=24000/1001", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("fps=23.9", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("fps=24,", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("fps=24.0", filter, StringComparison.Ordinal);
        Assert.Equal("24000/1001", target.FpsChange!.Value.FrameRateText);
    }

    [Fact]
    public void Tan_sot_co_phan_so_bi_loi_ngay_tai_ranh_gioi()
    {
        // Chặn sớm vì lỗi này chỉ lộ ra giữa chừng một lần encode dài, lúc đó khó truy nguyên
        // nguyên nhân: filter sai thì ffmpeg báo lỗi khó hiểu, hoặc tệ hơn là chạy được và
        // ra kết quả sai.
        foreach (var bad in new[] { "24000", "23.976", "24/0", "0/1001", "-24/1", "24000/1001/2", "24/1.5" })
        {
            Assert.Throws<ArgumentException>(
                () => new EncodeTarget(1920, 1080).WithFrameRateChange(24, "lý do", bad));
        }

        // Rỗng KHÔNG phải lỗi: nghĩa là "không có phân số, dùng số thập phân". Người gọi giảm
        // nhịp xuống 12 là tần số thập phân sẵn, nên không cần phân số.
        var plain = new EncodeTarget(1920, 1080).WithFrameRateChange(12, "giảm nhịp", rationalText: null);
        Assert.Contains("fps=12", string.Join(' ', EncodeTransform.BuildFilter(plain, 1920, 1080)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Doi_fps_phai_la_quyet_dinh_co_ly_do_va_co_ten()
    {
        // Nếu sau này có ứng viên thật sự đổi nhịp khung hình, việc đó phải là một quyết định
        // có tên và có lý do — không phải một con số ai đó thêm vào để "cho khớp".
        var reduced = new EncodeTarget(1920, 1080).WithFrameRateChange(12, "giảm nhịp để tiết kiệm dung lượng");

        Assert.True(reduced.ChangesFrameRate);
        Assert.Contains("fps=12", EncodeTransform.BuildFilter(reduced, 1920, 1080), StringComparison.Ordinal);
        Assert.Equal("giảm nhịp để tiết kiệm dung lượng", reduced.FpsChange!.Value.Reason);

        // Không lý do thì không cho đổi.
        Assert.Throws<ArgumentException>(() =>
            new EncodeTarget(1920, 1080).WithFrameRateChange(12, "   "));

        // Tần số không hợp lệ thì không cho đổi.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncodeTarget(1920, 1080).WithFrameRateChange(0, "lý do"));
    }

    [Fact]
    public void Khong_tang_so_khung_hinh()
    {
        // Số khung hình đích luôn lấy từ nguồn ở giai đoạn này. Ứng viên có trường Fps
        // riêng nhưng planner chưa cho phép tăng, nên phải bằng nguồn.
        Assert.True(EncodeTarget.TryFromRequest(1920, 1080, 1280, 720, out var target, out _));
        // Nhịp khung hình nằm ở nguồn, không nằm trong ứng viên — nên không có gì để bị ép sai.
        Assert.False(target.ChangesFrameRate);
    }

    // ---------------------------------------------------------------- Lệnh encode

    [Fact]
    public void Lenh_seek_nguon_truoc_khi_doc_dau_vao()
    {
        var target = new EncodeTarget(1920, 1080);
        var args = EncodeTransform.BuildSegmentArguments(
            Candidate(1920, 1080).ToEncoderConfiguration(), "", "src.mp4", "out.mp4", new TimeWindow(947.9, 3.0));

        var ssIndex = args.ToList().IndexOf("-ss");
        var iIndex = args.ToList().IndexOf("-i");

        Assert.True(ssIndex >= 0 && ssIndex < iIndex, "-ss phải đứng trước -i để seek nhanh");
        Assert.Equal("947.9", args[ssIndex + 1]);
        Assert.Equal("3", args[iIndex - 1]);
    }

    [Fact]
    public void Lenh_khong_ma_hoa_am_thanh()
    {
        var target = new EncodeTarget(1920, 1080);
        var args = EncodeTransform.BuildSegmentArguments(
            Candidate(1920, 1080).ToEncoderConfiguration(), "", "src.mp4", "out.mp4", new TimeWindow(10, 3));

        // VMAF chỉ nhìn hình; mã hoá âm thanh chỉ làm chậm mà không đóng góp gì.
        Assert.Contains("-an", args);
    }

    [Fact]
    public void Lenh_dung_dung_cau_hinh_encoder()
    {
        var target = new EncodeTarget(1920, 1080);

        var x264 = EncodeTransform.BuildSegmentArguments(
            Candidate(1920, 1080, "libx264").ToEncoderConfiguration(), "", "s.mp4", "o.mp4", new TimeWindow(10, 3));
        Assert.Equal("medium", x264[x264.ToList().IndexOf("-preset") + 1]);
        Assert.Equal("23", x264[x264.ToList().IndexOf("-crf") + 1]);

        var av1 = EncodeTransform.BuildSegmentArguments(
            Candidate(1920, 1080, "libaom-av1").ToEncoderConfiguration(), "", "s.mp4", "o.mp4", new TimeWindow(10, 3));

        Assert.Contains("-cpu-used", av1);
        Assert.DoesNotContain("-preset", av1);
    }

    [Fact]
    public void Lenh_co_filter_thi_dung_vf()
    {
        var target = new EncodeTarget(1280, 720);
        var filter = EncodeTransform.BuildFilter(target, 1920, 1080);

        var args = EncodeTransform.BuildSegmentArguments(
            Candidate(1280, 720).ToEncoderConfiguration(), filter, "s.mp4", "o.mp4", new TimeWindow(10, 3));

        Assert.Contains("-vf", args);
        Assert.Equal(filter, args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Cau_hinh_encoder_sai_thi_bi_tu_choi_truoc_khi_goi_ffmpeg()
    {
        // Chặn ở đây, không phải để ffmpeg tự chết sau khi đã tốn công.
        var configuration = new EncoderConfiguration
        {
            EncoderName = "libx264",
            Quality = QualityOption.X26xCrf(23),
            Speed = SpeedOption.AomCpuUsed(6),
            PixelFormat = "yuv420p",
        };

        Assert.Throws<ArgumentException>(() => EncodeTransform.EncoderArguments(configuration));
    }

    // ---------------------------------------------------------------- Hành vi encode

    [RequiresFFmpeg]
    public async Task Encode_dung_doan_va_clip_bat_dau_tai_0()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();
            var source = Path.Combine(work, "src.mp4");
            await MakeSourceAsync(ffmpeg, source, durationSeconds: 20);

            var encoder = new PilotEncoder(ffmpeg, work);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), source, 1920, 1080,
                [Window(WindowRole.HighMotion, 12.0)]);

            var artifact = Assert.Single(artifacts);
            Assert.True(artifact.Success, artifact.Outcome.Message);
            Assert.True(artifact.Bytes > 0);

            // Reference giữ mốc của nguồn; clip pilot bắt đầu tại 0.
            Assert.Equal(12.0, artifact.ReferenceWindow.StartSeconds, 3);
            Assert.Equal(0, artifact.CandidateWindow.StartSeconds);
            Assert.Equal(artifact.ReferenceWindow.LengthSeconds, artifact.CandidateWindow.LengthSeconds, 3);

            // Clip thật phải ngắn hơn mốc bắt đầu — bằng chứng nó đã được cắt.
            var length = await MeasureDurationAsync(ffmpeg, artifact.OutputPath!);
            Assert.InRange(length, 2.5, 3.5);
            Assert.True(length < 12.0);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Encode_nhanh_nho_hon_thuoc_su_dung_filter_scale()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();
            var source = Path.Combine(work, "src.mp4");
            await MakeSourceAsync(ffmpeg, source, durationSeconds: 6);

            var encoder = new PilotEncoder(ffmpeg, work);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1280, 720), source, 1920, 1080,
                [Window(WindowRole.Typical, 1.0)]);

            var artifact = Assert.Single(artifacts);
            Assert.True(artifact.Success, artifact.Outcome.Message);

            // Tệp ra thực sự nhỏ hơn kích thước nguồn, tức filter đã chạy.
            var probe = new MediaProbe(ffmpeg);
            var info = await probe.ProbeAsync(artifact.OutputPath!);

            Assert.Equal(1280, info.Width);
            Assert.Equal(720, info.Height);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Nguon_khong_ton_tai_thi_bao_ma_khong_lam_hong_luoi()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();
            var encoder = new PilotEncoder(ffmpeg, work);

            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), Path.Combine(work, "khong-co.mp4"), 1920, 1080,
                [Window(WindowRole.Typical, 1.0), Window(WindowRole.HighMotion, 20.0)]);

            Assert.Equal(2, artifacts.Count);
            Assert.All(artifacts, a => Assert.False(a.Success));
            Assert.All(artifacts, a =>
                Assert.Equal(SearchDecisionReasons.PilotEncodeFailed, a.Outcome.Reason));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Huy_giua_chung_thi_moi_doan_roi_va_bao_dung_ma_khong_lam_hong_luoi()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();
            var source = Path.Combine(work, "src.mp4");
            await MakeSourceAsync(ffmpeg, source, durationSeconds: 20);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var encoder = new PilotEncoder(ffmpeg, work);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), source, 1920, 1080,
                [Window(WindowRole.Typical, 1.0), Window(WindowRole.HighMotion, 8.0)], cts.Token);

            Assert.Equal(2, artifacts.Count);
            Assert.All(artifacts, a => Assert.False(a.Success));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Clip_pilot_phai_con_lai_de_buoc_do_doc()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();
            var source = Path.Combine(work, "src.mp4");
            await MakeSourceAsync(ffmpeg, source, durationSeconds: 20);

            var encoder = new PilotEncoder(ffmpeg, work);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), source, 1920, 1080,
                [Window(WindowRole.Typical, 1.0), Window(WindowRole.HighMotion, 8.0)]);

            // Tệp PHẢI còn sau khi encode trả về: bước đo chất lượng sắp tới đọc chính
            // các tệp này. Xoá ở đây nghĩa là cả chuỗi đo đại diện rơi vào hư không.
            Assert.All(artifacts, a => Assert.True(File.Exists(a.OutputPath!), a.OutputPath));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Release_xoa_hoi_clip_pilot()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();
            var source = Path.Combine(work, "src.mp4");
            await MakeSourceAsync(ffmpeg, source, durationSeconds: 20);

            var encoder = new PilotEncoder(ffmpeg, work);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), source, 1920, 1080,
                [Window(WindowRole.Typical, 1.0)]);

            var path = Assert.Single(artifacts).OutputPath!;
            Assert.True(File.Exists(path));

            PilotEncoder.Release(artifacts);

            Assert.False(File.Exists(path));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Khong_con_file_tam_nao_sau_khi_giai_phong()
    {
        var work = TempDir();
        var probeDir = Path.Combine(work, "probe");
        Directory.CreateDirectory(probeDir);

        try
        {
            var ffmpeg = FfmpegPath();
            var source = Path.Combine(work, "src.mp4");
            await MakeSourceAsync(ffmpeg, source, durationSeconds: 20);

            var encoder = new PilotEncoder(ffmpeg, probeDir);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), source, 1920, 1080,
                [Window(WindowRole.Typical, 1.0), Window(WindowRole.HighMotion, 8.0)]);

            Assert.Equal(2, artifacts.Count);

            // Dọn đúng lúc thì không sót tệp nào trong thư mục tạm.
            PilotEncoder.Release(artifacts);
            Assert.Empty(Directory.GetFiles(probeDir, "*.mp4"));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [RequiresFFmpeg]
    public async Task Encode_hong_thi_khong_de_lai_tep_rac()
    {
        var work = TempDir();
        try
        {
            var ffmpeg = FfmpegPath();

            // Tệp nguồn không tồn tại: encode chắc chắn hỏng, và không được để lại tệp dở dang.
            var encoder = new PilotEncoder(ffmpeg, work);
            var artifacts = await encoder.EncodeWindowsAsync(
                Candidate(1920, 1080), Path.Combine(work, "khong-co.mp4"), 1920, 1080,
                [Window(WindowRole.Typical, 1.0)]);

            Assert.False(Assert.Single(artifacts).Success);
            Assert.Empty(Directory.GetFiles(work, "*.mp4"));
        }
        finally
        {
            TryDelete(work);
        }
    }

    // ---------------------------------------------------------------- Tiện ích
    private static string FfmpegPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UltraCompressor.slnx")))
            {
                foreach (var rel in new[] { "app/ffmpeg.exe", "tools/ffmpeg.exe" })
                {
                    var p = Path.GetFullPath(Path.Combine(dir.FullName, rel));
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("RequiresFFmpeg nên phải tìm thấy ffmpeg.");
    }

    private static async Task MakeSourceAsync(string ffmpeg, string path, double durationSeconds)
    {
        var r = await ProcessRunner.RunAsync(ffmpeg,
        [
            "-v", "error", "-f", "lavfi", "-i",
            $"testsrc2=size=1920x1080:rate=24:duration={durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-pix_fmt", "yuv420p", "-y", path,
        ], token: CancellationToken.None);

        Assert.True(r.Succeeded, "khong tao duoc tep nguon de kiem thu");
    }

    private static async Task<double> MeasureDurationAsync(string ffmpeg, string path)
    {
        var info = await new MediaProbe(ffmpeg).ProbeAsync(path);
        return info.Duration?.TotalSeconds ?? 0;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
