using System.Diagnostics;
using System.Globalization;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Quét rẻ đặc tính khó nén dọc timeline, bằng cách <b>seek thưa</b> chứ không giải mã toàn bộ.
///
/// <para>Con số đo trên tệp 30 phút của người dùng quyết định thiết kế này:</para>
/// <list type="bullet">
/// <item>giải mã toàn bộ, không filter: <b>93,2 s</b> — giải mã chiếm gần hết thời gian;</item>
/// <item>giải mã toàn bộ + mọi filter: <b>111,8 s</b> — filter chỉ thêm ~19 s;</item>
/// <item>một cửa sổ 2 s qua seek: <b>0,29–0,48 s</b>.</item>
/// </list>
///
/// <para>Nên quét toàn timeline tốn hơn seek khoảng 200 lần, dù trông "rẻ hơn" vì chỉ
/// mở một tiến trình. Số cửa sổ lấy mẫu bị chặn trên/dưới để giữ tổng chi phí thấp; đổi
/// lại tệp dài hơn thì có nhiều mẫu hơn, theo log thời lượng.</para>
///
/// <para>Mỗi mẫu là một tiến trình riêng, nên số mẫu là điều kiện ràng buộc thời gian.
/// Trần <see cref="AppConfig.AnalysisMaxSamples"/> là hàng rào cho một cấu hình sai.</para>
/// </summary>
public sealed class TimelineScanner(string ffmpegPath, string tempDirectory)
{
    /// <summary>
    /// Giới hạn cho một lần seek. Quá là bỏ mẫu đó, không treo job — một tệp hỏng không
    /// được phép làm cả job đứng.
    /// </summary>
    private static readonly TimeSpan PerSampleTimeout = TimeSpan.FromSeconds(30);

    private readonly string _ffmpegPath = ffmpegPath;
    private readonly string _tempDirectory = tempDirectory;

    /// <summary>
    /// Số mẫu cần cho một tệp dài <paramref name="durationSeconds"/>.
    ///
    /// <para>Tăng theo log chứ không theo tuyến tính: một tệp gấp đôi thời lượng thì
    /// <b>một</b> mẫu phủ gấp đôi khoảng thời gian, và muốn vẫn bắt được các cảnh lạ thì
    /// số mẫu phải tăng, nhưng chậm hơn nhiều. Cùng đầu vào luôn cho cùng số mẫu.</para>
    /// </summary>
    public static int SampleCountFor(TimeSpan? duration, AppConfig config)
    {
        var minutes = duration is { } d && d > TimeSpan.Zero ? d.TotalMinutes : 1.0;
        var wanted = 8.0 + 2.0 * Math.Log2(1.0 + minutes);

        // Người dùng gõ sai cấu hình (min > max) không được làm ném exception giữa lúc
        // nén. Math.Clamp ném khi min > max, nên phải tự kiểm trước.
        var min = Math.Max(1, config.AnalysisMinSamples);
        var max = Math.Max(min, config.AnalysisMaxSamples);

        return Math.Clamp((int)Math.Ceiling(wanted), min, max);
    }

    /// <summary>
    /// Quét và trả về đặc trưng của từng mẫu. Không ném lỗi: quét hỏng thì trả về mảng
    /// rỗng để người gọi rơi về chiến lược dự phòng.
    /// </summary>
    public async Task<IReadOnlyList<WindowFeatures>> ScanAsync(
        string sourcePath, TimeSpan? duration, AppConfig config, CancellationToken token)
    {
        var count = SampleCountFor(duration, config);
        var seconds = duration is { } d && d > TimeSpan.Zero ? d.TotalSeconds : 0;
        var sampleLength = Math.Max(0.5, config.AnalysisSampleSeconds);

        // Quét cả tệp khi không biết thời lượng thì lấy một đoạn đầu, vì rải mẫu cần biết
        // chiều dài. Khi đó nhiều mẫu rơi vào cùng một chỗ nên chỉ giữ vài cái.
        var effectiveCount = seconds > 0 ? count : Math.Min(count, 2);

        Directory.CreateDirectory(_tempDirectory);
        var results = new List<WindowFeatures>(effectiveCount);

        for (var i = 0; i < effectiveCount; i++)
        {
            if (token.IsCancellationRequested) break;

            // Tránh sát hai đầu: đầu tệp thường là logo/credit, cuối tệp thường bị cắt cụt.
            // Với tệp ngắn hơn cả mẫu thì lấy từ 0.
            var start = seconds > 0
                ? Margin * seconds + (DurationFraction(i, effectiveCount) * (1 - 2 * Margin) * seconds)
                : 0;

            var sample = await MeasureAsync(sourcePath, start, sampleLength, config, token).ConfigureAwait(false);
            if (sample is { IsUsable: true }) results.Add(sample);
        }

        return results;
    }

    /// <summary>Chừa mỗi đầu một tỉ lệ thời lượng, tránh vùng không đại diện ở hai đầu.</summary>
    private const double Margin = 0.05;

