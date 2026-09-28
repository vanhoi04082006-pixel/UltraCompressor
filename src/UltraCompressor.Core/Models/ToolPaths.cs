namespace UltraCompressor.Core.Models;

/// <summary>Đường dẫn tới các công cụ ngoài. Rỗng = sẽ tìm theo quy tắc mặc định.</summary>
public sealed class ToolPaths
{
    public string? FFmpeg { get; set; }

    public string? FFplay { get; set; }

    public string? Gifsicle { get; set; }

    public string? Ghostscript { get; set; }
}
