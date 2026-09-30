using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Planning;

/// <summary>
/// Miền tìm kiếm của <b>một</b> codec: nó biết thang chất lượng riêng, cách đặt preset,
/// và những ràng buộc riêng của mình.
///
/// <para>Đây là ranh giới quan trọng nhất của kiến trúc. Trước đây CRF được lấy từ
/// <c>BaseCrf(goal)</c> rồi áp cho mọi codec, nghĩa là "Mạnh = CRF 28" được hiểu là 28
/// của x264, 28 của x265 và 28 của AV1 — ba con số hoàn toàn khác nghĩa. Ở đây mỗi codec
/// có miền riêng và <b>không</b> nhận miền số của codec khác.</para>
///
/// <para><b>Điểm còn lại trong miền là tọa độ tìm kiếm, không phải cam kết chất
/// lượng.</b> <c>CoarseQualityPoints</c> trả về những điểm khởi đầu để đo; chỉ
/// <c>QualityProbe</c> ở giai đoạn sau mới biết điểm nào thật sự đạt ngưỡng.</para>
/// </summary>
public interface IEncoderSearchDomain
{
    VideoCodec Codec { get; }

    /// <summary>Tên encoder truyền cho ffmpeg, ví dụ <c>libx265</c>.</summary>
    string EncoderName { get; }

    /// <summary>Tham số chất lượng nhỏ nhất mà codec chấp nhận. CRF nhỏ = chất lượng cao.</summary>
    double MinQuality { get; }

    /// <summary>Tham số chất lượng lớn nhất mà codec chấp nhận.</summary>
    double MaxQuality { get; }

    /// <summary>
    /// Các điểm chất lượng thô để bắt đầu tìm, theo thứ tự từ chất lượng cao xuống thấp.
    /// </summary>
    /// <param name="level">Mức người dùng chọn — dịch thành <i>vị trí trung tâm</i> của vùng tìm.</param>
    /// <param name="bias">
    /// Độ lệch do nội dung nguồn, trên thang [−1, 1]. Âm = nguồn khó, dời vùng tìm về
    /// chất lượng cao hơn. Đây là <b>ưu tiên</b>, không phải dự đoán chất lượng.
    /// </param>
    /// <param name="detailScale">
    /// Tỉ lệ số pixel của nhánh so với nguồn (1 = giữ nguyên độ phân giải nguồn). Rỗng thì
    /// mặc định 1.
    /// </param>
    IReadOnlyList<double> CoarseQualityPoints(
        CompressionLevel level, double bias, double detailScale, int count);

    /// <summary>
    /// Tên preset encode, do <b>ngân sách tính toán</b> quyết định — không phải do mode.
    /// </summary>
    /// <remarks>
    /// Giá trị này phải là <b>một thẻ đơn</b>, không phải mảnh cú pháp dòng lệnh. Trước đây
    /// miền AV1 trả về <c>"cpu-used=9"</c>, và nếu giai đoạn dựng lệnh ghép thành
    /// <c>-preset cpu-used=9</c> thì ffmpeg sẽ từ chối. Tên công tắc nằm ở
    /// <see cref="PresetSwitch"/>.
    /// </remarks>
    string Preset(ComputeBudget budget);

    /// <summary>Công tắc ffmpeg mang giá trị của <see cref="Preset"/>, ví dụ <c>-preset</c>.</summary>
    string PresetSwitch { get; }

    /// <summary>Công tắc ffmpeg mang giá trị chất lượng, ví dụ <c>-crf</c>.</summary>
    string QualitySwitch { get; }

    /// <summary>Định dạng pixel đầu ra theo thứ tự ưu tiên; phần tử đầu là mặc định.</summary>
    IReadOnlyList<string> PixelFormats { get; }

    /// <summary>Tune phù hợp với nhóm nội dung, hoặc null nếu codec không có tune phù hợp.</summary>
    string? TuneFor(ContentProfile profile);

    /// <summary>Kiểm tra một ứng viên có nằm trong miền này không. Trả về lý do nếu không.</summary>
    bool Validate(double quality, int width, int height, out string failure);
}

