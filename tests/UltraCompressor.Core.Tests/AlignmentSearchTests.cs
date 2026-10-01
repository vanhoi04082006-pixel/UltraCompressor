using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Căn khung hình khi đo VMAF: phạm vi hẹp, và <b>không được nới</b> để làm điểm đẹp hơn.
///
/// <para>Đây là hồ sơ nợ kỹ thuật của giai đoạn 4: mốc thời gian và timebase khác nhau làm
/// cùng một mốc giây trỏ vào hai khung khác nhau (đã đo: cùng mốc cho VMAF 7,0). Sửa ở đây là
/// dò ba cách căn ±1 khung và lấy điểm cao nhất — tức <b>đăng ký thời gian</b>.</para>
///
/// <para>Rủi ro của nó, và lý do phạm vi phải khóa: nếu mở rộng để "tìm offset cho điểm cao
/// nhất", thì một ứng viên lệch thật sẽ tìm được một cách căn đẹp và đi qua — tức đánh dấu
/// đúng loại tệp mà lưới sinh ra để chặn. Test dưới đây chặn đúng điều đó.</para>
/// </remarks>
public class AlignmentSearchTests
{
    private const string Source = "video.mp4";
    private const string Candidate = "candidate.mp4";

    /// <summary>
    /// Probe giả: trả điểm theo <b>cặp lệch khung</b> đang thử, nên test kiểm được cả việc
    /// dò rộng hơn lẫn việc chọn điểm.
    /// </summary>
    private sealed class AlignmentProbe : IQualityMeasure
    {
        private readonly Dictionary<(int, int), double> _scores;

        public AlignmentProbe(Dictionary<(int, int), double> scores) => _scores = scores;

        public List<(int CandidateDrop, int ReferenceDrop)> Attempts { get; } = [];

        public Task<QualityResult?> MeasureAsync(
            string referencePath,
            string candidatePath,
            TimeWindow referenceWindow,
            TimeWindow candidateWindow,
            int displayWidth,
            int displayHeight,
            int candidateWidth,
            int candidateHeight,
            VmafModel model,
            int candidateStartFrame = 0,
            int referenceStartFrame = 0,
            CancellationToken token = default)
        {
            Attempts.Add((candidateStartFrame, referenceStartFrame));

            var key = (candidateStartFrame, referenceStartFrame);
            var mean = _scores.GetValueOrDefault(key, 40.0);

            return Task.FromResult<QualityResult?>(new QualityResult(
                model.Id, referenceWindow, displayWidth, displayHeight, candidateWidth, candidateHeight,
                new QualitySample(mean, mean - 3, mean - 12, 0.998, 72)));
        }
    }

    private sealed class StubReferences : IReferenceWindowSource
    {
        public Task<IReadOnlyList<WindowReference>> ExtractAsync(
            string sourcePath, IReadOnlyList<RepresentativeWindow> windows, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<WindowReference>>(
            [
                .. windows.Select(w => new WindowReference(
                    w.Role, $"ref-{w.Role}.mp4", w.StartSeconds, w.DurationSeconds, 1_000_000, TimeSpan.FromSeconds(3))),
            ]);

        public void Release(IReadOnlyList<WindowReference> clips)
        {
        }
    }

    private static QualityGate Gate(IQualityMeasure probe) =>
        new(new AppConfig { MinSavingPercent = 1.0, QualityCheckEnabled = true },
            probe,
            scanner: null,
            references: new StubReferences());

    [Fact]
    public void Pham_vi_can_chi_co_dung_ba_cach()
    {
        // Ghim phạm vi: hằng số này tồn tại để khi ai đó thêm cách căn thứ tư thì test phải
        // đỏ, chứ không lặng lẽ mở rộng phạm vi.
        Assert.Equal(3, QualityGate.AlignmentSearchWidth);
    }

    [Fact]
    public async Task Khong_duoc_thu_qua_ba_cach_can()
    {
        var probe = new AlignmentProbe(new Dictionary<(int, int), double>
        {
            [(0, 0)] = 92,
            [(1, 0)] = 91,
            [(0, 1)] = 90,
        });

        await Gate(probe).EvaluateAsync(
            sourceSize: 100_000_000,
            candidateSize: 50_000_000,
            level: CompressionLevel.Balanced,
            kind: MediaKind.Video,
            sourcePath: Source,
            candidatePath: Candidate,
            durationSeconds: 60,
            displayWidth: 1920,
            displayHeight: 1080,
            token: CancellationToken.None);

        // Đúng ba lần cho mỗi đoạn, không hơn. Dò thêm là nới phạm vi.
        Assert.All(probe.Attempts, a =>
            Assert.Contains(a, new[] { (0, 0), (1, 0), (0, 1) }));

        Assert.True(probe.Attempts.Count >= 3);
    }

