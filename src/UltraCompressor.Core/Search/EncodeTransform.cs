using System.Globalization;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;

namespace UltraCompressor.Core.Search;

/// <summary>
/// Một yêu cầu thay đổi số khung hình, phải được nêu <b>tường minh</b>.
///
/// <para>Kiểu này tồn tại để sửa một lỗi đã mắc phải: trước đây ứng viên mang một con số
/// "số khung hình đích" mặc định bằng số khung hình nguồn, rồi tầng dựng lệnh phải tự so
/// sánh hai số thực để quyết định có cần `fps=` hay không. Chênh lệch 0,004 fps giữa
/// 23,976 và 23,98 là đủ để ffmpeg lặp hoặc bỏ khung hình, tức làm lệch trục thời gian
/// với tham chiếu và làm VMAF rơi từ 94 xuống 40.</para>
///
/// <para>Sửa ở đúng tầng: <b>không có mục tiêu FPS thì không có transform FPS.</b> Không
/// phải so sánh số gần đúng để đoán ý định. Ứng viên không đổi nhịp khung hình thì
/// <c>FpsChange</c> là <c>null</c>, và bộ lọc không bao giờ chứa <c>fps=</c>.</para>
/// </summary>
/// <param name="FrameRate">Tần số thẳng, dùng để so sánh và kiểm tra. Có thể là 0 khi
/// tần số chỉ được biểu diễn bằng phân số.</param>
/// <param name="Reason">Vì sao cần đổi, để log và để người review biết đây là quyết định.</param>
/// <param name="RationalText">
/// Tần số đúng dạng <b>lý phân số</b> mà ffmpeg hiểu, ví dụ <c>24000/1001</c>. Rỗng nghĩa
/// là phát <see cref="FrameRate"/> dạng thập phân — chỉ chấp nhận khi tần số <b>vốn dĩ</b>
/// là số thập phân hữu hạn như 12.0 hay 25.0.
/// </param>
public readonly record struct FpsChange(double FrameRate, string Reason, string? RationalText = null)
{
    /// <summary>
    /// Tần số đúng dạng <b>lý phân số</b> mà ffmpeg hiểu, ví dụ <c>24000/1001</c>.
    /// </summary>
    /// <remarks>
    /// <para>Đây là điểm quan trọng: <c>23.98</c> <b>không phải</b> 24000/1001 (= 23,976…),
    /// mà cũng không phải 24. Truyền số thập phân làm tròn là biến một tần số hữu tỷ lệ
    /// thành một tần số khác, và ffmpeg sẽ chuyển đổi khung hình theo đúng sai số đó.</para>
    ///
    /// <para>Vì vậy phân số phải đi vào <b>chuỗi</b>, không đi qua <c>double</c>:
    /// <c>24000/1001</c> không biểu diễn chính xác trong <c>double</c>, và bước
    /// <c>ToString("0.###")</c> sẽ cho ra <c>23.976</c> — vẫn sai. Người gọi muốn đổi sang
    /// tần số có phân số thì phải truyền chuỗi.</para>
    /// </remarks>
    public string FrameRateText => string.IsNullOrEmpty(RationalText)
        ? FrameRate.ToString("0.######", CultureInfo.InvariantCulture)
        : RationalText;
}

/// <summary>
/// Kích thước đích của một ứng viên encode, đã được kiểm tra hợp lệ.
///
/// <para>Kiểu này là <b>ranh giới</b> giữa "muốn gì" và "được phép làm gì". Nó chặn ở
/// đây ba điều mà nếu để lọt xuống tầng dựng lệnh sẽ hỏng theo kiểu rất khó nhận ra:
/// phóng to, méo tỉ lệ khung hình, và số khung hình lẻ khiến bộ mã hoá từ chối.</para>
/// </summary>
/// <param name="Width">Bề rộng đích, luôn chẵn và không vượt bề rộng nguồn.</param>
/// <param name="Height">Chiều cao đích, luôn chẵn và không vượt chiều cao nguồn.</param>
/// <param name="FpsChange">
/// Yêu cầu đổi nhịp khung hình, hoặc <c>null</c> — tức <b>giữ nguyên nhịp của nguồn</b>.
/// Mặc định là giữ nguyên, và đó là trường hợp duy nhất trong giai đoạn hiện tại.
/// </param>
public readonly record struct EncodeTarget(int Width, int Height, FpsChange? FpsChange = null)
{
    /// <summary>Có yêu cầu đổi nhịp khung hình không.</summary>
    public bool ChangesFrameRate => FpsChange is { } change && change.FrameRate > 0;

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
    ///
    /// <para><b>Nhịp khung hình luôn được giữ nguyên.</b> Hàm này không nhận tham số FPS
    /// đích, cố ý: muốn đổi thì phải đi qua <see cref="WithFrameRateChange"/>, nơi bắt
    /// buộc nêu lý do. Nhịp khung hình của nguồn là dữ liệu gốc; biến nó thành số thập
    /// phân rồi truyền ngược lại vào bộ lọc là cách làm mất dữ liệu.</para>
    /// </remarks>
    public static bool TryFromRequest(
        int sourceWidth,
        int sourceHeight,
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

        target = new EncodeTarget(width, heightEven);

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

    /// <summary>
    /// Tạo biến thể có yêu cầu đổi nhịp khung hình. Phải nêu lý do.
    /// </summary>
    /// <remarks>
    /// <para>Chưa có ứng viên nào dùng hàm này. Nó tồn tại để khi một ngày nào đó có, việc
    /// đổi nhịp phải là một quyết định <b>có tên và có lý do</b>, chứ không phải một con số
    /// mà ai đó thêm vào để "cho khớp".</para>
    /// </remarks>
    public EncodeTarget WithFrameRateChange(double frameRate, string reason) =>
        WithFrameRateChange(frameRate, reason, rationalText: null);

    /// <summary>
    /// Tạo biến thể có yêu cầu đổi nhịp khung hình bằng <b>phân số</b>, ví dụ
    /// <c>24000/1001</c>.
    /// </summary>
    /// <remarks>
    /// <para>Hàm này tồn tại vì <see cref="WithFrameRateChange(double, string)"/> không thể
    /// biểu diễn được tần số có phân số. Người gọi đã biết phân số chính xác (thường là từ
    /// metadata của nguồn) thì truyền nguyên văn, thay vì bắt họ đi vòng qua
    /// <c>double</c> rồi chấp nhận sai số làm tròn.</para>
    /// </remarks>
    public EncodeTarget WithFrameRateChange(double frameRate, string reason, string? rationalText)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Đổi nhịp khung hình phải nêu lý do.", nameof(reason));
        }

        if (frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRate), frameRate, "Tần số khung hình phải lớn 0.");
        }

        if (!string.IsNullOrWhiteSpace(rationalText) && !IsRational(rationalText))
        {
            // Chặn ngay ở đây: một chuỗi sai sẽ được đưa thẳng vào filter và ffmpeg báo lỗi
            // giữa chừng một lần encode dài, lúc đó khó truy nguyên nguyên nhân.
            throw new ArgumentException(
                $"'{rationalText}' không phải phân số dạng <tử số>/<mẫu số> mà ffmpeg hiểu.",
                nameof(rationalText));
        }

        return this with
        {
            FpsChange = new FpsChange(
                frameRate, reason, string.IsNullOrWhiteSpace(rationalText) ? null : rationalText),
        };
    }

    private static bool IsRational(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        return long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
            && num > 0
            && den > 0;
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}

