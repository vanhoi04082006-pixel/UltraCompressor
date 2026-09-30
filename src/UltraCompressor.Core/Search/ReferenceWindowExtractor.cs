using System.Diagnostics;
using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Search;

/// <summary>Một đoạn tham chiếu đã được cắt thành clip.</summary>
/// <param name="Role">Vai trò đoạn.</param>
/// <param name="Path">Tệp clip tham chiếu.</param>
/// <param name="OriginSeconds">Mốc đoạn trên tệp nguồn, để truy vết.</param>
/// <param name="LengthSeconds">Thời lượng đoạn.</param>
/// <param name="Bytes">Kích thước clip.</param>
/// <param name="Elapsed">Thời gian cắt.</param>
public sealed record WindowReference(
    WindowRole Role,
    string Path,
    double OriginSeconds,
    double LengthSeconds,
    long Bytes,
    TimeSpan Elapsed);

/// <summary>
/// Cắt các đoạn đại diện thành clip tham chiếu, dùng chung cho mọi ứng viên.
///
/// <para><b>Vì sao phải cắt thành clip thay vì seek thẳng vào tệp nguồn khi đo.</b> Đo
/// được trên tệp nguồn nguyên vẹn cho kết quả sai, và sai một cách rất tinh vi: hai bên
/// được lấy bằng hai đường seek khác nhau nên lệch nhau nửa khung hình. Trên nội dung
/// chuyển động nhanh, nửa khung hình đó làm VMAF rơi từ <b>93,5</b> xuống <b>41,3</b> —
/// như thể ứng viên nén tệ, trong khi thực tế chỉ là lệch khung.</para>
///
/// <para>Đo trên chính nguồn đó, tìm mốc cho điểm tối đa:</para>
/// <list type="table">
/// <item><term><c>ref = 64,98</c></term><description>mean 41,34</description></item>
/// <item><term><c>ref = 65,00</c></term><description>mean 41,34</description></item>
/// <item><term><c>ref = 65,02</c></term><description><b>mean 93,46</b> — mốc đúng</description></item>
/// <item><term>cả hai cùng cắt thành clip</term><description><b>mean 94,00</b></description></item>
/// </list>
///
/// <para>Và khi cắt cả hai bằng cùng một lệnh, điểm khung hình đầu tiên trùng nhau
/// <b>theo cách xây dựng</b>, không phải nhờ một con số thật phân nào khớp. Đó là thứ cần:
/// con số thật phân phụ thuộc bản ffmpeg và cấu trúc khung hình của tệp, nên không thể dựa
/// vào nó.</para>
///
/// <para><b>Cắt một lần, dùng cho mọi ứng viên.</b> Các ứng viên khác nhau chỉ khác ở phần
/// mã hoá, còn đoạn tham chiếu thì giống nhau. Cắt lại cho từng ứng viên vừa tốn công vừa
/// tạo thêm một nguồn lệch.</para>
///
/// <para>Tham chiếu được mã hoá <b>không tổn thất</b> (CRF 0) để nó mang đúng pixel gốc.
/// Mã hoá tổn thất ở tham chiếu sẽ cộng thêm một sai số giống nhau vào mọi ứng viên, và
/// sai số đó khác nhau theo độ dễ của nội dung — tức nó làm méo chính phép so sánh giữa
/// các ứng viên. Đổi lại là mất thời gian cắt một lần cho mỗi đoạn.</para>
/// </summary>
public sealed class ReferenceWindowExtractor(string ffmpegPath, string tempDirectory)
    : IReferenceWindowSource
{
    /// <summary>Chất lượng mã hoá cho clip tham chiếu. 0 = không tổn thất.</summary>
    public const int ReferenceQuality = 0;

    public static TimeSpan PerWindowTimeout { get; set; } = TimeSpan.FromSeconds(180);

    public async Task<IReadOnlyList<WindowReference>> ExtractAsync(
        string sourcePath,
        IReadOnlyList<RepresentativeWindow> windows,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(windows);

        Directory.CreateDirectory(tempDirectory);

        var references = new List<WindowReference>(windows.Count);

        foreach (var window in windows)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            references.Add(await ExtractOneAsync(sourcePath, window, token).ConfigureAwait(false));
        }

        return references;
    }

    private async Task<WindowReference> ExtractOneAsync(
        string sourcePath,
        RepresentativeWindow window,
        CancellationToken token)
    {
        var output = Path.Combine(
            tempDirectory,
            $"ref-{window.Role}-{window.StartSeconds.ToString("0", CultureInfo.InvariantCulture)}.mp4");

        // CÙNG CẤU TRÚC LỆNH với PilotEncoder: -ss trước -i, cùng -t. Đây là toàn bộ lý do
        // class này tồn tại — hai bên phải đi qua cùng một đường để điểm khung hình đầu
        // trùng nhau.
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", new TimeWindow(window.StartSeconds, window.DurationSeconds).StartText,
            "-t", new TimeWindow(window.StartSeconds, window.DurationSeconds).LengthText,
            "-i", sourcePath,
            "-map", "0:v:0?", "-an", "-sn", "-dn",
            "-c:v", "libx264", "-preset", "veryfast",
            "-crf", ReferenceQuality.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-y", output,
        };

        var watch = Stopwatch.StartNew();
        try
        {
            var result = await ProcessRunner.RunAsync(
                ffmpegPath, args, PerWindowTimeout, tempDirectory, token).ConfigureAwait(false);

            watch.Stop();

            if (!result.Succeeded || !File.Exists(output))
            {
                // Không có tham chiếu thì không đo được gì cả. Không dùng tệp rỗng làm
                // tham chiếu vì khi đó mọi ứng viên đều "rớt ngưỡng" theo cách không phản
                // ánh chất lượng thật.
                TryDelete(output);
                throw new InvalidOperationException(
                    $"không cắt được đoạn tham chiếu {window.Role} lúc "
                    + $"{window.StartSeconds.ToString("0", CultureInfo.InvariantCulture)}s; "
                    + "không thể đo chất lượng ứng viên nào cho tệp này");
            }

            return new WindowReference(
                window.Role,
                output,
                window.StartSeconds,
                window.DurationSeconds,
                new FileInfo(output).Length,
                watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            TryDelete(output);
            throw;
        }
    }

    /// <summary>
    /// Cài đặt seam. Chỉ chuyển tiếp — không có logic riêng, để cài đặt thật và bản giả trong
    /// test gọi đúng một hàm.
    /// </summary>
    Task<IReadOnlyList<WindowReference>> IReferenceWindowSource.ExtractAsync(
        string sourcePath, IReadOnlyList<RepresentativeWindow> windows, CancellationToken token) =>
        ExtractAsync(sourcePath, windows, token);

    /// <inheritdoc cref="IReferenceWindowSource.Release"/>
    void IReferenceWindowSource.Release(IReadOnlyList<WindowReference> references) => Release(references);

    /// <summary>Xoá các clip tham chiếu. Gọi sau khi đo xong mọi ứng viên.</summary>
    public static void Release(IReadOnlyList<WindowReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        foreach (var reference in references)
        {
            TryDelete(reference.Path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
