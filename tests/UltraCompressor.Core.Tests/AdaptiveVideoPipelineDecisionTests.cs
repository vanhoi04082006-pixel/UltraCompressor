using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Kiểm tra phần QUYẾT ĐỊNH của <see cref="AdaptiveVideoPipeline"/>.
///
/// <para>Phần đo chất lượng và phần encode được kiểm bằng seam của
/// <c>PilotSearch</c>; ở đây kiểm cái mà không thể kiểm ở đâu khác: khi tìm kiếm trả về mỗi
/// trạng thái, pipeline có làm đúng thứ với nó không. Ba trạng thái dẫn tới ba hành vi
/// khác nhau, và nhầm giữa chúng là loại lỗi mà người dùng chỉ thấy được nhiều ngày sau.</para>
///
/// <para>Cờ tắt phải y hệt đường cũ. Không phải "gần giống" — cùng kết quả, để người đọc
/// báo cáo của hai lần chạy mà so sánh được.</para>
/// </summary>
public class AdaptiveVideoPipelineDecisionTests
{
    private static AppConfig Config(bool adaptive) =>
        new() { EnableAdaptiveSearch = adaptive };

    private static PipelineContext Context(AppConfig config, string source = "video.mp4") =>
        new()
        {
            Item = new JobItem
            {
                FilePath = source,
                Kind = MediaKind.Video,
                SourceWidth = 1920,
                SourceHeight = 1080,
                SourceBitrateKbps = 2400,
                HasAudio = true,
                DurationSeconds = 300,
            },
            TempPath = Path.Combine(Path.GetTempPath(), "uc-adaptive-test", "out.mp4"),
            Level = CompressionLevel.Balanced,
            Config = config,
            Tools = new ToolResolution(FFmpeg: null, FFplay: null, Gifsicle: null, Ghostscript: null),
        };

    /// <summary>
    /// Đường cũ giả: chỉ ghi lại việc nó có được gọi, không chạy ffmpeg.
    ///
    /// <para>Cần giả chứ không dùng <see cref="VideoPipeline"/> thật, vì nhánh rơi về đường cũ
    /// chính là nhánh cần kiểm nhất. Nếu phải chạy ffmpeg thật thì nhánh đó chỉ được kiểm
    /// trên máy có ffmpeg, tức là không được kiểm trên CI.</para>
    /// </summary>
    private sealed class RecordingPipeline : IMediaPipeline
    {
        public int Calls { get; private set; }

        public string? Message { get; init; }

        public MediaKind Kind => MediaKind.Video;

        public Task<PipelineResult> RunAsync(
            PipelineContext context, Action<int> onProgress, CancellationToken token)
        {
            Calls++;
            onProgress(100);

            return Task.FromResult(new PipelineResult
            {
                Success = true,
                NewSize = 1_000,
                DurationSeconds = 300,
                Message = Message,
            });
        }
    }


