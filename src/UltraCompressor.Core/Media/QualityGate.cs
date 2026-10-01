using System.Diagnostics;
using System.Globalization;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Media;

/// <summary>Lý do ổn định để gom số liệu, không dùng để hiển thị cho người dùng.</summary>
public static class DecisionReasons
{
    /// <summary>Ứng viên không nhỏ hơn bản gốc.</summary>
    public const string OutputLargerThanSource = "OUTPUT_LARGER_THAN_SOURCE";

    /// <summary>Có nhỏ hơn, nhưng không đủ để bù công sức và tổn thất thế hệ.</summary>
    public const string InsufficientSizeSaving = "INSUFFICIENT_SIZE_SAVING";

    /// <summary>Ứng viên đo ra dưới ngưỡng chất lượng của mode.</summary>
    public const string QualityFloorNotMet = "QUALITY_FLOOR_NOT_MET";

    /// <summary>
    /// Nguồn vốn đã hiệu quả: muốn giữ ngưỡng chất lượng thì kết quả sẽ lớn hơn nguồn.
    /// Với <b>5A</b> mã này chưa dùng tới — nó là kết luận của giai đoạn sau, khi
    /// <c>ORIGINAL</c> trở thành một ứng viên ngang hàng thay vì một ngoại lệ cuối.
    /// </summary>
    public const string SourceAlreadyEfficient = "SOURCE_ALREADY_EFFICIENT";

    /// <summary>Không đo được chất lượng (công cụ hỏng, quá giờ). Không dùng để loại.</summary>
    public const string QualityNotMeasured = "QUALITY_NOT_MEASURED";

    /// <summary>Ứng viên được chấp nhận.</summary>
    public const string Accepted = "ACCEPTED";
}

/// <summary>Kết luận của cổng an toàn sau khi đã nén xong.</summary>
/// <param name="Accept">Giữ ứng viên thì true, giữ bản gốc thì false.</param>
/// <param name="Reason">Mã ổn định để gom số liệu.</param>
/// <param name="Skip">Lý do bỏ qua tương ứng, để hiển thị.</param>
/// <param name="Message">Thông báo tiếng Việt cho người dùng.</param>
/// <param name="Quality">Mẫu chất lượng đo được, null nếu không đo được hoặc không cần đo.</param>
public sealed record GateDecision(
    bool Accept,
    string Reason,
    SkipReason Skip,
    string? Message,
    QualitySample? Quality)
{
    public static GateDecision Keep(string reason, SkipReason skip, string? message, QualitySample? quality = null)
        => new(true, reason, skip, message, quality);

    public static GateDecision Reject(string reason, SkipReason skip, string message, QualitySample? quality = null)
        => new(false, reason, skip, message, quality);
}

/// <summary>
/// Lưới an toàn sau khi nén: không bao giờ trả cho người dùng một tệp vừa lớn hơn bản gốc
/// vừa kém chất lượng.
///
/// <para>Đây là giai đoạn <b>5A</b>: một lưới cơ học, quyết định chỉ từ kích thước thật của
/// tệp đã nén và một lần đo chất lượng trên một đoạn. Nó chấp nhận việc phải nén xong mới
/// biết kết quả có đáng giữ — mục tiêu trước mắt chỉ là không gây hại. Ở 5B, khi
/// <c>ORIGINAL</c> thành ứng viên ngang hàng, quyết định sẽ dựa trên dự đoán trước khi
/// nén thay vì đo sau.</para>
///
/// <para>Thứ tự kiểm tra là cố ý: <b>kích thước trước, chất lượng sau</b>. So kích thước
/// tốn không một mili giây, còn đo VMAF thì tốn một tiến trình ffmpeg. Với tệp mà bản
/// nén đã lớn hơn thì đo chất lượng cũng vô nghĩa — dù đạt VMAF 99 thì người dùng vẫn
/// mất dung lượng, nên bỏ luôn.</para>
/// </summary>
public sealed class QualityGate
{
    private readonly AppConfig _config;
    private readonly IQualityMeasure? _probe;
    private readonly TimelineScanner? _scanner;
    private readonly IReferenceWindowSource? _references;
    private readonly VmafModel _model = VmafModels.Default;

    public QualityGate(
        AppConfig config,
        IQualityMeasure? probe,
        TimelineScanner? scanner = null,
        IReferenceWindowSource? references = null)
    {
        _config = config;
        _probe = probe;
        _scanner = scanner;
        _references = references;
    }

    /// <summary>Ngưỡng chất lượng của mode, theo model sẽ dùng để đo.</summary>
    public QualityFloor FloorFor(CompressionLevel level) => QualityPolicy.For(level, _model);

