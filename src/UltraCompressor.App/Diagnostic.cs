using System.Diagnostics;
using UltraCompressor.Core;
using UltraCompressor.Core.Diagnostics;

namespace UltraCompressor.App;

/// <summary>
/// Ghi chẩn đoán ra cả nhật ký tệp lẫn cửa sổ đầu ra của trình chạy thử.
///
/// Cần thiết vì lỗi JavaScript của giao diện web không hiện ra cửa sổ WPF — nếu không ghi
/// lại thì trang trắng là không có đường nào tìm ra nguyên nhân.
/// </summary>
public static class Diagnostic
{
    private static FileLogger? _logger;

    public static void Attach(FileLogger logger) => _logger = logger;

    public static void Log(string message)
    {
        Debug.WriteLine(message);
        _logger?.LogDebug("webview", message);
    }
}
