namespace UltraCompressor.Core;

/// <summary>
/// Vị trí tệp dữ liệu.
///
/// Công cụ ngoài (ffmpeg, gifsicle, gswin64c) nằm cạnh tệp thực thi vì chúng được đóng gói
/// kèm. Còn cấu hình / phiên / nhật ký đặt ở <c>%LOCALAPPDATA%</c> vì thư mục cài đặt có thể
/// không ghi được (Program Files).
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    public static string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UltraCompressor");

    public static string WebRoot { get; set; } = Path.Combine(BaseDirectory, "wwwroot");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string SessionFile => Path.Combine(DataDirectory, "session.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string TempRoot => Path.Combine(DataDirectory, "tmp");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
