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
    private readonly QualityProbe? _probe;
    private readonly VmafModel _model = VmafModels.Default;

    public QualityGate(AppConfig config, QualityProbe? probe)
    {
        _config = config;
        _probe = probe;
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
        if (!_config.QualityCheckEnabled) return Accept(level, null);

        if (kind != MediaKind.Video)
        {
            // Ảnh/GIF/âm thanh/PDF: chưa có metric cảm nhận nào đo được bằng VMAF, nên
            // không giả vờ có. Chỉ còn lưới kích thước ở trên.
            return Accept(level, null);
        }

        if (_probe is null || displayWidth is not { } width || displayHeight is not { } height || width <= 0 || height <= 0)
        {
            return Accept(level, null);
        }

        var window = PickWindow(durationSeconds, _config.QualityCheckWindowSeconds);

        var sample = await _probe.MeasureAsync(
            sourcePath, candidatePath, window, width, height,
            candidateWidth: width, candidateHeight: height, model: _model, token).ConfigureAwait(false);

        if (sample is null)
        {
            // Không đo được KHÔNG phải là dữ liệu để loại tệp. Công cụ hỏng hay quá giờ
            // thì mọi tệp video sẽ bị bỏ nếu coi như thất bại — và người dùng sẽ không
            // nén được gì mà không hiểu vì sao.
            return Accept(level, null);
        }

        var quality = sample.Sample;
        var floor = FloorFor(level);

        if (!floor.Accepts(quality))
        {
            return GateDecision.Reject(
                DecisionReasons.QualityFloorNotMet,
                SkipReason.BelowMinSaving,
                $"Chất lượng {Format.Percent(quality.Mean / 100)} (ngưỡng {floor}) dù đã tiết kiệm "
                + $"{Format.Percent(saving)} — giữ bản gốc.",
                quality);
        }

        return Accept(level, quality);
    }

    private static GateDecision Accept(CompressionLevel level, QualitySample? quality) =>
        GateDecision.Keep(
            DecisionReasons.Accepted,
            SkipReason.None,
            quality is null
                ? null
                : $"VMAF {quality.Mean.ToString("0.0", CultureInfo.InvariantCulture)} (ngưỡng {QualityPolicy.For(level, VmafModels.Default)})",
            quality);

    /// <summary>
    /// Chọn đoạn đo một cách tất định: lấy giữa tệp.
    ///
    /// <para>Đo đầu tệp là chỗ dễ nhầm nhất — thường là logo, credit, hoặc một cảnh tĩnh
    /// ngắn, đo ở đó thì mọi tệp đều ra cùng một kết luận. Giai đoạn sau sẽ thay bằng bộ
    /// chọn nhiều đoạn đại diện; hàm này giữ chỗ cho chỗ đó, và vẫn tất định nếu chưa có.</para>
    /// </summary>
    public static TimeWindow PickWindow(double? durationSeconds, double windowSeconds)
    {
        var length = Math.Max(0.5, windowSeconds);
        var duration = durationSeconds is { } d && d > 0 ? d : length;
        var start = Math.Max(0, (duration - length) / 2);
        return new TimeWindow(start, Math.Min(length, duration));
    }
}