    [Fact]
    public async Task Ung_vien_loi_that_bi_lo_khong_duoc_cu_bang_cach_can()
    {
        // Ứng viên này rớt Ở CẢ BA cách căn — tức lỗi của nó không phải lệch mốc, mà là nội
        // dung thật sự khác. Không có phép căn hợp lệ nào đưa nó lên ngưỡng, và cũng không có
        // cách căn nào ngoài phạm vi được phép thử.
        var probe = new AlignmentProbe(new Dictionary<(int, int), double>
        {
            [(0, 0)] = 60,
            [(1, 0)] = 62,
            [(0, 1)] = 61,
        });

        var decision = await Gate(probe).EvaluateAsync(
            sourceSize: 100_000_000,
            candidateSize: 50_000_000,
            level: CompressionLevel.Balanced,
            kind: MediaKind.Video,
            sourcePath: Source,
            candidatePath: Candidate,
            durationSeconds: 60,
            displayWidth: 1920,
            displayHeight: 1080,
            token: CancellationToken.None);

        Assert.False(decision.Accept);
        Assert.Equal(DecisionReasons.QualityFloorNotMet, decision.Reason);
        Assert.Equal(3, probe.Attempts.Count);
    }

    [Fact]
    public async Task Cach_can_dung_da_khop_noi_dung_thi_van_dung()
    {
        // Trường hợp lệ của phép căn: nội dung chỉ khớp sau khi dịch khung. Phải chấp nhận —
        // và phải ghi lại, để lần sau biết là đăng ký chứ không phải ứng viên hỏng.
        var probe = new AlignmentProbe(new Dictionary<(int, int), double>
        {
            [(0, 0)] = 7,
            [(1, 0)] = 92,
            [(0, 1)] = 40,
        });

        var decision = await Gate(probe).EvaluateAsync(
            sourceSize: 100_000_000,
            candidateSize: 50_000_000,
            level: CompressionLevel.Balanced,
            kind: MediaKind.Video,
            sourcePath: Source,
            candidatePath: Candidate,
            durationSeconds: 60,
            displayWidth: 1920,
            displayHeight: 1080,
            token: CancellationToken.None);

        Assert.True(decision.Accept);
        var telemetry = Assert.IsType<AlignmentTelemetry>(decision.Alignment);
        Assert.True(telemetry.AnyShift);
        Assert.True(telemetry.WindowsNeedingShift > 0);
    }

    [Fact]
    public async Task Tan_su_lech_khung_phai_dem_duoc()
    {
        // Không lệch ở bất kỳ cách căn nào thì không được ghi nhận là có lệch. Mã chẩn đoán bắn
        // nhầm thì mất ý nghĩa, và cảnh báo trở thành tiếng ồn.
        var probe = new AlignmentProbe(new Dictionary<(int, int), double>
        {
            [(0, 0)] = 93,
            [(1, 0)] = 92,
            [(0, 1)] = 91,
        });

        var decision = await Gate(probe).EvaluateAsync(
            sourceSize: 100_000_000,
            candidateSize: 50_000_000,
            level: CompressionLevel.Balanced,
            kind: MediaKind.Video,
            sourcePath: Source,
            candidatePath: Candidate,
            durationSeconds: 60,
            displayWidth: 1920,
            displayHeight: 1080,
            token: CancellationToken.None);

        Assert.True(decision.Accept);
        var telemetry = Assert.IsType<AlignmentTelemetry>(decision.Alignment);
        Assert.False(telemetry.AnyShift);
        Assert.Equal(0d, telemetry.ShiftRate);
    }

    [Fact]
    public async Task Lech_khung_thuong_xuyen_phai_canh_bao()
    {
        // Tần suất lệch khung cao là TÍN HIỆU ĐÚNG về đường cắt clip hoặc dấu thời gian, và
        // phải nói ra. Cố tình KHÔNG nới phạm vi căn để làm nó im đi — đó là cách che lỗi.
        var probe = new AlignmentProbe(new Dictionary<(int, int), double>
        {
            [(0, 0)] = 7,
            [(1, 0)] = 93,
            [(0, 1)] = 93,
        });

        var decision = await Gate(probe).EvaluateAsync(
            sourceSize: 100_000_000,
            candidateSize: 50_000_000,
            level: CompressionLevel.Balanced,
            kind: MediaKind.Video,
            sourcePath: Source,
            candidatePath: Candidate,
            durationSeconds: 60,
            displayWidth: 1920,
            displayHeight: 1080,
            token: CancellationToken.None);

        var telemetry = Assert.IsType<AlignmentTelemetry>(decision.Alignment);
        Assert.True(telemetry.ShiftRate >= QualityGate.FrequentShiftRate);
        Assert.Contains("Cảnh báo", decision.Message ?? string.Empty, StringComparison.Ordinal);
    }
}
