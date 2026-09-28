namespace UltraCompressor.Core.Toolchain;

public enum ToolKind
{
    FFmpeg,
    FFplay,
    Gifsicle,
    Ghostscript,
}

/// <summary>Kết quả kiểm tra một công cụ ngoài.</summary>
public enum ToolHealth
{
    /// <summary>Chưa kiểm tra.</summary>
    Unknown = 0,

    /// <summary>Có mặt và chạy thật được.</summary>
    Ok,

    /// <summary>Không tìm thấy tệp thực thi.</summary>
    Missing,

    /// <summary>
    /// Có mặt nhưng không chạy được (thiếu DLL, stub, sai kiến trúc...).
    /// Bản gốc gặp đúng trường hợp này với gswin64c.exe và báo lỗi im lặng.
    /// </summary>
    Broken,

    /// <summary>Có mặt nhưng không bắt buộc (ví dụ ffplay chỉ dùng để xem trước).</summary>
    Optional,
}

public sealed record ToolReport
{
    public required ToolKind Kind { get; init; }

    public required string DisplayName { get; init; }

    public required bool Required { get; init; }

    public string? Path { get; init; }

    public ToolHealth Health { get; init; } = ToolHealth.Unknown;

    public string? Version { get; init; }

    /// <summary>Giải thích cụ thể, hiển thị cho người dùng biết phải làm gì.</summary>
    public string? Message { get; init; }

    public bool IsUsable => Health is ToolHealth.Ok or ToolHealth.Optional;
}
