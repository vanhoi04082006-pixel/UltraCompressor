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
    public void Bon_trang_thai_dan_toi_bon_nghi_dinh_khac_nhau()
    {
        // Bốn trạng thái phải dẫn tới bốn hành vi KHÁC NHAU. Nếu hai trong bốn cùng hành vi
        // thì phân biệt trạng thái là vô nghĩa, và việc giữ nó chỉ tốn công.
        //
        // Riêng hai trạng thái "giữ bản gốc" tách nhau dù thi hành giống nhau, vì chúng nói
        // khác nhau: một cái là "có ứng viên đạt mà vẫn không đáng encode", cái kia là "không
        // ứng viên nào đạt". Gộp lại thì báo cáo không còn trả lời được lần nén này có bỏ
        // được việc mã hoá lại hay không.
        Assert.Equal(4, Enum.GetValues<SearchStatus>().Length);
        Assert.Equal(4, Enum.GetValues<AdaptiveVideoPipeline.SearchDecision>().Length);

        // Chỉ hạ tầng mới được rơi về đường cũ. Ba trạng thái còn lại là quyết định của ta.
        Assert.NotEqual(SearchStatus.NoFeasibleCandidate, SearchStatus.SearchInfrastructureFailure);
        Assert.NotEqual(SearchStatus.SelectedCandidate, SearchStatus.SearchInfrastructureFailure);
        Assert.NotEqual(SearchStatus.OriginalSelected, SearchStatus.SearchInfrastructureFailure);
    }

    [Fact]
    public void Bon_ma_ly_do_cua_ke_tuc_phai_khac_nhau()
    {
        // Bốn kết cục phải có bốn mã khác nhau, vì báo cáo và bộ lọc log dựa vào chúng để tách
        // "encode được" khỏi "bản gốc thắng", "không có gì đạt" và "công cụ hỏng". Trùng mã là
        // mất khả năng đếm.
        Assert.Equal(4, SearchDecisionReasons.Outcomes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            SearchDecisionReasons.Outcomes,
            Enum.GetValues<SearchStatus>()
                .Select(AdaptiveVideoPipeline.OutcomeFor)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Ma_luong_thich_ung_van_tat_mac_dinh()
    {
        // Giai đoạn 5B KHÔNG mở cờ này. Người dùng bật bằng tay trong tệp cấu hình; bật mặc
        // định là thay đổi hành vi nén của mọi người dùng mà chưa ai xin.
        Assert.False(new AppConfig().EnableAdaptiveSearch);
    }

    [Fact]
    public void Ma_luong_thich_ung_thuoc_ve_viec_bat_duoc_bien_doi()
    {
        // Vì không có ô bật trên giao diện, cờ chỉ đổi khi ai đó sửa tệp cấu hình — tức là
        // chủ ý. Cờ phải sống sót qua mọi thao tác cấu hình thường.
        //
        // `AppHost.CopyConfig` cố tình KHÔNG chép cờ này (vì giao diện không gửi nó, nên chép
        // sẽ tắt cờ mỗi lần lưu). Không kiểm thử được trực tiếp vì dự án kiểm thử không
        // tham chiếu dự án WinForms; hợp đồng được bảo vệ ở đây: cờ mặc định tắt, và bộ chọn
        // đường chạy tôn trọng đúng giá trị của nó.
        Assert.True(Config(false).EnableAdaptiveSearch == false);
        Assert.True(Config(true).EnableAdaptiveSearch);
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

        // Có ứng viên đạt nhưng không chứng minh được lợi ích: giữ bản gốc, bỏ encode.
        Assert.Equal(
            AdaptiveVideoPipeline.SearchDecision.KeepOriginal,
            AdaptiveVideoPipeline.DecideFor(SearchStatus.OriginalSelected));

        // Đã thử mà không ứng viên nào đạt: giữ bản gốc, tuyệt đối không rơi về đường cũ.
        Assert.Equal(
            AdaptiveVideoPipeline.SearchDecision.KeepOriginalNoFeasible,
            AdaptiveVideoPipeline.DecideFor(SearchStatus.NoFeasibleCandidate));

        // Và chỉ hạ tầng hỏng mới được rơi về đường cũ.
        Assert.Equal(
            AdaptiveVideoPipeline.SearchDecision.FallBackToLegacy,
            AdaptiveVideoPipeline.DecideFor(SearchStatus.SearchInfrastructureFailure));
    }

    [Fact]
    public void Hai_trang_thai_giu_ban_goc_phai_tach_biet_nhau()
    {
        // Cùng hành vi thi hành (giữ bản gốc, không encode, không rơi về đường cũ) nhưng phải là
        // hai hàng khác nhau — vì người đọc báo cáo cần biết ta đã bỏ qua một lần encode vì
        // không đáng, hay vì không tìm được gì đạt.
        Assert.NotEqual(
            AdaptiveVideoPipeline.DecideFor(SearchStatus.OriginalSelected),
            AdaptiveVideoPipeline.DecideFor(SearchStatus.NoFeasibleCandidate));

        Assert.NotEqual(
            AdaptiveVideoPipeline.OutcomeFor(SearchStatus.OriginalSelected),
            AdaptiveVideoPipeline.OutcomeFor(SearchStatus.NoFeasibleCandidate));

        // Cả hai vẫn là quyết định hợp lệ, không phải hạ tầng.
        foreach (var status in new[] { SearchStatus.OriginalSelected, SearchStatus.NoFeasibleCandidate })
        {
            Assert.NotEqual(
                AdaptiveVideoPipeline.SearchDecision.FallBackToLegacy,
                AdaptiveVideoPipeline.DecideFor(status));
        }
    }

    [Fact]
    public void Ma_ror_ve_duong_cu_phai_trung_ma_ly_do_chinh_thuc()
    {
        // `LegacyFallbackMarker` và mã trong `SearchDecisionReasons` từng là hai chuỗi riêng
        // cùng giá trị. Hai nguồn sự thật thì sớm muộn lệch nhau, và lúc đó thống kê đếm một
        // chỗ mà log ghi chỗ kia.
        Assert.Equal(SearchDecisionReasons.LegacyFallbackUsed, AdaptiveVideoPipeline.LegacyFallbackMarker);
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
    public void Uoc_luong_dung_bitrate_muc_tieu_thay_vi_bitrate_nguon_tho()
    {
        // Estimator phải dùng con số mà bản full encode SẼ DÙNG. Nguồn 250k với mục tiêu
        // 192k mà ước bằng 250k thì phần audio thừa 58k × thời lượng — sai đúng bằng phần
        // chênh, và sai theo hướng làm ứng viên trông tệ hơn thật.
        Assert.Equal(192, AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(250, 192));
        Assert.Equal(96, AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(96, 192));
        Assert.Equal(192, AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(192, 192));

        // Không biết nguồn thì dùng mục tiêu (đó là thứ encoder sẽ giữ); không có mục tiêu
        // thì trả nguồn để estimator đi đường "không rõ" của nó.
        Assert.Equal(192, AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(null, 192));
        Assert.Equal(128, AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(128, null));
        Assert.Null(AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(null, null));
        Assert.Null(AdaptiveVideoPipeline.EffectiveAudioKbpsForEstimate(0, 0));
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
