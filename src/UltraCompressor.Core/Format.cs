using System.Globalization;

namespace UltraCompressor.Core;

/// <summary>Định dạng số/thời gian cho UI. Giữ đúng cách hiển thị của bản gốc v12.</summary>
public static class Format
{
    /// <summary>Dung lượng theo lũy thừa 1024, luôn dùng dấu chấm thập phân.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return "0 bytes";
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
        if (bytes < 1048576) return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / 1024.0:F1} KB");
        if (bytes < 1073741824) return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / 1048576.0:F2} MB");
        return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / 1073741824.0:F2} GB");
    }

    /// <summary>Dung lượng có dấu, dùng cho biểu đồ (<c>+1.2 GB</c> / <c>-340 MB</c>).</summary>
    public static string SizeSigned(long bytes) => (bytes >= 0 ? "+" : "-") + Size(Math.Abs(bytes));

    public static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:F1}%");

    /// <summary>Thời lượng. Âm = chưa biết, trên 1 ngày = rút gọn.</summary>
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--:--";
        if (seconds > 86400) return "> 1 ngày";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }
}
