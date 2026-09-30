using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Lưới an toàn sau khi nén (giai đoạn 5A).
///
/// <para>Test ở đây dùng probe null là chủ ý: nhánh kích thước phải đúng <b>không cần</b>
/// ffmpeg, vì đó là nhánh rẻ nhất và là nhánh đã tồn tại từ trước. Nhánh chất lượng cần
/// ffmpeg thật nên được kiểm bằng chạy tích hợp, không giả lập ở đây — giả lập thì test
/// vẫn xanh khi <c>QualityProbe</c> hỏng.</para>
/// </summary>
public class QualityGateTests
{
    private static QualityGate Gate(double minSaving = 1.0, bool quality = false) =>
        new(new AppConfig { MinSavingPercent = minSaving, QualityCheckEnabled = quality }, probe: null);

    private const string Source = "video.mp4";
    private const string Candidate = "candidate.mp4";

    private static Task<GateDecision> Evaluate(
        QualityGate gate,
        long sourceSize,
        long candidateSize,
        CompressionLevel level = CompressionLevel.Balanced,
        MediaKind kind = MediaKind.Video) =>
        gate.EvaluateAsync(
            sourceSize, candidateSize, level, kind,
            Source, Candidate,
            durationSeconds: 600,
            displayWidth: 1920, displayHeight: 1080,
            CancellationToken.None);

    // ---------------------------------------------------------------- invariant

    [Fact]
    public async Task Khong_bao_gi_tra_tep_lon_hon_ban_goc()
    {
        // Đây là invariant quan trọng nhất của cả giai đoạn. Với mọi cặp kích thước,
        // kết quả mà người dùng nhận phải không bao giờ lớn hơn bản gốc.
        var gate = Gate();
        long[] sizes = [0, 1, 1024, 1_000_000, 5_000_000, 113_000_000];

        foreach (var source in sizes)
        {
            foreach (var candidate in sizes)
            {
                var decision = await Evaluate(gate, source, candidate);

                if (decision.Accept)
                {
                    Assert.True(
                        candidate < source,
                        $"nhận ứng viên {candidate} cho bản gốc {source}");
                }
            }
        }
    }

    [Fact]
    public async Task Bang_hon_ban_goc_thi_chi_vao_duoc_khi_du_tiet_kiem()
    {
        var gate = Gate(minSaving: 10);

        // Nhỏ hơn 10% thì loại, dù vẫn nhỏ hơn bản gốc.
        var tooSmall = await Evaluate(gate, 1_000_000, 950_000);
        Assert.False(tooSmall.Accept);
        Assert.Equal(DecisionReasons.InsufficientSizeSaving, tooSmall.Reason);

        // Vừa đúng ngưỡng thì qua (so sánh dùng <, nên bằng ngưỡng là qua).
        var enough = await Evaluate(gate, 1_000_000, 900_000);
        Assert.True(enough.Accept);
    }

    [Fact]
    public async Task Bang_hon_ban_goc_va_du_tiet_kiem_thi_vao()
    {
        var decision = await Evaluate(Gate(), 113_000_000, 50_000_000);
        Assert.True(decision.Accept);
        Assert.Equal(DecisionReasons.Accepted, decision.Reason);
    }

    // ---------------------------------------------------------------- mã lý do

    [Fact]
    public async Task Bang_hon_va_bang_thuoc_dung_ma_ly_do()
    {
        Assert.Equal(
            DecisionReasons.OutputLargerThanSource,
            (await Evaluate(Gate(), 1_000, 2_000)).Reason);

        Assert.Equal(
            DecisionReasons.InsufficientSizeSaving,
            (await Evaluate(Gate(minSaving: 50), 1_000_000, 600_000)).Reason);
    }

    [Fact]
    public async Task Cac_ma_ly_do_la_chuoi_on_dinh_de_gom_so_lieu()
    {
        // Đổi thông báo tiếng Việt thì được; đổi mã này thì mọi báo cáo cũ hỏng theo.
        Assert.Equal("OUTPUT_LARGER_THAN_SOURCE", DecisionReasons.OutputLargerThanSource);
        Assert.Equal("INSUFFICIENT_SIZE_SAVING", DecisionReasons.InsufficientSizeSaving);
        Assert.Equal("QUALITY_FLOOR_NOT_MET", DecisionReasons.QualityFloorNotMet);
        Assert.Equal("SOURCE_ALREADY_EFFICIENT", DecisionReasons.SourceAlreadyEfficient);
        Assert.Equal("ACCEPTED", DecisionReasons.Accepted);
    }

