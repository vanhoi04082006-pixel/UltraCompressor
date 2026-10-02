using System.Globalization;

namespace UltraCompressor.Core.Encoders;

/// <summary>
/// Kiểm tra một cờ nhóm có chứa họ encoder cụ thể không.
/// </summary>
public static class EncoderKindMaskExtensions
{
    public static bool Accepts(this EncoderKindMask mask, EncoderKind kind) =>
        (mask & EncoderKinds.ToMask(kind)) != EncoderKindMask.None;
}

/// <summary>
/// Miền giá trị một encoder chấp nhận, kèm nơi con số đó đến từ đâu.
///
/// <para>Phần "nguồn" không phải trang trí. Cùng một tuỳ chọn có thể có miền khác nhau giữa
/// các bản dựng, và con số nào đúng lại phụ thuộc bản đang chạy. Ghi rõ đã đo ở đâu giúp
/// câu hỏi "miền này còn đúng không" có một câu trả lời.</para>
/// </summary>
public readonly record struct ValueRange(double Min, double Max, string Source)
{
    public bool Contains(double value) => value >= Min && value <= Max;

    public override string ToString() =>
        $"{Min.ToString("0.##", CultureInfo.InvariantCulture)}–{Max.ToString("0.##", CultureInfo.InvariantCulture)} ({Source})";
}

/// <summary>Tập giá trị rời rạc một encoder chấp nhận, kèm nơi xác nhận.</summary>
public sealed record NamedValueSet(IReadOnlyList<string> Values, string Source)
{
    public bool Contains(string value) => Values.Contains(value, StringComparer.Ordinal);

    public override string ToString() => $"{string.Join('/', Values)} ({Source})";
}

/// <summary>Họ encoder. Dùng để chặn việc ghép đúng công tắc nhưng sai encoder.</summary>
public enum EncoderKind
{
    Unknown,
    X264,
    X265,
    LibaomAv1,
    SvtAv1,
    Hardware,
}

/// <summary>
/// Nhóm encoder mà một tuỳ chọn dùng được, dạng cờ.
///
/// <para>Cần cờ chứ không một giá trị đơn vì có tuỳ chọn hợp lệ với nhiều họ: preset có tên
/// của x26x dùng cho cả libx264 lẫn libx265. Nếu buộc một họ, hoặc phải nhân đôi lớp, hoặc
/// phải nới kiểm tra cho tất cả.</para>
/// </summary>
[Flags]
public enum EncoderKindMask
{
    None = 0,
    X264 = 1 << 0,
    X265 = 1 << 1,
    LibaomAv1 = 1 << 2,
    SvtAv1 = 1 << 3,
    Hardware = 1 << 4,

    /// <summary>Họ dùng chung thang CRF và tập tên preset.</summary>
    X26x = X264 | X265,
}

public static class EncoderKinds
{
    public static EncoderKindMask ToMask(EncoderKind kind) => kind switch
    {
        EncoderKind.X264 => EncoderKindMask.X264,
        EncoderKind.X265 => EncoderKindMask.X265,
        EncoderKind.LibaomAv1 => EncoderKindMask.LibaomAv1,
        EncoderKind.SvtAv1 => EncoderKindMask.SvtAv1,
        EncoderKind.Hardware => EncoderKindMask.Hardware,
        _ => EncoderKindMask.None,
    };
}

/// <summary>
/// <b>Tuỳ chọn chất lượng đã mang ngữ nghĩa của encoder</b>, không phải chuỗi tự do.
///
/// <para>Mỗi encoder một lớp riêng vì <b>thang số khác nhau</b>: CRF 30 của x264, CRF 30
/// của libaom và QP 30 của SVT-AV1 là ba mức chất lượng không liên quan. Tách lớp nghĩa là
/// "ghép nhầm thang" trở thành lỗi biên dịch, không phải lỗi chạy.</para>
///
/// <para>Đây là nửa còn lại của lỗi đã gặp ở giai đoạn 3. Nếu miền trả chuỗi
/// <c>"cpu-used=8"</c> và tầng dựng lệnh ghép thành <c>-preset cpu-used=8</c>, ffmpeg không
/// báo lỗi mà âm thầm bỏ qua, khiến encode chậm hơn 74 lần.</para>
/// </summary>
public abstract record QualityOption
{
    /// <summary>Công tắc ffmpeg mang giá trị này, ví dụ <c>-crf</c> hoặc <c>-qp</c>.</summary>
    public abstract string Switch { get; }

