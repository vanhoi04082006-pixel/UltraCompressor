namespace UltraCompressor.Core.Models;

/// <summary>Lý do một tệp không bị thay thế. Luôn có giá trị cụ thể, không có "im lặng".</summary>
public enum SkipReason
{
    None = 0,

    /// <summary>Tệp không còn tồn tại trên đĩa.</summary>
    FileMissing,

    /// <summary>Không thuộc các định dạng được hỗ trợ.</summary>
    UnsupportedFormat,

    /// <summary>Bị bộ lọc loại trừ cấu hình (tên, kích thước, thư mục).</summary>
    ExcludedByFilter,

    /// <summary>Nén xong nhưng kết quả không nhỏ hơn bản gốc.</summary>
    NoSizeGain,

    /// <summary>Kết quả nhỏ hơn nhưng dưới ngưỡng tiết kiệm tối thiểu.</summary>
    BelowMinSaving,

    /// <summary>Công cụ cần thiết không có hoặc không chạy được (Ghostscript, gifsicle...).</summary>
    MissingTool,

    /// <summary>Tiến trình nén trả về mã lỗi.</summary>
    ProcessFailed,

    /// <summary>Bị hủy giữa chừng.</summary>
    Cancelled,

    /// <summary>Lỗi I/O hoặc lỗi khác — xem <see cref="JobItem.Message"/>.</summary>
    Error,
}