    [Fact]
    public async Task Thi_hut_tieu_kiem_thi_giu_ban_goc_chu_khong_phai_bao_loi()
    {
        // Bỏ qua có lý do, khác hẳn lỗi kỹ thuật: người dùng thấy "không đáng nén" chứ
        // không thấy job lỗi.
        var decision = await Evaluate(Gate(), 1_000, 2_000);
        Assert.Equal(SkipReason.NoSizeGain, decision.Skip);
        Assert.False(decision.Accept);
    }

    // ---------------------------------------------------------------- những gì KHÔNG được loại

    [Fact]
    public async Task Khong_do_duoc_chat_luong_thi_khong_loi()
    {
        // probe null = không đo được. Nếu coi đây là thất bại thì mọi tệp video sẽ bị
        // loại khi ffmpeg hỏng, và người dùng không nén được gì mà không hiểu vì sao.
        var decision = await Evaluate(Gate(quality: true), 1_000_000, 500_000);

        Assert.True(decision.Accept);
        Assert.Null(decision.Quality);
    }

    [Fact]
    public async Task Tat_kiem_tra_chat_luong_thi_van_giu_luoi_kich_thuoc()
    {
        var gate = Gate(quality: false);

        Assert.True((await Evaluate(gate, 1_000_000, 500_000)).Accept);
        Assert.False((await Evaluate(gate, 1_000_000, 1_000_000)).Accept);
    }

    [Fact]
    public async Task Khong_phai_video_thi_khong_co_so_do_chat_luong_nao_de_bat()
    {
        // VMAF không áp dụng cho ảnh/GIF/PDF. Giả vờ có sẽ tạo ra một cổng rỗng.
        var decision = await Evaluate(Gate(quality: true), 1_000_000, 500_000, kind: MediaKind.Image);

        Assert.True(decision.Accept);
        Assert.Null(decision.Quality);
    }

    [Fact]
    public async Task Khong_biet_ky_thuoc_tinh_anh_thi_van_vao_duoc()
    {
        // Không có kích thước hiển thị thì không dựng được chuỗi filter đo. Thiếu dữ liệu
        // không phải lý do để bỏ tệp người dùng.
        var gate = Gate(quality: true);
        var decision = await gate.EvaluateAsync(
            1_000_000, 500_000, CompressionLevel.Balanced, MediaKind.Video,
            Source, Candidate, 600, displayWidth: null, displayHeight: null, CancellationToken.None);

        Assert.True(decision.Accept);
    }

    // ---------------------------------------------------------------- chọn đoạn đo
    [Fact]
    public void Du_phong_khi_quet_hong_phao_vao_trong_tep()
    {
        // Không đo được gì thì đứng ở vị trí chia đều, và tuyệt đối không vượt ra ngoài tệp.
        var selection = RepresentativeWindowSelector.Fallback(
            TimeSpan.FromSeconds(600), Config(), new ScanStats(0, 0, TimeSpan.Zero, true));

        Assert.True(selection.UsedFallback);
        Assert.Equal(3, selection.Windows.Count);
        foreach (var w in selection.Windows)
        {
            Assert.True(w.StartSeconds >= 0, $"bat dau {w.StartSeconds}");
            Assert.True(w.EndSeconds <= 600.5, $"ket thuc {w.EndSeconds}");
        }
    }

    [Fact]
    public void Du_phong_tren_tep_ngan_thi_bi_it_vao_ranh()
    {
        var selection = RepresentativeWindowSelector.Fallback(
            TimeSpan.FromSeconds(1.5), Config(), new ScanStats(0, 0, TimeSpan.Zero, true));

        foreach (var w in selection.Windows)
        {
            Assert.True(w.StartSeconds >= 0, $"bat dau {w.StartSeconds}");
            Assert.True(w.EndSeconds <= 1.5, $"ket thuc {w.EndSeconds}");
        }
    }

    [Fact]
    public void Du_phong_phai_noi_ro_la_dung_phong()
    {
        var selection = RepresentativeWindowSelector.Fallback(
            TimeSpan.FromSeconds(600), Config(), new ScanStats(0, 0, TimeSpan.Zero, true));

        Assert.Contains("Quét đặc tính thất bại", selection.Windows[0].Reason);
    }

    [Fact]
    public void Du_phong_phai_tat_dinh()
    {
        // Cùng đầu vào phải cho cùng kết quả, không lệch theo thứ tự thực thi.
        var config = Config();
        var a = RepresentativeWindowSelector.Fallback(TimeSpan.FromSeconds(600), config, ScanStats.None);
        var b = RepresentativeWindowSelector.Fallback(TimeSpan.FromSeconds(600), config, ScanStats.None);

        Assert.Equal(
            a.Windows.Select(w => (w.StartSeconds, w.Role)),
            b.Windows.Select(w => (w.StartSeconds, w.Role)));
    }

    private static AppConfig Config() => new() { TargetWindowCount = 3, WindowDurationSeconds = 3 };
}