    /// <summary>
    /// Quyết định có dùng ứng viên vừa nén xong không.
    /// </summary>
    /// <param name="sourceSize">Kích thước bản gốc, tính từ đĩa.</param>
    /// <param name="candidateSize">Kích thước tệp đã nén, tính từ đĩa — không phải ước lượng từ bitrate.</param>
    /// <param name="level">Mức mục tiêu, quyết định ngưỡng chất lượng.</param>
    /// <param name="kind">Loại media. Chỉ video mới đo được VMAF.</param>
    /// <param name="sourcePath">Bản gốc, làm chuẩn so sánh.</param>
    /// <param name="candidatePath">Tệp vừa nén.</param>
    /// <param name="durationSeconds">Thời lượng nguồn, dùng để chọn đoạn đo.</param>
    /// <param name="displayWidth">Bề rộng hiển thị của nguồn — cả hai luồng được đưa về đúng cỡ này.</param>
    /// <param name="displayHeight">Cao hiển thị của nguồn.</param>
    public async Task<GateDecision> EvaluateAsync(
        long sourceSize,
        long candidateSize,
        CompressionLevel level,
        MediaKind kind,
        string sourcePath,
        string candidatePath,
        double? durationSeconds,
        int? displayWidth,
        int? displayHeight,
        CancellationToken token)
    {
        // ---- 1. Kích thước: rẻ, và đủ để loại phần lớn tệp vô dụng.
        if (candidateSize >= sourceSize)
        {
            return GateDecision.Reject(
                DecisionReasons.OutputLargerThanSource,
                SkipReason.NoSizeGain,
                $"Bản nén lớn hơn bản gốc ({Format.Size(candidateSize)} so với {Format.Size(sourceSize)}) — giữ bản gốc.");
        }

        var saving = sourceSize > 0 ? (double)(sourceSize - candidateSize) * 100.0 / sourceSize : 0;
        var minimum = _config.MinSavingPercent;

        if (saving < minimum)
        {
            return GateDecision.Reject(
                DecisionReasons.InsufficientSizeSaving,
                SkipReason.BelowMinSaving,
                $"Chỉ tiết kiệm {Format.Percent(saving)}, dưới ngưỡng {Format.Percent(minimum)} — giữ bản gốc.");
        }

        // ---- 2. Chất lượng: chỉ khi tệp đủ lớn để việc đo đáng giá, và chỉ cho video.
        if (!_config.QualityCheckEnabled) return Accept(level, null, 0, null, string.Empty);

        if (kind != MediaKind.Video)
        {
            // Ảnh/GIF/âm thanh/PDF: chưa có metric cảm nhận nào đo được bằng VMAF, nên
            // không giả vờ có. Chỉ còn lưới kích thước ở trên.
            return Accept(level, null, 0, null, string.Empty);
        }

        if (_probe is null || displayWidth is not { } width || displayHeight is not { } height || width <= 0 || height <= 0)
        {
            return Accept(level, null, 0, null, string.Empty);
        }

        // Chọn đoạn theo nội dung, không phải theo phần trăm thời lượng. Trước đây chỉ lấy
        // một đoạn ở giữa tệp: với tệp mà đầu là cảnh tĩnh và giữa là cảnh cháy, đo giữa
        // thì hỏng, đo đầu thì qua — cả hai đều không đại diện cho tệp.
        var selection = await SelectWindowsAsync(sourcePath, kind, durationSeconds, token).ConfigureAwait(false);
        var floor = FloorFor(level);

        // Số đo tệ nhất đã thấy, để dùng cho thông báo và để báo cáo. Không tham gia quyết
        // định ở đây — quyết định vẫn là: đoạn nào dưới ngưỡng thì loại, đoạn nào đạt thì qua.
        QualitySample? worst = null;
        WindowRole? worstRole = null;
        string worstAlignment = string.Empty;
        var measuredCount = 0;

        foreach (var window in selection.Windows)
        {
            if (token.IsCancellationRequested) break;

            var (sample, alignment) = await MeasureWindowAsync(
                sourcePath,
                candidatePath,
                window,
                width,
                height,
                _model,
                token).ConfigureAwait(false);
            if (sample is null) continue;

            measuredCount++;
            if (worst is null || sample.Mean < worst.Mean)
            {
                worst = sample;
                worstRole = window.Role;
                worstAlignment = alignment;
            }

            if (floor.Accepts(sample)) continue;

            // Cách căn được ghi vào thông báo khi khác mặc định: đó là dấu vết duy nhất cho
            // biết con số này đến từ cặp khung hình nào. Không có nó, lần sau không tái lập
            // được phép đo.
            var aligned = alignment.Length > 0 ? $" ({alignment})" : string.Empty;

            return GateDecision.Reject(
                DecisionReasons.QualityFloorNotMet,
                SkipReason.BelowMinSaving,
                $"Đoạn {window.Role} lúc {window.StartSeconds.ToString("0", CultureInfo.InvariantCulture)}s "
                + $"chỉ đạt VMAF {sample.Mean.ToString("0.0", CultureInfo.InvariantCulture)} (ngưỡng {floor}){aligned} — giữ bản gốc.",
                sample);
        }

        // Không đo được đoạn nào thì KHÔNG loại. Công cụ hỏng hay quá giờ thì mọi tệp
        // video sẽ bị bỏ nếu coi như thất bại, và người dùng không nén được gì.
        //
        // Mang theo số đo tệ nhất khi có. Trước đây chỗ này gọi Accept(level, null) nên
        // đo được rồi đạt thì số đo bị ném đi: `item.QualityScore` và `QualityP5` luôn null
        // với mọi tệp được chấp nhận, và nhìn vào log không phân biệt được "đo rồi đạt" với
        // "không đo được". Quyết định thì không sai, chỉ là mất khả năng quan sát — và mất
        // telemetry thì sai số liệu ở các giai đoạn sau.
        return Accept(level, worst, measuredCount, worstRole, worstAlignment);
    }

