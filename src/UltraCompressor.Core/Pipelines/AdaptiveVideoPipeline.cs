using System.Globalization;
using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Đường nén thích ứng theo nội dung: sinh tập ứng viên, đo thật, rồi mới encode toàn tệp.
///
/// <para>Khác <see cref="VideoPipeline"/> ở chỗ: kế hoạch cũ chọn tham số bằng quy tắc
/// <i>trước</i> khi biết tệp nén được không; ở đây ứng viên được đo trên chính những đoạn
/// khó nhất của tệp trước khi tốn công encode cả tệp. Người dùng thấy "giữ nguyên" hoặc
/// "nén" dựa trên số đo, không dựa trên một bảng tra cứu CRF.</para>
///
/// <para><b>Bốn kết cục, bốn hành vi khác nhau — đây là phần quan trọng nhất của lớp này.</b></para>
/// <list type="number">
/// <item><description><b><see cref="SearchDecisionReasons.PilotSelected"/></b> → encode toàn
/// tệp bằng phép biến đổi DÙNG CHUNG với phép thử, rồi lưới chất lượng cuối (Phase 5A) giữ
/// bất biến <c>NewSize &lt;= OldSize</c> như mọi đường khác.</description></item>
/// <item><description><b><see cref="SearchDecisionReasons.OriginalSelected"/></b> → có ứng
/// viên đạt chất lượng, nhưng không ứng viên nào chứng minh được lợi ích dung lượng đủ ý nghĩa
/// so với chính nguồn. Giữ bản gốc và <b>bỏ qua</b> một lần encode toàn tệp. Bản gốc là một
/// ứng viên ngang hàng, được so bằng số đo thật chứ không bằng metadata.</description></item>
/// <item><description><b><see cref="SearchDecisionReasons.NoFeasibleCandidate"/></b> → đã
/// thử mà không ứng viên nào đạt chất lượng. Giữ bản gốc, KHÔNG chạy lại bằng đường cũ.
/// Tệp đã nén hiệu quả là chuyện thường; chạy lại đường cũ ở đây sẽ âm thầm nén một tệp mà
/// ta vừa kết luận là không nén được.</description></item>
/// <item><description><b><see cref="SearchDecisionReasons.LegacyFallbackUsed"/></b> → hỏng
/// hạ tầng (không đo được ứng viên nào) → rơi về đường cũ, và ghi kèm mã lý do gốc. Chỉ
/// nhánh này được phép rơi, vì đây là lần duy nhất ta thật sự <i>không biết</i> và đường cũ
/// là cách duy nhất còn lại.</description></item>
/// </list>
///
/// <para>Hai kết cục giữ bản gốc <b>phải phân biệt</b>. Gộp chúng thì báo cáo không trả lời
/// được câu hỏi duy nhất quan trọng: lần nén này có <i>bỏ được việc mã hoá lại</i> không, và
/// vì sao.</para>
///
/// <para>Việc chọn giữa đường này và <see cref="VideoPipeline"/> do
/// <see cref="AppConfig.EnableAdaptiveSearch"/>, mặc định <c>false</c>. Tắt cờ thì hành vi
/// cũ giữ nguyên từng bít.</para>
///
/// <para>Đường cũ nhận vào qua <see cref="IMediaPipeline"/> chứ không phải lớp cụ thể, vì
/// nhánh rơi về đường cũ là nhánh cần kiểm thử nhất — và không thể kiểm thử được nếu phải
/// chạy ffmpeg thật. Chỉ cần nó là pipeline video.</para>
/// </summary>
public sealed class AdaptiveVideoPipeline(IMediaPipeline legacy) : FFmpegPipelineBase
{
    public override MediaKind Kind => MediaKind.Video;

    /// <summary>
    /// Mã lý do ghi kèm khi phải rơi về đường cũ.
    /// </summary>
    /// <remarks>
    /// Trỏ về <see cref="SearchDecisionReasons.LegacyFallbackUsed"/> chứ không khai một
    /// chuỗi thứ hai cùng giá trị. Hai hằng cùng nội dung là hai nguồn sự thật, và sớm muộn
    /// chúng sẽ lệch nhau — lúc đó thống kê đếm một chỗ mà log ghi chỗ kia.
    /// </remarks>
    public const string LegacyFallbackMarker = SearchDecisionReasons.LegacyFallbackUsed;