    /// <summary>Giá trị đã định dạng, dùng nguyên văn trong lệnh.</summary>
    public abstract string Text { get; }

    /// <summary>Tên tham số để hiển thị, ví dụ <c>CRF</c> hay <c>QP</c>.</summary>
    public abstract string ParameterName { get; }

    /// <summary>Họ encoder mà tuỳ chọn này hợp lệ.</summary>
    public abstract EncoderKindMask ApplicableKinds { get; }

    /// <summary>Miền giá trị, đã đo.</summary>
    public abstract ValueRange AcceptedRange { get; }

    public double Numeric => double.Parse(Text, CultureInfo.InvariantCulture);

    public bool IsInRange => AcceptedRange.Contains(Numeric);

    public void Validate(EncoderKind actualKind, out string failure)
    {
        if (!ApplicableKinds.Accepts(actualKind))
        {
            failure = $"{ParameterName} không dùng cho encoder {actualKind} (chỉ dành cho {ApplicableKinds})";
            return;
        }

        if (!IsInRange)
        {
            failure = $"{ParameterName} {Text} ngoài miền đã đo {AcceptedRange}";
            return;
        }

        failure = string.Empty;
    }

    /// <summary>Hai thẻ cho <c>ProcessStartInfo.ArgumentList</c>.</summary>
    public IReadOnlyList<string> ToArguments() => [Switch, Text];

    public override string ToString() => $"{Switch} {Text}";

    /// <summary>CRF của x264/x265 — thang 0–51.</summary>
    public static QualityOption X26xCrf(double value) => new X26xCrfOption(value);

    /// <summary>
    /// CRF của <b>libaom</b> — thang 0–63, rộng hơn hẳn x26x.
    ///
    /// <para>Tách riêng khỏi <see cref="X26xCrf"/> vì cùng tên công tắc <c>-crf</c> nhưng
    /// miền khác nhau. Dùng chung một lớp sẽ chặn oan ứng viên AV1 hợp lệ, hoặc cho phép
    /// ứng viên AV1 vượt miền mà không ai hay biết.</para>
    /// </summary>
    public static QualityOption LibaomCrf(double value) => new LibaomCrfOption(value);

    /// <summary>
    /// QP của SVT-AV1. <b>Không phải CRF</b> và không dùng chung miền với bất kỳ thứ gì.
    /// Chưa có encoder này trong bản đóng gói nên miền chưa được kiểm chứng bằng encode thật.
    /// </summary>
    public static QualityOption SvtAv1Qp(int value) => new SvtAv1QpOption(value);

    /// <summary>CQ của encoder phần cứng (NVENC/QSV/AMF). Miền chưa được hiệu chỉnh — xem ghi chú.</summary>
    public static QualityOption HardwareConstantQuality(int value) => new HardwareCqOption(value);

    private sealed record X26xCrfOption(double Value) : QualityOption
    {
        public override string Switch => "-crf";
        public override string Text => Value.ToString("0.##", CultureInfo.InvariantCulture);
        public override string ParameterName => "CRF";
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.X26x;
        public override ValueRange AcceptedRange => EncoderRanges.Crf26x;
    }

    private sealed record LibaomCrfOption(double Value) : QualityOption
    {
        public override string Switch => "-crf";
        public override string Text => Value.ToString("0.##", CultureInfo.InvariantCulture);
        public override string ParameterName => "CRF (libaom)";
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.LibaomAv1;
        public override ValueRange AcceptedRange => EncoderRanges.CrfLibaom;
    }

    private sealed record SvtAv1QpOption(int Value) : QualityOption
    {
        public override string Switch => "-qp";
        public override string Text => Value.ToString(CultureInfo.InvariantCulture);
        public override string ParameterName => "QP (SVT-AV1)";
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.SvtAv1;
        public override ValueRange AcceptedRange => EncoderRanges.QpSvtAv1;
    }

    private sealed record HardwareCqOption(int Value) : QualityOption
    {
        public override string Switch => "-cq";
        public override string Text => Value.ToString(CultureInfo.InvariantCulture);
        public override string ParameterName => "CQ";
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.Hardware;
        public override ValueRange AcceptedRange => EncoderRanges.CqHardware;
    }
}

