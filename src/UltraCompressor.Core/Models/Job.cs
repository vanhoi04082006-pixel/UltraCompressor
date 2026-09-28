using System.Text.Json.Serialization;

namespace UltraCompressor.Core.Models;

/// <summary>Một job = nén toàn bộ media trong một thư mục (kể cả thư mục con).</summary>
public sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FolderPath { get; set; } = string.Empty;

    [JsonIgnore]
    public string FolderName
    {
        get
        {
            try
            {
                return new DirectoryInfo(FolderPath).Name;
            }
            catch
            {
                return FolderPath;
            }
        }
    }

    public JobStatus Status { get; set; } = JobStatus.Waiting;

    /// <summary>Mức nén đã dùng cho lần chạy này — giữ nguyên khi tạm dừng rồi chạy tiếp.</summary>
    public CompressionLevel Level { get; set; } = CompressionLevel.Balanced;

    /// <summary>Chạy thử: nén vào thư mục tạm, không ghi đè tệp gốc.</summary>
    public bool DryRun { get; set; }

    /// <summary>Thư mục đích khi xuất kết quả ra nơi khác. Null = ghi đè tệp gốc.</summary>
    public string? OutputFolder { get; set; }

    public List<JobItem> Items { get; set; } = [];

    [JsonIgnore]
    public long TotalFiles => Items.Count;

    [JsonIgnore]
    public long ProcessedCount => Items.Count(i => i.IsComplete);

    [JsonIgnore]
    public long BytesOriginal => Items.Sum(i => i.OldSize);

    [JsonIgnore]
    public long BytesSaved => Items.Sum(i => i.SavedBytes);

    /// <summary>Tiến độ 0–100 theo số tệp đã xong.</summary>
    [JsonIgnore]
    public int Progress => Items.Count == 0 ? 0 : (int)((long)ProcessedCount * 100L / Items.Count);

    /// <summary>Giây còn lại ước tính, âm nghĩa = không xác định.</summary>
    public double EtaSeconds { get; set; } = -1;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Lỗi chặn cả job, nếu có.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>True khi job đã được duyệt và ghi đè lên tệp gốc.</summary>
    public bool Committed { get; set; }
}