    /// <summary>
    /// Đo một đoạn bằng cùng điểm neo thời gian với giai đoạn thử, rồi căn khung hình.
    /// </summary>
    /// <remarks>
    /// <para>Hai lớp căn, vì hai loại lệch khác nhau. Đo trực tiếp bằng hai lần seek vào
    /// nguồn và tệp đã nén có thể lệch nhau một khung hình, và trên tệp thật điều đó làm
    /// VMAF của cùng một ứng viên rơi từ 90,3 xuống 85,1 — nên lưới cuối phải đo
    /// clip-vs-clip từ cùng mốc 0 như giai đoạn thử. Nhưng clip-vs-clip vẫn chưa đủ: tệp
    /// nguồn (timebase 90k, mốc 0,021s) và tệp đầu ra (timebase 24k, mốc 0,041s) có cùng số
    /// khung hình mà cùng một mốc giây lại trỏ vào hai khung khác nhau — đã đo: cùng mốc
    /// cho VMAF 7,0, còn bỏ một khung ứng viên thì lên đúng nội dung gốc.</para>
    ///
    /// <para>Vì vậy sau khi cắt clip, còn thử ba cách căn — (0,0), (bỏ 1 khung ứng viên),
    /// (bỏ 1 khung tham chiếu) — và lấy điểm cao nhất. Chỉ ±1 khung, vì lệch nửa khung do
    /// timebase/mốc bắt đầu không thể đẩy lệch quá một khung; lệch hơn thế là lỗi khác
    /// (rớt khung, sai FPS) và KHÔNG được hấp thụ lặng lẽ. Chọn điểm cao nhất ở đây là
    /// đăng ký thời gian (registration), không phải nâng điểm chất lượng: ứng viên kém
    /// thật thì mọi cách căn đều thấp.</para>
    ///
    /// <para>Khi không có nguồn cắt clip, giữ hành vi đo trực tiếp cũ để các test không cần
    /// ffmpeg vẫn kiểm được logic quyết định. Khi cắt clip hỏng, đoạn đó được coi là
    /// <b>không đo được</b> và lưới fail-open như cũ.</para>
    /// </remarks>
    private async Task<(QualitySample? Sample, string Alignment)> MeasureWindowAsync(
        string sourcePath,
        string candidatePath,
        RepresentativeWindow window,
        int width,
        int height,
        VmafModel model,
        CancellationToken token)
    {
        if (_probe is null) return (null, string.Empty);

        if (_references is null)
        {
            var direct = await _probe.MeasureAsync(
                sourcePath, candidatePath,
                new TimeWindow(window.StartSeconds, window.DurationSeconds),
                new TimeWindow(window.StartSeconds, window.DurationSeconds),
                width, height,
                candidateWidth: width, candidateHeight: height,
                model: model, token: token)
                .ConfigureAwait(false);

            return (direct?.Sample, string.Empty);
        }

        IReadOnlyList<WindowReference>? sourceClips = null;
        IReadOnlyList<WindowReference>? candidateClips = null;

        try
        {
            sourceClips = await _references.ExtractAsync(sourcePath, [window], token).ConfigureAwait(false);
            candidateClips = await _references.ExtractAsync(candidatePath, [window], token).ConfigureAwait(false);

            var sourceClip = sourceClips.Count > 0 ? sourceClips[0] : null;
            var candidateClip = candidateClips.Count > 0 ? candidateClips[0] : null;
            if (sourceClip is null || candidateClip is null) return (null, string.Empty);

            // Ba cách căn: giữ nguyên, bỏ 1 khung ứng viên, bỏ 1 khung tham chiếu.
            // Thứ tự cố ý: (0,0) trước để khi mọi cách bằng nhau thì không ghi căn chỉnh
            // vào báo cáo — chỉ lệch thật mới để lại dấu vết.
            (QualitySample? Sample, string Alignment)? best = null;

            foreach (var (candidateDrop, referenceDrop, label) in AlignmentCandidates)
            {
                var measured = await _probe.MeasureAsync(
                    sourceClip.Path, candidateClip.Path,
                    new TimeWindow(0, sourceClip.LengthSeconds),
                    new TimeWindow(0, candidateClip.LengthSeconds),
                    width, height,
                    candidateWidth: width, candidateHeight: height,
                    model: model,
                    candidateStartFrame: candidateDrop,
                    referenceStartFrame: referenceDrop,
                    token)
                    .ConfigureAwait(false);

                if (measured?.Sample is not { } sample) continue;

                if (best is null || sample.Mean > best.Value.Sample!.Mean)
                {
                    best = (sample, label);
                }
            }

            return best ?? (null, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, string.Empty);
        }
        finally
        {
            if (sourceClips is not null) _references.Release(sourceClips);
            if (candidateClips is not null) _references.Release(candidateClips);
        }
    }