/// <summary>
/// Miền chung cho hai codec x264/x265: cùng thang chất lượng, khác preset và tune.
///
/// <para><b>Trần 51 là chọn có chủ đích, không phải đặc tính đo được của cả hai.</b> Đo
/// trên bản ffmpeg đi kèm (<c>8.0.1-essentials</c>): x265 <b>từ chối</b> tham số 52 trở
/// lên, còn x264 thì không chặn — nhưng việc lớp bọc không kiểm tra không phải bằng chứng
/// rằng con số đó là một CRF có nghĩa. Nên cả hai bị kẹp ở 51: đây là trần quen thuộc của
/// H.264, và ngoài nó tệp đầu ra thường đã lớn hơn tệp nguồn, tức là ứng viên vô dụng.</para>
///
/// <para>Sàn 0 cũng là chọn có chủ đích: cả hai codec chấp nhận <c>-1</c>, nhưng -1
/// nghĩa là chuyển sang chế độ lượng tử hằng, <b>không phải</b> chất lượng hằng — không
/// phải thứ ứng viên nào ở đây muốn.</para>
/// </summary>
public abstract class X26xSearchDomain(VideoCodec codec, string encoderName, double defaultQuality) : IEncoderSearchDomain
{
    public VideoCodec Codec { get; } = codec;

    public string EncoderName { get; } = encoderName;

    /// <summary>
    /// Miền chất lượng. Không phải hằng số của lớp: x264/x265 dùng 0–51 còn libaom dùng
    /// 0–63, và nếu khai báo bằng <c>new</c> thì giao diện vẫn trả miền của lớp cha — tức
    /// ứng viên AV1 ngoài 0–51 sẽ bị chặn oan mà không có dấu hiệu gì.
    /// </summary>
    public virtual double MinQuality => 0;

    public virtual double MaxQuality => 51;

    /// <summary>
    /// Vị trí trung tâm mặc định của miền. Đây là <b>giá trị mặc định của chính encoder</b>
    /// (x264 23, x265 28), không phải con số do mức nén chọn — nên mode không thể biến nó
    /// thành cam kết chất lượng.
    /// </summary>
    protected double DefaultQuality { get; } = defaultQuality;

    /// <summary>
    /// <b>Chưa hiệu chỉnh.</b> Số điểm chất lượng cần dịch xuống bao nhiêu cho mỗi lần
    /// giảm một nửa số pixel, để giữ chất lượng cảm nhận.
    ///
    /// <para>Chiều là chắc chắn và có thể kiểm chứng không cần đo: nhánh nhỏ hơn thì mỗi
    /// pixel phải được mã hoá <i>kỹ</i> hơn, tức tham số chất lượng phải <i>nhỏ</i> hơn.
    /// Nếu giữ nguyên tham số như nhánh nguồn, mọi nhánh nhỏ đều sẽ hỏng ngưỡng chất
    /// lượng và tốn công encode vô ích.</para>
    ///
    /// <para><b>Độ lớn</b> thì chưa có số đo nào trong kho, nên đây là giá trị khởi đầu
    /// đánh dấu rõ, cần hiệu chỉnh khi có corpus đủ rộng. Con số này
    /// <b>không</b> phải ngưỡng chất lượng và không được dùng để kết luận ứng viên nào
    /// đạt — việc đó thuộc <c>QualityProbe</c> ở giai đoạn sau.</para>
    /// </summary>
    private const double QualityPerHalvingUncalibrated = 4.0;

    /// <summary>
    /// Mode chỉ dịch vị trí trung tâm trong <b>một băng hẹp</b> quanh mặc định của codec,
    /// rồi bị kẹp vào miền. Băng hẹp vì mode là ý định chất lượng, còn miền là giới hạn
    /// kỹ thuật của encoder.
    /// </summary>
    private const double ModeOffsetRange = 6.0;