/// <summary>
/// <b>Tuỳ chọn tốc độ đã mang ngữ nghĩa của encoder.</b>
///
/// <para>Kiểu này là nơi ngăn được đúng lỗi đã gặp: mỗi lớp con biết <b>công tắc riêng
/// của nó</b> và miền riêng, nên <c>AomCpuUsed</c> không bao giờ sinh <c>-preset</c>, còn
/// <c>X26xPreset</c> chỉ nhận tên trong danh sách đóng nên không thể mang giá trị
/// <c>cpu-used=8</c>.</para>
///
/// <para>Miền của SVT-AV1 <b>không phải</b> miền của libaom. Cùng tên tuỳ chọn, hai
/// encoder khác hẳn, và lấy nhầm miền chỉ lộ ra lúc encode thật.</para>
/// </summary>
public abstract record SpeedOption
{
    public abstract string Switch { get; }

    public abstract string Text { get; }

    public abstract EncoderKindMask ApplicableKinds { get; }

    public abstract string AcceptedDescription { get; }

    public abstract bool AcceptsValue();

    public void Validate(EncoderKind actualKind, out string failure)
    {
        if (!ApplicableKinds.Accepts(actualKind))
        {
            failure = $"tuỳ chọn tốc độ không dùng cho encoder {actualKind} (chỉ dành cho {ApplicableKinds})";
            return;
        }

        if (!AcceptsValue())
        {
            failure = $"giá trị \"{Text}\" không hợp lệ; chấp nhận: {AcceptedDescription}";
            return;
        }

        failure = string.Empty;
    }

    public IReadOnlyList<string> ToArguments() => [Switch, Text];

    public override string ToString() => $"{Switch} {Text}";

    /// <summary>Preset có tên của x264/x265, truyền bằng <c>-preset</c>.</summary>
    public static SpeedOption X26xPreset(string name) => new X26xPresetOption(name);

    /// <summary>
    /// Mức tốc độ của <b>libaom</b>, truyền bằng <c>-cpu-used</c>.
    ///
    /// <para>Miền 0–8 là <b>đo trên bản ffmpeg đi kèm</b>, không phải 0–9 quen thuộc của
    /// SVT-AV1. Truyền <c>9</c> bị từ chối: <c>Value 9.000000 for parameter 'cpu-used' out
    /// of range [0 - 8]</c>.</para>
    /// </summary>
    public static SpeedOption AomCpuUsed(int value) => new AomCpuUsedOption(value);

    /// <summary>Preset của SVT-AV1 — miền khác hẳn libaom, và dùng QP chứ không dùng CRF.</summary>
    public static SpeedOption SvtAv1Preset(int value) => new SvtAv1PresetOption(value);

    /// <summary>Preset của encoder phần cứng, tên do encoder đó định nghĩa.</summary>
    public static SpeedOption HardwareEncoderPreset(string name, string switchName = "-preset")
        => new HardwarePresetOption(name, switchName);

    private sealed record X26xPresetOption(string Value) : SpeedOption
    {
        public override string Switch => "-preset";
        public override string Text => Value;

        public override EncoderKindMask ApplicableKinds => EncoderKindMask.X26x;
        public override string AcceptedDescription => EncoderRanges.Preset26x.ToString();
        public override bool AcceptsValue() => EncoderRanges.Preset26x.Contains(Value);
    }

    private sealed record AomCpuUsedOption(int Value) : SpeedOption
    {
        public override string Switch => "-cpu-used";
        public override string Text => Value.ToString(CultureInfo.InvariantCulture);
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.LibaomAv1;
        public override string AcceptedDescription => EncoderRanges.CpuUsedLibaom.ToString();
        public override bool AcceptsValue() => EncoderRanges.CpuUsedLibaom.Contains(Value);
    }

    private sealed record SvtAv1PresetOption(int Value) : SpeedOption
    {
        public override string Switch => "-preset";
        public override string Text => Value.ToString(CultureInfo.InvariantCulture);
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.SvtAv1;
        public override string AcceptedDescription => EncoderRanges.PresetSvtAv1.ToString();
        public override bool AcceptsValue() => EncoderRanges.PresetSvtAv1.Contains(Value);
    }

    private sealed record HardwarePresetOption(string Value, string SwitchName) : SpeedOption
    {
        public override string Switch => SwitchName;
        public override string Text => Value;
        public override EncoderKindMask ApplicableKinds => EncoderKindMask.Hardware;
        public override string AcceptedDescription => "tên do encoder phần cứng định nghĩa (chưa đo trong kho)";
        public override bool AcceptsValue() => Value.Length > 0;
    }
}

