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
    /// Kết quả đã nén xong và đang chờ người dùng duyệt, tệp kết quả vẫn còn trên đĩa.
    /// Bấm “Duyệt” sẽ thay thế tệp gốc bằng tệp này, không nén lại.
    /// </summary>
    public bool IsPredicted { get; set; }

    public SkipReason Skip { get; set; } = SkipReason.None;

    /// <summary>Bản sao lưu của tệp gốc, dùng để hoàn tác.</summary>
    public string? BackupPath { get; set; }

    /// <summary>
    /// Kết quả nén ở chế độ thử: đã nén thật và <b>đang giữ tệp đó trên đĩa</b> để người
    /// dùng mở so sánh, phát cả hai bản, rồi mới bấm “Duyệt”.
    ///
    /// <para>Trước đây chế độ thử nén xong là xoá tệp ngay, nên không còn gì để so sánh —
    /// hộp so sánh báo “chưa có bản nén”, và bấm “Duyệt” phải nén lại từ đầu. Đúng cái
    /// người dùng cần là: nén xong thấy kết quả ngay, so sánh thấy ưng thì mới duyệt.</para>
    ///
    /// <para>Tệp nằm trong thư mục tạm nên không lẫn vào thư mục nguồn, và được dọn khi
    /// đóng ứng dụng, xoá job, hoặc quét tệp tạm cũ lúc khởi động.</para>
    /// </summary>
    public string? StagedPath { get; set; }

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

    /// <summary>
    /// Điểm VMAF phân vị 5%. Nhìn <see cref="QualityScore"/> một mình thì vài cảnh hỏng
    /// bị che bởi phần lớn khung đẹp — số liệu thật trên clip người dùng cho thấy
    /// mean 88,36 đi kèm min 83,85.
    /// </summary>
    public double? QualityP5 { get; set; }

    /// <summary>
    /// Mã lý do ổn định cho lưới an toàn: <c>ACCEPTED</c>, <c>OUTPUT_LARGER_THAN_SOURCE</c>,
    /// <c>INSUFFICIENT_SIZE_SAVING</c>, <c>QUALITY_FLOOR_NOT_MET</c>…
    ///
    /// <para>Tách khỏi <see cref="Message"/> vì thông báo tiếng Việt sẽ đổi theo thời gian
    /// còn mã này phải giữ nguyên để gom số liệu. Ví dụ: đo được bao nhiêu tệp rơi vào
    /// <c>QUALITY_FLOOR_NOT_MET</c> sau khi nâng ngưỡng.</para>
    /// </summary>
    public string? DecisionReason { get; set; }

    /// <summary>Thời lượng media (giây), lấy từ ffmpeg -i. Null = chưa biết (ảnh, PDF).</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Nguồn có luồng âm thanh không. Null = chưa probe.</summary>
    public bool? HasAudio { get; set; }

    public int? SourceWidth { get; set; }

    public int? SourceHeight { get; set; }

    /// <summary>Bitrate nguồn (kbit/s) nếu đã probe.</summary>
    public double? SourceBitrateKbps { get; set; }

    /// <summary>
    /// Tham số đã chọn cho tệp này, bằng tiếng Việt — ví dụ
    /// <c>CRF 26 (medium) · 1920px → 1280px · tiếng 128k — nguồn đã rất nén (0.05 bit/px/khung)</c>.
    ///
    /// Người dùng chỉ chọn mức mục tiêu, không chọn tham số. Trường này là thứ trả lời
    /// "vì sao tệp này bị nén như vậy" mà không bắt họ phải mở nhật ký.
    /// </summary>
    public string? Plan { get; set; }

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