/// <summary>
/// Phép biến đổi dùng chung cho cả pilot lẫn encode toàn tệp.
///
/// <para>Điểm mấu chốt của kiến trúc giai đoạn 4: <b>pilot và encode toàn tệp phải dùng
/// cùng một phép biến đổi</b>. Nếu pilot encode ở 1920×1080 còn bản đầy đủ ở 1280×720,
/// thì phép đo đang đo một thứ khác với thứ sẽ giao cho người dùng — ứng viên có thể đạt
/// VMAF rồi ra tệp hỏng, hoặc ngược lại. Đó là lý do phép biến đổi nằm ở đây, gọi từ hai
/// nơi, thay vì mỗi nơi viết một bản.</para>
///
/// <para>Chuỗi rỗng nghĩa là giữ nguyên cả bề rộng lẫn nhịp khung hình, nên không dựng
/// <c>-vf</c> và không tốn công gì.</para>
/// </summary>
public static class EncodeTransform
{
    /// <summary>
    /// Dựng <c>-vf</c> cho một kích thước đích đã kiểm tra.
    /// </summary>
    /// <param name="target">Kích thước đích.</param>
    /// <param name="sourceWidth">Bề rộng nguồn, dùng để biết có cần thu nhỏ không.</param>
    /// <param name="sourceHeight">Chiều cao nguồn.</param>
    public static string BuildFilter(EncodeTarget target, int sourceWidth, int sourceHeight)
    {
        var parts = new List<string>(2);

        // CHỈ dựng `fps=` khi ứng viên nêu tường minh rằng muốn đổi nhịp.
        //
        // Không có so sánh "gần bằng" nào ở đây, và đó là điểm: so sánh số thực từng gây
        // ra lỗi `fps=23.98` trên một nguồn 24000/1001, buộc ffmpeg lặp/bỏ khung hình và
        // làm VMAF rơi từ 94 xuống 40. Không có mục tiêu thì không có transform.
        if (target.ChangesFrameRate)
        {
            parts.Add($"fps={target.FpsChange!.Value.FrameRateText}");
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
    /// quyết định hình ảnh được tạo ra như thế nào. Lượt B gọi đúng hàm này cho cả bản đầy
    /// đủ, nên pilot và bản cuối không thể lệch nhau do ai đó sửa một bên.</para>
    ///
    /// <para>Không mã hoá âm thanh ở đây vì đây là đường của clip thử nghiệm. Bản đầy đủ có
    /// bước âm thanh riêng ở tầng trên, không phải ở đây.</para>
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

    /// <summary>
    /// Dựng lệnh encode toàn tệp, dùng <b>cùng</b> phép biến đổi với pilot.
    /// </summary>
    /// <remarks>
    /// <para>Điểm khác biệt duy nhất so với <see cref="BuildSegmentArguments"/> là không
    /// có <c>-ss</c>/<c>-t</c> và có phần âm thanh. Mọi thứ ảnh hưởng tới chất lượng hình
    /// ảnh — bộ lọc, encoder, tần số chất lượng, preset, định dạng pixel — đều đến từ cùng
    /// một nguồn.</para>
    /// </remarks>
    public static IReadOnlyList<string> BuildFullArguments(
        EncoderConfiguration configuration,
        string filter,
        string sourcePath,
        string output,
        int? audioBitrateKbps)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

        args.AddRange(["-i", sourcePath, "-map", "0:v:0?", "-map", "0:a:0?", "-map_metadata", "0"]);

        if (filter.Length > 0)
        {
            args.AddRange(["-vf", filter]);
        }

        args.AddRange(EncoderArguments(configuration));

        if (audioBitrateKbps is { } kbps and > 0)
        {
            args.AddRange(["-c:a", "aac", "-b:a", $"{kbps.ToString(CultureInfo.InvariantCulture)}k"]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(["-movflags", "+faststart", "-y", output]);

        return args;
    }
}
