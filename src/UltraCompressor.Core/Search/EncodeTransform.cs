using System.Globalization;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Search;

/// <summary>
/// Kích thước và số khung hình của một ứng viên encode, đã được kiểm tra hợp lệ.
///
/// <para>Kiểu này là <b>ranh giới</b> giữa "muốn gì" và "được phép làm gì". Nó chặn ở
/// đây ba điều mà nếu để lọt xuống tầng dựng lệnh thì sẽ hỏng theo kiểu rất khó nhận ra:
/// phóng to, méo tỉ lệ khung hình, và số khung hình lẻ khiến bộ mã hoá từ chối.</para>
/// </summary>
/// <param name="Width">Bề rộng đích, luôn chẵn và không vượt bề rộng nguồn.</param>
/// <param name="Height">Chiều cao đích, luôn chẵn và không vượt chiều cao nguồn.</param>
/// <param name="Fps">Số khung hình đích; bằng số khung hình nguồn ở giai đoạn này.</param>
public readonly record struct EncodeTarget(int Width, int Height, double Fps)
{
    /// <summary>
    /// Dựng kích thước đích từ hồ sơ nguồn và yêu cầu của ứng viên.
    /// </summary>
    /// <remarks>
    /// <para><b>Không bao giờ phóng to.</b> Phóng to không tạo thêm thông tin, chỉ làm tệp
    /// to lên mà chất lượng cảm nhận không tăng — tệ hơn, vì thuật toán nén phải tốn công
    /// mã hoá những vùng trống.</para>
    ///
    /// <para><b>Giữ đúng tỉ lệ khung hình.</b> Bề rộng được tính từ chiều cao mong muốn
    /// nhân với tỉ lệ của nguồn, rồi làm tròn xuống số chẵn (codec yêu cầu bề rộng và chiều
    /// cao chẵn với hầu hết định dạng pixel). Không dùng tỉ lệ cố định 16:9 vì nguồn có
    /// thể là 4:3 hoặc dọng — đặt 16:9 lên nguồn 4:3 là méo hình.</para>
    /// </remarks>
    public static bool TryFromRequest(
        int sourceWidth,
        int sourceHeight,
        double sourceFps,
        int requestedWidth,
        int requestedHeight,
        out EncodeTarget target,
        out string failure)
    {
        target = default;

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            failure = $"kích thước nguồn không hợp lệ: {sourceWidth}x{sourceHeight}";
            return false;
        }

        // Sàn 2: một ứng viên nhỏ hơn thế không có nghĩa nén, và nhiều bộ giải mã từ chối.
        var height = Math.Clamp(requestedHeight, 2, sourceHeight);
        var heightEven = MakeEven(height);

        // Giữ tỉ lệ khung hình nguồn, rồi kẹp để không vượt bề rộng nguồn.
        var scaledWidth = (int)Math.Round(
            (double)sourceWidth * heightEven / sourceHeight, MidpointRounding.AwayFromZero);
        var width = Math.Clamp(MakeEven(scaledWidth), 2, MakeEven(sourceWidth));

        var fps = sourceFps > 0 ? sourceFps : 0;

        target = new EncodeTarget(width, heightEven, fps);

        if (width % 2 != 0 || heightEven % 2 != 0)
        {
            failure = $"kích thước phải chẵn: {width}x{heightEven}";
            return false;
        }

        if (width > sourceWidth || heightEven > sourceHeight)
        {
            failure = $"không được phóng to: yêu cầu {requestedWidth}x{requestedHeight}, nguồn {sourceWidth}x{sourceHeight}";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}

/// <summary>
/// Chuỗi biến đổi dùng chung cho cả pilot lẫn encode toàn tệp.
///
/// <para>Điểm mấu chốt của kiến trúc giai đoạn 4: <b>pilot và encode toàn tệp phải dùng
/// cùng một phép biến đổi</b>. Nếu pilot encode ở 1920×1080 còn bản đầy ra ở 1280×720,
/// thì phép đo đang đo một thứ khác với thứ sẽ giao cho người dùng — ứng viên có thể đạt
/// VMAF rồi ra tệp hỏng, hoặc ngược lại. Đó là lý do phép biến đổi nằm ở đây, gọi từ hai
/// nơi, thay vì mỗi nơi viết một bản.</para>
///
/// <para>Chuỗi rỗng nghĩa là giữ nguyên cả bề rộng lẫn số khung hình, nên không dựng
/// <c>-vf</c> và không tốn công gì.</para>
/// </summary>
public static class EncodeTransform
{
    /// <summary>
    /// Dựng <c>-vf</c> cho một kích thước đích đã kiểm tra.
    /// </summary>
    /// <param name="target">Kích thước đích; bằng kích thước nguồn thì không cần filter.</param>
    /// <param name="sourceWidth">Bề rộng nguồn, dùng để biết có cần thu nhỏ không.</param>
    /// <param name="sourceHeight">Chiều cao nguồn.</param>
    /// <param name="sourceFps">
    /// Số khung hình nguồn. Bắt buộc phải có: nếu không, mọi ứng viên đều bị ép về `fps=`
    /// kể cả khi bằng nguồn — và ép sai một phần nghìn fps làm ffmpeg lặp hoặc bỏ khung hình.
    /// </param>
    public static string BuildFilter(
        EncodeTarget target, int sourceWidth, int sourceHeight, double sourceFps = 0)
    {
        var parts = new List<string>(2);

        // CHỈ dựng `fps=` khi tỉ lệ thật sự khác nguồn.
        //
        // Bỏ điều kiện "chưa hỗ trợ đổi fps" ở đây là một lỗi đã mắc phải: hàm cứ dựng
        // `fps=23.98` cho một nguồn 23,976 fps. ffmpeg phải chuyển tỉ lệ khung hình để làm
        // việc đó, tức lặp hoặc bỏ khung, và kết quả là ứng viên lệch trục thời gian với
        // tham chiếu: VMAF rơi từ 94 xuống 40, ứng viên tốt bị loại oan.
        //
        // Ngưỡng SoFpsEpsilon rộng hơn nhiều sai số làm tròn: 23,976 và 23,98 là cùng một
        // tần số, ép chúng khác nhau là tự tạo ra lỗi.
        if (target.Fps > 0 && Math.Abs(target.Fps - sourceFps) > FpsEpsilon)
        {
            parts.Add($"fps={target.Fps.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (target.Width < sourceWidth || target.Height < sourceHeight)
        {
            // Chiều cao cố định, bề rộng tính theo để giữ tỉ lệ; `-2` để ffmpeg tự làm tròn
            // về số chẵn theo quy tắc của định dạng pixel.
            parts.Add($"scale={target.Width.ToString(CultureInfo.InvariantCulture)}:-2");
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// Sai số tối đa coi như bằng nhau khi so tần số khung hình.
    ///
    /// <para>Nguồn 23,976 fps là số thực, và nhiều tệp khai báo 23,98. Ép một tệp 23,976 thành
    /// 23,98 buộc ffmpeg lặp/bỏ khung hình để bù chênh lệch 0,004 — thứ vô nghĩa về hình
    /// ảnh nhưng đủ để phá đồng bộ thời gian với tham chiếu.</para>
    /// </summary>
    public const double FpsEpsilon = 0.05;

    /// <summary>
    /// Các thẻ encoder, theo đúng một nguồn sự thật là <see cref="EncoderConfiguration"/>.
    /// </summary>
    public static IReadOnlyList<string> EncoderArguments(EncoderConfiguration configuration)
    {
        if (!configuration.Validate(out var failure))
        {
            throw new ArgumentException(
                $"Cấu hình encoder không hợp lệ: {failure}", nameof(configuration));
        }

        return configuration.ToArguments();
    }

    /// <summary>
    /// Dựng lệnh encode một đoạn: cắt đoạn, áp phép biến đổi chung, rồi mã hoá.
    /// </summary>
    /// <remarks>
    /// <para>Đặt ở đây cùng <see cref="BuildFilter"/> vì lý do đã nêu: đây là <b>một</b> nơi
    /// quyết định hình ảnh được tạo ra như thế nào. Lượt B sẽ gọi đúng hàm này cho bản đầy
    /// đủ, nên pilot và bản cuối không thể lệch nhau do ai đó sửa một bên.</para>
    ///
    /// <para>Không mã hoá âm thanh ở đây vì đây là đường của clip thử nghiệm. Bản đầy đủ sẽ
    /// có bước âm thanh riêng ở tầng trên, không phải ở đây.</para>
    /// </remarks>
    public static IReadOnlyList<string> BuildSegmentArguments(
        EncoderConfiguration configuration,
        string filter,
        string sourcePath,
        string output,
        TimeWindow window)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

        // Tìm đoạn TRƯỚC khi đọc đầu vào: `-ss` trước `-i` là seek nhanh, không cần giải mã
        // phần đầu tệp. Đặt sau `-i` thì ffmpeg phải đọc và bỏ qua từng khung, tốn thời gian
        // tuyến tính theo mốc bắt đầu.
        args.AddRange(["-ss", window.StartText, "-t", window.LengthText, "-i", sourcePath]);

        args.AddRange(["-map", "0:v:0?", "-an", "-sn", "-dn"]);

        if (filter.Length > 0)
        {
            args.AddRange(["-vf", filter]);
        }

        args.AddRange(EncoderArguments(configuration));
        args.AddRange(["-movflags", "+faststart", "-y", output]);

        return args;
    }
}