    private static double DurationFraction(int index, int count) =>
        count <= 1 ? 0.5 : index / (double)(count - 1);

    private async Task<WindowFeatures?> MeasureAsync(
        string sourcePath, double start, double length, AppConfig config, CancellationToken token)
    {
        var logName = $"scan-{Guid.NewGuid():N}.txt";
        var logPath = Path.Combine(_tempDirectory, logName);

        try
        {
            // fps đặt sau scale để phần tử ảnh chưa bị ném đi vô ích, và đặt trước các bộ
            // đo để chúng chỉ việc tính trên số khung đã lược bớt.
            //
            // `normalized_entropy.normal.Y` đã nằm trên [0,1] do chính ffmpeg quy đổi,
            // nên không phải tự chế một công thức chuẩn hoá entropy rồi đo lại xem có khớp.
            var filter =
                $"fps={config.AnalysisSampleFps.ToString("0.##", CultureInfo.InvariantCulture)}," +
                "scale=320:180:flags=bilinear,format=gray," +
                "scdet=threshold=10,signalstats,entropy,blurdetect," +
                $"metadata=print:file={logName}";

            var result = await ProcessRunner.RunAsync(
                _ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    // -ss trước -i: seek nhanh, không giải mã từ đầu.
                    "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                    "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", sourcePath,
                    "-vf", filter,
                    "-f", "null", "-",
                ],
                PerSampleTimeout,
                _tempDirectory,
                token).ConfigureAwait(false);

            if (!result.Succeeded || !File.Exists(logPath)) return null;

            return Parse(logPath, start, length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Tệp rác phải đi ngay: mỗi mẫu một tệp, 24 mẫu là 24 tệp nếu sót.
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
            }
            catch
            {
                // TempWorkspace dọn theo phiên.
            }
        }
    }

    /// <summary>
    /// Đọc log metadata của ffmpeg. Định dạng là các cặp <c>frame: n  pts: …</c> rồi
    /// <c>key=value</c> mỗi dòng, lặp lại theo từng khung.
    /// </summary>
    internal static WindowFeatures? Parse(string logPath, double start, double length)
    {
        var spatial = new List<double>(16);
        var motion = new List<double>(16);
        var scene = new List<double>(16);
        var blur = new List<double>(16);
        var luma = new List<double>(16);

        var pending = new Dictionary<string, double>(StringComparer.Ordinal);

        void Flush()
        {
            if (pending.Count == 0) return;

            if (pending.TryGetValue(Entropy, out var e)) spatial.Add(e);
            if (pending.TryGetValue(SceneScore, out var s)) scene.Add(s);
            if (pending.TryGetValue(Motion, out var m)) motion.Add(m);
            if (pending.TryGetValue(Blur, out var b)) blur.Add(b);
            if (pending.TryGetValue(Luma, out var l)) luma.Add(l);

            pending.Clear();
        }

        foreach (var line in File.ReadLines(logPath))
        {
            if (line.Length == 0) continue;

            if (line.StartsWith("frame:", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator];
            if (!IsInteresting(key)) continue;

            if (double.TryParse(
                    line.AsSpan(separator + 1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                pending[key] = value;
            }
        }

        Flush();

        if (spatial.Count == 0) return null;

        return new WindowFeatures(
            StartSeconds: start,
            SampleSeconds: length,
            // Khung đầu tiên luôn có YDIF và scd.score bằng 0 vì chưa có khung trước để
            // so. Đưa nó vào trung bình làm hạ cả mẫu một cách không có ý nghĩa, nên bỏ.
            Spatial: spatial.Average(),
            Motion: AverageIgnoringFirstZero(motion),
            SceneScore: scene.Count > 0 ? scene.Max() : 0,
            Blur: blur.Count > 0 ? blur.Average() : 0,
            LumaAverage: luma.Count > 0 ? luma.Average() : 0,
            Frames: spatial.Count);
    }

    private const string Entropy = "lavfi.entropy.normalized_entropy.normal.Y";
    private const string SceneScore = "lavfi.scd.score";
    private const string Motion = "lavfi.signalstats.YDIF";
    private const string Blur = "lavfi.blur";
    private const string Luma = "lavfi.signalstats.YAVG";

    private static bool IsInteresting(string key) =>
        key is Entropy or SceneScore or Motion or Blur or Luma;

    /// <summary>
    /// Trung bình bỏ giá trị 0 đứng đầu. Ở khung đầu ffmpeg chưa có khung trước nên
    /// YDIF và scd.score bằng 0; tính cả vào sẽ hạ điểm mọi mẫu đều như nhau và làm mất
    /// khả năng phân biệt.
    /// </summary>
    private static double AverageIgnoringFirstZero(List<double> values)
    {
        if (values.Count <= 1) return values.Count == 1 ? values[0] : 0;

        var start = values[0] == 0 ? 1 : 0;
        var span = values.Skip(start).ToList();
        return span.Count == 0 ? 0 : span.Average();
    }
}
