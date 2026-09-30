namespace UltraCompressor.Core.Planning;

/// <summary>
/// Những encoder ffmpeg thực sự có trong bản dựng đang chạy.
///
/// <para>Đây là dữ liệu <b>đo</b>, không phải giả định. Bộ lập kế hoạch không được sinh
/// ứng viên mà ffmpeg không encode được: lệnh sẽ chết ngay và người dùng chỉ thấy job
/// lỗi chung chung, không biết là do thiếu codec hay do chất lượng.</para>
/// </summary>
public sealed record EncoderCapabilities
{
    /// <summary>Không có gì cả: mọi ứng viên đều bị loại. Đây là trạng thái an toàn khi chưa dò được.</summary>
    public static EncoderCapabilities None { get; } = new() { AvailableEncoders = new HashSet<string>(StringComparer.Ordinal) };

    public required IReadOnlySet<string> AvailableEncoders { get; init; }

    public bool Has(string encoderName) => AvailableEncoders.Contains(encoderName);

    /// <summary>Tạo từ danh sách tên encoder đọc được từ <c>ffmpeg -encoders</c>.</summary>
    public static EncoderCapabilities FromEncoderNames(IEnumerable<string> names) =>
        new() { AvailableEncoders = new HashSet<string>(names, StringComparer.Ordinal) };

    /// <summary>Khả năng tối thiểu để có thể lập kế hoạch: luôn có H.264 trong mọi bản dựng ffmpeg.</summary>
    public static EncoderCapabilities Baseline { get; } = FromEncoderNames(["libx264"]);
}
