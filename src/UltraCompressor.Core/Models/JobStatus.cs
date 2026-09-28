namespace UltraCompressor.Core.Models;

/// <summary>Vòng đời của một job nén thư mục.</summary>
public enum JobStatus
{
    /// <summary>Đã thêm vào hàng chờ, chưa chạy.</summary>
    Waiting = 0,

    /// <summary>Đang nén.</summary>
    Running,

    /// <summary>Tạm dừng (toàn cục hoặc riêng job).</summary>
    Paused,

    /// <summary>Đã nén xong, chờ người dùng duyệt kết quả. Tệp gốc vẫn còn nguyên.</summary>
    PendingReview,

    /// <summary>Đã duyệt, đã ghi đè tệp gốc, backup đã được dọn theo chính sách.</summary>
    Committed,

    /// <summary>Chạy nhưng có lỗi chặn tiến độ.</summary>
    Failed,

    /// <summary>Người dùng hủy.</summary>
    Cancelled,
}
