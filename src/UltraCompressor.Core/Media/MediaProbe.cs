using System.Globalization;
using System.Text.RegularExpressions;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Những gì biết được về một tệp media, đủ để lập kế hoạch nén.
///
/// Mọi trường đều có thể null: probe có thể thất bại (tệp đang mở, container lạ, ffmpeg
/// không đọc được). Kế hoạch nén phải có đường lùi về hành vi hợp lý khi thiếu dữ liệu,
/// chứ không được ném lỗi — nén được tệp với tham số mặc định vẫn hơn là bỏ qua.
/// </summary>
public sealed record MediaInfo
{
    public TimeSpan? Duration { get; init; }

    /// <summary>Bitrate của luồng video nếu probe được, kbit/s.</summary>
    public double? BitrateKbps { get; init; }

    public bool HasVideo { get; init; }

    public bool HasAudio { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>
    /// Số khung hình mỗi giây. Quan trọng cho kế hoạch: hạ 60 fps xuống 30 tiết kiệm
    /// gần một nửa mà mắt gần như không thấy, trong khi hạ 30 xuống 24 thì rõ.
    /// </summary>
    public double? Fps { get; init; }

    /// <summary>Tên codec video nguồn, chữ thường: <c>h264</c>, <c>hevc</c>, <c>vp9</c>, <c>av1</c>…</summary>
    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    /// <summary>Số kênh âm thanh. 2 là stereo, 1 là mono, 6 là 5.1.</summary>
    public int? AudioChannels { get; init; }

    /// <summary>Tần số lấy mẫu, Hz.</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>Số bit màu mỗi thành phần. Ảnh 8 bit thì JPEG hay vỡ block hơn 10/12 bit.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Định dạng pixel, ví dụ <c>yuv420p</c>. Ảnh màu phẳng không bị JPEG bóp block.</summary>
    public string? PixelFormat { get; init; }

    /// <summary>Ảnh tĩnh, không có khung hình — không có % tiến độ theo thời gian.</summary>
    public bool IsStillImage { get; init; }

    /// <summary>
    /// Đặc trưng nội dung theo ITU-T P.910. Null = chưa đo, hoặc đo hỏng.
    ///
    /// <para>Tách khỏi probe chuẩn vì tốn thời gian hơn: probe này phải giải mã vài khung
    /// hình, còn probe trên chỉ đọc header. Chỉ pipeline video cần tới.</para>
    /// </summary>
    public ContentComplexity? Complexity { get; init; }

    /// <summary>
    /// Mật độ bit của nguồn, theo **bit trên mỗi pixel trên mỗi khung**. Đây là con số
    /// quan trọng nhất khi quyết định có nén lại hay không.
    ///
    /// Một tệp cho ra 0,05 bit/pixel/khung thì đã bị nén tới mức mà kỹ thuật mã hoá lại
    /// chỉ thêm nhiễu hạt mà không thu được byte nào. Ngược lại 0,5 bit/pixel là dư nhiều
    /// và nén lại thu được nhiều.
    ///
    /// Không tính được khi thiếu dữ liệu, trả null.
    /// </summary>
    public double? BitsPerPixelPerFrame
    {
        get
        {
            if (BitrateKbps is not { } kbps || kbps <= 0) return null;
            if (Width is not { } w || w <= 0) return null;
            if (Height is not { } h || h <= 0) return null;

            var rate = Fps is { } f && f > 0 ? f : 30.0;
            return kbps * 1000.0 / (w * (long)h * rate);
        }
    }

    /// <summary>
    /// Codec nguồn có hiệu quả hơn H.264 bao nhiêu không. Mã hoá HEVC/VP9/AV1 cần
    /// khoảng 30–50% ít bit hơn H.264 ở cùng chất lượng, nên nén lại chúng bằng H.264 ở
    /// cùng CRF sẽ ra tệp **lớn hơn bản gốc** — đúng trường hợp phải nâng CRF mạnh hoặc bỏ qua.
    /// </summary>
    public bool SourceCodecIsMoreEfficientThanH264 =>
        VideoCodec is { } codec &&
        (codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ||
         codec.Contains("h265", StringComparison.OrdinalIgnoreCase) ||
         codec.Contains("vp9", StringComparison.OrdinalIgnoreCase) ||
         codec.Contains("av1", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Đọc thông tin media bằng <c>ffmpeg -i</c>.
///
/// Không dùng ffprobe vì bản đóng gói không kèm, và <c>ffmpeg -i</c> in ra đủ những gì
/// ta cần. ffmpeg trả exit code khác 0 khi không có tệp đầu ra, nên ta chỉ đọc stderr.
/// </summary>
public sealed partial class MediaProbe(string ffmpegPath)
{
    private readonly Dictionary<string, MediaInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;

        var result = await ProcessRunner.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-nostdin", "-i", path],
            token: token);

        var lines = result.StandardErrorTail;
        var videoLine = lines.FirstOrDefault(l => VideoStreamRegex().IsMatch(l));
        var audioLine = lines.FirstOrDefault(l => AudioStreamRegex().IsMatch(l));

        // Bitrate: ưu tiên tổng của tệp nguồn, không thì mới lấy riêng luồng video. Dùng
        // bitrate tổng thì mật độ bit/px/khung phản ánh đúng "tệp này có bao nhiêu bit",
        // đó là thứ quyết định nén lại còn thu được không.
        var totalBitrate = FFmpegOutputParser.FindInputBitrateKbps(lines);
        var videoBitrate = FFmpegOutputParser.FindVideoStreamBitrateKbps(lines);
        var dimensions = FFmpegOutputParser.ParseDimensions(videoLine);

        var info = new MediaInfo
        {
            Duration = FirstDuration(lines),
            BitrateKbps = totalBitrate ?? videoBitrate,
            HasVideo = videoLine is not null,
            HasAudio = audioLine is not null,
            Width = dimensions.Width,
            Height = dimensions.Height,
            Fps = ParseFps(videoLine),
            VideoCodec = ParseCodec(videoLine),
            AudioCodec = ParseCodec(audioLine),
            AudioChannels = ParseChannels(audioLine),
            AudioSampleRate = ParseSampleRate(audioLine),
            BitDepth = ParseBitDepth(videoLine),
            PixelFormat = ParseToken(videoLine, "yuv", "gray", "rgb", "gbr"),
            IsStillImage = videoLine is null,
        };

        _cache[path] = info;
        return info;
    }

    [GeneratedRegex(@"Stream #\d+:\d+.*?:\s*Video:", RegexOptions.IgnoreCase)]
    private static partial Regex VideoStreamRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?:\s*Audio:", RegexOptions.IgnoreCase)]
    private static partial Regex AudioStreamRegex();

    [GeneratedRegex(@"(?<w>\d{2,5})x(?<h>\d{2,5})")]
    private static partial Regex DimensionRegex();

    // "30 fps" — cần bắt số trước chữ fps, và cả dạng "tbr".
    [GeneratedRegex(@"(?<fps>\d+(?:[.,]\d+)?)\s*fps", RegexOptions.IgnoreCase)]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"(?<c>hevc|h265|h264|avc1|vp9|av1|mpeg4|vp8|vc1|wmv3|theora|prores|dnxhd|ffv1|huffyu?)", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"\b(?<r>\d+)\s*Hz\b", RegexOptions.IgnoreCase)]
    private static partial Regex SampleRateRegex();

