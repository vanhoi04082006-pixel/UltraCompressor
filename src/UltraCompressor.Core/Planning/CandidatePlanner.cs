using System.Globalization;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Planning;

/// <summary>Đặc trưng nguồn dùng làm <b>ưu tiên</b> khi sinh ứng viên.</summary>
public sealed record VideoSourceProfile
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double Fps { get; init; }

    public double? BitrateKbps { get; init; }

    public double? BitsPerPixelPerFrame { get; init; }

    public ContentProfile Content { get; init; } = ContentProfile.Unknown;

    public ContentComplexity? Complexity { get; init; }

    /// <summary>
    /// Byte thật của tệp nguồn. Không phải ưu tiên — mà là <b>sự thật</b> mà nhánh
    /// <see cref="OriginalCandidate"/> khai báo. Thiếu thì nhánh đó không sinh ra, vì một
    /// ứng viên mà không biết kích thước thì không so được với bất cứ ứng viên nào.
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>Codec của nguồn, để mô tả. Không dùng để quyết định.</summary>
    public string? CodecName { get; init; }

    public bool HasAudio { get; init; }

    public double? AudioBitrateKbps { get; init; }
}

/// <summary>
/// Sinh tập ứng viên có cấu trúc: codec → nhánh độ phân giải → các điểm chất lượng.
///
/// <para>Hàm thuần: cùng đầu vào thì cùng tập ứng viên theo cùng thứ tự. Không mở tệp,
/// không gọi ffmpeg, không encode. Chỉ <i>sinh</i> phương án — quyết định thắng thua thuộc
/// giai đoạn sau, sau khi đo.</para>
///
/// <para>Ba điều kiện bất di bất dịch, mỗi cái có một test riêng chặn:</para>
/// <list type="number">
/// <item><b>Mode không bao giờ ánh xạ thẳng ra tham số encoder.</b> Mode dịch vị trí
/// trung tâm của vùng tìm trong một băng hẹp quanh mặc định của codec. Không có
/// <c>Balanced ⇒ CRF 26</c>, không có <c>Strong ⇒ 720p</c>.</item>
/// <item><b>Không phóng to, không tăng FPS, không phá tỉ lệ khung hình.</b> Mọi ứng viên có
/// kích thước không vượt quá nguồn và FPS bằng đúng FPS nguồn.</item>
/// <item><b>Đặc trưng nguồn chỉ là ưu tiên.</b> Nó dịch vị trí trung tâm và quyết định có
/// mở nhánh hình nhỏ hơn không. Nó không được tuyên bố ứng viên nào "chắc chắn đạt chất
/// lượng", và không được dùng để kết luận "giữ nguyên bản gốc" — đó là việc của
/// <c>QualityProbe</c> ở giai đoạn sau.</item>
/// <item><b>Nhánh giữ nguyên bản gốc là ứng viên ngang hàng.</b> Nó nằm trong
/// <see cref="CandidatePlan.Candidates"/> từ đầu, có định danh ổn định, và chỉ được thắng
/// bằng <b>số đo thật</b> ở <c>OriginalComparison</c> — không bằng bất kỳ ngưỡng metadata
/// nào.</item>
/// </list>
/// </summary>
public static class CandidatePlanner
{
    /// <summary>
    /// Các mốc chiều cao dùng làm "rung" thang. Chọn rung <b>gần nguồn nhất và thấp hơn</b>,
    /// không lấy rung cố định theo mode.
    /// </summary>
    private static readonly int[] HeightRungs = [2160, 1440, 1080, 720, 480, 360];

    /// <summary>Số ứng viên tối đa. Trần cứng chống cấu hình sai gây lãng phí.</summary>
    private const int AbsoluteMaxCandidates = 96;

