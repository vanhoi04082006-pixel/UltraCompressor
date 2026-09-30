using System.Globalization;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Media;

/// <summary>Đoạn thời gian dùng để đo chất lượng, tính từ đầu tệp.</summary>
public readonly record struct TimeWindow(double StartSeconds, double LengthSeconds)
{
    public double EndSeconds => StartSeconds + LengthSeconds;

    /// <summary>Chuỗi dạng <c>ss</c>/<c>t</c> cho tham số ffmpeg, theo quy ước InvariantCulture.</summary>
    public string StartText => Math.Max(0, StartSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    public string LengthText => LengthSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Kết quả gom từ nhiều khung hình của một lần đo.</summary>
/// <param name="Mean">Trung bình toàn bộ khung đo được.</param>
/// <param name="P5">Phân vị 5% — dùng để chặn hiện tượng "phần lớn tệp ổn, vài cảnh hỏng nặng".</param>
/// <param name="Min">Khung tệ nhất.</param>
/// <param name="SsimMean">SSIM trung bình, tính chung lượt với VMAF nên không tốn thêm lượt giải mã nào.</param>
/// <param name="Frames">Số khung thực sự đo được.</param>
public sealed record QualitySample(double Mean, double P5, double Min, double SsimMean, int Frames);

/// <summary>Kết quả đo đầy đủ cho một ứng viên trên một đoạn.</summary>
public sealed record QualityResult(
    string ModelId,
    TimeWindow Window,
    int DisplayWidth,
    int DisplayHeight,
    int CandidateWidth,
    int CandidateHeight,
    QualitySample Sample)
{
    /// <summary>Ứng viên có nhỏ hơn vùng hiển thị không — tức đã bị thu nhỏ.</summary>
    public bool WasDownscaled => CandidateWidth < DisplayWidth;
}

/// <summary>
/// Đo chất lượng ứng viên so với bản gốc bằng VMAF (kèm SSIM trong cùng một lượt).
///
/// <para>Đây là bước quyết định "còn nén được không" — không phải bước trang trí. Có một
/// trường hợp đo thật cho thấy vì sao phải dùng VMAF chứ không dùng SSIM: ứng viên
/// 1080p bị thu xuống 720p rồi so sánh ở 1080p cho <b>VMAF 78,2</b> nhưng
/// <b>SSIM 0,9972</b>. Chỉ dùng SSIM thì coi như đạt và chấp nhận mất 56% chiều cao
/// trong khi người xem thấy rõ.</para>
///
/// <para>Hai điều kiện bắt buộc, sai là đo ra số vô nghĩa:</para>
/// <list type="number">
/// <item>Hai luồng phải cùng kích thước. Ứng viên bị thu nhỏ được phóng lại về đúng
/// kích thước hiển thị của nguồn trước khi so, để mất chiều không gian bị trừ điểm
/// đúng như nó đáng bị trừ.</item>
/// <item>Thứ tự là <b>[cái bị nén][bản gốc]</b> và phép so không đối xứng. Cùng một cặp
/// tệp, đảo thứ tự cho 78,2 và 86,6. Sai thứ tự thì mọi cổng chất lượng đảo ngược mà
/// không báo lỗi.</item>
/// </list>
/// </summary>
public sealed class QualityProbe(string ffmpegPath, string tempDirectory)
{
    /// <summary>
    /// Giới hạn thời gian cho một lần đo. Quá là bỏ đoạn đó, không treo job.
    ///
    /// <para>240 giây không phải con số bừa: lần quét hiệu chỉnh từng gặp một cảnh
    /// chuyển động mạnh ở 1080p mà x265 CRF 18 vượt mốc 120 giây ban đầu, đo hỏng. Cửa sổ
    /// đo sau này lấy ở độ phân giải thấp hơn nên sẽ nhanh hơn nhiều; mốc này là chốt
    /// chặn cuối để một cảnh lạ không kéo dài cả job.</para>
    /// </summary>
    private static readonly TimeSpan PerWindowTimeout = TimeSpan.FromSeconds(240);

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, QualitySample> Cache = new(StringComparer.Ordinal);

    private readonly string _ffmpegPath = ffmpegPath;
    private readonly string _tempDirectory = tempDirectory;

    /// <summary>
    /// Đo một đoạn. Trả null nếu ffmpeg lỗi, hết giờ, hoặc log không có khung nào —
    /// người gọi phải coi đây là "không đo được" chứ không phải "đạt".
    /// </summary>
    public async Task<QualityResult?> MeasureAsync(
        string referencePath,
        string candidatePath,
        TimeWindow window,
        int displayWidth,
        int displayHeight,
        int candidateWidth,
        int candidateHeight,
        VmafModel model,
        CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return null;

        var key = string.Join('|',
            referencePath, candidatePath, window.StartText, window.LengthText,
            displayWidth, displayHeight, model.Id,
            StampOf(candidatePath));

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached is { } hit
                    ? new QualityResult(model.Id, window, displayWidth, displayHeight, candidateWidth, candidateHeight, hit)
                    : null;
            }
        }

        var sample = await MeasureCoreAsync(
            referencePath, candidatePath, window, displayWidth, displayHeight, model, token)
            .ConfigureAwait(false);

        lock (CacheLock)
        {
            Cache[key] = sample!;
        }

        return sample is null
            ? null
            : new QualityResult(model.Id, window, displayWidth, displayHeight, candidateWidth, candidateHeight, sample);
    }

    private async Task<QualitySample?> MeasureCoreAsync(
        string referencePath,
        string candidatePath,
        TimeWindow window,
        int displayWidth,
        int displayHeight,
        VmafModel model,
        CancellationToken token)
    {
        var logName = $"vmaf-{Guid.NewGuid():N}.json";
        var logPath = Path.Combine(_tempDirectory, logName);

        try
        {
            Directory.CreateDirectory(_tempDirectory);

            var width = displayWidth.ToString(CultureInfo.InvariantCulture);
            var height = displayHeight.ToString(CultureInfo.InvariantCulture);

            // log_fmt=json là bắt buộc: log_path đoán định dạng theo phần mở rộng nhưng bản
            // dựng này vẫn xuất XML cho tên đuôi .json, và JSON là thứ ta cần để tự tính P5.
            //
            // log_path chỉ nhận TÊN TỆP TRẦN, không nhận đường dẫn tuyệt đối: dấu `:` của
            // "C:" phá vỡ cú pháp filtergraph, và bốn kiểu escape khác nhau đều bị ffmpeg bỏ
            // qua lặng lẽ — exit 0, không lỗi, không có tệp log. Nên ffmpeg được chạy với
            // thư mục làm việc là thư mục tạm.
            var libvmaf =
                $"libvmaf=model=version={model.Id}:feature=name=float_ssim:log_path={logName}:log_fmt=json";

            var filter =
                $"[0:v]format=yuv420p10le,scale={width}:{height}:flags=lanczos[ref];" +
                $"[1:v]format=yuv420p10le,scale={width}:{height}:flags=lanczos[dis];" +
                $"[dis][ref]{libvmaf}";

            var result = await ProcessRunner.RunAsync(
                _ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-ss", window.StartText, "-t", window.LengthText, "-i", referencePath,
                    "-ss", window.StartText, "-t", window.LengthText, "-i", candidatePath,
                    "-lavfi", filter,
                    "-f", "null", "-",
                ],
                PerWindowTimeout,
                _tempDirectory,
                token).ConfigureAwait(false);

            if (!result.Succeeded || !File.Exists(logPath)) return null;

            return ParseSample(logPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Đo hỏng không được làm hỏng việc nén: thiếu số đo thì vẫn nén bằng đường lùi.
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
            }
            catch
            {
                // Tệp tạm sót lại vô hại; TempWorkspace dọn theo phiên.
            }
        }
    }

    /// <summary>
    /// Tự tính mean/P5/min từ điểm theo khung. libvmaf chỉ in ra điểm gộp, mà điểm gộp
    /// không có phân vị thấp — đúng thứ ta cần để bắt cảnh hỏng mà phần lớn tệp che.
    /// </summary>
    private static QualitySample? ParseSample(string logPath) =>
        QualityLog.TryParse(File.ReadAllText(logPath), out var sample) ? sample : null;

    /// <summary>
    /// Dấu vân tay tệp ứng viên: kích thước + thời điểm sửa. Ứng viên cùng đường dẫn nhưng
    /// nội dung khác (nén lại lần hai) phải đo lại, không được dùng kết quả cũ.
    /// </summary>
    private static string StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "?";
        }
    }
}
