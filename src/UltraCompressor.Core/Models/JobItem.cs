using System.Text.Json.Serialization;

namespace UltraCompressor.Core.Models;

/// <summary>Một tệp trong job. POCO thuần để serialize thẳng sang UI.</summary>
public sealed class JobItem
{
    public string FilePath { get; set; } = string.Empty;

    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    [JsonIgnore]
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;

    public MediaKind Kind { get; set; }

    public long OldSize { get; set; }

    public long NewSize { get; set; }

    /// <summary>Tệp đã xử lý xong (thành công hoặc bị bỏ qua).</summary>
    public bool IsComplete { get; set; }

    /// <summary>Kết quả đã được ghi đè lên tệp gốc.</summary>
    public bool IsApplied { get; set; }

    /// <summary>
    /// Kết quả chỉ là dự đoán ở chế độ thử: đã nén thật để đo nhưng đã xoá kết quả và
    /// chưa đụng vào tệp gốc. Bấm “Duyệt” sẽ nén lại và mới ghi đè.
    /// </summary>
    public bool IsPredicted { get; set; }

    public SkipReason Skip { get; set; } = SkipReason.None;

    /// <summary>Bản sao lưu của tệp gốc, dùng để hoàn tác.</summary>
    public string? BackupPath { get; set; }

    /// <summary>Đường dẫn tệp kết quả khi chạy chế độ xuất ra thư mục khác.</summary>
    public string? OutputPath { get; set; }

    [JsonIgnore]
    public int Percent { get; set; }

    [JsonIgnore]
    public bool IsProcessing { get; set; }

    /// <summary>Thời lượng nén tính bằng giây.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Thông báo lỗi / ghi chú để hiển thị và ghi log.</summary>
    public string? Message { get; set; }

    /// <summary>Điểm chất lượng VMAF nếu đã chạy đo. Null = chưa đo.</summary>
    public double? QualityScore { get; set; }

    /// <summary>Thời lượng media (giây), lấy từ ffmpeg -i. Null = chưa biết (ảnh, PDF).</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Nguồn có luồng âm thanh không. Null = chưa probe.</summary>
    public bool? HasAudio { get; set; }

    public int? SourceWidth { get; set; }

    public int? SourceHeight { get; set; }

    /// <summary>Bitrate nguồn (kbit/s) nếu đã probe.</summary>
    public double? SourceBitrateKbps { get; set; }

    /// <summary>Tệp nén thành công (không bị bỏ qua, không lỗi).</summary>
    [JsonIgnore]
    public bool Succeeded => IsComplete && Skip == SkipReason.None;

    /// <summary>
    /// Byte đã tiết kiệm. Tính cả khi kết quả chưa được ghi đè (chế độ thử),
    /// vì người dùng vẫn cần thấy mức tiết kiệm trước khi duyệt.
    /// </summary>
    [JsonIgnore]
    public long SavedBytes => Succeeded && OldSize > NewSize ? OldSize - NewSize : 0;

    [JsonIgnore]
    public double SavedPercent => OldSize > 0 ? (double)SavedBytes * 100.0 / OldSize : 0.0;
}