    public static CandidatePlan Generate(
        VideoSourceProfile source,
        CompressionLevel level,
        ComputeBudget budget,
        EncoderCapabilities capabilities,
        AppConfig config)
    {
        var notes = new List<string>();
        var rejected = new List<string>();
        var considered = new List<string>();
        var skipped = new List<string>();

        if (source.Width <= 0 || source.Height <= 0)
        {
            return new CandidatePlan
            {
                Candidates = [],
                Diagnostics = new CandidateDiagnostics
                {
                    ResolutionBranches = 0,
                    CodecsConsidered = [],
                    CodecsSkipped = [],
                    Rejected = ["nguồn không đọc được kích thước"],
                    DuplicatesRemoved = 0,
                    Truncated = 0,
                    Notes = ["Không sinh ứng viên nào: thiếu kích thước nguồn."],
                },
            };
        }

        var branches = ResolutionBranches(source, budget, level, config, notes, rejected);
        if (branches.Count == 0)
        {
            return new CandidatePlan
            {
                Candidates = [],
                Diagnostics = new CandidateDiagnostics
                {
                    ResolutionBranches = 0,
                    CodecsConsidered = [],
                    CodecsSkipped = [],
                    Rejected = rejected,
                    DuplicatesRemoved = 0,
                    Truncated = 0,
                    Notes = notes,
                },
            };
        }

        var domains = DomainsToTry(capabilities, config, budget, considered, skipped, notes);
        if (domains.Count == 0)
        {
            return new CandidatePlan
            {
                Candidates = [],
                Diagnostics = new CandidateDiagnostics
                {
                    ResolutionBranches = branches.Count,
                    CodecsConsidered = considered,
                    CodecsSkipped = skipped,
                    Rejected = rejected,
                    DuplicatesRemoved = 0,
                    Truncated = 0,
                    Notes = notes,
                },
            };
        }

        var bias = ContentBias(source);
        var pointsPerBranch = QualityPointsPerBranch(budget);
        var candidates = new List<VideoEncodeCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;

        // Nhánh độ phân giải: lớn xuống nhỏ. Ứng viên đầu tiên của mỗi nhánh là điểm dò
        // thô, những điểm sau là neo chất lượng để khoanh biên ở giai đoạn tìm kiếm.
        foreach (var branch in branches)
        {
            foreach (var domain in domains)
            {
                var speed = domain.Speed(budget);
                var pixelFormat = domain.PixelFormats[0];
                var tune = domain.TuneFor(source.Content);
                var branchId = $"{domain.Codec}/{branch.Width}x{branch.Height}";

                var points = domain.CoarseQualityPoints(
                    level, bias, branch.DetailScale(source), pointsPerBranch);

                for (var i = 0; i < points.Count; i++)
                {
                    var quality = points[i];

                    if (!domain.Validate(quality, branch.Width, branch.Height, out var error))
                    {
                        rejected.Add(error);
                        continue;
                    }

                    // Ép tham số chất lượng thành tuỳ chọn có kiểu ngay tại đây, một lần và
                    // duy nhất. Từ chỗ này trở đi không còn con số chất lượng trần nào nữa,
                    // nên không thể vô tình so số của x264 với số của libaom.
                    var qualityOption = domain.Quality(quality);

                    var candidate = new VideoEncodeCandidate(
                        $"{branchId}/{qualityOption.Switch.TrimStart('-')}{qualityOption.Text}")
                    {
                        Codec = domain.Codec,
                        EncoderName = domain.EncoderName,
                        Quality = qualityOption,
                        Width = branch.Width,
                        Height = branch.Height,
                        Fps = source.Fps,
                        Speed = speed,
                        PixelFormat = pixelFormat,
                        Tune = tune,
                        Origin = i == 0 ? CandidateOrigin.CoarseProbe : CandidateOrigin.QualityAnchor,
                        BranchId = branchId,
                        PointIndex = i,
                        PointCount = points.Count,
                        Reason = Explain(domain, branch, quality, speed, i, points.Count),
                    };

                    // Loại trùng ngữ nghĩa: cùng codec, cùng kích thước, cùng tham số chất
                    // lượng, cùng preset, cùng tune. Giai đoạn này chưa có số đo kích
                    // thước thật nên chưa loại được ứng viên bị chi phối, nhưng trùng ngữ
                    // nghĩa thì loại được ngay.
                    var key = string.Join('|',
                        candidate.EncoderName, candidate.Width, candidate.Height,
                        qualityOption.Switch, qualityOption.Text,
                        speed.Switch, speed.Text, candidate.PixelFormat, candidate.Tune ?? string.Empty);

                    if (!seen.Add(key))
                    {
                        duplicates++;
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }
        }

        var truncated = 0;
        var cap = Math.Clamp(config.MaxInitialCandidates, 1, AbsoluteMaxCandidates);
        if (candidates.Count > cap)
        {
            truncated = candidates.Count - cap;
            candidates = [.. candidates.Take(cap)];
            notes.Add($"Giới hạn {cap} ứng viên, bỏ {truncated} ứng viên ngoài giới hạn.");
        }

        return new CandidatePlan
        {
            // Nhánh không-nén đứng ĐẦU danh sách, vì nó là ứng viên hợp lệ và phải được nhìn
            // thấy cùng các ứng viên khác — không phải một nhánh phụ sinh ra sau.
            //
            // Thêm SAU khi cắt giới hạn, cố ý: nhánh này không tốn encode nào, nên nó không
            // được phép bị loại chỉ vì danh sách ứng viên encode chạm trần. Bỏ nhánh không-nén
            // vì cấu hình hẹp là đúng cái lỗi mà giai đoạn này sinh ra để chữa.
            Candidates = PrependOriginal(candidates, source),
            Diagnostics = new CandidateDiagnostics
            {
                ResolutionBranches = branches.Count,
                CodecsConsidered = considered,
                CodecsSkipped = skipped,
                Rejected = rejected,
                DuplicatesRemoved = duplicates,
                Truncated = truncated,
                Notes = notes,
            },
        };
    }

    /// <summary>
    /// Sinh nhánh giữ nguyên bản gốc, hoặc không sinh gì nếu chưa biết byte nguồn.
    /// </summary>
    /// <remarks>
    /// <b>Không dùng metadata để quyết định</b> ở đây, và đó là điểm mấu chốt: điều kiện duy
    /// nhất là "biết tệp nguồn nặng bao nhiêu byte". Không hề có <c>bpppf &lt; x</c>, không có
    /// <c>bitrate &lt; y</c>, không có <c>codec == AV1 thì giữ nguyên</c>. Những thứ đó là
    /// điều ta <i>đoán</i>, và đoán thì không được phép ra quyết định không hoàn tác được.
    /// Nhánh này chỉ được thắng sau khi có số đo thật, ở <c>OriginalComparison</c>.
    /// </remarks>
    private static IReadOnlyList<CompressionCandidate> PrependOriginal(
        List<VideoEncodeCandidate> encodes, VideoSourceProfile source)
    {
        if (source.SizeBytes is not { } bytes || bytes <= 0)
        {
            return [.. encodes];
        }

        return
        [
            new OriginalCandidate(OriginalCandidate.StableId, bytes)
            {
                Width = source.Width,
                Height = source.Height,
                Fps = source.Fps,
                CodecName = source.CodecName,
                HasAudio = source.HasAudio,
                AudioBitrateKbps = source.AudioBitrateKbps,
            },
            .. encodes,
        ];
    }

    // ---------------------------------------------------------------- nhánh hình

    /// <summary>
    /// Một nhánh ứng viên: một tổ hợp codec × kích thước. Mỗi nhánh được dò thô ở điểm đầu
    /// rồi khoanh biên bằng các điểm sau, thay vì dựng sẵn một danh sách CRF phẳng cho cả tệp.
    /// </summary>
    private readonly record struct Branch(int Width, int Height, bool IsSourceSize)
    {
        /// <summary>
        /// Tỉ lệ số pixel so với nguồn, dùng để giữ chất lượng cảm nhận khi hạ độ phân giải.
        /// Nhánh nguồn = 1.
        /// </summary>
        public double DetailScale(VideoSourceProfile source) =>
            source.Width * source.Height <= 0 ? 1.0 : (double)Width * Height / (source.Width * source.Height);
    }

    private static List<Branch> ResolutionBranches(
        VideoSourceProfile source,
        ComputeBudget budget,
        CompressionLevel level,
        AppConfig config,
        List<string> notes,
        List<string> rejected)
    {
        var branches = new List<Branch> { new(source.Width, source.Height, true) };

        var wanted = budget switch
        {
            ComputeBudget.Fast => 1,
            ComputeBudget.Thorough => 3,
            _ => 2,
        };

        // Mức Mạnh được phép mở nhiều nhánh hơn ở ngân sách thường, vì người dùng đã nói
        // rằng chấp nhận giảm mạnh chất lượng để lấy dung lượng. Đây là *giới hạn* của việc
        // thử, không phải quyết định cuối cùng.
        if (level == CompressionLevel.Strong && budget != ComputeBudget.Fast) wanted++;
        wanted = Math.Max(1, Math.Min(wanted, Math.Max(1, config.MaxResolutionBranches)));

        foreach (var rung in HeightRungs)
        {
            if (branches.Count >= wanted) break;

            // Chỉ đi xuống. Nguồn 720p không được sinh 1440p.
            if (rung >= source.Height) continue;

            // Không xuống quá thấp so với nguồn: từ 1080p xuống 360p là mất 2/3 chiều cao,
            // thường là thứ tệp sẽ không được dùng tới. Cần một mức sàn tương đối.
            if (rung < source.Height * 0.5) continue;

            var width = SnapWidth(source.Width, source.Height, rung);
            if (width <= 0 || width % 2 != 0)
            {
                rejected.Add($"bỏ rung {rung}p: bề rộng tính ra không chẵn ({width})");
                continue;
            }

            branches.Add(new Branch(width, rung, false));
        }

        if (branches.Count < wanted)
        {
            notes.Add(
                $"Nguồn {source.Width}x{source.Height} chỉ cho phép {branches.Count} nhánh hình " +
                $"(mong muốn {wanted}); không phóng to để lấp chỗ trống.");
        }

        return branches;
    }

    /// <summary>Giữ đúng tỉ lệ khung hình của nguồn, làm tròn về số chẵn cho codec.</summary>
    private static int SnapWidth(int sourceWidth, int sourceHeight, int targetHeight)
    {
        if (targetHeight <= 0) return 0;
        var width = (int)Math.Round((double)sourceWidth * targetHeight / sourceHeight, MidpointRounding.AwayFromZero);
        return Math.Max(2, width - (width % 2));
    }

    // ---------------------------------------------------------------- codec

    private static List<IEncoderSearchDomain> DomainsToTry(
        EncoderCapabilities capabilities,
        AppConfig config,
        ComputeBudget budget,
        List<string> considered,
        List<string> skipped,
        List<string> notes)
    {
        var domains = new List<IEncoderSearchDomain>();
        var all = new IEncoderSearchDomain[]
        {
            new X264SearchDomain(),
            new X265SearchDomain(),
            new LibaomAv1SearchDomain(),
        };

        foreach (var domain in all)
        {
            if (!capabilities.Has(domain.EncoderName))
            {
                skipped.Add($"{domain.EncoderName} (bản ffmpeg này không có)");
                continue;
            }

            if (domain.Codec == VideoCodec.Av1 && !config.EnableAv1Search)
            {
                skipped.Add($"{domain.EncoderName} (tắt mặc định: libaom chậm hơn libx265 một đến hai bậc độ lũy)");
                continue;
            }

            if (domain.Codec == VideoCodec.Av1 && budget != ComputeBudget.Thorough)
            {
                skipped.Add($"{domain.EncoderName} (chỉ thử ở ngân sách Kỹ)");
                continue;
            }

            considered.Add(domain.EncoderName);
            domains.Add(domain);
        }

        // Không codec nào dùng được thì báo rõ, thay vì trả về tập rỗng im lặng.
        if (domains.Count == 0) notes.Add("Không có codec video nào khả dụng.");

        return domains;
    }

    // ---------------------------------------------------------------- điểm chất lượng

    private static int QualityPointsPerBranch(ComputeBudget budget) => budget switch
    {
        ComputeBudget.Fast => 2,
        ComputeBudget.Thorough => 4,
        _ => 3,
    };

    /// <summary>
    /// Độ lệch do nội dung, trên thang [−1, 1]. Âm = dời vùng tìm về chất lượng cao hơn.
    ///
    /// <para>Giá trị này chỉ dịch <b>vị trí trung tâm của vùng tìm</b>; nó không phải dự
    /// đoán chất lượng, và tuyệt đối không được dùng để kết luận "không đáng nén".</para>
    ///
    /// <para><b>Chỉ dùng tín hiệu độ khó nội dung.</b> Mật độ bit của nguồn
    /// (<see cref="VideoSourceProfile.BitsPerPixelPerFrame"/>) là số đo thật và rất hữu ích
    /// để dự đoán dung lượng, nhưng ở đây nó <b>không</b> được dùng để dịch vùng tìm, vì
    /// chiều ảnh hưởng chưa được xác lập bằng số đo nào trong kho.</para>
    ///
    /// <para>Trước đây chỗ này dịch theo mật độ bit, kèm comment giải thích <i>ngược
    /// chiều</i> với đoạn code ngay bên dưới: comment nói nguồn hết dự trữ thì "nén nhẹ
    /// hơn", còn code lại hạ tham số chất lượng — tức nén <i>nặng</i> hơn. Cả hai hướng
    /// đều nghe hợp lý:</para>
    /// <list type="bullet">
    /// <item><description>hướng "nén mạnh": nguồn đã không còn chi tiết, giữ chất lượng cao
    /// cũng không thu được byte nào, chỉ tốn dung lượng.</description></item>
    /// <item><description>hướng "nén nhẹ": nguồn đã bị nén đến mức hạt nhiễu lộ lên, nén thêm
    /// sẽ hỏng hình, nên phải giữ chất lượng.</description></item>
    /// </list>
    ///
    /// <para>Không có số đo nào trong kho để chọn giữa hai hướng, nên thay vì tung đồng xu,
    /// mật độ bit bị loại khỏi vị trí này và ghi lại là câu hỏi mở. Nó vẫn được giữ trong
    /// hồ sơ nguồn để giai đoạn tìm kiếm và giai đoạn hiệu chỉnh dùng.</para>
    /// </summary>
    internal static double ContentBias(VideoSourceProfile source)
    {
        var bias = 0.0;

        if (source.Complexity is { Samples: > 0 } complexity)
        {
            // Nội dung nhiều chuyển động khó hơn, nên dời về chất lượng cao hơn một chút:
            // vùng tìm rộng ra phía chất lượng cao, và bù lại ở nhánh chất lượng thấp hơn.
            //
            // Chiều này kiểm chứng được mà không cần đo chất lượng: nhiều chuyển động nghĩa
            // là nhiều khối phải dựng lại mỗi khung hình, và đó là nơi xuất hiện hạt và
            // nhấp nháy trước tiên khi siết tham số chất lượng.
            var motion = Math.Clamp(complexity.TemporalActivity / 30.0, 0, 1);
            bias -= motion * 0.3;
        }

        return Math.Clamp(bias, -1, 1);
    }

    // ---------------------------------------------------------------- lời giải thích

    /// <summary>
    /// Lời giải thích cho người đọc log: ứng viên này là gì, sinh ra vì lý do gì, và
    /// <b>cấu hình thật</b> của nó là bao nhiêu.
    /// </summary>
    private static string Explain(
        IEncoderSearchDomain domain,
        Branch branch,
        double quality,
        SpeedOption speed,
        int pointIndex,
        int pointCount)
    {
        var size = branch.IsSourceSize
            ? $"giữ nguyên độ phân giải nguồn {branch.Width}x{branch.Height}"
            : $"nhánh nhỏ hơn {branch.Width}x{branch.Height}";

        var role = pointIndex == 0
            ? "điểm dò thô"
            : pointIndex < pointCount - 1
                ? "neo chất lượng để khoanh biên"
                : "điểm nén mạnh nhất của nhánh";

        // In ra cấu hình thật. Trước đây chỗ này in chỉ số điểm ("2/3"), tức là log nói
        // "chất lượng = 2/3" — một câu trả lời không ai dùng được để dựng lệnh ffmpeg.
        // Tuỳ chọn đã có kiểu nên tự biết tên công tắc của mình.
        return $"{domain.EncoderName} · {size} · {role} · {speed} · "
            + $"{domain.Quality(quality)}";
    }
}
