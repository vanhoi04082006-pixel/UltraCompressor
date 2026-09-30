using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Regression cho lỗi mất số đo trên nhánh ACCEPTED.
///
/// <para><b>Lỗi:</b> <c>QualityGate.EvaluateAsync</c> đo từng đoạn đại diện; đoạn nào đạt
/// ngưỡng thì <c>continue</c>. Cuối cùng hàm trả <c>Accept(level, null)</c> — nghĩa là đo
/// được rồi đạt thì <b>ném mất</b> <c>QualitySample</c>. Hệ quả:
/// <c>item.QualityScore</c> và <c>item.QualityP5</c> luôn <c>null</c> với mọi tệp được chấp
/// nhận, và log không phân biệt được "đo rồi đạt" với "không đo được".</para>
///
/// <para><b>Không phải lỗi an toàn</b>: quyết định vẫn đúng, vì đoạn dưới ngưỡng vẫn bị loại
/// ngay ở vòng lặp. Nhưng mất telemetry thì số liệu ở giai đoạn sau sai, và người đọc log
/// hiểu sai chuyện gì đã xảy ra.</para>
///
/// <para>Cần một <see cref="QualityProbe"/> giả để đo được; nếu không, nhánh chất lượng bị
/// bỏ qua và test này sẽ xanh một cách vô nghĩa. Vì vậy probe giả bị đếm số lần gọi và trả
/// số đo cố ý khác nhau theo đoạn.</para>
/// </summary>
public class QualityGateObservabilityTests
{
    private const string Source = "video.mp4";
    private const string Candidate = "candidate.mp4";

    /// <summary>
    /// Probe giả trả số đo theo thứ tự đoạn được hỏi, và ghi lại để test kiểm được nó
    /// thực sự được gọi.
    /// </summary>
    private sealed class ScriptedProbe(params double[] means) : IQualityMeasure
    {
        private readonly Queue<double> _means = new(means);
        private int _calls;

        public int Calls => _calls;

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
            CancellationToken token = default)
        {
            _calls++;

            if (_means.Count == 0)
            {
                return Task.FromResult<QualityResult?>(null);
            }

            var mean = _means.Dequeue();
            return Task.FromResult<QualityResult?>(new QualityResult(
                model.Id, referenceWindow, displayWidth, displayHeight, candidateWidth, candidateHeight,
                new QualitySample(Mean: mean, P5: mean - 3, Min: mean - 12, SsimMean: 0.998, Frames: 72)));
        }
    }

    private static Task<GateDecision> Evaluate(
        IQualityMeasure probe,
        long sourceSize = 10_000_000,
        long candidateSize = 4_000_000,
        CompressionLevel level = CompressionLevel.Balanced) =>
        new QualityGate(new AppConfig { QualityCheckEnabled = true, MinSavingPercent = 1.0 }, probe)
            .EvaluateAsync(
                sourceSize, candidateSize, level, MediaKind.Video,
                Source, Candidate, durationSeconds: 120, displayWidth: 1920, displayHeight: 1080,
                CancellationToken.None);

    [Fact]
    public async Task Chap_nhan_phai_giu_so_do_cua_doan_te_nhat()
    {
        // Cả ba đoạn đều đạt ngưỡng 89. Đoạn tệ nhất là 91.
        var probe = new ScriptedProbe(95, 91, 93);

        var decision = await Evaluate(probe);

        Assert.True(decision.Accept);
        Assert.Equal(DecisionReasons.Accepted, decision.Reason);

        // Trước khi sửa, dòng này là null.
        Assert.NotNull(decision.Quality);
        Assert.Equal(91, decision.Quality!.Mean);
        Assert.Equal(88, decision.Quality.P5);

        // Và probe thực sự được gọi — nếu không thì test này xanh một cách giả.
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public async Task Khong_do_duoc_thi_khong_bam_vao_so_do()
    {
        // Không đo được đoạn nào thì vẫn chấp nhận (fail-open, giữ nguyên Phase 5A), nhưng
        // KHÔNG được bịa ra một số đo.
        var probe = new ScriptedProbe();

        var decision = await Evaluate(probe);

        Assert.True(decision.Accept);
        Assert.Null(decision.Quality);
    }

    [Fact]
    public async Task Doan_vao_duoc_giua_cac_doan_duc_thi_van_lo()
    {
        // Đoạn giữa rớt dưới ngưỡng 89: vẫn phải loại y như trước. Bản sửa này chỉ giữ số
        // đo, KHÔNG nới lỏng quyết định.
        var probe = new ScriptedProbe(95, 80, 93);

        var decision = await Evaluate(probe);

        Assert.False(decision.Accept);
        Assert.Equal(DecisionReasons.QualityFloorNotMet, decision.Reason);
        Assert.Equal(80, decision.Quality!.Mean);
    }

    [Fact]
    public async Task Moi_mode_dung_nguong_cua_no_khi_chap_nhan()
    {
        // Số đo mang theo phải là của chính tệp, không phải mặc định.
        var light = await Evaluate(new ScriptedProbe(95, 94), level: CompressionLevel.Light);
        var strong = await Evaluate(new ScriptedProbe(95, 94), level: CompressionLevel.Strong);

        Assert.True(light.Accept);
        Assert.True(strong.Accept);
        Assert.Equal(94, light.Quality!.Mean);
        Assert.Equal(94, strong.Quality!.Mean);

        // Ngưỡng khác nhau, và thông báo phải nói đúng ngưỡng đã dùng.
        Assert.Contains("93", light.Message!, StringComparison.Ordinal);
        Assert.Contains("84", strong.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Thong_bao_no_roi_dung_diem_dang_va_so_do()
    {
        var probe = new ScriptedProbe(95, 91, 93);

        var decision = await Evaluate(probe);

        Assert.NotNull(decision.Message);
        Assert.Contains("91.0", decision.Message!, StringComparison.Ordinal);
        Assert.Contains("P5", decision.Message!, StringComparison.Ordinal);
    }
}