/// <summary>
/// Miền giá trị đã đo — <b>nguồn sự thật duy nhất</b> trong kho.
///
/// <para>Toàn bộ số ở đây lấy từ <c>ffmpeg -h encoder=...</c> và từ thử encode thật trên bản
/// đóng gói <c>8.0.1-essentials</c>. Đổi phiên bản ffmpeg thì phải đo lại và sửa ở đây,
/// không sửa rải ở các lớp con.</para>
/// </summary>
public static class EncoderRanges
{
    /// <summary>
    /// CRF của x264/x265.
    ///
    /// <para>Đo: x265 <b>từ chối</b> 52 trở lên. x264 thì không chặn, nhưng lớp bọc không kiểm
    /// tra không phải bằng chứng rằng con số đó là một CRF có nghĩa. Trần 51 là <b>lựa chọn có
    /// chủ đích</b>: ngoài đó tệp ra thường đã lớn hơn tệp nguồn, tức ứng viên vô dụng. Sàn 0
    /// vì cả hai chấp nhận <c>-1</c>, nhưng -1 nghĩa là chuyển sang chế độ lượng tử hằng chứ
    /// không phải chất lượng hằng.</para>
    /// </summary>
    public static ValueRange Crf26x { get; } = new(0, 51, "đo trên ffmpeg 8.0.1-essentials");

    /// <summary>CRF của libaom. Đo: <c>-crf &lt;int&gt; ... from -1 to 63</c>; sàn 0 như trên.</summary>
    public static ValueRange CrfLibaom { get; } = new(0, 63, "đo trên ffmpeg 8.0.1-essentials");

    /// <summary>
    /// QP của SVT-AV1. <b>Không dùng chung với CRF.</b> Lấy từ tài liệu SVT-AV1 vì encoder
    /// chưa có trong bản đóng gói, nên <b>chưa được kiểm chứng bằng encode thật</b>.
    /// </summary>
    public static ValueRange QpSvtAv1 { get; } = new(1, 63, "tài liệu SVT-AV1 — CHƯA đo trên bản đóng gói");

    /// <summary>
    /// cpu-used của libaom. Đo: <c>-cpu-used &lt;int&gt; ... (from 0 to 8) (default 1)</c>, và
    /// <c>-cpu-used 9</c> bị từ chối. Giá trị nhỏ = chậm và nén tốt.
    /// </summary>
    public static ValueRange CpuUsedLibaom { get; } = new(0, 8, "đo trên ffmpeg 8.0.1-essentials");

    /// <summary>Preset của SVT-AV1 (0–13). <b>Khác</b> miền libaom và khác tập tên của x26x. Chưa đo.</summary>
    public static ValueRange PresetSvtAv1 { get; } = new(0, 13, "tài liệu SVT-AV1 — CHƯA đo trên bản đóng gói");

    /// <summary>
    /// CQ của encoder phần cứng (NVENC/QSV/AMF). Miền 0–51 là miền <b>tiêu chuẩn của
    /// cả ba họ</b>, chưa phải miền đo của ứng dụng.
    /// </summary>
    /// <remarks>
    /// <para><b>Đã kiểm chứng trên máy này, và kết quả là "chưa dùng".</b> Chạy encode thật
    /// với bản ffmpeg đóng gói 8.0.1:</para>
    /// <list type="bullet">
    /// <item><c>hevc_nvenc</c> — chạy được, ra HEVC Main hợp lệ.</item>
    /// <item><c>hevc_qsv</c> — chạy được, ra HEVC Main hợp lệ.</item>
    /// <item><c>hevc_amf</c> — hỏng: <c>amfrt64.dll failed to open</c> (máy không có phần
    /// cứng AMD / driver).</item>
    /// </list>
    /// <para>Cả hai encoder chạy được đều nhận <c>-cq</c>, nên công tắc không sai. Cái chưa có
    /// là <b>quan hệ giữa CQ và chất lượng</b>: đó là thang riêng của từng họ, không cùng
    /// nghĩa với CRF của x26x, và kho chưa có đường cong nào đo được. Không đo thì không
    /// dùng.</para>
    ///
    /// <para><b>Vì sao vẫn giữ mã này.</b> `KindOf` nhận ra tên encoder phần cứng, và nếu một
    /// kế hoạch trong tương lai chọn phải loại đó thì phải có chỗ để viết đúng công tắc.
    /// Xoá thì khi ấy kế hoạch sẽ rơi về nhánh mặc định và sinh <c>-crf</c> cho encoder không
    /// có tuỳ chọn đó — lỗi nghiêm trọng hơn nhiều so với việc có một miền chưa hiệu chỉnh.</para>
    ///
    /// <para><b>Đường tìm kiếm không đưa encoder phần cứng vào danh sách</b> — xem
    /// <c>CandidatePlanner.DomainsToTry</c>.</para>
    /// </remarks>
    public static ValueRange CqHardware { get; } = new(0, 51, "tiêu chuẩn 0-51 của NVENC/QSV/AMF — CHƯA đo CQ↔chất lượng");

