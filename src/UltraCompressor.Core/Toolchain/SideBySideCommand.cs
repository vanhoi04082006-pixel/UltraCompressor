using System.Globalization;

namespace UltraCompressor.Core.Toolchain;

/// <summary>
/// Lệnh ghép hai tệp video cạnh nhau để xem bằng một cửa sổ.
///
/// <para>Hai tiến trình nối với nhau bằng một pipe:</para>
/// <code>
/// ffmpeg ... -filter_complex hstack ... pipe:1   |   ffplay -i pipe:0
/// </code>
///
/// <para><b>Vì sao phải qua pipe, không phải gọi thẳng ffplay.</b> Kế hoạch ban đầu là
/// "một ffplay với <c>-f hstack</c>". Chạy thật cho thấy cả hai cách đều không dùng
/// được, và đây là các lý do cụ thể — đã kiểm chứng trên tệp thật của người dùng:</para>
/// <list type="bullet">
/// <item><c>-f hstack</c> → <c>Unknown input format: hstack</c>. hstack là <i>bộ lọc</i>,
/// không phải định dạng đầu vào.</item>
/// <item><c>-filter_complex</c> → <c>Option not found</c>. Đó là tuỳ chọn của ffmpeg CLI,
/// ffplay không có.</item>
/// <item><c>-vf "[0:v][1:v]hstack"</c> với hai <c>-i</c> → <c>provided as input filename,
/// but ... was already specified</c>. <b>ffplay chỉ nhận một tệp.</b> Đây là giới hạn
/// của chính ffplay, không phải lỗi cú pháp.</item>
/// </list>
///
/// <para>Cách chạy được là để ffmpeg CLI ghép rồi đổ sang ffplay. Nó vẫn đạt đủ ba điều
/// cần: một cửa sổ, hai video cạnh nhau, và đồng bộ tuyệt đối — vì chỉ có một đồng hồ duy
/// nhất trong một pipeline. Ngoài ra nó chạy ngoài tiến trình ứng dụng nên không tranh
/// CPU với ffmpeg đang nén, và không đụng tới sự kiện <c>WebResourceRequested</c> của
/// WebView2 vốn chạy trên UI thread.</para>
/// </summary>
public sealed record SideBySideCommand(
    string FfmpegPath,
    IReadOnlyList<string> FfmpegArguments,
    string FfplayPath,
    IReadOnlyList<string> FfplayArguments)
{
    /// <summary>Dựng lại lệnh để hiển thị và ghi nhật ký.</summary>
    public string Display()
    {
        var encode = new List<string>(FfmpegArguments.Count + 1) { Quote(FfmpegPath) };
        foreach (var a in FfmpegArguments) encode.Add(Quote(a));

        var play = new List<string>(FfplayArguments.Count + 1) { Quote(FfplayPath) };
        foreach (var a in FfplayArguments) play.Add(Quote(a));

        return $"{string.Join(' ', encode)}  |  {string.Join(' ', play)}";
    }

    private static string Quote(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}

/// <summary>Dựng lệnh so sánh cạnh nhau cho một cặp tệp.</summary>
public static class SideBySidePlayer
{
    /// <summary>Chiều cao tối đa cho mỗi bên, để cửa sổ không tràn màn hình.</summary>
    public const int MaxPaneHeight = 540;

    /// <summary>
    /// Dựng cặp lệnh ffmpeg + ffplay cho hai tệp đặt cạnh nhau.
    /// </summary>
    /// <param name="ffmpegPath">ffmpeg.exe — phía ghép.</param>
    /// <param name="ffplayPath">ffplay.exe — phía hiển thị.</param>
    /// <param name="leftPath">Bản gốc (bên trái).</param>
    /// <param name="rightPath">Bản đã nén (bên phải).</param>
    /// <param name="leftWidth">Bề rộng bản gốc, dùng để tính kích thước cửa sổ.</param>
    /// <param name="leftHeight">Chiều cao bản gốc, null nếu chưa biết.</param>
    /// <param name="rightWidth">Bề rộng bản nén.</param>
    /// <param name="rightHeight">Chiều cao bản nén, null nếu chưa biết.</param>
    /// <param name="title">Tiêu đề cửa sổ.</param>
    public static SideBySideCommand Build(
        string ffmpegPath,
        string ffplayPath,
        string leftPath,
        string rightPath,
        int? leftWidth,
        int? leftHeight,
        int? rightWidth,
        int? rightHeight,
        string? title = null)
    {
        // hstack đòi hai luồng cùng chiều cao. Thường đã bằng nhau, nhưng bản nén có thể
        // bị thu nhỏ theo kế hoạch nên phải cân trước. -2 trong scale = bề rộng tự tính
        // theo tỉ lệ, không méo khung hình.
        var sameHeight = leftHeight is { } lh && lh > 0
            && rightHeight is { } rh && rh > 0
            && lh == rh;

        var paneHeight = sameHeight ? Math.Min(leftHeight!.Value, MaxPaneHeight) : MaxPaneHeight;

        var graph = sameHeight
            ? "[0:v][1:v]hstack=inputs=2[v]"
            : $"[0:v]scale=-2:{paneHeight}[a];[1:v]scale=-2:{paneHeight}[b];[a][b]hstack=inputs=2[v]";

        var encode = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        encode.Add("-i");
        encode.Add(leftPath);
        encode.Add("-i");
        encode.Add(rightPath);
        encode.Add("-filter_complex");
        encode.Add(graph);

        // Mọi tham số phải nằm TRƯỚC dấu `-` ở cuối. Đặt sau thì ffmpeg coi là output
        // thứ hai và báo "Unable to choose an output format for 'pipe:1'".
        encode.Add("-map");
        encode.Add("[v]");
        encode.Add("-map");
        encode.Add("1:a?");
        encode.Add("-c:v");
        encode.Add("rawvideo");
        encode.Add("-c:a");
        encode.Add("aac");
        encode.Add("-b:a");
        encode.Add("128k");
        encode.Add("-f");
        encode.Add("nut");
        encode.Add("-");

        var play = new List<string> { "-autoexit", "-hide_banner", "-loglevel", "error" };
        if (!string.IsNullOrWhiteSpace(title))
        {
            play.Add("-window_title");
            play.Add(title);
        }

        play.Add("-i");
        play.Add("pipe:0");

        var aspect = AspectOf(leftWidth, leftHeight, rightWidth, rightHeight);
        play.Add("-x");
        play.Add(((int)Math.Round(paneHeight * aspect) * 2).ToString(CultureInfo.InvariantCulture));
        play.Add("-y");
        play.Add(paneHeight.ToString(CultureInfo.InvariantCulture));

        return new SideBySideCommand(ffmpegPath, encode, ffplayPath, play);
    }

    /// <summary>
    /// Tỉ lệ khung hình của một bên; không biết thì mặc định 16:9.
    ///
    /// <para>Chỉ dùng để đo kích thước cửa sổ cho vừa màn hình — sai tỉ lệ ở đây chỉ làm
    /// cửa sổ lệch, không méo hình. Méo hình đã bị chặn bằng cách cùng chiều cao và scale
    /// giữ tỉ lệ ở trên.</para>
    /// </summary>
    private static double AspectOf(int? width, int? height, int? otherWidth, int? otherHeight)
    {
        if (width is { } w && height is { } h && w > 0 && h > 0) return (double)w / h;
        if (otherWidth is { } ow && otherHeight is { } oh && ow > 0 && oh > 0) return (double)ow / oh;
        return 16.0 / 9.0;
    }
}
