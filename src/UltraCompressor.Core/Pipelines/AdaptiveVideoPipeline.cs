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
/// <para><b>Ba kết cục, ba hành vi khác nhau — đây là phần quan trọng nhất của lớp này.</b></para>
/// <list type="number">
/// <item><description><b>Có ứng viên đạt</b> → encode toàn tệp bằng phép biến đổi DÙNG CHUNG
/// với phép thử, rồi lưới chất lượng cuối (Phase 5A) giữ bất biến
/// <c>NewSize &lt;= OldSize</c> như mọi đường khác.</description></item>
/// <item><description><b>Không ứng viên nào đạt</b> → giữ bản gốc, KHÔNG chạy lại bằng đường
/// cũ. Tệp đã nén hiệu quả là chuyện thường; chạy lại đường cũ ở đây sẽ âm thầm nén một
/// tệp mà ta vừa kết luận là không nén được.</description></item>
/// <item><description><b>Hỏng hạ tầng</b> (không đo được ứng viên nào) → rơi về đường cũ, và
/// ghi kèm mã lý do gốc. Chỉ nhánh này được phép rơi, vì đây là lần duy nhất ta thật sự
/// <i>không biết</i> và đường cũ là cách duy nhất còn lại.</description></item>
/// </list>
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

    /// <summary>Mã lý do ghi kèm khi phải rơi về đường cũ.</summary>
    public const string LegacyFallbackMarker = "LEGACY_FALLBACK_USED";

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

        var windows = await ChooseWindowsAsync(context, token).ConfigureAwait(false);
        if (windows.Count == 0)
        {
            return await FallbackAsync(
                context, onProgress, "không chọn được đoạn đại diện nào", token).ConfigureAwait(false);
        }

        var search = new PilotSearch(
            new PilotEncoder(ffmpeg, context.TempPath),
            new ReferenceWindowExtractor(ffmpeg, context.TempPath),
            new QualityProbe(ffmpeg, context.TempPath));

        SearchResult result;
        try
        {
            result = await search.RunAsync(
                new SearchRequest
                {
                    SourcePath = context.SourcePath,
                    SourceSizeBytes = SourceFileBytes(context.SourcePath),
                    SourceWidth = sourceWidth,
                    SourceHeight = sourceHeight,
                    SourceDurationSeconds = probe.Duration?.TotalSeconds ?? 0,
                    SourceAudioBitrateKbps = probe.AudioBitrateKbps,
                    HasAudio = probe.HasAudio,
                    Windows = windows,
                    Candidates = candidates,
                    Level = context.Level,
                    Model = VmafModels.Default,
                    MaxEvaluations = context.Config.MaxSearchEvaluations,
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

        return result.Status switch
        {
            // Chỉ nhánh này rơi về đường cũ. Hai nhánh còn lại giữ nguyên quyết định của ta.
            SearchStatus.SearchInfrastructureFailure => await FallbackAsync(
                context, onProgress,
                $"{result.Outcome.Reason}: {result.Outcome.Message}", token).ConfigureAwait(false),

            // Quyết định hợp lệ rằng không nén được. KHÔNG rơi về đường cũ.
            SearchStatus.NoFeasibleCandidate => PipelineResult.NotWorthIt(
                SkipReason.NotWorthIt, 0, Describe(result)),

            _ => await EncodeFullAsync(context, onProgress, result, token).ConfigureAwait(false),
        };
    }

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
        return legacyResult with
        {
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

    private static int? AudioTargetFor(PipelineContext context)
    {
        if (context.Item.HasAudio is false) return null;

        // `JobItem` không mang bitrate âm thanh riêng, và `SourceBitrateKbps` là bitrate
        // TỔNG của cả tệp — dùng nó làm trần âm thanh sẽ cho phép nâng âm thanh lên tới
        // mức vô lý. Ước lượng phần âm thanh chỉ dùng cho XẾP HẠNG, nên giá trị mặc định
        // ở đây là hợp lý; nếu sau này `JobItem` mang thêm bitrate âm thanh thì nối vào đó.
        const int DefaultAudioKbps = 128;
        return DefaultAudioKbps;
    }

    private static async Task<IReadOnlyList<RepresentativeWindow>> ChooseWindowsAsync(
        PipelineContext context, CancellationToken token)
    {
        var scanner = new TimelineScanner(context.Tools.FFmpeg!, context.TempPath);
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
    private static string Describe(SearchResult result)
    {
        var s = result.Statistics;
        var summary =
            $"tìm kiếm: {s.CandidatesEvaluated}/{s.CandidatesPlanned} ứng viên đã đo, "
            + $"{s.QualityMeasurements} phép đo VMAF, "
            + $"{s.MeasurementsSavedByEarlyReject} phép đo tiết kiệm nhờ loại sớm, "
            + $"{s.TotalElapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s — "
            + result.Outcome.Message;

        if (result.Selected is { } selected)
        {
            summary +=
                $"; chọn {selected.Candidate.Id} "
                + $"(VMAF đoạn tệ nhất {QualityAggregator.RankingQuality(selected.Aggregate).ToString("0.0", CultureInfo.InvariantCulture)}, "
                + $"ước lượng {(selected.Estimate.TotalBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB)";
        }

        return summary;
    }
}