    /// <summary>Tập tên preset của x264 và x265 — giống nhau, khác mọi encoder khác.</summary>
    public static NamedValueSet Preset26x { get; } = new(
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"],
        "đo trên ffmpeg 8.0.1-essentials");

    /// <summary>
    /// Xác định họ encoder từ tên, để chặn việc ghép đúng công tắc nhưng <b>sai encoder</b>:
    /// <c>-cpu-used 8</c> với <c>-c:v libx264</c> là tuỳ chọn mà encoder đó không có.
    /// </summary>
    public static EncoderKind KindOf(string? encoderName) => encoderName switch
    {
        "libx264" => EncoderKind.X264,
        "libx265" => EncoderKind.X265,
        "libaom-av1" => EncoderKind.LibaomAv1,
        "libsvtav1" => EncoderKind.SvtAv1,
        "h264_nvenc" or "hevc_nvenc" or "av1_nvenc"
            or "h264_qsv" or "hevc_qsv" or "av1_qsv"
            or "h264_amf" or "hevc_amf" or "av1_amf"
            or "h264_videotoolbox" or "hevc_videotoolbox"
            => EncoderKind.Hardware,
        _ => EncoderKind.Unknown,
    };
}

/// <summary>
/// Cấu hình encode đầy đủ của một encoder, đã mang sẵn ngữ nghĩa.
///
/// <para>Đây là thứ <b>duy nhất</b> được phép biết tên công tắc ffmpeg. Tầng trên mô tả
/// <i>muốn gì</i>; tầng này quyết định <i>viết gì</i>.</para>
/// </summary>
public sealed record EncoderConfiguration
{
    public required string EncoderName { get; init; }

    public required QualityOption Quality { get; init; }

    public required SpeedOption Speed { get; init; }

    public string? Tune { get; init; }

    public required string PixelFormat { get; init; }

    public EncoderKind Kind => EncoderRanges.KindOf(EncoderName);

    /// <summary>
    /// Kiểm tra cấu hình trước khi chạy. Gọi ở <b>ranh giới</b>, không phải ở tầng sinh ứng
    /// viên: encoder nào thực sự tồn tại thì chỉ biết lúc dựng lệnh.
    /// </summary>
    public bool Validate(out string failure)
    {
        if (EncoderName.Length == 0)
        {
            failure = "tên encoder rỗng";
            return false;
        }

        var kind = Kind;
        if (kind == EncoderKind.Unknown)
        {
            failure = $"không nhận ra encoder \"{EncoderName}\" — không có miền giá trị để kiểm";
            return false;
        }

        Quality.Validate(kind, out failure);
        if (failure.Length > 0)
        {
            failure = $"{EncoderName}: {failure}";
            return false;
        }

        Speed.Validate(kind, out failure);
        if (failure.Length > 0)
        {
            failure = $"{EncoderName}: {failure}";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// Thẻ ffmpeg cho phần encoder, gồm cả <c>-c:v</c>.
    ///
    /// <para>Đây là <b>tầng dựng lệnh cuối</b> cho tuỳ chọn encoder, nên test phải đặt ở đây
    /// chứ không chỉ ở tầng lập kế hoạch: chỗ duy nhất có thể sinh ra
    /// <c>-preset cpu-used=8</c> chính là tầng này.</para>
    /// </summary>
    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string>(9) { "-c:v", EncoderName };

        args.AddRange(Quality.ToArguments());
        args.AddRange(Speed.ToArguments());
        args.AddRange(["-pix_fmt", PixelFormat]);

        if (!string.IsNullOrEmpty(Tune))
        {
            args.AddRange(["-tune", Tune]);
        }

        return args;
    }

    public override string ToString() =>
        $"{EncoderName} {Quality} {Speed}{(string.IsNullOrEmpty(Tune) ? "" : $" -tune {Tune}")} -pix_fmt {PixelFormat}";
}
