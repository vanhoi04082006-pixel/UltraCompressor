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

    // Dòng tổng kết của ffmpeg dùng "bitrate= 262.1kbits/s" (có dấu bằng), dòng Input #0
    // dùng "bitrate: 1629 kb/s" (dấu hai chấm, KHÔNG có chữ "t"), còn ffprobe lại dùng
    // "bitrate: 1629000" không có đơn vị. Regex cũ chỉ nhận "kbits" nên bỏ sót đúng
    // dòng Input #0 — tức bỏ sót bitrate thật của tệp.
    [GeneratedRegex(
        @"bitrate\s*[:=]\s*(?<n>\d+(?:[.,]\d+)?)\s*(?:kbits?|kb)s?",
        RegexOptions.IgnoreCase)]
    private static partial Regex BitrateRegex();

    // Dòng mô tả luồng viết tốc độ trần như "..., 1374 kb/s, 23.98 fps, ..." — KHÔNG có
    // chữ "bitrate" phía trước. Regex trên không khớp chỗ này, nên cần một regex riêng
    // cho phần tử "N kb/s" trần.
    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?:kbits?|kb)/s", RegexOptions.IgnoreCase)]
    private static partial Regex StreamBitrateRegex();

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

    /// <summary>Đọc bitrate (kbit/s) từ một dòng có chữ "bitrate".</summary>
    public static double? ParseBitrateKbps(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        return AsKbps(BitrateRegex(), line);
    }

    /// <summary>
    /// Đọc tốc độ trần viết trần trong dòng mô tả luồng ("..., 1374 kb/s, ...").
    /// Ưu tiên số đi kèm chữ "kb/s"; dòng cuối đôi khi có cả <c>kb/s</c> lẫn <c>tbr</c>.
    /// </summary>
    public static double? ParseStreamBitrateKbps(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        return AsKbps(StreamBitrateRegex(), line);
    }

    private static double? AsKbps(Regex regex, string line)
    {
        var m = regex.Match(line);
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

    /// <summary>
    /// Tìm bitrate tổng của tệp nguồn trong stderr của ffmpeg.
    ///
    /// <para>Bitrate nằm ở dòng <c>Duration:</c>, tức dòng NGAY SAU dòng
    /// <c>Input #0</c> — không phải trên chính dòng đó. Nên phải quét từ vị trí
    /// <c>Input #0</c> tới trước dòng <c>Stream #</c> đầu tiên; quét toàn bộ sẽ vô tình
    /// lấy nhầm bitrate của riêng một luồng.</para>
    ///
    /// <para>Không lấy <c>lines[^1]</c> như bản gốc: dòng cuối là
    /// "At least one output file must be specified", nên cách đó luôn trả null và bitrate
    /// mất trắng. Hậu quả âm thầm: planner không tính được mật độ bit/px/khung, rơi về
    /// tham số nền cho mọi tệp, job vẫn chạy và vẫn ra tệp — chỉ là không thích ứng gì
    /// cả. Đo thật mới thấy: video 19 phút chỉ giảm được 9,7%.</para>
    /// </summary>
    public static double? FindInputBitrateKbps(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains("Input #", StringComparison.OrdinalIgnoreCase)) continue;

            // Dòng "Input #0" không mang số; bitrate nằm ở vài dòng kế, dừng trước "Stream #".
            for (var j = i; j < lines.Count; j++)
            {
                if (j > i && lines[j].Contains("Stream #", StringComparison.OrdinalIgnoreCase)) break;

                var value = ParseBitrateKbps(lines[j]);
                if (value is > 0) return value;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Tìm bitrate của riêng luồng video từ dòng mô tả luồng. Dùng làm dự phòng khi
    /// không đọc được bitrate tổng.
    /// </summary>
    public static double? FindVideoStreamBitrateKbps(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (!HasVideoStreamRegex().IsMatch(line)) continue;
            var value = ParseStreamBitrateKbps(line);
            if (value is > 0) return value;
        }

        return null;
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
