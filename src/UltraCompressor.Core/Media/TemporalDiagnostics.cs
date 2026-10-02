using System.Globalization;
using System.Text.RegularExpressions;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Media;

/// <summary>Sự thật về trục thời gian của MỘT tệp/clip, đọc bằng ffmpeg thật.</summary>
/// <param name="StartTimeSeconds">Mốc bắt đầu container, đọc từ dòng <c>Duration: …, start: …</c>.</param>
/// <param name="TimeBase">Timebase thật của luồng, đọc từ <c>config in time_base</c> của showinfo.</param>
/// <param name="FrameRate">Tần số khung thật, đọc từ <c>config … frame_rate</c> của showinfo.</param>
/// <param name="DurationSeconds">Thời lượng container khai báo.</param>
/// <param name="FirstFramePtsSeconds">PTS thực của khung hình đầu tiên sau giải mã.</param>
/// <param name="DecodedFrames">Số khung thật sự giải mã, đếm từ dòng tổng kết <c>frame= N</c>.</param>
public sealed record TemporalFacts(
    double? StartTimeSeconds,
    string? TimeBase,
    string? FrameRate,
    double? DurationSeconds,
    double? FirstFramePtsSeconds,
    long DecodedFrames)
{
    public static TemporalFacts Unknown { get; } = new(null, null, null, null, null, 0);

    /// <summary>Có đủ dữ kiện để so sánh hai bên không.</summary>
    public bool IsUsable => FirstFramePtsSeconds is not null && DecodedFrames > 0;

    /// <summary>Một dòng ngắn cho báo cáo, không đổi khổi theo độ dài tệp.</summary>
    public override string ToString()
    {
        static string F(double? v) => v is { } x ? x.ToString("0.######", CultureInfo.InvariantCulture) : "?";

        return $"start={F(StartTimeSeconds)} tb={TimeBase ?? "?"} rate={FrameRate ?? "?"} "
            + $"dur={F(DurationSeconds)} firstPts={F(FirstFramePtsSeconds)} decoded={DecodedFrames}";
    }
}

