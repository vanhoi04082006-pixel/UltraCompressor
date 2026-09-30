using UltraCompressor.Core.Encoders;

namespace UltraCompressor.Core.Planning;

/// <summary>
/// Một phương án nén, chưa phải quyết định.
///
/// <para>Giai đoạn này <b>chỉ sinh</b> phương án. Chưa encode thử, chưa đo chất lượng, chưa
/// biết phương án nào thắng. Việc đó thuộc giai đoạn sau.</para>
///
/// <para>Kiểu gốc là lớp trừu tượng, không phải một record đóng: nhánh "giữ nguyên bản
/// gốc" chưa được đưa vào lúc này, nhưng phải là một ứng viên ngang hàng chứ không phải
/// một ngoại lệ ở cuối đường ống. Thiết kế sẵn ở đây để giai đoạn sau chỉ cần thêm một
/// loại, không phải xây lại cả chuỗi lập kế hoạch và tìm kiếm.</para>
/// </summary>
public abstract record CompressionCandidate(string Id);

/// <summary>
/// Nhánh "không nén". Không có số đo chất lượng, không có thời gian encode.
///
/// <para>Chưa dùng để ra quyết định ở giai đoạn này. Nhưng khi nó được dùng, phải biểu
/// diễn đúng như nó là: kích thước bằng byte thật của nguồn, tổn thất chất lượng bằng 0,
/// chi phí encode bằng 0 — chứ không phải "encode nguồn với chính nó rồi gắn một con số
/// VMAF vào", vì cách đó tự tạo ra dữ liệu giả.</para>
/// </summary>
public sealed record OriginalCandidate(string Id, long SourceBytes) : CompressionCandidate(Id);

/// <summary>
/// Vai trò của điểm này trong <b>quá trình tìm kiếm</b>.
///
/// <para>Chỉ mô tả vai trò tìm kiếm, <b>không</b> mô tả nhánh. Việc một ứng viên có thuộc
/// nhánh giữ nguyên độ phân giải nguồn hay không đã nằm ở <c>BranchId</c> và kích thước;
/// trộn hai ý nghĩa đó vào một kiểu duy nhất dễ sinh ra mệnh đề đúng vô nghĩa, và
/// giai đoạn tìm kiếm sẽ không biết đâu là điểm dò thô.</para>
/// </summary>
public enum CandidateOrigin
{
    /// <summary>Điểm đầu tiên của nhánh, dùng để dò vùng khả thi trước khi tốn công khoanh biên.</summary>
    CoarseProbe,

    /// <summary>Điểm bổ sung trong cùng nhánh, dùng để khoanh biên quanh điểm đã dò.</summary>
    QualityAnchor,
}

/// <summary>
/// Một ứng viên encode cụ thể, mô tả đầy đủ để chạy được lệnh ffmpeg.
///
/// <para>Bất biến và không tự chứa kết quả đo. Không có trường "VMAF dự kiến" vì bất cứ
/// con số nào chưa đo đều là bịa, và một con số bịa đặt vào đúng chỗ mà quyết định sẽ đọc
/// là nguy hiểm hơn là thiếu hẳn.</para>
/// </summary>
public sealed record VideoEncodeCandidate(string Id) : CompressionCandidate(Id)
{
    public required VideoCodec Codec { get; init; }

    /// <summary>Tên encoder cho ffmpeg, lấy từ miền tìm kiếm của codec.</summary>
    public required string EncoderName { get; init; }

    /// <summary>
    /// Tuỳ chọn chất lượng của <b>đúng codec này</b>: thang số, công tắc và miền giá trị
    /// đều do miền tìm kiếm quyết định.
    ///
    /// <para>Kiểu này thay cho một <c>double</c> trần. X264 <c>-crf 30</c>, libaom
    /// <c>-crf 30</c> và SVT-AV1 <c>-qp 30</c> là ba mức chất lượng không liên quan; để
    /// chúng chung một kiểu số là để mời người kế tiếp so chúng như thể cùng thang.</para>
    /// </summary>
    public required QualityOption Quality { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Luôn bằng FPS nguồn ở giai đoạn này: không tăng, không giảm.</summary>
    public required double Fps { get; init; }

    /// <summary>
    /// Tuỳ chọn tốc độ, do ngân sách tính toán quyết định. Kiểu đã biết công tắc, nên không
    /// thể sinh ra <c>-preset cpu-used=8</c>.
    /// </summary>
    public required SpeedOption Speed { get; init; }

    public required string PixelFormat { get; init; }

    public string? Tune { get; init; }

    public required CandidateOrigin Origin { get; init; }

    /// <summary>
    /// Nhánh cha của ứng viên: cùng codec và cùng kích thước thì cùng nhánh. Giai đoạn tìm
    /// kiếm cần điều này để dò thô rồi khoanh biên, thay vì thử mọi tổ hợp.
    /// </summary>
    public required string BranchId { get; init; }

    /// <summary>Vị trí của điểm này trong nhánh, từ 0 (chất lượng cao nhất) trở đi.</summary>
    public required int PointIndex { get; init; }

    /// <summary>Số điểm chất lượng trong nhánh.</summary>
    public required int PointCount { get; init; }

    /// <summary>Câu giải thích tiếng Việt, đủ để trả lời "vì sao thử phương án này".</summary>
    public required string Reason { get; init; }

    /// <summary>Giá trị chất lượng dạng số, chỉ để hiển thị và gom nhóm. So số này giữa hai codec khác nhau là vô nghĩa.</summary>
    public double QualityValue => Quality.Numeric;

    /// <summary>Ép ứng viên thành cấu hình encode có thể dựng lệnh.</summary>
    public EncoderConfiguration ToEncoderConfiguration() => new()
    {
        EncoderName = EncoderName,
        Quality = Quality,
        Speed = Speed,
        Tune = Tune,
        PixelFormat = PixelFormat,
    };

    /// <summary>Kiểm tra ứng viên có dựng được lệnh hợp lệ không. Gọi ở ranh giới.</summary>
    public bool Validate(out string failure) => ToEncoderConfiguration().Validate(out failure);
}