    /// <param name="detailScale">
    /// Tỉ lệ số pixel của nhánh so với nguồn: 1 = giữ nguyên độ phân giải nguồn, 0,5 =
    /// một nửa. Giá trị dưới 1 kéo vùng tìm về chất lượng cao hơn, vì cùng tham số chất
    /// lượng ở nhánh nhỏ sẽ mất nhiều chi tiết cảm nhận hơn.
    /// </param>
    public IReadOnlyList<double> CoarseQualityPoints(CompressionLevel level, double bias, double detailScale, int count)
    {
        var offset = level switch
        {
            CompressionLevel.Light => -ModeOffsetRange,
            CompressionLevel.Strong => +ModeOffsetRange,
            _ => 0.0,
        };

        // Nguồn khó (bias âm) thì dời về chất lượng cao hơn: tham số chất lượng nhỏ hơn.
        var center = DefaultQuality + offset + Math.Clamp(bias, -1, 1) * ModeOffsetRange;

        // Nhánh nhỏ hơn: mỗi pixel phải kỹ hơn, nên tham số chất lượng giảm. log2 cho
        // "mỗi lần giảm một nửa" là đúng nghĩa, không phải phép nhân tuyến tính bịa.
        var halvings = Math.Max(0, -Math.Log2(Math.Max(1e-6, detailScale)));
        center -= halvings * QualityPerHalvingUncalibrated;

        var step = Math.Max(1.0, ModeOffsetRange * 2.0 / Math.Max(1, count));
        var points = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            // Làm tròn về số nguyên ngay ở đây, một lần và duy nhất.
            //
            // Cả ba encoder này lấy tham số chất lượng dạng số nguyên, nên giữ số thực
            // chỉ mang lại nhiễu: log in ra "16.64029999423075", ID mang theo, và lệnh
            // ffmpeg nhận đúng con số đó. Làm tròn từng nơi thì dễ lệch — nơi này là ranh
            // giới duy nhất mà giá trị chất lượng đi ra ngoài miền.
            points.Add(Math.Round(Math.Clamp(center - step * i, MinQuality, MaxQuality), MidpointRounding.AwayFromZero));
        }