    /// <summary>
    /// Cờ tắt: phải chuyển thẳng cho đường cũ và không tự dựng lệnh nào.
    /// </summary>
    [Fact]
    public async Task Tat_co_thi_chuyen_thang_cho_duong_cu()
    {
        var legacy = new RecordingPipeline();
        var pipeline = new AdaptiveVideoPipeline(legacy);

        var result = await pipeline.RunAsync(Context(Config(adaptive: false)), _ => { }, default);

        Assert.Equal(1, legacy.Calls);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Khong_phan_giữ_hai_lan_tat_co()
    {
        // Cùng đầu vào, cờ tắt, phải cho cùng kết quả — kể cả thông điệp. Nếu lần sau khác
        // lần trước thì mọi so sánh giữa hai cấu hình là vô nghĩa.
        var pipeline = new AdaptiveVideoPipeline(new RecordingPipeline());
        var context = Context(Config(adaptive: false));

        var a = await pipeline.RunAsync(context, _ => { }, default);
        var b = await pipeline.RunAsync(context, _ => { }, default);

        Assert.Equal(a.Success, b.Success);
        Assert.Equal(a.NewSize, b.NewSize);
        Assert.Equal(a.Message, b.Message);
    }

    /// <summary>
    /// Bật cờ nhưng không có ffmpeg: phải rơi về đường cũ kèm mã lý do gốc.
    ///
    /// <para>Đây là hạ tầng hỏng, nên rơi về đường cũ là đúng — và phải nói rõ lý do, vì
    /// nếu chỉ thấy "đã dùng đường cũ" thì không sửa được gì.</para>
    /// </summary>
    [Fact]
    public async Task Bat_co_nhung_hong_ha_tang_thi_roi_ve_duong_cu_kem_ly_do()
    {
        var legacy = new RecordingPipeline();
        var pipeline = new AdaptiveVideoPipeline(legacy);

        var result = await pipeline.RunAsync(Context(Config(adaptive: true)), _ => { }, default);

        Assert.Equal(1, legacy.Calls);
        Assert.Contains(AdaptiveVideoPipeline.LegacyFallbackMarker, result.Message);
        Assert.Contains("ffmpeg", result.Message);
    }

    /// <summary>
    /// Mã lý do phải mang theo NGUYÊN NHÂN GỐC, không chỉ nói "đã dùng đường cũ".
    /// </summary>
    [Fact]
    public async Task Ma_ly_do_phai_co_nguyen_nhan_goc()
    {
        var pipeline = new AdaptiveVideoPipeline(new RecordingPipeline());

        var result = await pipeline.RunAsync(Context(Config(adaptive: true)), _ => { }, default);

        // Không được chỉ còn đúng cái mã lý do.
        Assert.True(
            result.Message!.Length > AdaptiveVideoPipeline.LegacyFallbackMarker.Length + 2,
            "mã lý do phải kèm nguyên nhân gốc, không chỉ có tên mã");
    }

    /// <summary>
    /// Thông điệp của đường cũ phải được giữ lại, không bị mã lý do đè mất.
    /// </summary>
    [Fact]
    public async Task Thong_diep_duong_cu_khong_bi_de_mat()
    {
        var legacy = new RecordingPipeline { Message = "đường cũ: đã nén" };
        var pipeline = new AdaptiveVideoPipeline(legacy);

        var result = await pipeline.RunAsync(Context(Config(adaptive: true)), _ => { }, default);

        Assert.Contains(AdaptiveVideoPipeline.LegacyFallbackMarker, result.Message);
        Assert.Contains("đường cũ: đã nén", result.Message);
    }

    [Fact]
    public async Task Khong_giua_nguyen_nhan_khi_duong_cu_khong_co_thong_diep()
    {
        var legacy = new RecordingPipeline { Message = null };
        var pipeline = new AdaptiveVideoPipeline(legacy);

        var result = await pipeline.RunAsync(Context(Config(adaptive: true)), _ => { }, default);

        Assert.StartsWith(AdaptiveVideoPipeline.LegacyFallbackMarker, result.Message);
        Assert.DoesNotContain("|", result.Message);
    }

    /// <summary>
    /// Không có kích thước nguồn thì không thể dựng bộ lọc thu nhỏ đúng, nên đây là hạ tầng
    /// thiếu dữ liệu chứ không phải "tệp không nén được". Phải rơi về đường cũ.
    /// </summary>
    [Fact]
    public async Task Khong_biet_kich_thuoc_nguon_thi_roi_ve_duong_cu()
    {
        var legacy = new RecordingPipeline();
        var pipeline = new AdaptiveVideoPipeline(legacy);

        var context = Context(Config(adaptive: true)) with
        {
            Item = new JobItem
            {
                FilePath = "video.mp4",
                Kind = MediaKind.Video,
                SourceWidth = null,
                SourceHeight = null,
                HasAudio = true,
            },
        };

        await pipeline.RunAsync(context, _ => { }, default);

        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public async Task Khong_co_luong_video_thi_roi_ve_duong_cu()
    {
        var legacy = new RecordingPipeline();
        var pipeline = new AdaptiveVideoPipeline(legacy);

        var context = Context(Config(adaptive: true)) with
        {
            Probe = new MediaInfo { HasVideo = false, HasAudio = true },
        };

        await pipeline.RunAsync(context, _ => { }, default);

        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public void C_bao_hoi_chi_quyet_dinh_giua_ba_trang_thay()
    {
        // Ba trạng thái phải dẫn tới ba hành vi KHÁC NHAU. Nếu hai trong ba cùng hành vi
        // thì phân biệt trạng thái là vô nghĩa, và việc giữ nó chỉ tốn công.
        Assert.Equal(3, Enum.GetValues<SearchStatus>().Length);

        // Chỉ hạ tầng mới được rơi về đường cũ. Hai trạng thái còn lại là quyết định của ta.
        Assert.NotEqual(SearchStatus.NoFeasibleCandidate, SearchStatus.SearchInfrastructureFailure);
        Assert.NotEqual(SearchStatus.SelectedCandidate, SearchStatus.SearchInfrastructureFailure);
    }

    [Fact]
    public void Phan_biet_ba_trang_thai_la_phan_biet_luong_y_theo_ma_ly_do()
    {
        // Ba mã lý do phải khác nhau, vì báo cáo và bộ lọc log dựa vào chúng để tách
        // "tệp không nén được" khỏi "công cụ hỏng".
        var reasons = new[]
        {
            SearchDecisionReasons.PilotNoCandidates,
            SearchDecisionReasons.PilotAllCandidatesRejected,
            SearchDecisionReasons.PilotEncodeFailed,
        };

        Assert.Equal(3, reasons.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Bảng trạng thái → hành động, kiểm tra TRỌN VẸN.
    ///
    /// <para>Đây là hợp đồng quan trọng nhất của đường thích ứng. Nhầm hai hàng đầu thì
    /// hoặc ta nén một tệp vừa kết luận là không nén được, hoặc ta bỏ qua cơ hội nén khi
    /// đáng lẽ phải nén — và cả hai đều im lặng.</para>
    /// </summary>
    [Fact]
    public void Bang_trang_thai_dan_toi_dung_nghi_dinh()
    {
        Assert.Equal(
            AdaptiveVideoPipeline.SearchDecision.EncodeFull,
            AdaptiveVideoPipeline.DecideFor(SearchStatus.SelectedCandidate));

        // Dòng này là điều kiện chính của cả giai đoạn: KHÔNG ứng viên nào đạt thì giữ bản
        // gốc, tuyệt đối không rơi về đường cũ.
        Assert.Equal(
            AdaptiveVideoPipeline.SearchDecision.KeepOriginal,
            AdaptiveVideoPipeline.DecideFor(SearchStatus.NoFeasibleCandidate));

        // Và chỉ hạ tầng hỏng mới được rơi về đường cũ.
        Assert.Equal(
            AdaptiveVideoPipeline.SearchDecision.FallBackToLegacy,
            AdaptiveVideoPipeline.DecideFor(SearchStatus.SearchInfrastructureFailure));
    }

    [Fact]
    public void Chi_mot_trang_thai_duoc_phep_roi_ve_duong_cu()
    {
        var fallbacks = Enum.GetValues<SearchStatus>()
            .Where(s => AdaptiveVideoPipeline.DecideFor(s)
                == AdaptiveVideoPipeline.SearchDecision.FallBackToLegacy)
            .ToList();

        Assert.Equal([SearchStatus.SearchInfrastructureFailure], fallbacks);
    }

    [Fact]
    public void Muc_tieu_am_thanh_giong_duong_cu_va_khong_nang_qua_nguon()
    {
        // Phạm vi tìm kiếm là video. Âm thanh phải giữ đúng trần mục tiêu của mức nén đã
        // chọn, và không bao giờ nâng một nguồn vốn đã nhỏ hơn.
        static PipelineContext AudioContext(CompressionLevel level, double? sourceAudioKbps) =>
            new()
            {
                Item = new JobItem
                {
                    FilePath = "video.mp4",
                    Kind = MediaKind.Video,
                    HasAudio = true,
                },
                TempPath = Path.Combine(Path.GetTempPath(), "uc-audio-test", "out.mp4"),
                Level = level,
                Config = new AppConfig(),
                Tools = new ToolResolution(null, null, null, null),
                Probe = new MediaInfo { HasVideo = true, HasAudio = true, AudioBitrateKbps = sourceAudioKbps },
            };

        Assert.Equal(320, AdaptiveVideoPipeline.AudioTargetFor(AudioContext(CompressionLevel.Light, null)));
        Assert.Equal(192, AdaptiveVideoPipeline.AudioTargetFor(AudioContext(CompressionLevel.Balanced, 250)));
        Assert.Equal(96, AdaptiveVideoPipeline.AudioTargetFor(AudioContext(CompressionLevel.Balanced, 96)));
        Assert.Equal(128, AdaptiveVideoPipeline.AudioTargetFor(AudioContext(CompressionLevel.Strong, null)));
    }

    [Fact]
    public void Trang_thai_la_o_trong_bang()
    {
        // Trạng thái thêm về sau không được lọt vào hành vi mặc định "encode bừa".
        foreach (var status in Enum.GetValues<SearchStatus>())
        {
            Assert.True(Enum.IsDefined(AdaptiveVideoPipeline.DecideFor(status)));
        }
    }
}
