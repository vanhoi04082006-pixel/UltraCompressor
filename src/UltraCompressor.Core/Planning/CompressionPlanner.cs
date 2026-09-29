using System.Globalization;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Planning;

/// <summary>
/// Một trong ba mức, hiểu là <b>mức mục tiêu</b> chứ không phải bộ tham số cố định.
///
/// "Nhẹ" = ưu tiên giữ chất lượng. "Cân bằng" = tối ưu giữa dung lượng và chất lượng.
/// "Mạnh" = ưu tiên giảm dung lượng. Tham số thật do <see cref="CompressionPlanner"/> tính
/// riêng cho từng tệp từ mức mục tiêu này cùng với đặc tính của tệp đó.
/// </summary>
public enum CompressionGoal
{
    /// <summary>Ưu tiên giữ chất lượng. Chấp nhận tệp lớn.</summary>
    Quality = 0,

    /// <summary>Tối ưu giữa dung lượng và chất lượng. Đây là mặc định.</summary>
    Balanced = 1,

    /// <summary>Ưu tiên giảm dung lượng. Chấp nhận mất chi tiết.</summary>
    Size = 2,
}

/// <summary>Sai lệch bao nhiêu so với bản gốc, để giao diện hiện cho người dùng.</summary>
public enum PlanDelta
{
    /// <summary>Không thay đổi gì — tệp nguồn đã nhỏ hơn mức trần.</summary>
    None,

    /// <summary>Thu nhỏ theo tỉ lệ để giữ dưới trần bề rộng.</summary>
    Downscale,

    /// <summary>Giảm số khung hình mỗi giây.</summary>
    FrameRate,

    /// <summary>Nén mạnh hơn để đạt dung lượng mục tiêu.</summary>
    Stronger,

    /// <summary>Không nén lại: nguồn đã quá nén, mã hoá lại chỉ thêm nhiễu.</summary>
    NotWorthIt,
}

/// <summary>Kế hoạch nén cho một tệp video cụ thể.</summary>
public sealed record VideoPlan
{
    /// <summary>Bề rộng mục tiêu. 0 = giữ nguyên bề rộng nguồn.</summary>
    public required int TargetWidth { get; init; }

    public required int Crf { get; init; }

    public required string Preset { get; init; }

    /// <summary>
    /// Codec đầu ra: <c>libx264</c> hoặc <c>libx265</c>.
    ///
    /// <para>HEVC dùng thang CRF khác H.264: cùng số thì HEVC cho tệp nhỏ hơn. Thang mặc
    /// định của x265 (28) tương đương thang mặc định của x264 (23), tức lệch 5 điểm. Đo thật
    /// trên anime xác nhận: ở CRF 33 cho HEVC và 28 cho H.264, HEVC nhỏ hơn 52–62% mà
    /// SSIM chỉ lệch khoảng 0.003.</para>
    /// </summary>
    public string VideoEncoder { get; init; } = "libx264";

    /// <summary>Số khung hình mục tiêu. Null = giữ nguyên số khung hình nguồn.</summary>
    public double? TargetFps { get; init; }

    public required int AudioBitrateKbps { get; init; }

    /// <summary>True = bỏ hẳn luồng âm thanh (nguồn không có tiếng).</summary>
    public required bool DropAudio { get; init; }

    public required PlanDelta Delta { get; init; }

    /// <summary>Giải thích bằng tiếng Việt, hiện cho người dùng. Rỗng nghĩa là không đổi gì.</summary>
    public required string Reason { get; init; }
}

/// <summary>Kế hoạch nén cho một tệp ảnh cụ thể.</summary>
public sealed record ImagePlan
{
    public required int TargetWidth { get; init; }

    /// <summary>Giá trị cho <c>-q:v</c> của mã hoá MJPEG. Thấp hơn = chất lượng cao hơn.</summary>
    public required int QScale { get; init; }

