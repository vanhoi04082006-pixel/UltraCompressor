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
/// Nhánh "không nén": giữ nguyên bản gốc. Một ứng viên ngang hàng, không phải ngoại lệ.
///
/// <para><b>Không có điểm chất lượng, và đó là điểm cố ý.</b> ORIGINAL không đi qua VMAF với
/// chính nó: so 100 với 100 là so với bản thân nó, cho ra 100, rồi mọi ứng viên encode đều
/// "thua" một thứ không có nghĩa. Nếu ta gắn VMAF 100 vào đây để đưa vào mô hình Pareto
/// chung thì mô hình đó hỏng theo đúng cách nó trông hợp lý nhất — nên ORIGINAL có
/// <b>ngữ nghĩa so sánh riêng</b> (xem <c>OriginalComparison</c>), không đi qua Pareto.</para>
///
/// <para>Thay vào đó nó khai báo đúng những điều mà mình <b>thật sự</b> có:</para>
/// <list type="bullet">
/// <item><description>byte thật của nguồn, kích thước, nhịp khung hình, codec, tính chất
/// âm thanh — tất cả đều là sự thật đọc được, không phải suy ra;</description></item>
/// <item><description>tổn thất chất lượng theo thế hệ bằng 0, vì không re-encode;</description></item>
/// <item><description>chi phí encode toàn tệp bằng 0;</description></item>
/// <item><description>không tạo rủi ro tương thích mới: container, codec, profile đều giữ
/// nguyên nên không thể phát sinh thứ mà thiết bị cũ không phát được.</description></item>
/// </list>
///
/// <para>Còn lại là điều nó <b>không</b> có: ước lượng dung lượng (đã biết chính xác rồi),
/// và bất kỳ điểm chất lượng dự kiến nào. Điểm dự kiến chính là thứ nguy hiểm nhất, vì nó
/// được đặt vào đúng chỗ mà quyết định sẽ đọc.</para>
/// </remarks>
/// <param name="Id">Định danh ổn định, luôn là <see cref="StableId"/>.</param>
/// <param name="SourceBytes">Byte thật của tệp nguồn — không phải ước lượng.</param>
public sealed record OriginalCandidate(string Id, long SourceBytes) : CompressionCandidate(Id)
{
    /// <summary>
    /// Định danh ổn định của nhánh này. Hằng số chứ không phải chuỗi dựng ở chỗ gọi, để mã
    /// lý do và đối chiếu log không phụ thuộc vào việc ai đó viết lại tên.
    /// </summary>
    public const string StableId = "ORIGINAL";

    /// <summary>Không re-encode nên không có tổn thất chất lượng theo thế hệ nào.</summary>
    public const double GenerationalQualityLoss = 0;

    /// <summary>Không encode nên không tốn thời gian encode toàn tệp.</summary>
    public const double EncodeComputeCostSeconds = 0;

    public int Width { get; init; }

    public int Height { get; init; }

    public double Fps { get; init; }

    /// <summary>Codec của nguồn, đọc từ probe. Rỗng khi không xác định được — không đoán.</summary>
    public string? CodecName { get; init; }

    public bool HasAudio { get; init; }

    public double? AudioBitrateKbps { get; init; }

    /// <summary>Bề rộng × chiều cao, dùng cho thông báo và đối chiếu.</summary>
    public string Dimensions => $"{Width}x{Height}";
}

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
