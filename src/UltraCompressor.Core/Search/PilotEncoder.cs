using System.Diagnostics;
using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Search;

/// <summary>Kết quả encode một đoạn thử nghiệm cho một ứng viên.</summary>
/// <param name="CandidateId">Ứng viên nào.</param>
/// <param name="Role">Vai trò đoạn đại diện, để báo cáo mà không phải đoán lại từ câu chữ.</param>
/// <param name="ReferenceWindow">Đoạn trên tệp nguồn, ví dụ <c>[947,9s, 950,9s]</c>.</param>
/// <param name="CandidateWindow">
/// Đoạn trên chính clip pilot vừa tạo, luôn <c>[0, d]</c>.
/// </param>
/// <param name="OutputPath">Tệp clip pilot, hoặc null nếu encode hỏng.</param>
/// <param name="Bytes">Kích thước clip pilot.</param>
/// <param name="Target">Kích thước và số khung hình đích, sau khi đã kiểm tra không phóng to.</param>
/// <param name="FfmpegArguments">Lệnh ffmpeg đã chạy, ghi lại để truy vết.</param>
/// <param name="Elapsed">Thời gian encode.</param>
/// <param name="Outcome">Mã lý do và câu chữ.</param>
public sealed record PilotArtifact(
    string CandidateId,
    WindowRole Role,
    TimeWindow ReferenceWindow,
    TimeWindow CandidateWindow,
    string? OutputPath,
    long Bytes,
    EncodeTarget Target,
    IReadOnlyList<string> FfmpegArguments,
    TimeSpan Elapsed,
    SearchOutcome Outcome)
{
    public bool Success => Outcome.Reason != SearchDecisionReasons.PilotEncodeFailed && OutputPath is not null;
}

/// <summary>
/// Encode <b>chỉ các đoạn đại diện</b> của một ứng viên, không encode toàn tệp.
///
/// <para>Mục đích là trả lời "ứng viên này có đạt chất lượng không" mà không phải chờ encode
/// cả tệp. Clip thử nghiệm là bản cắt của đúng đoạn đại diện, nên nó bắt đầu tại 0 — vì
/// vậy phép đo phải truyền <c>referenceWindow</c> và <c>candidateWindow</c> khác nhau.</para>
///
/// <para><b>Không mã hoá âm thanh.</b> VMAF chỉ nhìn hình, và mã hoá âm thanh chỉ làm chậm
/// phép thử mà không đóng góp gì cho quyết định. Phần ước lượng dung lượng phía sau có
/// tính riêng phần âm thanh, và ghi rõ giả định đó.</para>
/// </summary>
public sealed class PilotEncoder(string ffmpegPath, string tempDirectory) : IPilotEncodeRunner
{
    /// <summary>Hết giờ cho một lần encode đoạn. Ngắn hơn hẳn thời gian của một cảnh tốt
    /// nhưng dài hơn một đoạn treo.</summary>
    public static TimeSpan PerWindowTimeout { get; set; } = TimeSpan.FromSeconds(180);

    public async Task<IReadOnlyList<PilotArtifact>> EncodeWindowsAsync(
        VideoEncodeCandidate candidate,
        string sourcePath,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<RepresentativeWindow> windows,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(windows);

        if (!EncodeTarget.TryFromRequest(
                sourceWidth, sourceHeight,
                candidate.Width, candidate.Height,
                out var target, out var targetFailure))
        {
            // Chặn trước khi gọi ffmpeg: một lệnh encode sai thì tốn công vô ích và tệ hơn,
            // sinh ra tệp rác.
            return
            [
                Failed(candidate, windows, targetFailure, target, [], TimeSpan.Zero),
            ];
        }

        var artifacts = new List<PilotArtifact>(windows.Count);

        foreach (var window in windows)
        {
            if (token.IsCancellationRequested)
            {
                artifacts.Add(Failed(
                    candidate, [window], "đã hủy trước khi encode đoạn này",
                    target, [], TimeSpan.Zero));
                continue;
            }

            artifacts.Add(await EncodeOneAsync(
                candidate, sourcePath, sourceWidth, sourceHeight, target, window, token)
                .ConfigureAwait(false));
        }

        return artifacts;
    }