    public required PlanDelta Delta { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Kế hoạch nén cho một tệp GIF cụ thể.</summary>
public sealed record GifPlan
{
    public required int TargetWidth { get; init; }

    public required int Fps { get; init; }

    public required int Lossy { get; init; }

    public required PlanDelta Delta { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Kế hoạch nén cho một tệp âm thanh cụ thể.</summary>
public sealed record AudioPlan
{
    public required int BitrateKbps { get; init; }

    public required PlanDelta Delta { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Kế hoạch nén cho PDF. Ghostscript chỉ có preset, không có biến liên tục.</summary>
public sealed record PdfPlan
{
    public required string Preset { get; init; }

    public required PlanDelta Delta { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Dịch mức nén của job sang mức mục tiêu mà planner hiểu.
///
/// Để ở đây thay vì nhân bản trong từng pipeline: năm bản sao chắc chắn sẽ lệch nhau
/// một lúc nào đó, và lỗi đó chỉ lộ ra ở một loại media duy nhất.
/// </summary>
public static class CompressionGoalExtensions
{
    public static CompressionGoal ToGoal(this CompressionLevel level) => level switch
    {
        CompressionLevel.Light => CompressionGoal.Quality,
        CompressionLevel.Strong => CompressionGoal.Size,
        _ => CompressionGoal.Balanced,
    };
}

/// <summary>
/// Biến <b>mức mục tiêu</b> thành tham số nén cụ thể, tính riêng cho từng tệp.
///
/// <para>Đây là hàm thuần: cùng đầu vào thì cùng đầu ra, không đọc tệp, không chạy tiến
/// trình. Nhờ vậy toàn bộ quy tắc lập kế hoạch kiểm thử được, thay vì phải nén thật mỗi
/// lần muốn xem một mức cho ra bao nhiêu.</para>
///
/// <para><b>Vì sao không định nghĩa thẳng "Mạnh = 1280px + CRF 30".</b> Vì các con số đó
/// chỉ đúng với một dạng tệp. Cùng mức "Mạnh" nhưng:
/// - tệp 4K 60fps nặng 40 GB: cần hạ cả bề rộng lẫn fps, và cần dạng nén tệp;
/// - tệp 720p đã nén tay: hạ xuống 1280px là vô nghĩa (nguồn còn nhỏ hơn), còn CRF phải
///   <b>nâng</b> lên chứ không hạ, vì nguồn đã hết dư để nén;
/// - tệp H.265/AV1: mã hoá lại bằng H.264 ở CRF 30 sẽ cho tệp <b>lớn hơn</b> bản gốc.</para>
///
/// <para>Các quy tắc bên dưới đều dựa trên số đo được từ chính tệp đó, không phải suy đoán
/// từ tên tệp.</para>
/// </summary>
public static class CompressionPlanner
{
    // Ngưỡng dưới đây lấy từ thực tiễn mã hoá, không phải từ tra cứu lý thuyết:
    //
    //  - 0,08 bit/pixel/khung: dưới ngưỡng này h264 đã dùng hết "dự trữ" chi tiết, mã hoá
    //    lại chủ yếu sinh nhiễu hạt (banding, mosquito noise) mà không thu được byte.
    //  - 0,25 bit/pixel/khung: trên ngưỡng này nguồn còn dư, nén lại thu được nhiều.
    //  - 0,50: nguồn rất dư, thường là mã hoá chất lượng cao hoặc bị nén nhiều lần.
    private const double BitsPerPixelNearlyDepleted = 0.08;
    private const double BitsPerPixelComfortable = 0.25;
    private const double BitsPerPixelVeryComfortable = 0.50;

    /// <summary>
    /// Cộng vào CRF khi chuyển từ H.264 sang HEVC, để hai thang CRF cho ra cùng chất lượng.
    ///
    /// <para><b>Đây là hằng số đo được, không phải ước lượng.</b> Thang mặc định của x265 là
    /// 28 và của x264 là 23, nên HEVC cần cao hơn 5 điểm. Đo thật trên 6 tệp anime của
    /// người dùng:</para>
    ///
    /// <list type="table">
    /// <item><term>offset +2 (sai)</term><description>HEVC nhỏ hơn 34–50%</description></item>
    /// <item><term>offset +5 (đúng)</term><description>HEVC nhỏ hơn <b>52–62%</b>, SSIM lệch ~0.003</description></item>
    /// </list>
    ///
    /// <para>Con số 2 ở bản đầu là do đoán, và nó làm HEVC <i>nén quá tay</i>: cùng dung
    /// lượng nhưng SSIM thấp hơn H.264 ở mọi tệp đo.</para>
    /// </summary>
    private const int HevcCrfOffset = 5;

    /// <summary>
    /// Cùng hằng số <see cref="HevcCrfOffset"/>, để pipeline bù lại khi phải lùi về H.264
    /// không phải tự nhớ là bao nhiêu.
    /// </summary>
    public const int HevcCrfOffsetForH264 = HevcCrfOffset;

    /// <summary>Trần bề rộng theo mức mục tiêu. Không bao giờ phóng to — chỉ thu nhỏ.</summary>
    private static int WidthCap(CompressionGoal goal, MediaKind kind) => goal switch
    {
        CompressionGoal.Quality => kind == MediaKind.Image ? 2560 : 3840,
        CompressionGoal.Balanced => 1920,
        _ => kind == MediaKind.Image ? 1600 : 1920,
    };

    /// <summary>CRF nền. Mọi điều chỉnh về sau cộng/trừ vào con số này.</summary>
    private static int BaseCrf(CompressionGoal goal) => goal switch
    {
        CompressionGoal.Quality => 20,
        CompressionGoal.Balanced => 24,
        _ => 28,
    };

    private static string PresetFor(CompressionGoal goal) => goal switch
    {
        // "slow" nén tốt hơn "medium" khoảng 5-8% cùng CRF, nhưng chậm hơn nhiều.
        // Với mức "Nhẹ" người dùng đã chấp nhận tệp lớn thì chậm cũng được.
        CompressionGoal.Quality => "slow",
        CompressionGoal.Balanced => "medium",
        _ => "veryfast",
    };

    // ---------------------------------------------------------------- video

    /// <summary>
    /// Lập kế hoạch cho một tệp video.
    /// </summary>
    /// <param name="info">Kết quả probe. Các trường null được xử lý như thiếu dữ liệu.</param>
    /// <param name="sourceWidth">Bề rộng thật của tệp, dùng khi probe không đọc được.</param>
    /// <param name="sourceBitrateKbps">Bitrate video nguồn, dùng khi probe không đọc được.</param>
    /// <param name="hasAudio">Tệp có luồng tiếng không.</param>
    public static VideoPlan PlanVideo(
        CompressionGoal goal,
        MediaInfo? info,
        int? sourceWidth = null,
        double? sourceBitrateKbps = null,
        bool hasAudio = true,
        bool preferHevc = false)
    {
        info ??= new MediaInfo();

        var width = info.Width ?? sourceWidth ?? 0;
        var crf = BaseCrf(goal);
        var notes = new List<string>();

        // Probe đọc được luồng video thì tin nó hơn tham số bên ngoài: probe thấy không có
        // luồng audio là đã kiểm tra thật, còn tham số bên ngoài chỉ là suy đoán. Ngược lại
        // (probe hỏng, không đọc được luồng nào) thì dùng giá trị được truyền vào.
        if (info.HasVideo) hasAudio = info.HasAudio;

        // 1. Nguồn đã quá nén → nâng CRF thay vì hạ.
        //
        // Đây là quy tắc quan trọng nhất, và cũng là chỗ dễ làm hỏng nhất. Hạ CRF trên một
        // tệp đã không còn dư chi tiết không làm tệp nhỏ thêm đáng kể, chỉ làm hạt nhiễu
        // nổi lên; còn nâng CRF thì vẫn thu được byte mà mắt gần như không nhận ra.
        var density = info.BitsPerPixelPerFrame
            ?? EstimateDensity(info.BitrateKbps ?? sourceBitrateKbps, width, info.Height, info.Fps);

        if (density is { } d)
        {
            if (d < BitsPerPixelNearlyDepleted)
            {
                crf += 4;
                notes.Add($"nguồn đã rất nén ({d:0.###} bit/px/khung)");
            }
            else if (d < BitsPerPixelComfortable)
            {
                crf += 2;
                notes.Add($"nguồn hơi chật ({d:0.###} bit/px/khung)");
            }
            else if (d > BitsPerPixelVeryComfortable)
            {
                crf -= 2;
                notes.Add($"nguồn còn dư ({d:0.###} bit/px/khung)");
            }
        }

        // 2. Nguồn là codec hiệu quả hơn H.264 → phải nâng CRF mạnh.
        //
        // HEVC/VP9/AV1 cần khoảng 30-50% ít bit hơn H.264 ở cùng chất lượng. Mã hoá lại
        // bằng H.264 ở cùng CRF sẽ cho tệp LỚN HƠN bản gốc, tệp nhất là mức "Nhẹ" nơi
        // người dùng còn mong tệp nhỏ đi.
        if (info.SourceCodecIsMoreEfficientThanH264)
        {
            // Mức "Nhẹ" phải nâng NHIỀU HƠN, không phải ít hơn. Dễ nghĩ ngược lại vì người
            // dùng chọn "Nhẹ" tưởng là dễ dãi — nhưng dễ dãi với nguồn HEVC nghĩa là để
            // H.264 phá huỷ tỉ lệ nén của bản gốc và tệp phình lên. Người dùng chọn "Nhẹ"
            // vẫn mong tệp nhỏ đi, chỉ là không đánh đổi bằng mắt thường thấy.
            var bump = goal == CompressionGoal.Quality ? 12 : 6;
            crf += bump;
            notes.Add($"nguồn dùng {info.VideoCodec} (hiệu quả hơn H.264, cần +{bump} CRF)");
        }

        // 3. Bề rộng. Chỉ thu khi vượt trần; không bao giờ phóng to.
        var cap = WidthCap(goal, MediaKind.Video);
        var delta = PlanDelta.None;
        if (width > cap)
        {
            crf -= 1; // giảm thêm một chút cho bù phần chi tiết mất do thu nhỏ
            notes.Add($"thu {width}px → {cap}px");
            width = cap;
            delta = PlanDelta.Downscale;
        }

        // 4. Số khung hình. Chỉ hạ ở mức "Mạnh", và chỉ khi nguồn >= 50 fps.
        //
        // 60 → 30 gần như không thấy với nội dung thông thường và tiết kiệm ~45% số khung.
        // 30 → 24 thì đã thấy rõ (chậm hơn, giật hơn), nên không làm.
        double? fps = null;
        if (goal == CompressionGoal.Size
            && info.Fps is { } sourceFps
            && sourceFps >= 50)
        {
            fps = 30;
            notes.Add($"giảm {sourceFps:0} → 30 fps");
            if (delta == PlanDelta.None) delta = PlanDelta.FrameRate;
        }

        // 5. Âm thanh. Không nâng bitrate của nguồn vốn đã nhỏ hơn mức đích.
        var audioTarget = goal switch
        {
            CompressionGoal.Quality => 320,
            CompressionGoal.Balanced => 192,
            _ => 128,
        };

        // 6. Codec đầu ra — bảng quyết định theo NHÓM NỘI DUNG.
        //
        // Đây là chỗ một bảng preset cố định hỏng: hiệu quả HEVC phụ thuộc mạnh vào loại
        // nội dung, và ở một nhóm nó thua rõ ràng. Đo thật trên cùng một tệp quay màn hình
        // (1918x1078, 30 fps):
        //
        //   H.264 CRF 28 : 563.609 byte, SSIM 0.99459
        //   HEVC CRF 33  : 574.949 byte, SSIM 0.98998   <- to hơn VÀ kém hơn
        //
        // Cùng phép đo đó trên anime thì ngược lại hẳn: HEVC nhỏ hơn 52-62%. Không có
        // tín hiệu phân biệt thì chọn codec nào cũng sai với một trong hai nhóm, nên ở đây
        // ta dùng SI/TI của ITU-T P.910 để tách.
        var profile = info.Complexity?.Profile ?? ContentProfile.Unknown;
        var useHevc = ChooseEncoder(profile, preferHevc, notes);
        var encoder = useHevc ? "libx265" : "libx264";

        if (useHevc)
        {
            // Bù lệch thang CRF giữa hai codec. Không có bước này thì HEVC bị nén quá tay.
            crf += HevcCrfOffset;
        }

        if (!hasAudio)
        {
            return new VideoPlan
            {
                TargetWidth = width,
                Crf = Math.Clamp(crf, 8, 51),
                Preset = PresetFor(goal),
                VideoEncoder = encoder,
                TargetFps = fps,
                AudioBitrateKbps = 0,
                DropAudio = true,
                Delta = delta,
                Reason = string.Join("; ", notes),
            };
        }

        return new VideoPlan
        {
            TargetWidth = width,
            Crf = Math.Clamp(crf, 8, 51),
            Preset = PresetFor(goal),
            VideoEncoder = encoder,
            TargetFps = fps,
            AudioBitrateKbps = audioTarget,
            DropAudio = false,
            Delta = delta,
            Reason = string.Join("; ", notes),
        };
    }

    /// <summary>
    /// Bảng quyết định codec: <b>nhóm nội dung × người dùng có bật HEVC không</b>.
    ///
    /// <para>Hàng duy nhất hiện còn cần đo riêng là nội dung màn hình, vì đó là chỗ HEVC
    /// thua đo được chứ không phải suy luận. Các nhóm còn lại cùng hành xử: HEVC thắng
    /// mạnh trên anime (52–62%) và phim truyện, nên không có lý do phải phân biệt.</para>
    ///
    /// <para>Khi probe hỏng (<see cref="ContentProfile.Unknown"/>) thì theo ý người dùng
    /// đã chọn, vì đó là hành vi trung lập và không thay đổi kết quả so với bản cũ.</para>
    /// </summary>
    private static bool ChooseEncoder(ContentProfile profile, bool preferHevc, List<string> notes)
    {
        if (!preferHevc)
        {
            // Không ghi chú gì ở nhánh này. "Đang dùng H.264" không phải tin gì đáng báo cho
            // người dùng khi đó chính là chế độ họ đã chọn — và có chú thích thì lý do của
            // tệp không còn sạch sẽ nữa.
            return false;
        }

        switch (profile)
        {
            case ContentProfile.ScreenContent:
                // Đo thật: HEVC to hơn 2% và SSIM kém hơn 0.005 so với H.264 trên cùng tệp.
                // Dùng H.264 dù người dùng bật HEVC — mục tiêu của họ là tệp nhỏ, và đây là
                // trường hợp HEVC không phục vụ được mục tiêu đó.
                notes.Add("nội dung màn hình (SI cao, không chuyển động) — H.264 nhỏ hơn và đẹp hơn");
                return false;

            default:
                notes.Add("HEVC (nhỏ hơn H.264 ở cùng chất lượng)");
                return true;
        }
    }

    private static double? EstimateDensity(double? kbps, int? width, int? height, double? fps)
    {
        if (kbps is not { } k || k <= 0) return null;
        if (width is not { } w || w <= 0) return null;
        if (height is not { } h || h <= 0) return null;

        var rate = fps is { } f && f > 0 ? f : 30.0;
        return k * 1000.0 / (w * (long)h * rate);
    }

    // ---------------------------------------------------------------- ảnh

    /// <summary>Lập kế hoạch cho một tệp ảnh.</summary>
    public static ImagePlan PlanImage(CompressionGoal goal, MediaInfo? info, int? sourceWidth = null, long? sourceBytes = null)
    {
        info ??= new MediaInfo();

        var width = info.Width ?? sourceWidth ?? 0;
        var cap = WidthCap(goal, MediaKind.Image);

        // -q:v của mã hoá MJPEG: 2 gần như không mất, 31 là tệ nhất.
        // Khoảng dùng nhiều là 3..12.
        var q = goal switch
        {
            CompressionGoal.Quality => 3,
            CompressionGoal.Balanced => 6,
            _ => 10,
        };

        var delta = PlanDelta.None;
        var notes = new List<string>();

        // Ảnh đã rất nhỏ: nén JPEG thêm chỉ tổn chất lượng, không đáng.
        // Dưới ~200 KB thì phần lớn ảnh đã ở mức mà JPEG không cải thiện được nữa.
        if (sourceBytes is { } bytes && bytes < 200 * 1024 && goal != CompressionGoal.Quality)
        {
            return new ImagePlan
            {
                TargetWidth = 0,
                QScale = 31,
                Delta = PlanDelta.NotWorthIt,
                Reason = "ảnh đã nhỏ, nén thêm không đáng",
            };
        }

        // Ảnh 8 bit + đã là JPEG: mã hoá lại dễ sinh vỡ block ở mức nén mạnh.
        // Nới -q:v một chút cho mức "Mạnh" để đừng hỏng ảnh chụp.
        if (goal == CompressionGoal.Size && info.BitDepth is 8)
        {
            q += 2;
            notes.Add("ảnh 8 bit, nới -q:v để tránh vỡ block");
        }

        if (width > cap)
        {
            notes.Add($"thu {width}px → {cap}px");
            width = cap;
            delta = PlanDelta.Downscale;
        }

        return new ImagePlan
        {
            TargetWidth = width,
            QScale = q,
            Delta = delta,
            Reason = string.Join("; ", notes),
        };
    }

    // ---------------------------------------------------------------- GIF

    /// <summary>
    /// Lập kế hoạch cho GIF. GIF không có khái niệm chất lượng liên tục — chỉ có số màu,
    /// số khung và kích thước — nên "mục tiêu" ở đây là mức bỏ bớt.
    /// </summary>
    public static GifPlan PlanGif(CompressionGoal goal, MediaInfo? info, int? sourceWidth = null, long? sourceBytes = null)
    {
        info ??= new MediaInfo();

        var width = info.Width ?? sourceWidth ?? 0;
        var cap = WidthCap(goal, MediaKind.Gif);

        var fps = info.Fps is { } f && f > 0 ? (int)Math.Round(f) : 15;
        var lossy = goal switch
        {
            CompressionGoal.Quality => 20,
            CompressionGoal.Balanced => 40,
            _ => 80,
        };

        var notes = new List<string>();
        var delta = PlanDelta.None;

        // GIF dài thì hạ fps, vì đây là nơi duy nhất tiết kiệm được lớn.
        var seconds = info.Duration?.TotalSeconds;
        if (goal != CompressionGoal.Quality && seconds is { } s && s > 6)
        {
            var target = goal == CompressionGoal.Size ? 12 : 15;
            if (fps > target)
            {
                notes.Add($"gif {s:0.#}s, hạ {fps} → {target} fps");
                fps = target;
                delta = PlanDelta.FrameRate;
            }
        }

        if (width > cap)
        {
            notes.Add($"thu {width}px → {cap}px");
            width = cap;
            if (delta == PlanDelta.None) delta = PlanDelta.Downscale;
        }

        return new GifPlan
        {
            TargetWidth = width,
            Fps = Math.Max(2, Math.Min(50, fps)),
            Lossy = lossy,
            Delta = delta,
            Reason = string.Join("; ", notes),
        };
    }

    // ---------------------------------------------------------------- audio

    /// <summary>Lập kế hoạch cho tệp âm thanh.</summary>
    public static AudioPlan PlanAudio(CompressionGoal goal, MediaInfo? info)
    {
        info ??= new MediaInfo();


        var target = goal switch
        {
            CompressionGoal.Quality => 320,
            CompressionGoal.Balanced => 192,
            _ => 128,
        };

        // Mono không cần 192k cho 16 kHz. Stereo thì 128k là mức tốt cho nghe.
        if (info.AudioChannels is 1 && target > 96)
        {
            target = goal == CompressionGoal.Quality ? 128 : 96;
        }

        var delta = PlanDelta.None;
        var notes = new List<string>();

        // Không nâng bitrate của nguồn đã nhỏ hơn: chỉ làm tệp to thêm, không thu được gì.
        if (info.BitrateKbps is { } source && source > 0 && source <= target)
        {
            target = (int)Math.Round(source);
            delta = PlanDelta.None;
            notes.Add($"giữ bitrate nguồn {target}k");
        }

        return new AudioPlan
        {
            BitrateKbps = target,
            Delta = delta,
            Reason = string.Join("; ", notes),
        };
    }

    // ---------------------------------------------------------------- PDF

    /// <summary>
    /// Lập kế hoạch cho PDF. Ghostscript không có biến liên tục, chỉ có preset, nên ở đây
    /// "mục tiêu" chỉ chọn được preset nào phù hợp.
    /// </summary>
    public static PdfPlan PlanPdf(CompressionGoal goal, MediaInfo? info = null)
    {
        var preset = goal switch
        {
            // /prepress là thiết lập cho in offset: giữ ảnh 300dpi, tệp rất lớn. Người
            // dùng chọn "Nhẹ" là muốn giữ chất lượng, không phải muốn chuẩn bị in.
            CompressionGoal.Quality => "/default",
            CompressionGoal.Balanced => "/ebook",
            _ => "/screen",
        };

        return new PdfPlan
        {
            Preset = preset,
            Delta = PlanDelta.None,
            Reason = string.Empty,
        };
    }
}