    /// <param name="context">Ngữ cảnh của job.</param>
    /// <param name="onProgress">Báo tiến trình 0…100.</param>
    /// <param name="token">Dừng theo yêu cầu người dùng.</param>
    public override Task<PipelineResult> RunAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Cờ tắt: đường cũ, nguyên vẹn. Không chỉ "gần giống" — cùng bộ dựng lệnh, cùng
        // kế hoạch, cùng kết quả, để so sánh được.
        if (!context.Config.EnableAdaptiveSearch)
        {
            return legacy.RunAsync(context, onProgress, token);
        }

        return RunAdaptiveAsync(context, onProgress, token);
    }

    private async Task<PipelineResult> RunAdaptiveAsync(
        PipelineContext context, Action<int> onProgress, CancellationToken token)
    {
        var ffmpeg = context.Tools.FFmpeg;
        if (ffmpeg is null)
        {
            return await FallbackAsync(
                context, onProgress, "không tìm thấy ffmpeg.exe", token).ConfigureAwait(false);
        }

        var probe = context.Probe ?? new MediaInfo();
        if (!probe.HasVideo)
        {
            return await FallbackAsync(
                context, onProgress, "probe không thấy luồng video", token).ConfigureAwait(false);
        }

        var sourceWidth = context.Item.SourceWidth ?? 0;
        var sourceHeight = context.Item.SourceHeight ?? 0;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return await FallbackAsync(
                context, onProgress, "không biết kích thước nguồn", token).ConfigureAwait(false);
        }

        var profile = new VideoSourceProfile
        {
            Width = sourceWidth,
            Height = sourceHeight,
            Fps = probe.Fps ?? 0,
            BitrateKbps = context.Item.SourceBitrateKbps,
            BitsPerPixelPerFrame = probe.BitsPerPixelPerFrame,
            Content = ContentProfile.Unknown,
            Complexity = probe.Complexity,

            // Bốn trường cuối chỉ để mô tả nguồn cho nhánh "giữ nguyên bản gốc". Không trường
            // nào trong số đó được phép quyết định — chúng chỉ được đọc khi so bản gốc với
            // ứng viên encode, tức là SAU khi đã có số đo thật.
            SizeBytes = context.Item.OldSize > 0 ? context.Item.OldSize : null,
            CodecName = probe.VideoCodec,
            HasAudio = probe.HasAudio,
            AudioBitrateKbps = probe.AudioBitrateKbps,
        };

        var plan = CandidatePlanner.Generate(
            profile,
            context.Level,
            context.Config.ComputeBudget,
            EncoderCapabilities.Baseline,
            context.Config);

        var candidates = plan.EncodeCandidates.ToList();
        if (candidates.Count == 0)
        {
            return await FallbackAsync(
                context, onProgress,
                $"planner không sinh được ứng viên nào: {plan.Diagnostics}", token).ConfigureAwait(false);
        }

        // Thư mục làm việc RIÊNG cho phép đo. KHÔNG dùng `context.TempPath` — đó là đường
        // dẫn tệp ĐẦU RA, không phải thư mục. Đưa nhầm vào bất kỳ công cụ nào (scanner,
        // encoder, probe) thì nó tạo một thư mục mang tên `out.mp4`, rồi bước encode toàn
        // tệp không ghi được tệp vào chính cái thư mục đó.
        //
        // Lỗi này chỉ lộ ra khi thật sự chạy: mọi unit test đều xanh vì không hề có tệp
        // nào được ghi.
        var searchDirectory = Path.Combine(
            Path.GetDirectoryName(context.TempPath) ?? Path.GetTempPath(),
            $"uc-adaptive-{Guid.NewGuid():N}");

        Directory.CreateDirectory(searchDirectory);

        try
        {
            var windows = await ChooseWindowsAsync(context, searchDirectory, token).ConfigureAwait(false);
            if (windows.Count == 0)
            {
                return await FallbackAsync(
                    context, onProgress, "không chọn được đoạn đại diện nào", token).ConfigureAwait(false);
            }

            SearchResult result;
            try
            {
                var search = new PilotSearch(
                    new PilotEncoder(ffmpeg, searchDirectory),
                    new ReferenceWindowExtractor(ffmpeg, searchDirectory),
                    new QualityProbe(ffmpeg, searchDirectory));

                result = await search.RunAsync(
                    new SearchRequest
                    {
                        SourcePath = context.SourcePath,
                        SourceSizeBytes = SourceFileBytes(context.SourcePath),
                        SourceWidth = sourceWidth,
                        SourceHeight = sourceHeight,
                        SourceDurationSeconds = probe.Duration?.TotalSeconds ?? 0,
                        SourceAudioBitrateKbps = context.Item.HasAudio is false
                        ? null
                        : EffectiveAudioKbpsForEstimate(
                            probe.AudioBitrateKbps, AudioTargetFor(context)),
                        HasAudio = probe.HasAudio,
                        Windows = windows,
                        Candidates = candidates,
                        Level = context.Level,
                        Model = VmafModels.Default,
                        MaxEvaluations = context.Config.MaxSearchEvaluations,

                        // Ngưỡng TIẾT KIỆM của cấu hình, truyền xuống để so bản gốc với ứng
                        // viên bằng đúng tiêu chuẩn người dùng đã đặt. Không có ngưỡng thứ hai
                        // ở đây — lớp tìm kiếm không được tự chế ra tiêu chuẩn riêng.
                        MinSavingPercent = context.Config.MinSavingPercent,

                        // MPEG-TS: đã đo thấy remux stream-copy sang container này làm mất
                        // 4,1 điểm VMAF mà không đổi một pixel, và nguyên nhân chưa truy ra
                        // được. Số đo trên nguồn như vậy có thể lệch vì DẤU THỜI GIAN chứ
                        // không phải vì nén, nên nó không được làm bằng chứng cho quyết định
                        // không hoàn tác được — tức không được dùng để kết luận giữ bản gốc.
                        Confidence = MediaClassifier.IsMpegTs(context.SourcePath)
                            ? MeasurementConfidence.Uncertain
                            : MeasurementConfidence.Trusted,
                    },
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Không lường trước được hỏng hạ tầng, nên bắt rộng ở đây rồi hạ cấp thành
                // fallback. Ném ra ngoài sẽ làm job chết mà không nén được gì.
                return await FallbackAsync(
                    context, onProgress, $"tìm kiếm ném ngoại lệ: {ex.Message}", token).ConfigureAwait(false);
            }

            // Bảng quyết định là một chỗ duy nhất; `switch` ở đây chỉ làm nhiệm vụ thi hành nó.
            return DecideFor(result.Status) switch
            {
                SearchDecision.EncodeFull => await EncodeFullAsync(
                    context, onProgress, result, token).ConfigureAwait(false),

                // Cả hai nhánh giữ bản gốc đều hợp lệ và đều không encode. Khác nhau ở chỗ
                // đã thử được gì, và `Describe` ghi rõ điều đó — kể cả khi hai nhánh này cùng
                // trả về `NotWorthIt`, người đọc vẫn phân biệt được. Mã kết cục đi kèm để
                // `item.DecisionReason` không bị trống trên đường thoát sớm này.
                SearchDecision.KeepOriginal or SearchDecision.KeepOriginalNoFeasible =>
                    PipelineResult.NotWorthIt(SkipReason.NotWorthIt, 0, Describe(result))
                        with
                    { Reason = OutcomeFor(result.Status) },

                _ => await FallbackAsync(
                    context, onProgress, $"{result.Outcome.Reason}: {result.Outcome.Message}", token)
                    .ConfigureAwait(false),
            };
        }
        finally
        {
            // Dọn cả thư mục, không chỉ các tệp: `Release` của encoder xoá clip, nhưng thư
            // mục rỗng thì tích luỹ lại tới hàng trăm thư mục rỗng trong workspace.
            TryDeleteDirectory(searchDirectory);
        }
    }

    /// <summary>Hành động mà mỗi trạng thái tìm kiếm dẫn tới.</summary>
    public enum SearchDecision
    {
        /// <summary>Có ứng viên do được và chứng minh được lợi ích: encode toàn tệp rồi để
        /// lưới cuối giữ.</summary>
        EncodeFull,

        /// <summary>
        /// Có ứng viên đạt chất lượng nhưng không chứng minh được lợi ích dung lượng: giữ bản
        /// gốc, bỏ qua một lần encode.
        /// </summary>
        KeepOriginal,

        /// <summary>
        /// Không ứng viên nào đạt chất lượng: giữ bản gốc.
        /// </summary>
        /// <remarks>
        /// Tách khỏi <see cref="KeepOriginal"/> vì hai tình huống nói khác nhau: một cái là
        /// "đã tìm, không có gì đáng làm", cái kia là "không tìm được gì đạt". Cùng hành vi
        /// thi hành, nhưng khác ý nghĩa — và báo cáo phải cho biết cái nào đã xảy ra.
        /// </remarks>
        KeepOriginalNoFeasible,

        /// <summary>Không đo được gì: rơi về đường cũ, kèm mã lý do gốc.</summary>
        FallBackToLegacy,
    }

    /// <summary>
    /// Bảng ánh xạ trạng thái tìm kiếm sang hành động. Tách riêng để bảng quyết định quan
    /// trọng nhất của đường này được kiểm thử trọn vẹn, thay vì nằm ẩn trong một biểu
    /// thức <c>switch</c> chỉ chạy được khi có ffmpeg.
    ///
    /// <para>Hàm cố tình dùng <c>_</c> cho trạng thái lạ thay vì ném lỗi: trạng thái mới
    /// thêm vào về sau mặc định phải an toàn (không nén bừa), và <c>EncodeFull</c> thì không
    /// phải mặc định an toàn.</para>
    /// </summary>
    public static SearchDecision DecideFor(SearchStatus status) => status switch
    {
        SearchStatus.SelectedCandidate => SearchDecision.EncodeFull,
        SearchStatus.OriginalSelected => SearchDecision.KeepOriginal,
        SearchStatus.NoFeasibleCandidate => SearchDecision.KeepOriginalNoFeasible,
        _ => SearchDecision.FallBackToLegacy,
    };

    /// <summary>
    /// Mã kết cục của đường chạy, để báo cáo và thống kê đếm được.
    /// </summary>
    /// <remarks>
    /// Lấy từ trạng thái chứ không lấy từ câu chữ trong <c>Outcome</c>: câu chữ sẽ được viết
    /// lại, còn bốn mã này là hợp đồng với người đọc báo cáo và với thống kê.
    /// </remarks>
    public static string OutcomeFor(SearchStatus status) => status switch
    {
        SearchStatus.SelectedCandidate => SearchDecisionReasons.PilotSelected,
        SearchStatus.OriginalSelected => SearchDecisionReasons.OriginalSelected,
        SearchStatus.NoFeasibleCandidate => SearchDecisionReasons.NoFeasibleCandidate,
        _ => SearchDecisionReasons.LegacyFallbackUsed,
    };

    private static async Task<PipelineResult> EncodeFullAsync(
        PipelineContext context,
        Action<int> onProgress,
        SearchResult result,
        CancellationToken token)
    {
        if (result.Selected is not { } selected)
        {
            // Không xảy ra nếu trạng thái là SelectedCandidate, nhưng nếu xảy ra thì phải
            // hạ cấp xuống "không nén" chứ không phải encode bừa một ứng viên nào đó.
            return PipelineResult.NotWorthIt(
                SkipReason.NotWorthIt, 0, "trạng thái nói có lựa chọn nhưng không có ứng viên nào");
        }

        var sourceWidth = context.Item.SourceWidth ?? 0;
        var sourceHeight = context.Item.SourceHeight ?? 0;

        if (!EncodeTarget.TryFromRequest(
                sourceWidth, sourceHeight,
                selected.Candidate.Width, selected.Candidate.Height,
                out var target, out var targetFailure))
        {
            return PipelineResult.Failed(
                SkipReason.Error, $"Kích thước ứng viên không hợp lệ: {targetFailure}");
        }

        var filter = EncodeTransform.BuildFilter(target, sourceWidth, sourceHeight);

        // Dùng CHUNG phép biến đổi với phần thử. Nếu encode toàn tệp dùng bộ lọc khác thì
        // ta đã đo một thứ và nén một thứ khác, và mọi số VMAF thu được là vô nghĩa.
        var args = EncodeTransform.BuildFullArguments(
            selected.Candidate.ToEncoderConfiguration(),
            filter,
            context.SourcePath,
            context.TempPath,
            AudioTargetFor(context));

        var (process, duration) = await ExecuteAsync(context, args, onProgress, token)
            .ConfigureAwait(false);

        var outcome = Interpret(process, duration, context.Item, context.TempPath);
        return outcome.Success
            ? outcome with { Message = Describe(result) }
            : outcome;
    }

    private async Task<PipelineResult> FallbackAsync(
        PipelineContext context,
        Action<int> onProgress,
        string rootCause,
        CancellationToken token)
    {
        var legacyResult = await legacy.RunAsync(context, onProgress, token).ConfigureAwait(false);

        // Ghi mã lý do GỐC, không chỉ "đã dùng đường cũ". Người đọc cần biết tìm kiếm hỏng ở
        // đâu; nếu chỉ thấy LEGACY_FALLBACK_USED thì không sửa được gì.
        var marker = $"{LegacyFallbackMarker} ({rootCause})";

        // `Reason` ghi kèm để `item.DecisionReason` không bị trống: khi đường cũ chạy được
        // thì kết quả là thành công và lưới 5A sẽ gán mã của nó, nhưng khi đường cũ cũng bỏ
        // qua thì đây là lý do duy nhất còn lại để giải thích.
        return legacyResult with
        {
            Reason = LegacyFallbackMarker,
            Message = string.IsNullOrWhiteSpace(legacyResult.Message)
            ? marker
            : $"{marker} | {legacyResult.Message}"
        };
    }

    /// <summary>
    /// Kích thước tệp nguồn, byte.
    ///
    /// <para>Đọc trong thử: tệp có thể vừa bị xoá hoặc không còn quyền đọc giữa lúc lập kế
    /// hoạch và lúc gọi, và để <c>FileInfo</c> ném ra ngoài ở đây sẽ làm cả job chết.
    /// Không đọc được thì trả 0 — chỉ dùng cho báo cáo tiết kiệm, và 0 khiến báo cáo nói
    /// "không rõ" chứ không nói dối.</para>
    /// </summary>
    private static long SourceFileBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        // Dọn dẹp KHÔNG được làm hỏng kết quả nén, nên nuốt lỗi. Thường là ffmpeg chưa
        // thoát hẳn và Windows còn giữ handle; thư mục tạm, sẽ được dọn ở lượt sau.
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static int? AudioTargetFor(PipelineContext context)
    {
        if (context.Item.HasAudio is false) return null;

        // Cùng trần mục tiêu theo mức nén như đường cũ: Nhẹ 320k, Cân bằng 192k, Mạnh 128k.
        // Đường thích ứng không được tự phát minh một mục tiêu âm thanh khác, vì âm thanh
        // không thuộc phạm vi tìm kiếm video.
        var target = CompressionProfile.For(context.Level).AudioBitrateKbps;

        // Không nâng bitrate của nguồn vốn đã nhỏ hơn mục tiêu. Dùng bitrate âm thanh thật
        // từ probe, không dùng bitrate tổng của tệp: tổng 2.612 kb/s không phải là trần
        // hợp lý cho một luồng âm thanh 250 kb/s.
        if (context.Probe?.AudioBitrateKbps is { } source && source > 0 && source < target)
        {
            target = (int)Math.Round(source);
        }

        return target;
    }

    /// <summary>
    /// Bitrate âm thanh để ƯỚC LƯỢNG dùng — phải là con số mà bản full encode sẽ dùng.
    ///
    /// <para>Trước đây truyền thẳng bitrate nguồn vào estimator, sai đúng một trường hợp:
    /// nguồn lớn hơn mục tiêu (nguồn 250k, mục tiêu 192k) thì phần audio ước thừa 58k ×
    /// thời lượng — trên tệp 300s là thừa ~2 MB, đủ làm lệch xếp hạng hai ứng viên gần
    /// nhau. Dùng min ở đây thì ước lượng khớp với lệnh encode thật.</para>
    /// </summary>
    internal static double? EffectiveAudioKbpsForEstimate(double? probeAudioKbps, int? encodeTargetKbps)
    {
        if (encodeTargetKbps is not { } target || target <= 0)
        {
            // Không encode âm thanh (hoặc không biết mục tiêu): để estimator đi đường
            // "không rõ" của nó thay vì bịa 0.
            return probeAudioKbps is > 0 ? probeAudioKbps : null;
        }

        if (probeAudioKbps is not > 0)
        {
            return target;
        }

        return Math.Min(probeAudioKbps.Value, target);
    }

    private static async Task<IReadOnlyList<RepresentativeWindow>> ChooseWindowsAsync(
        PipelineContext context, string tempDirectory, CancellationToken token)
    {
        var scanner = new TimelineScanner(context.Tools.FFmpeg!, tempDirectory);
        var duration = context.Item.DurationSeconds is { } d && d > 0
            ? TimeSpan.FromSeconds(d)
            : (TimeSpan?)null;

        var samples = await scanner
            .ScanAsync(context.SourcePath, duration, context.Config, token)
            .ConfigureAwait(false);

        var selection = RepresentativeWindowSelector.Select(
            samples, duration, context.Config, ScanStats.None);

        return selection.Windows;
    }

    /// <summary>Câu giải thích cho người đọc báo cáo, đủ để trả lời "vì sao chọn cái này".</summary>
    /// <remarks>
    /// Câu này luôn <b>bắt đầu bằng mã kết cục</b> (<c>PILOT_SELECTED</c>,
    /// <c>ORIGINAL_SELECTED</c>, <c>NO_FEASIBLE_CANDIDATE</c> hay
    /// <c>LEGACY_FALLBACK_USED</c>), vì người đọc cần lọc và đếm theo kết cục trước khi đọc
    /// tới lý do. Với lần nén giữ nguyên bản gốc, câu này còn ghi thẳng
    /// <b>số byte toàn tệp đã tiết kiệm</b> — con số duy nhất chứng minh được giai đoạn 5B
    /// có tác dụng hay không.
    /// </remarks>
    private static string Describe(SearchResult result)
    {
        var s = result.Statistics;
        var summary =
            $"{OutcomeFor(result.Status)} — "
            + $"tìm kiếm: {s.CandidatesEvaluated}/{s.CandidatesPlanned} ứng viên đã đo, "
            + $"{s.QualityMeasurements} phép đo VMAF, "
            + $"{s.MeasurementsSavedByEarlyReject} phép đo tiết kiệm nhờ loại sớm, "
            + $"{s.CandidatesPruned} ứng viên bị cắt theo giả định đơn điệu, "
            + $"{s.TotalElapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s — "
            + result.Outcome.Message;

        if (result.Selected is { } selected)
        {
            summary +=
                $"; chọn {selected.Candidate.Id} "
                + $"(VMAF đoạn tệ nhất {QualityAggregator.RankingQuality(selected.Aggregate).ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"ước lượng {(selected.Estimate.TotalBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB)";
        }

        if (result.OriginalComparison is { } comparison)
        {
            summary += comparison.FullEncodeAvoided
                ? $"; bỏ qua 1 lần encode toàn tệp, tiết kiệm "
                    + $"{comparison.SourceBytes.ToString("N0", CultureInfo.InvariantCulture)} B "
                    + $"({(comparison.SourceBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB) "
                    + "và toàn bộ thời gian encode"
                : $"; đã so với bản gốc: {comparison.Message}";
        }

        if (s.BranchesNonMonotonic > 0)
        {
            summary +=
                $"; cảnh báo {SearchDecisionReasons.NonMonotonicBranchObserved} ở "
                + $"{s.BranchesNonMonotonic}/{s.BranchesTotal} nhánh — phép đo không đơn điệu, "
                + "xem nhật ký chi tiết";
        }

        return summary;
    }
}