    private async Task<PilotArtifact> EncodeOneAsync(
        VideoEncodeCandidate candidate,
        string sourcePath,
        int sourceWidth,
        int sourceHeight,
        EncodeTarget target,
        RepresentativeWindow window,
        CancellationToken token)
    {
        Directory.CreateDirectory(tempDirectory);

        // Tên tệp phải duy nhất theo từng lần encode. Tên theo ứng viên + đoạn sẽ va nhau
        // khi hai tệp cùng được xử lý song song trong một workspace, và clip sau ghi đè
        // clip trước giữa lúc đang đo.
        // Clip thử nghiệm luôn là MP4, kể cả khi nguồn là Matroska hay MPEG-TS: lệnh mang
        // `-movflags +faststart`, và clip phải cùng container với clip tham chiếu để phép đo so
        // được hai tệp cùng điều kiện. Nguồn sự thật: `OutputContainer`.
        var output = Path.Combine(
            tempDirectory,
            $"pilot-{Sanitize(candidate.Id)}-{window.Role}-{window.StartSeconds.ToString("0", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}{OutputContainer.Mp4}");

        // Clip thử nghiệm là bản CẮT của đúng đoạn này, nên nó bắt đầu tại 0. Giữ điều này
        // ở dạng hằng số thay vì suy ra từ đầu, để không ai vô tình truyền nhầm mốc của
        // nguồn vào phép đo.
        var referenceWindow = new TimeWindow(window.StartSeconds, window.DurationSeconds);
        var candidateWindow = new TimeWindow(0, window.DurationSeconds);

        // Kích thước nguồn phải là kích thước THẬT, không phải 0: điều kiện dựng filter
        // scale là so với kích thước nguồn, nên truyền 0 sẽ khiến nhánh thu nhỏ không bao
        // giờ chạy — tức ứng viên nhỏ hơn lại được encode ở nguyên kích thước nguồn.
        //
        // Tần số khung hình nguồn cũng phải truyền thật, vì BuildFilter dùng nó để quyết
        // định có cần `fps=` hay không.
        var filter = EncodeTransform.BuildFilter(target, sourceWidth, sourceHeight);
        var args = EncodeTransform.BuildSegmentArguments(
            candidate.ToEncoderConfiguration(), filter, sourcePath, output, referenceWindow);
        var watch = Stopwatch.StartNew();

        try
        {
            var result = await ProcessRunner.RunAsync(
                ffmpegPath, args, PerWindowTimeout, tempDirectory, token).ConfigureAwait(false);

            watch.Stop();

            if (!result.Succeeded || !File.Exists(output))
            {
                var reason = FirstErrorLine(result.StandardErrorTail)
                    ?? $"ffmpeg trả mã {result.ExitCode}";

                // Encode hỏng thì không còn gì để đo, nên dọn ngay.
                TryDelete(output);

                return Failed(candidate, [window], reason, target, args, watch.Elapsed);
            }

            // CỐ TÌNH KHÔNG xoá khi thành công. Clip này là thứ bước đo chất lượng sắp tới
            // cần đọc; xoá ở đây nghĩa là `OutputPath` trỏ tới một tệp không tồn tại, và cả
            // chuỗi đo đại diện trục thời gian rơi vào hư không.
            //
            // Vòng đời tệp thuộc giai đoạn tìm kiếm, đi qua
            // <see cref="Release(IReadOnlyList{PilotArtifact})"/>.
            return new PilotArtifact(
                CandidateId: candidate.Id,
                Role: window.Role,
                ReferenceWindow: referenceWindow,
                CandidateWindow: candidateWindow,
                OutputPath: output,
                Bytes: new FileInfo(output).Length,
                Target: target,
                FfmpegArguments: args,
                Elapsed: watch.Elapsed,
                Outcome: new SearchOutcome(
                    SearchDecisionReasons.PilotSelected,
                    $"encode đoạn {window.Role} lúc {window.StartSeconds.ToString("0", CultureInfo.InvariantCulture)}s xong"));
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            TryDelete(output);
            return Failed(candidate, [window], "đã hủy", target, args, watch.Elapsed);
        }
    }

