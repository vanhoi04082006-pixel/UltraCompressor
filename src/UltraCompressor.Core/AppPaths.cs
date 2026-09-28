using System.IO;

namespace UltraCompressor.Core;

/// <summary>
/// Vị trí tệp của ứng dụng.
///
/// Yêu cầu: <b>mọi thứ liên quan tới dự án phải nằm trong duy nhất thư mục dự án</b>,
/// không rải ở ổ đĩa khác. Trước đây cấu hình / phiên / nhật ký / profile WebView2 nằm ở
/// <c>%LOCALAPPDATA%\UltraCompressor</c> — trùng đúng thư mục cài, nên một lần
/// <c>setup.ps1</c> là ổ C: phình lên 130 MB (ffmpeg.exe 95 MB + profile WebView2 32 MB)
/// mà người dùng không hề biết. Nay tất cả nằm dưới <see cref="DataDirectory"/>.
///
/// Ưu tiên tìm thư mục gốc, theo thứ tự:
///  1. biến môi trường <c>UC_DATA_DIR</c> — chỉ định thẳng thư mục dữ liệu;
///  2. biến môi trường <c>UC_ROOT</c> — chỉ định thư mục gốc dự án;
///  3. thư mục <c>app\</c> cạnh tệp thực thi (khi <c>setup.ps1</c> cài vào dự án);
///  4. <c>src\</c> hoặc <c>publish\</c> cạnh tệp thực thi (chạy thử bằng <c>dotnet run</c>);
///  5. từ tệp thực thi đi lên tối đa 8 cấp, dừng ở thư mục nào có <c>UltraCompressor.slnx</c>;
///  6. cuối cùng mới rơi về <c>AppContext.BaseDirectory</c>.
///
/// Thứ tự này đảm bảo chạy thử trong <c>bin\Debug\</c> và chạy bản cài vẫn ghi cùng một
/// nơi — không sinh ra hai bộ dữ liệu cạnh tranh nhau.
/// </summary>
public static class AppPaths
{
    /// <summary>Tên thư mục dữ liệu, đặt ngay dưới thư mục gốc dự án.</summary>
    public const string DataFolderName = "data";

    private static string? _root;

    /// <summary>
    /// Thư mục gốc dự án. Mọi thứ khác đều dẫn xuất từ đây.
    /// </summary>
    public static string RootDirectory
    {
        get => _root ??= LocateRoot();
        set => _root = value;
    }

    /// <summary>Thư mục chứa tệp thực thi.</summary>
    public static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// Nơi lưu cấu hình, phiên, nhật ký, tệp tạm và profile WebView2.
    /// Không bao giờ trùng <see cref="BaseDirectory"/> — trùng là mất dữ liệu mỗi lần cài lại.
    /// </summary>
    public static string DataDirectory { get; set; } = Path.Combine(RootDirectory, DataFolderName);

    /// <summary>Thư mục giao diện web đi kèm tệp thực thi.</summary>
    public static string WebRoot { get; set; } = Path.Combine(BaseDirectory, "wwwroot");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string SessionFile => Path.Combine(DataDirectory, "session.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string TempRoot => Path.Combine(DataDirectory, "tmp");

    /// <summary>
    /// Profile WebView2. Nằm trong thư mục dữ liệu chứ không phải thư mục cài: nó phình
    /// lên vài chục MB và cần xoá được bằng một lệnh dọn dữ liệu.
    /// </summary>
    public static string WebViewProfileDirectory => Path.Combine(DataDirectory, "webview");

    /// <summary>
    /// Nơi cài đặt cũ ở ổ C:. Chỉ dùng để phát hiện và dọn khi người dùng đã chuyển hẳn
    /// sang thư mục dự án — không đọc hay ghi vào đó nữa.
    /// </summary>
    public static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltraCompressor");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string LocateRoot()
    {
        var explicitData = Environment.GetEnvironmentVariable("UC_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitData))
        {
            return Path.GetFullPath(explicitData);
        }

        var explicitRoot = Environment.GetEnvironmentVariable("UC_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var baseDir = Path.GetFullPath(BaseDirectory);

        // Chạy thử: ...\src\UltraCompressor.App\bin\Debug\net10.0-windows\ -> dự án ở trên.
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UltraCompressor.slnx")))
            {
                return dir.FullName;
            }
        }

        // Bản cài do setup.ps1 đặt cạnh dự án: <root>\app\UltraCompressor.exe.
        foreach (var relative in new[] { "app", "publish" })
        {
            var candidate = Path.Combine(baseDir, relative);
            if (File.Exists(Path.Combine(candidate, "wwwroot", "index.html")))
            {
                return baseDir;
            }
        }

        // Chạy từ gốc dự án (ví dụ `dotnet run --project ...` với BaseDirectory khác).
        foreach (var relative in new[] { "src", "publish", "app" })
        {
            var candidate = Path.Combine(baseDir, relative, "wwwroot", "index.html");
            if (File.Exists(candidate))
            {
                return baseDir;
            }
        }

        return baseDir;
    }
}
