namespace UltraCompressor.Core.Planning;

/// <summary>
/// Ngân sách tính toán: bao nhiêu tài nguyên hệ thống được phép dùng để <i>tìm</i> một
/// cách nén tốt.
///
/// <para>Đây là trục độc lập với <see cref="Models.CompressionLevel"/>, và việc tách hai
/// trục này là điều kiện tiên quyết cho kiến trúc đúng. Rất nhiều công cụ nhầm
/// "nhanh" thành "chất lượng thấp", rồi gắn preset encoder vào mức nén. Kết quả là
/// người dùng chọn "Nhẹ" thì nhận tệp tệ, và không có cách nào vừa nhanh vừa đẹp.</para>
///
/// <para>Ràng buộc bất di bất dịch: <b>mọi ngân sách đều dùng chung một
/// <c>QualityPolicy</c></b>. <c>Balanced + Fast</c> và <c>Balanced + Thorough</c> phải có
/// cùng ngưỡng VMAF, chỉ khác ở chỗ cái sau được phép tìm kỹ hơn. Nếu một ngân sách hạ
/// được ngưỡng thì kiến trúc đã hỏng, và có test chặn.</para>
/// </summary>
public enum ComputeBudget
{
    /// <summary>Ít ứng viên, preset nhanh, giảm độ phân giải không suy ra gì thêm.</summary>
    Fast,

    /// <summary>Mặc định: đủ ứng viên để tìm được điểm thoả ngưỡng mà không tốn nhiều.</summary>
    Normal,

    /// <summary>Nhiều ứng viên, preset chậm hơn, thử nhiều nhánh độ phân giải hơn.</summary>
    Thorough,
}
