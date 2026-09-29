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

    /// <summary>
    /// Codec video đầu ra: <c>h264</c> (mặc định) hoặc <c>hevc</c>.
    ///
    /// <para><b>Vì sao mặc định là H.264, không tự chọn theo nội dung.</b> Đo thật trên hai
    /// tệp cùng codec nguồn, cùng độ phân giải, trên cùng một máy cho kết quả ngược nhau:</para>
    ///
    /// <list type="bullet">
    /// <item>Quay màn hình: x264 CRF 28 → 0,650 MB; x265 CRF 30 → 0,652 MB. Cùng dung
    /// lượng, SSIM còn thấp hơn — HEVC tốn gấp 5,3 lần thời gian mà không thu được byte
    /// nào.</item>
    /// <item>Anime: x264 CRF 28 → 6,962 MB; x265 CRF 32 → 1,706 MB. Nhỏ hơn 4,1 lần, SSIM
    /// chỉ giảm 0,003.</item>
    /// </list>
    ///
    /// <para>HEVC đáng dùng hay không phụ thuộc nội dung, mà tín hiệu phân biệt được đo trên
    /// hai tệp thì không đủ tin để tự bật. Tự bật là tự quyết thay người dùng rồi đoán
    /// sai — nên để họ chọn, và nói rõ cái giá.</para>
    /// </summary>
    public string VideoCodec { get; set; } = "h264";
}