    /// <summary>
    /// Cài đặt seam. Chỉ chuyển tiếp — không có logic riêng, để cài đặt thật và bản giả trong
    /// test gọi đúng một hàm.
    /// </summary>
    Task<IReadOnlyList<PilotArtifact>> IPilotEncodeRunner.EncodeAsync(
        VideoEncodeCandidate candidate,
        string sourcePath,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<RepresentativeWindow> windows,
        CancellationToken token) =>
        EncodeWindowsAsync(candidate, sourcePath, sourceWidth, sourceHeight, windows, token);

    /// <inheritdoc cref="IPilotEncodeRunner.Release"/>
    void IPilotEncodeRunner.Release(IReadOnlyList<PilotArtifact> artifacts) => Release(artifacts);

    /// <summary>
    /// Xoá các clip thử nghiệm. Gọi một lần khi đã đo xong cả ứng viên.
    /// </summary>
    /// <remarks>
    /// <para>Việc này ở tay người gọi chứ không tự động, vì encoder không biết lúc nào thì
    /// đo xong. Kiểm thử cần kiểm cả hai chiều: tệp còn lại trong khi đang đo, và không
    /// còn gì sau khi gọi hàm này.</para>
    /// </remarks>
    public static void Release(IReadOnlyList<PilotArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        foreach (var artifact in artifacts)
        {
            if (artifact.OutputPath is { } path)
            {
                TryDelete(path);
            }
        }
    }

    private static PilotArtifact Failed(
        VideoEncodeCandidate candidate,
        IReadOnlyList<RepresentativeWindow> windows,
        string detail,
        EncodeTarget target,
        IReadOnlyList<string> args,
        TimeSpan elapsed)
    {
        var where = windows.Count == 1
            ? $"đoạn {windows[0].Role} lúc {windows[0].StartSeconds.ToString("0", CultureInfo.InvariantCulture)}s"
            : $"{windows.Count} đoạn";

        return new PilotArtifact(
            CandidateId: candidate.Id,
            Role: windows.Count == 1 ? windows[0].Role : WindowRole.Typical,
            ReferenceWindow: windows.Count == 1
                ? new TimeWindow(windows[0].StartSeconds, windows[0].DurationSeconds)
                : new TimeWindow(0, 0),
            CandidateWindow: new TimeWindow(0, 0),
            OutputPath: null,
            Bytes: 0,
            Target: target,
            FfmpegArguments: args,
            Elapsed: elapsed,
            Outcome: new SearchOutcome(
                SearchDecisionReasons.PilotEncodeFailed,
                $"không encode được {where}: {detail}"));
    }

    private static string? FirstErrorLine(IReadOnlyList<string>? stderr)
    {
        if (stderr is null || stderr.Count == 0)
        {
            return null;
        }

        foreach (var line in stderr)
        {
            if (line.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No such", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        return null;
    }

    private static string Sanitize(string id)
    {
        // KHÔNG cho phép dấu gạch chéo. ID ứng viên có dạng "libx264/1920x1080/crf23", nên
        // giữ nguyên sẽ tạo ra một đường dẫn nằm trong thư mục con chưa tồn tại: ffmpeg báo
        // "No such file or directory" và không encode được. Lỗi này chỉ lộ ra khi thật sự
        // chạy ffmpeg.
        Span<char> buffer = stackalloc char[id.Length];
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_';
        }

        return new string(buffer);
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
            // Tệp đang bị giữ: thường là ffmpeg chưa thoát hẳn. Dọn ở lượt gọi sau hoặc
            // khi workspace bị giải phóng. Không ném, vì dọn dẹp không được làm hỏng kết quả.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