        return points;
    }

    public abstract string Preset(ComputeBudget budget);

    public virtual string PresetSwitch => "-preset";

    public virtual string QualitySwitch => "-crf";

    public IReadOnlyList<string> PixelFormats { get; } = ["yuv420p10le", "yuv420p"];

    public abstract string? TuneFor(ContentProfile profile);

    public bool Validate(double quality, int width, int height, out string failure)
    {
        if (quality < MinQuality || quality > MaxQuality)
        {
            failure = $"{EncoderName}: tham số chất lượng {quality} ngoài miền {MinQuality}–{MaxQuality}";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            failure = $"{EncoderName}: kích thước {width}x{height} không hợp lệ";
            return false;
        }

        if (width % 2 != 0 || height % 2 != 0)
        {
            failure = $"{EncoderName}: kích thước phải là số chẵn, nhận {width}x{height}";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}

/// <summary>
/// H.264. Miền CRF rộng nhất, preset nhanh nhất, và là codec duy nhất hợp lý với nội dung
/// màn hình — đo thật trên tệp quay màn hình: HEVC to hơn 2% và SSIM kém hơn 0,005.
/// </summary>
public sealed class X264SearchDomain : X26xSearchDomain
{
    public X264SearchDomain()
        : base(VideoCodec.H264, "libx264", defaultQuality: 23)
    {
    }

    public override string Preset(ComputeBudget budget) => budget switch
    {
        ComputeBudget.Fast => "veryfast",
        ComputeBudget.Thorough => "slow",
        _ => "medium",
    };

    public override string? TuneFor(ContentProfile profile) => profile switch
    {
        // HEVC sinh ra hình khối 16x16 rõ nét; H.264 xử lý nội dung màn hình tốt hơn.
        ContentProfile.ScreenContent => "stillimage",
        _ => null,
    };
}

/// <summary>HEVC: nhỏ hơn H.264 52–62% ở cùng mức chất lượng, nhưng chậm hơn nhiều.</summary>
public sealed class X265SearchDomain : X26xSearchDomain
{
    public X265SearchDomain()
        : base(VideoCodec.Hevc, "libx265", defaultQuality: 28)
    {
    }

    public override string Preset(ComputeBudget budget) => budget switch
    {
        ComputeBudget.Fast => "fast",
        ComputeBudget.Thorough => "slow",
        _ => "medium",
    };

    public override string? TuneFor(ContentProfile profile) => profile switch
    {
        // HEVC có Screen Content Coding riêng; dùng nó cho nội dung màn hình thay vì mặc
        // định, vốn tối ưu cho nội dung tự nhiên. Có số đo trong kho.
        ContentProfile.ScreenContent => "screencontent",

        // Cố tình trả null cho phần còn lại. Trước đây chỗ này trả "animation" cho mọi
        // thứ không phải màn hình, tức là áp tune anime lên quay thật, game và phim — với
        // lý do "hầu như nội dung của chúng ta là anime". Thứ nhất, phần lớn tệp trong
        // kho không phải anime; thứ hai, `ContentProfile` phân loại theo độ khó chuyển
        // động, không phải theo thể loại nội dung, nên nó không đủ thông tin để chọn
        // tune anime. Chọn tune anime sai làm hỏng ảnh thật.
        //
        // Khi có tín hiệu thể loại nội dung đo được, hãy thêm nhánh ở đây — kèm số đo.
        _ => null,
    };
}

/// <summary>
/// AV1 <b>libaom</b>. Miền 0–63. Tên lớp ghi rõ <c>libaom</c> vì SVT-AV1 là encoder
/// khác hẳn: nó dùng thang <c>QP</c> chứ không phải <c>CRF</c>, nên một lớp tên
/// "SvtAv1" gắn với encoder <c>libaom-av1</c> sẽ khiến người đọc tưởng công cụ đã hỗ trợ
/// SVT-AV1 trong khi thực tế chưa. Thêm SVT-AV1 sau cần một lớp riêng, không sửa lớp này.
///
/// <para>Tắt mặc định vì lý do đo được, không phải vì chưa hỗ trợ: libaom chậm hơn
/// libx265 khoảng một đến hai bậc độ lũy, nên bật mặc định sẽ làm công cụ dừng vô dùng
/// với tệp dài. Muốn thử thì bật <c>EnableAv1Search</c> cùng ngân sách
/// <see cref="ComputeBudget.Thorough"/>.</para>
/// </summary>
public sealed class LibaomAv1SearchDomain : X26xSearchDomain
{
    public LibaomAv1SearchDomain()
        // libaom: tham số -crf chạy 0..63, khác hẳn 0..51 của x26x. Dùng chung miền với
        // x26x sẽ là đúng loại lỗi mà interface này sinh ra để chặn.
        : base(VideoCodec.Av1, "libaom-av1", defaultQuality: 32)
    {
    }

    public override string Preset(ComputeBudget budget) => budget switch
    {
        // cpu-used: 0 chậm nhất, 8 nhanh nhất — chiều NGƯỢC với preset của x264/x265.
        // Trả về thẻ trần; công tắc do PresetSwitch chỉ định.
        //
        // Trần là 8, đo trên bản ffmpeg đi kèm: "-cpu-used 9" bị từ chối với
        // "Value 9.000000 for parameter 'cpu-used' out of range [0 - 8]". Trước đây chỗ
        // này dùng 9 theo thói quen của tài liệu SVT-AV1, và sẽ chỉ lộ lỗi khi encode
        // thật. Miền này phụ thuộc bản dựng, nên hằng số đi kèm ghi rõ nguồn.
        ComputeBudget.Fast => "8",
        ComputeBudget.Thorough => "3",
        _ => "6",
    };

    /// <summary>libaom không dùng <c>-preset</c> kiểu x264; mức tốc độ nằm ở <c>-cpu-used</c>.</summary>
    /// <remarks>
    /// <b>Đo trên bản ffmpeg đi kèm, tệp 5 giây 640x360, CRF 40:</b>
    /// <list type="table">
    /// <item><term><c>-cpu-used 8</c></term><description>2,05 s — đúng ý định</description></item>
    /// <item><term><c>-preset "cpu-used=8"</c></term><description>151,8 s — bị bỏ qua âm thầm</description></item>
    /// <item><term>không truyền gì</term><description>154,3 s, tệp giống hệt dòng trên</description></item>
    /// </list>
    ///
    /// <para>Dạng sai không hề báo lỗi: ffmpeg cho tệp ra <b>giống hệt</b> mặc định
    /// (cpu-used 1) và chậm hơn 74 lần. Với tệp 40 phút, một ứng viên "Nhanh" sẽ mất
    /// hơn 20 giờ thay vì 16 phút, và người dùng chỉ thấy nó "thành công". Đây là dạng
    /// lỗi tệ nhất: thất bại mà trông như thành công.</para>
    /// </remarks>
    public override string PresetSwitch => "-cpu-used";

    public override string? TuneFor(ContentProfile profile) => null;

    /// <summary>
    /// libaom: <c>-crf</c> chạy 0–63, khác hẳn 0–51 của x264/x265.
    ///
    /// <para>Đo trên bản ffmpeg đi kèm: <c>-crf &lt;int&gt;</c> "from -1 to 63". Sàn 0 vì
    /// -1 là chế độ lượng tử hằng, không phải chất lượng hằng. Trần 63 là số được
    /// công cụ chấp nhận, không phải số đẹp: ngoài đó tệp ra gần như chắc chắn lớn hơn
    /// nguồn, và bước sau sẽ loại.</para>
    /// </summary>
    public override double MaxQuality => 63;
}