/// <summary>
/// Đọc sự thật về trục thời gian bằng ffmpeg thật.
///
/// <para>Tồn tại để trả lời một câu hỏi mà "điểm VMAF có đạt không" không trả lời được:
/// <b>khi hai clip được ghép cặp khung hình, khung hình đầu tiên của chúng có thực sự là hai
/// khung cùng nội dung không?</b> Đo VMAF rồi thấy "bỏ một khung thì điểm lên" chỉ nói ra
/// triệu chứng; nó không nói nguyên nhân, và không bảo đảm bỏ khung đó là cách sửa đúng thay
/// vì chỉ là cách làm điểm đẹp hơn.</para>
///
/// <para>Lớp chẩn đoán: đọc metadata và chuỗi thời gian, không căn, không chọn offset, không
/// đổi kết quả đo nào.</para>
///
/// <para><b>Về chỗ dễ sai:</b> bản ffmpeg đi kèm không có ffprobe và không hiểu
/// <c>-print_format json</c>, nên mọi trường ở đây đều phải đọc từ log dạng văn bản. Vì vậy
/// tất cả mẫu đều được ghim vào test bằng <b>log thật đã chép</b>, không phải log tự nhớ —
/// bản này từng đọc nhầm số khung hình làm số mốc bắt đầu, và im lặng trả về con số sai.</para>
/// </summary>
public static partial class TemporalProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(180);

    // Log thật của ffmpeg đi kèm: "  Duration: 00:00:03.00, start: 0.000000, bitrate: 594 kb/s"
    [GeneratedRegex(@"Duration:\s*(?<clock>\d+:\d+:\d+(?:\.\d+)?),\s*start:\s*(?<start>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPattern();

    // Log thật: "[Parsed_showinfo_0 @ 000001d8ea1ce440] config in time_base: 1/12288, frame_rate: 24/1"
    [GeneratedRegex(@"config in time_base:\s*(?<tb>\d+/\d+),\s*frame_rate:\s*(?<rate>\d+/\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StreamConfigPattern();

    // Log thật: "… n: 0 pts: 0 pts_time:0 duration: 512 duration_time:0.0416667 …"
    [GeneratedRegex(@"n:\s*0\s+pts:\s*\S+\s+pts_time:(?<t>-?[\d.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FirstPtsPattern();

    [GeneratedRegex(@"frame=\s*(?<n>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameTotalPattern();

    /// <summary>
    /// Đọc mốc bắt đầu, timebase, tần số khung, PTS khung đầu và tổng số khung thật.
    /// </summary>
    /// <remarks>
    /// <para>Ba lượt, tách bạch là bắt buộc vì <see cref="ProcessRunner"/> chỉ giữ 60 dòng
    /// stderr cuối:</para>
    /// <list type="number">
    /// <item><c>-i</c> không output: dòng <c>Duration … start</c>. Không giải mã nên log rất
    /// ngắn, còn trong phần được giữ.</item>
    /// <item><c>-vf showinfo -frames:v 1</c>: lấy <c>config in time_base</c>, <c>frame_rate</c>
    /// và dòng <c>n: 0</c>. Giới hạn một khung vì nếu giải mã hết clip, showinfo in một dòng
    /// mỗi khung và <c>n: 0</c> bị đẩy khỏi 60 dòng đuôi — số đo biến thành số sai mà không
    /// báo lỗi.</item>
    /// <item><c>-f null</c> không filter: dòng tổng kết <c>frame= N</c> luôn nằm cuối log.</item>
    /// </list>
    /// <para>Không bao giờ ném lỗi: chẩn đoán hỏng thì trả null, vì mất chẩn đoán còn hơn làm
    /// hỏng việc nén.</para>
    /// </remarks>
    public static async Task<TemporalFacts?> ProbeAsync(
        string ffmpegPath, string path, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(path)) return null;

        try
        {
            var header = await Run(
                ffmpegPath, ["-hide_banner", "-nostdin", "-i", path], token).ConfigureAwait(false);

            var shown = await Run(
                ffmpegPath,
                [
                    "-hide_banner", "-nostdin", "-loglevel", "info",
                    "-i", path, "-vf", "showinfo", "-frames:v", "1",
                    "-f", "null", "-",
                ],
                token).ConfigureAwait(false);

            var total = await Run(
                ffmpegPath,
                ["-hide_banner", "-nostdin", "-i", path, "-f", "null", "-"],
                token).ConfigureAwait(false);

            var (start, duration) = ParseContainer(header.StandardErrorText);
            var (timeBase, frameRate) = ParseStreamConfig(shown.StandardErrorText);

            return new TemporalFacts(
                start,
                timeBase,
                frameRate,
                duration,
                ParseFirstPts(shown.StandardErrorText),
                ParseFrameTotal(total.StandardErrorText));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static Task<ProcessResult> Run(
        string ffmpegPath, IReadOnlyList<string> args, CancellationToken token) =>
        ProcessRunner.RunAsync(ffmpegPath, args, ProbeTimeout, token);

    /// <summary>Mốc bắt đầu và thời lượng, từ dòng <c>Duration</c>.</summary>
    internal static (double? Start, double? Duration) ParseContainer(string stderr)
    {
        var match = ContainerPattern().Match(stderr);
        if (!match.Success) return (null, null);

        return (
            ParseNumber(match.Groups["start"].Value),
            ParseClock(match.Groups["clock"].Value));
    }

    /// <summary>Timebase và tần số khung thật của luồng, từ dòng cấu hình showinfo.</summary>
    internal static (string? TimeBase, string? FrameRate) ParseStreamConfig(string stderr)
    {
        var match = StreamConfigPattern().Match(stderr);
        return match.Success
            ? (match.Groups["tb"].Value, match.Groups["rate"].Value)
            : (null, null);
    }

    /// <summary>PTS của khung đầu tiên, đọc từ lượt chỉ giải mã đúng một khung.</summary>
    internal static double? ParseFirstPts(string stderr)
    {
        var match = FirstPtsPattern().Match(stderr);
        return match.Success ? ParseNumber(match.Groups["t"].Value) : null;
    }

    /// <summary>Tổng khung đã giải mã, đọc từ dòng tổng kết của ffmpeg.</summary>
    internal static long ParseFrameTotal(string stderr)
    {
        long value = 0;
        foreach (Match match in FrameTotalPattern().Matches(stderr))
        {
            if (long.TryParse(match.Groups["n"].Value, out var n)) value = n;
        }

        return value;
    }

    private static double? ParseNumber(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseClock(string clock)
    {
        var parts = clock.Split(':');
        if (parts.Length < 3) return null;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return null;
        }

        return h * 3600 + m * 60 + s;
    }
}