    [GeneratedRegex(@"\b(?<d>8|10|12|14|16)\b[\s-]*bit")]
    private static partial Regex BitDepthRegex();

    [GeneratedRegex(@"\b(?<c>mono|stereo|quad|\d\.\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelsRegex();

    private static TimeSpan? FirstDuration(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var d = FFmpegOutputParser.ParseDuration(line);
            if (d is not null) return d;
        }
        return null;
    }

    private static double? ParseFps(string? line)
    {
        if (line is null) return null;
        var m = FpsRegex().Match(line);
        if (!m.Success) return null;
        return double.TryParse(
            m.Groups["fps"].Value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var fps) && fps > 0
            ? Math.Round(fps, 3)
            : null;
    }

    private static string? ParseCodec(string? line)
    {
        if (line is null) return null;
        var m = CodecRegex().Match(line);
        return m.Success ? m.Groups["c"].Value.ToLowerInvariant() : null;
    }

    private static int? ParseSampleRate(string? line)
    {
        if (line is null) return null;
        var m = SampleRateRegex().Match(line);
        return m.Success && int.TryParse(m.Groups["r"].Value, out var r) ? r : null;
    }

    private static int? ParseBitDepth(string? line)
    {
        if (line is null) return null;
        var m = BitDepthRegex().Match(line);
        return m.Success && int.TryParse(m.Groups["d"].Value, out var d) ? d : null;
    }

    private static int? ParseChannels(string? line)
    {
        if (line is null) return null;

        var m = ChannelsRegex().Match(line);
        if (!m.Success) return null;

        var text = m.Groups["c"].Value;
        if (text.Equals("mono", StringComparison.OrdinalIgnoreCase)) return 1;
        if (text.Equals("stereo", StringComparison.OrdinalIgnoreCase)) return 2;
        if (text.Equals("quad", StringComparison.OrdinalIgnoreCase)) return 4;

        // Dạng "5.1", "7.1" — lấy số trước dấu chấm.
        var dot = text.IndexOf('.');
        return dot > 0
        && int.TryParse(text[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n + 1
            : null;
    }

    /// <summary>Lấy token bắt đầu bằng một trong các tiền tố cho trước, ví dụ <c>yuv420p</c>.</summary>
    private static string? ParseToken(string? line, params string[] prefixes)
    {
        if (line is null) return null;

        foreach (var token in line.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (prefixes.Any(p => token.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return token;
        }

        return null;
    }
}
