using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Media;

public sealed record MediaInfo
{
    public TimeSpan? Duration { get; init; }

    public double? BitrateKbps { get; init; }

    public bool HasVideo { get; init; }

    public bool HasAudio { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Ảnh tĩnh, không có khung hình — không có % tiến độ theo thời gian.</summary>
    public bool IsStillImage { get; init; }
}

/// <summary>
/// Đọc thông tin media bằng <c>ffmpeg -i</c>.
///
/// Không dùng ffprobe vì bản đóng gói không kèm, và <c>ffmpeg -i</c> in ra đủ những gì
/// ta cần. ffmpeg trả exit code khác 0 khi không có tệp đầu ra, nên ta chỉ đọc stderr.
/// </summary>
public sealed class MediaProbe(string ffmpegPath)
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
        var dimension = ParseDimension(lines);

        var info = new MediaInfo
        {
            Duration = FirstDuration(lines),
            BitrateKbps = lines.Count > 0 ? FFmpegOutputParser.ParseBitrateKbps(lines[^1]) : null,
            HasVideo = FFmpegOutputParser.HasVideoStream(lines),
            HasAudio = FFmpegOutputParser.HasAudioStream(lines),
            Width = dimension?.Width,
            Height = dimension?.Height,
            IsStillImage = !FFmpegOutputParser.HasVideoStream(lines),
        };

        _cache[path] = info;
        return info;
    }

    private static TimeSpan? FirstDuration(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var d = FFmpegOutputParser.ParseDuration(line);
            if (d is not null) return d;
        }
        return null;
    }

    private static (int Width, int Height)? ParseDimension(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.Contains("Video:", StringComparison.OrdinalIgnoreCase)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(?<w>\d{2,5})x(?<h>\d{2,5})");
            if (!m.Success) continue;
            if (int.TryParse(m.Groups["w"].Value, out var w) && int.TryParse(m.Groups["h"].Value, out var h))
            {
                return (w, h);
            }
        }
        return null;
    }
}