    /// <summary>
    /// Các cách căn khung hình được phép thử, theo thứ tự ưu tiên.
    /// </summary>
    private static readonly (int CandidateDrop, int ReferenceDrop, string Label)[] AlignmentCandidates =
    [
        (0, 0, string.Empty),
        (1, 0, "căn −1 khung ứng viên"),
        (0, 1, "căn −1 khung tham chiếu"),
    ];

    /// <summary>
    /// Quét rồi chọn đoạn. Bọc thử: mọi lỗi quét rơi về chiến lược vị trí chia đều.
    /// </summary>
    private async Task<WindowSelection> SelectWindowsAsync(
        string sourcePath, MediaKind kind, double? durationSeconds, CancellationToken token)
    {
        _lastDuration = durationSeconds is { } d ? TimeSpan.FromSeconds(d) : null;

        if (kind != MediaKind.Video || _scanner is null)
        {
            _lastStats = new ScanStats(0, 0, TimeSpan.Zero, FellBack: true);
            return RepresentativeWindowSelector.Fallback(_lastDuration, _config, _lastStats);
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var samples = await _scanner.ScanAsync(sourcePath, _lastDuration, _config, token).ConfigureAwait(false);
            watch.Stop();

            _lastStats = new ScanStats(
                SamplesRequested: TimelineScanner.SampleCountFor(_lastDuration, _config),
                SamplesMeasured: samples.Count,
                Duration: watch.Elapsed,
                FellBack: false);

            return RepresentativeWindowSelector.Select(samples, _lastDuration, _config, _lastStats);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _lastStats = new ScanStats(0, 0, TimeSpan.Zero, FellBack: true);
            return RepresentativeWindowSelector.Fallback(_lastDuration, _config, _lastStats);
        }
    }

    private TimeSpan? _lastDuration;

    /// <summary>Số liệu lần chọn đoạn gần nhất, để ghi log và chẩn đoán.</summary>
    public ScanStats LastSelection => _lastStats;

    private ScanStats _lastStats = ScanStats.None;

    /// <summary>
    /// Chấp nhận, kèm số đo tệ nhất nếu có.
    /// </summary>
    /// <param name="worst">Số đo của đoạn có mean thấp nhất — ràng buộc gắn nhất.</param>
    /// <param name="measuredCount">Số đoạn đo được; phân biệt "đo rồi đạt" với "không đo được".</param>
    private static GateDecision Accept(
        CompressionLevel level,
        QualitySample? worst,
        int measuredCount,
        WindowRole? worstRole,
        string worstAlignment)
    {
        if (worst is null)
        {
            return GateDecision.Keep(
                DecisionReasons.Accepted, SkipReason.None, null, null);
        }

        var floor = QualityPolicy.For(level, VmafModels.Default);
        var where = worstRole is null ? string.Empty : $" ở đoạn {worstRole}";
        var aligned = worstAlignment.Length > 0 ? $" ({worstAlignment})" : string.Empty;

        return GateDecision.Keep(
            DecisionReasons.Accepted,
            SkipReason.None,
            $"VMAF {worst.Mean.ToString("0.0", CultureInfo.InvariantCulture)}"
                + $" (P5 {worst.P5.ToString("0.0", CultureInfo.InvariantCulture)}){where}"
                + $", ngưỡng {floor}"
                + $" — đo {measuredCount.ToString(CultureInfo.InvariantCulture)} đoạn, đoạn tệ nhất{aligned}.",
            worst);
    }
}
