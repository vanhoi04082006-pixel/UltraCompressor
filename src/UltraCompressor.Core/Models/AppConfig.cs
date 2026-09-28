namespace UltraCompressor.Core.Models;

/// <summary>Cấu hình ứng dụng, lưu ở <c>config.json</c> cạnh tệp thực thi.</summary>
public sealed class AppConfig
{
    public CompressionLevel Level { get; set; } = CompressionLevel.Balanced;

    /// <summary>
    /// Mặc định chạy thử: nén ra thư mục tạm, xem kết quả rồi mới ghi đè. Đây là hành vi mặc
    /// định an toàn nhất — người dùng luôn xem được kết quả trước khi mất bản gốc.
    /// </summary>
    public bool DryRunDefault { get; set; } = true;

    /// <summary>Số nén song song. 0 = tự động theo số nhân/cpu và RAM.</summary>
    public int MaxConcurrent { get; set; }

    public ToolPaths Tools { get; set; } = new();

    /// <summary>
    /// Phần trăm tiết kiệm tối thiểu để chấp nhận kết quả. Độc lập với mức nén —
    /// sửa bug B3 của bản gốc (bản gốc dùng ngưỡng tăng dần nên mức Mạnh lại khó đạt nhất).
    /// </summary>
    public double MinSavingPercent { get; set; } = 1.0;

    /// <summary>Bỏ qua tệp nhỏ hơn mức này. 0 = không bỏ qua.</summary>
    public long MinFileSizeBytes { get; set; }

    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Danh sách mẫu tên tệp cần bỏ qua, không phân biệt hoa thường. Ví dụ: <c>*.bak</c>, <c>Thumbs.db</c>.</summary>
    public List<string> ExcludePatterns { get; set; } = ["*.bak", "*.tmp", "Thumbs.db", ".DS_Store", "*~"];

    /// <summary>Số ngày giữ tệp <c>.bak</c> sau khi duyệt. 0 = giữ vĩnh viễn.</summary>
    public int KeepBackupDays { get; set; } = 30;

    /// <summary>Chạy đo chất lượng VMAF trên vài frame mẫu trước khi áp dụng kết quả.</summary>
    public bool MeasureQuality { get; set; }

    /// <summary>Cảnh báo nếu ổ đĩa không đủ chỗ cho bước ghi tạm.</summary>
    public bool CheckFreeSpace { get; set; } = true;

    /// <summary>Hệ số nhân số luồng nén so với số nhân logic.</summary>
    public double ConcurrencyScale { get; set; } = 0.5;

    /// <summary>Chỉ báo mức log ghi ra tệp: Error, Warning, Info, Debug.</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>Chủ đề giao diện: <c>system</c>, <c>light</c>, <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";
}
