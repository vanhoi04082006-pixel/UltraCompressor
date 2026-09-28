using System.Text.RegularExpressions;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Trích thông tin từ stderr của ffmpeg.
/// Không dùng <c>ffprobe</c> vì bản đóng gói không kèm (và ffmpeg đã in đủ).
/// Regex linh hoạt hơn bản gốc: chấp nhận giờ >= 100 và phần giây thập phân nhiều chữ số.
/// </summary>
public static partial class FFmpegOutputParser
{
    [GeneratedRegex(@"Duration:\s*(?<h>\d+):(?<m>\d{1,2}):(?<s>\d{1,2}(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"time=\s*(?<h>\d+):(?<m>\d{1,2}):(?<s>\d{1,2}(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    // Dòng tổng kết của ffmpeg dùng "bitrate= 262.1kbits/s" (có dấu bằng), còn ffprobe và
    // một số bản ffmpeg dùng "bitrate: 262 kb/s". Chấp nhận cả hai, không có "s" cũng được.
    [GeneratedRegex(@"bitrate\s*[:=]\s*(?<n>\d+(?:\.\d+)?)\s*kbits?(?:/s)?", RegexOptions.IgnoreCase)]
    private static partial Regex BitrateRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?:\s*Video:", RegexOptions.IgnoreCase)]
    private static partial Regex HasVideoStreamRegex();

    // Dòng `out_time_us=5920000` do `-progress pipe:1` in ra. Chỉ đọc `out_time_us`
    // chứ không đọc `out_time_ms`: ffmpeg ghi giá trị đó cũng bằng micro giây, tên gọi
    // gây hiểu nhầm và làm phần trăm nhảy gấp 1000 lần.
    [GeneratedRegex(@"^out_time_us=(?<us>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressTimeRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?:\s*Audio:", RegexOptions.IgnoreCase)]
    private static partial Regex HasAudioStreamRegex();

    /// <summary>Đọc thời lượng tổng. Null nghĩa là không xác định (<c>Duration: N/A</c>).</summary>
    public static TimeSpan? ParseDuration(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = DurationRegex().Match(line);
        return m.Success ? Compose(m) : null;
    }

    /// <summary>Đọc mốc thời gian đã xử lý từ dòng <c>time=00:01:23.45</c>.</summary>
    public static TimeSpan? ParseTime(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = TimeRegex().Match(line);
        return m.Success ? Compose(m) : null;
    }

    /// <summary>Đọc bitrate (kbit/s) từ dòng tổng kết của ffmpeg.</summary>
    public static double? ParseBitrateKbps(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = BitrateRegex().Match(line);
        return m.Success && double.TryParse(
            m.Groups["n"].Value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) ? v : null;
    }

    /// <summary>
    /// Đọc mốc thời gian đã xử lý từ dòng <c>out_time_us=</c> mà
    /// <c>-progress pipe:1</c> ghi ra. Trả về null nếu dòng không phải dòng đó.
    /// </summary>
    public static TimeSpan? ParseProgressTime(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = ProgressTimeRegex().Match(line.Trim());
        return m.Success
            ? TimeSpan.FromTicks(long.Parse(
                m.Groups["us"].Value,
                System.Globalization.CultureInfo.InvariantCulture) * 10)
            : null;
    }

    public static bool HasVideoStream(IEnumerable<string> lines) => lines.Any(l => HasVideoStreamRegex().IsMatch(l));

    public static bool HasAudioStream(IEnumerable<string> lines) => lines.Any(l => HasAudioStreamRegex().IsMatch(l));

    /// <summary>Phần trăm 0–99 (giữ 100 cho bước cuối), hoặc null nếu chưa đủ dữ liệu.</summary>
    public static int? ToPercent(TimeSpan position, TimeSpan total)
    {
        if (total <= TimeSpan.Zero) return null;
        var ratio = position.TotalSeconds / total.TotalSeconds;
        if (ratio < 0) return 0;
        if (ratio >= 1) return 99;
        return (int)Math.Floor(ratio * 100);
    }

    private static TimeSpan Compose(Match m)
    {
        var h = int.Parse(m.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var sec = double.Parse(
            m.Groups["s"].Value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);
        return new TimeSpan(h, min, 0) + TimeSpan.FromSeconds(sec);
    }
}
