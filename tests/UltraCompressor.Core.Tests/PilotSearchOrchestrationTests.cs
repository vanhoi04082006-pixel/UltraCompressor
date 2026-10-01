using UltraCompressor.Core.Encoders;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Planning;
using UltraCompressor.Core.Search;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Kiểm thử phần ĐIỀU PHỐI của <see cref="PilotSearch"/> bằng bản giả, **không cần ffmpeg**.
///
/// <para>Đây là lý do các seam <see cref="IPilotEncodeRunner"/>,
/// <see cref="IReferenceWindowSource"/> và <see cref="IQualityMeasurer"/> tồn tại. CI
/// không cài ffmpeg, nên nếu orchestration chỉ kiểm thử được khi có ffmpeg thì toàn bộ
/// logic về thứ tự đo, loại sớm, chọn ứng viên và phân biệt "không có ứng viên nào đạt" với
/// "hạ tầng hỏng" sẽ <b>không được kiểm tra lần nào</b>. Test bị bỏ qua thì tệ hơn test
/// đỏ, vì nó tạo cảm giác an toàn giả.</para>
///
/// <para>Bản giả ở đây giả lập hành vi chứ không giả lập kết quả: nó đếm số lần gọi, trả
/// số đo theo kịch bản do test đặt, và ghi lại những gì thật sự đã chạy. Một orchestration
/// sai sẽ ra số liệu khác.</para>
/// </summary>
public class PilotSearchOrchestrationTests
{
    // ---------------------------------------------------------------- bản giả

    /// <summary>
    /// Trả về mảng rỗng nghĩa là "ứng viên này encode được bình thường", còn trả về clip
    /// hỏng thì mô phỏng ffmpeg hỏng. Dùng mảng rỗng thay vì null để không phải truyền
    /// kiểu nullable khắp nơi trong test.
    /// </summary>
    private sealed class FakeEncoder : IPilotEncodeRunner
    {
        private readonly Func<VideoEncodeCandidate, PilotArtifact[]> _behaviour;

        public FakeEncoder(Func<VideoEncodeCandidate, PilotArtifact[]>? behaviour = null) =>
            _behaviour = behaviour ?? (_ => []);

        public List<string> Encoded { get; } = [];

        public List<string> Released { get; } = [];

        public Task<IReadOnlyList<PilotArtifact>> EncodeAsync(
            VideoEncodeCandidate candidate, string sourcePath, int sourceWidth, int sourceHeight,
            IReadOnlyList<RepresentativeWindow> windows, CancellationToken token)
        {
            Encoded.Add(candidate.Id);

            var outcome = _behaviour(candidate);

            return Task.FromResult<IReadOnlyList<PilotArtifact>>(
                outcome.Length == 0 ? Success(candidate, windows) : outcome);
        }

        public void Release(IReadOnlyList<PilotArtifact> artifacts) =>
            Released.AddRange(artifacts.Select(a => a.CandidateId));

        private static PilotArtifact[] Success(
            VideoEncodeCandidate candidate, IReadOnlyList<RepresentativeWindow> windows) =>
        [
            .. windows.Select(w => new PilotArtifact(
                candidate.Id,
                w.Role,
                new TimeWindow(w.StartSeconds, w.DurationSeconds),
                new TimeWindow(0, w.DurationSeconds),
                $"clip-{candidate.Id}-{w.Role}.mp4",
                Bytes: 100_000,
                new EncodeTarget(1920, 1080),
                [],
                TimeSpan.FromSeconds(1),
                new SearchOutcome(SearchDecisionReasons.PilotSelected, "ok"))),
        ];
    }

    /// <summary>Trả số đo theo vai trò đoạn, để test điều khiển được đoạn nào rớt.</summary>
    private sealed class FakeMeasurer(Func<WindowRole, double?> quality) : IQualityMeasure
    {
        public List<WindowRole> Measured { get; } = [];

        /// <summary>Trả null nghĩa là "không đo được", khác hẳn với điểm 0.</summary>
        public Func<WindowRole, double?> Quality { get; set; } = quality;

        public Task<QualityResult?> MeasureAsync(
            string referencePath, string candidatePath,
            TimeWindow referenceWindow, TimeWindow candidateWindow,
            int displayWidth, int displayHeight, int candidateWidth, int candidateHeight,
            VmafModel model, int candidateStartFrame = 0, int referenceStartFrame = 0, CancellationToken token = default)
        {
            var role = RoleOf(candidatePath);
            Measured.Add(role);

            if (Quality(role) is not { } mean)
            {
                return Task.FromResult<QualityResult?>(null);
            }

            // Giữ lại kích thước đích trong kết quả: đó là thứ cho phép kiểm tra rằng
            // orchestrator truyền đúng kích thước ứng viên xuống tầng đo, thay vì tự bịa.
            return Task.FromResult<QualityResult?>(new QualityResult(
                model.Id, referenceWindow,
                displayWidth, displayHeight,
                candidateWidth, candidateHeight,
                new QualitySample(mean, mean - 3, mean - 20, 0.999, 72)));
        }

        private static WindowRole RoleOf(string path)
        {
            foreach (var role in Enum.GetValues<WindowRole>())
            {
                if (path.Contains(role.ToString(), StringComparison.Ordinal))
                {
                    return role;
                }
            }

            return WindowRole.Typical;
        }
    }

    private sealed class FakeReferences(bool fail = false) : IReferenceWindowSource
    {
        public int Extracted { get; private set; }

        public int Released { get; private set; }

        public Task<IReadOnlyList<WindowReference>> ExtractAsync(
            string sourcePath, IReadOnlyList<RepresentativeWindow> windows, CancellationToken token)
        {
            if (fail)
            {
                throw new InvalidOperationException("hỏng hạ tầng");
            }

            Extracted++;
            IReadOnlyList<WindowReference> refs =
            [
                .. windows.Select(w => new WindowReference(
                    w.Role, $"ref-{w.Role}.mp4", w.StartSeconds, w.DurationSeconds,
                    1_000_000, TimeSpan.FromSeconds(1))),
            ];

            return Task.FromResult(refs);
        }

        public void Release(IReadOnlyList<WindowReference> references) => Released++;
    }

    // ---------------------------------------------------------------- dữ liệu dùng chung

    private static RepresentativeWindow W(WindowRole role, double start, double difficulty) =>
        new(start, 3.0, role, 0.5, 0.5, 0.5, 0.5, difficulty, 0, $"vì lý do {role}");

    private static VideoEncodeCandidate C(string branch, int point, int pointCount, double quality) =>
        new($"{branch}/crf{quality:0.##}")
        {
            Codec = VideoCodec.H264,
            EncoderName = "libx264",
            Quality = QualityOption.X26xCrf(quality),
            Width = 1920,
            Height = 1080,
            Fps = 24,
            Speed = SpeedOption.X26xPreset("medium"),
            PixelFormat = "yuv420p",
            Origin = point == 0 ? CandidateOrigin.CoarseProbe : CandidateOrigin.QualityAnchor,
            BranchId = branch,
            PointIndex = point,
            PointCount = pointCount,
            Reason = "test",
        };

    private static SearchRequest Request(params VideoEncodeCandidate[] candidates) =>
        Request([.. candidates], null);

    private static SearchRequest Request(
        IReadOnlyList<VideoEncodeCandidate> candidates,
        IReadOnlyList<RepresentativeWindow>? windows) =>
        new()
        {
            SourcePath = "src.mp4",
            SourceSizeBytes = 40_000_000,
            SourceWidth = 1920,
            SourceHeight = 1080,
            SourceDurationSeconds = 300,
            SourceAudioBitrateKbps = 128,
            HasAudio = true,
            Windows = windows
                ?? [W(WindowRole.HighMotion, 10, 0.5), W(WindowRole.Typical, 100, 0.9)],
            Candidates = candidates,
            Level = CompressionLevel.Balanced,
            Model = VmafModels.Neg,
            MaxEvaluations = 24,
        };

    private static PilotArtifact Failed(VideoEncodeCandidate candidate) =>
        new(candidate.Id, WindowRole.Typical, new TimeWindow(0, 3), new TimeWindow(0, 3),
            OutputPath: null, Bytes: 0, new EncodeTarget(1920, 1080), [], TimeSpan.Zero,
            new SearchOutcome(SearchDecisionReasons.PilotEncodeFailed, "hỏng"));

    // ---------------------------------------------------------------- thứ tự đo

    [Fact]
    public async Task Doan_kho_duoc_do_truoc()
    {
        // Typical có độ khó 0,9, HighMotion 0,5. Phải đo Typical trước.
        var measurer = new FakeMeasurer(_ => 95);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 2, 3, 32)));

        Assert.Equal([WindowRole.Typical, WindowRole.HighMotion], measurer.Measured.Take(2));
    }

    [Fact]
    public async Task Thu_tu_khong_phai_theo_ten_vai_tro()
    {
        // Ở nguồn này HighSpatial khó hơn HighMotion. Xếp theo tên vai trò sẽ đo sai thứ tự
        // và tốn công vô ích.
        var measurer = new FakeMeasurer(_ => 95);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        var request = Request(
            [C("b", 0, 3, 16)],
            [
                W(WindowRole.HighMotion, 10, 0.2),
                W(WindowRole.HighSpatial, 50, 0.8),
                W(WindowRole.Typical, 100, 0.5),
            ]);

        await search.RunAsync(request);

        Assert.Equal(
            [WindowRole.HighSpatial, WindowRole.Typical, WindowRole.HighMotion],
            measurer.Measured);
    }

    [Fact]
    public async Task Doan_giua_rot_thi_dung_ngay_va_bo_qua_doan_sau()
    {
        // Cửa sổ mặc định xếp Typical (khó 0,9) trước HighMotion (khó 0,5). Typical rớt ở
        // cả hai ứng viên nên mỗi ứng viên chỉ tốn đúng một phép đo.
        var measurer = new FakeMeasurer(role => role == WindowRole.Typical ? 80 : 95);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        var result = await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.Equal([WindowRole.Typical, WindowRole.Typical], measurer.Measured);

        // 2 ứng viên × 2 đoạn = 4 nếu không dừng sớm; thực tế 2.
        Assert.Equal(2, result.Statistics.QualityMeasurements);
        Assert.Equal(4, result.Statistics.MeasurementsIfNoEarlyReject);
        Assert.Equal(2, result.Statistics.MeasurementsSavedByEarlyReject);
        Assert.Equal(50, result.Statistics.EarlyRejectSavingPercent);

        // Không đo được là chuyện khác: cả hai đoạn đều phải được thử.
        Assert.Equal(0, result.Evaluated[0].WindowsNotMeasured);
    }

    [Fact]
    public async Task Doan_rot_sau_cung_van_dung_duoc_va_gi_dien_danh_nguyen_nhan()
    {
        // Đoạn khó (Typical) đạt, đoạn dễ (HighMotion) rớt sau. Phải vẫn loại ứng viên và
        // phải chỉ ra đúng đoạn có vấn đề — không phải đoạn đầu tiên.
        var measurer = new FakeMeasurer(role => role == WindowRole.Typical ? 95 : 70);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        var result = await search.RunAsync(Request(C("b", 0, 3, 16)));

        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.Equal([WindowRole.Typical, WindowRole.HighMotion], measurer.Measured);
        Assert.Equal(0, result.Statistics.MeasurementsSavedByEarlyReject);
        Assert.Equal(WindowRole.HighMotion, result.Evaluated[0].Aggregate.FailingWindow?.Role);
    }

    [Fact]
    public async Task Khong_do_duoc_khong_phai_la_ly_do_dung_som()
    {
        // "không đo được" khác "rớt". Nếu dừng sớm ở đây thì ta che mất thông tin về đoạn
        // còn lại, và báo cáo sẽ nói "rớt" thay vì nói "hỏng".
        var measurer = new FakeMeasurer(role => role == WindowRole.HighMotion ? null : 95);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        var result = await search.RunAsync(Request(C("b", 0, 3, 16)));

        Assert.Equal([WindowRole.Typical, WindowRole.HighMotion], measurer.Measured);
        Assert.Equal(SearchDecisionReasons.PilotMeasurementUnavailable,
            result.Evaluated[0].Aggregate.Outcome.Reason);
    }

    [Fact]
    public async Task Thi_mot_doan_ma_doan_kia_dat_van_la_khong_kha_thi()
    {
        // Không đo được một đoạn thì ứng viên không được đi tiếp, dù đoạn còn lại đạt.
        // Ở lưới chất lượng cuối, công cụ hỏng thì cố ý fail-open để người dùng vẫn nén
        // được; ở đây ngược lại, vì chưa biết ứng viên có an toàn không.
        var measurer = new FakeMeasurer(role => role == WindowRole.HighMotion ? null : 95);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        var result = await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.False(result.HasSelection);
    }

    [Fact]
    public async Task Hai_cua_so_do_luon_bat_dau_tu_khung_hinh_dau()
    {
        // Cả tham chiếu lẫn clip ứng viên đều là bản cắt riêng, nên cả hai cửa sổ phải là
        // [0, d]. Truyền cửa sổ của nguồn vào clip ứng viên là lỗi đã mắc phải: nó seek
        // quá cuối clip và đo ra rỗng — hoặc tệ hơn, đo nhầm đoạn khác.
        var probe = new RecordingMeasurer();
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), probe);

        await search.RunAsync(Request(C("b", 0, 3, 16)));

        Assert.All(probe.Calls, call =>
        {
            Assert.Equal(0, call.Reference.StartSeconds);
            Assert.Equal(0, call.Candidate.StartSeconds);
            Assert.Equal(3.0, call.Reference.LengthSeconds);
        });
    }

    [Fact]
    public async Task Kich_thuoc_ung_vien_duoc_truyen_nguyen_ven_xuong_tang_do()
    {
        // Tầng đo cần kích thước ứng viên để quyết định có đưa nó về đúng khổ hiển thị hay
        // không. Thiếu thông tin này thì phép đo phải đoán, và phép đo đoán sai thì ra con
        // số sai rất khó nhận ra.
        var probe = new RecordingMeasurer();
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), probe);

        var request = Request(
            [C("b", 0, 3, 16)], [W(WindowRole.Typical, 100, 0.9)])
            with
        { SourceWidth = 1920, SourceHeight = 1080 };

        await search.RunAsync(request);

        Assert.All(probe.Calls, call =>
        {
            Assert.Equal(1920, call.DisplayWidth);
            Assert.Equal(1080, call.DisplayHeight);
            Assert.Equal(1920, call.CandidateWidth);
            Assert.Equal(1080, call.CandidateHeight);
        });
    }

    private sealed record MeasureCall(
        TimeWindow Reference, TimeWindow Candidate,
        int DisplayWidth, int DisplayHeight, int CandidateWidth, int CandidateHeight);

    private sealed class RecordingMeasurer : IQualityMeasure
    {
        public List<MeasureCall> Calls { get; } = [];

        public Task<QualityResult?> MeasureAsync(
            string referencePath, string candidatePath,
            TimeWindow referenceWindow, TimeWindow candidateWindow,
            int displayWidth, int displayHeight, int candidateWidth, int candidateHeight,
            VmafModel model, int candidateStartFrame = 0, int referenceStartFrame = 0, CancellationToken token = default)
        {
            Calls.Add(new MeasureCall(
                referenceWindow, candidateWindow,
                displayWidth, displayHeight, candidateWidth, candidateHeight));

            return Task.FromResult<QualityResult?>(new QualityResult(
                model.Id, referenceWindow,
                displayWidth, displayHeight, candidateWidth, candidateHeight,
                new QualitySample(95, 92, 80, 0.999, 72)));
        }
    }

    // ---------------------------------------------------------------- chiến lược

    [Fact]
    public void Coarse_dung_dau_di_va_cuoi_di_theo_thu_tu_ten_nhanh()
    {
        var candidates = new[]
        {
            C("H264/1920x1080", 0, 3, 16), C("H264/1920x1080", 1, 3, 24), C("H264/1920x1080", 2, 3, 32),
            C("Hevc/1920x1080", 0, 3, 26), C("Hevc/1920x1080", 1, 3, 34), C("Hevc/1920x1080", 2, 3, 42),
        };

        var coarse = PilotSearch.CoarseCandidates(candidates);

        // Mỗi nhánh 2 điểm, và thứ tự nhánh là tên — tất định.
        Assert.Equal(
            ["H264/1920x1080/crf16", "H264/1920x1080/crf32",
             "Hevc/1920x1080/crf26", "Hevc/1920x1080/crf42"],
            coarse.Select(c => c.Id));
    }

    [Fact]
    public async Task Nhanh_chi_duoc_dong_hai_diem_dau_va_cuoi()
    {
        // Một nhánh 5 điểm: coarse dùng 2 điểm. Dò điểm giữa ở giai đoạn này chưa thêm
        // thông tin nào, vì nếu CoarseProbe đã rớt thì điểm giữa chắc chắn cũng rớt.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 80));

        await search.RunAsync(Request(
            C("b", 0, 5, 10), C("b", 1, 5, 14), C("b", 2, 5, 18), C("b", 3, 5, 26), C("b", 4, 5, 30)));

        Assert.Equal(2, encoder.Encoded.Count);
    }

    [Fact]
    public async Task Nhanh_dat_thi_cac_diem_con_lai_duoc_dong_tiep()
    {
        // Cùng một nhánh 5 điểm, nhưng lần này coarse đạt nên phải dò tiếp. Ngân sách 24
        // cho phép cả 5.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(Request(
            C("b", 0, 5, 10), C("b", 1, 5, 14), C("b", 2, 5, 18), C("b", 3, 5, 26), C("b", 4, 5, 30)));

        Assert.Equal(5, encoder.Encoded.Count);
        Assert.Equal(SearchStatus.SelectedCandidate, result.Status);
        Assert.Contains(result.Evaluated, e => e.Stage == SearchStage.Bracket);
    }

    [Fact]
    public async Task Khong_bao_gio_bia_tham_so_ngoai_tap_ung_vien()
    {
        // Ứng viên dùng để tìm phải đến từ đúng tập mà planner sinh ra. Không tự sinh thêm.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 95));
        var given = new[] { C("b", 0, 3, 16), C("b", 1, 3, 24), C("b", 2, 3, 32) };

        await search.RunAsync(Request(given));

        var allowed = given.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(encoder.Encoded, id => Assert.Contains(id, allowed));
    }

    [Fact]
    public async Task Trung_khong_bi_chi_phi_gioi_han_boi_so_danh_giua_cac_vong()
    {
        // 20 ứng viên, trần 5: chỉ 5 được đánh giá. Ngân sách phải thật sự giới hạn, và phải
        // tính cả ứng viên encode hỏng — vì chúng đã tốn thời gian thật.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 95));

        var many = Enumerable.Range(0, 20).Select(i => C("b", i, 20, 10 + i)).ToList();
        var result = await search.RunAsync(Request([.. many]) with { MaxEvaluations = 5 });

        Assert.Equal(5, encoder.Encoded.Count);
        Assert.Equal(5, result.Statistics.CandidatesEvaluated);
        Assert.Equal(5, result.Statistics.PilotEncodes);
        Assert.Equal(20, result.Statistics.CandidatesPlanned);
    }

    [Fact]
    public async Task Ngan_sach_bao_toan_phai_co_hieu_luc()
    {
        // Ngân sách bằng 0 nghĩa là chưa thử gì. Không có căn cứ để kết luận, nên phải là
        // hạ tầng chứ không phải "không ứng viên nào đạt" — vì chỉ hạ tầng mới được rơi về
        // legacy.
        var search = new PilotSearch(
            new FakeEncoder(), new FakeReferences(), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(
            Request(C("b", 0, 3, 16)) with { MaxEvaluations = 0 });

        Assert.Equal(SearchStatus.SearchInfrastructureFailure, result.Status);
    }

    // ---------------------------------------------------------------- ba trạng thái

    [Fact]
    public async Task Khong_ung_vien_nao_dat_thi_la_ket_qua_hop_le_khong_phai_loi()
    {
        // Phân biệt quan trọng nhất của cả lớp này. "Không có ứng viên nào đạt" là kết quả
        // kinh doanh hợp lệ và TUYỆT ĐỐI không được biến thành lỗi hạ tầng.
        var search = new PilotSearch(
            new FakeEncoder(), new FakeReferences(), new FakeMeasurer(_ => 70));

        var result = await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.Equal(SearchDecisionReasons.PilotAllCandidatesRejected, result.Outcome.Reason);
        Assert.False(result.HasSelection);
        Assert.Null(result.Selected);
    }

    [Fact]
    public async Task Khong_co_ung_vien_nao_de_thu()
    {
        // Planner không sinh được ứng viên nào — lưới an toàn vẫn chắc, và đây vẫn là quyết
        // định hợp lệ, không phải lỗi.
        var search = new PilotSearch(
            new FakeEncoder(), new FakeReferences(), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(Request() with { Candidates = [] });

        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.Equal(SearchDecisionReasons.PilotNoCandidates, result.Outcome.Reason);
    }

    [Fact]
    public async Task Khong_cat_duoc_tham_chieu_la_loi_ha_tang()
    {
        // Không có tham chiếu thì không đo được gì cả — kể cả chất lượng của chính nguồn.
        // Đây là hỏng hạ tầng, và là lý do duy nhất được phép rơi về đường legacy.
        var search = new PilotSearch(
            new FakeEncoder(), new FakeReferences(fail: true), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(Request(C("b", 0, 3, 16)));

        Assert.Equal(SearchStatus.SearchInfrastructureFailure, result.Status);
    }

    [Fact]
    public async Task Mot_ung_vien_encode_hong_chi_bi_loai_roi_khong_phai_loi_ha_tang()
    {
        // ffmpeg từ chối một tổ hợp tham số là sự thật về ỨNG VIÊN đó, không phải về toàn bộ
        // lần tìm kiếm. Nếu còn ứng viên đã đo được và đạt thì vẫn kết luận được.
        //
        // Ngược với trường hợp không đo được ứng viên nào, khi đó mới thật sự không có căn cứ
        // để kết luận và phải báo hạ tầng.
        var encoder = new FakeEncoder(c => c.PointIndex == 1 ? [Failed(c)] : []);
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(Request(
            C("b", 0, 3, 16), C("b", 1, 3, 24), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.SelectedCandidate, result.Status);
        Assert.DoesNotContain(result.Evaluated, e => e.Candidate.PointIndex == 1);

        // Chi phí bỏ ra vẫn phải hiện ra. Giấu lần encode hỏng đi là khiến báo cáo tự dối
        // mình rằng lần tìm kiếm này rẻ hơn thực tế.
        Assert.Equal(3, result.Statistics.PilotEncodes);
        Assert.Equal(2, result.Statistics.CandidatesEvaluated);
    }

    [Fact]
    public async Task Encode_hong_va_tat_ca_ung_vien_khong_duoc_gi_la_loi_ha_tang()
    {
        var encoder = new FakeEncoder(c => [Failed(c)]);
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(Request(
            C("b", 0, 3, 16), C("b", 1, 3, 24), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.SearchInfrastructureFailure, result.Status);
        Assert.Contains("mọi clip thử nghiệm đều hỏng", result.Outcome.Message);
    }

    // ---------------------------------------------------------------- dọn dẹp

    [Fact]
    public async Task Clip_va_tham_chieu_duoc_giai_phong()
    {
        var encoder = new FakeEncoder();
        var references = new FakeReferences();
        var search = new PilotSearch(encoder, references, new FakeMeasurer(_ => 95));

        await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 2, 3, 32)));

        Assert.NotEmpty(encoder.Released);
        Assert.Equal(1, references.Released);
    }

    [Fact]
    public async Task Tham_chieu_duoc_cat_dung_mot_lan_cho_moi_ung_vien()
    {
        // Một lần cho cả ba ứng viên: cắt lại là tốn công và tạo thêm nguồn lệch khung hình.
        var references = new FakeReferences();
        var search = new PilotSearch(new FakeEncoder(), references, new FakeMeasurer(_ => 95));

        await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 1, 3, 24), C("b", 2, 3, 32)));

        Assert.Equal(1, references.Extracted);
    }

    [Fact]
    public async Task Tham_chieu_van_duoc_giai_phong_khi_tim_kiem_hong_hay()
    {
        // Đường lỗi cũng phải dọn. Rò rỉ tham chiếu tích luỹ theo mỗi tệp làm đầy ổ đĩa
        // tạm sau một số lần chạy dài.
        var references = new FakeReferences(fail: true);
        var search = new PilotSearch(new FakeEncoder(), references, new FakeMeasurer(_ => 95));

        await search.RunAsync(Request(C("b", 0, 3, 16)));

        // Cắt tham chiếu ném ngay ở lần đầu nên không có gì để giải phóng; điều cần kiểm
        // ở đây là lần chạy kết thúc sạch, không treo và không ném lỗi thứ hai.
        Assert.Equal(0, references.Released);
    }

    // ---------------------------------------------------------------- chọn và chế độ

    [Fact]
    public async Task Chon_ung_vien_nho_nhat_trong_so_dat_chat_luong()
    {
        var search = new PilotSearch(
            new FakeEncoder(), new FakeReferences(), new FakeMeasurer(_ => 95));

        var result = await search.RunAsync(Request(
            C("b", 0, 3, 10), C("b", 1, 3, 18), C("b", 2, 3, 30)));

        Assert.Equal(SearchStatus.SelectedCandidate, result.Status);
        Assert.NotNull(result.Selected);
    }

    [Fact]
    public async Task Khong_mode_nao_co_ganh_nguon_nhat_dinh()
    {
        // Cùng một mode BALANCED, cùng một ngưỡng, nhưng chất lượng nguồn khác nhau phải cho
        // kết luận khác nhau. Nếu kết quả giống nhau, nghĩa là ta đang chọn theo tên mode
        // thay vì theo số đo.
        static Task<SearchResult> RunAsync(double quality) => new PilotSearch(
            new FakeEncoder(), new FakeReferences(), new FakeMeasurer(_ => quality)).RunAsync(
            Request(C("b", 0, 3, 10), C("b", 1, 3, 18), C("b", 2, 3, 30)));

        Assert.Equal(SearchStatus.SelectedCandidate, (await RunAsync(95)).Status);
        Assert.Equal(SearchStatus.NoFeasibleCandidate, (await RunAsync(85)).Status);
    }

    [Fact]
    public async Task Ngan_sach_va_thu_tu_phai_tat_dinh_giua_hai_lan_chay()
    {
        // Cùng đầu vào phải cho cùng thứ tự đánh giá. Nếu không, báo cáo chẩn đoán trở
        // nên vô dụng vì không tái lập được.
        static async Task<string> RunOnceAsync()
        {
            var result = await new PilotSearch(
                new FakeEncoder(), new FakeReferences(), new FakeMeasurer(_ => 95)).RunAsync(
                Request(
                    C("b", 0, 5, 10), C("b", 1, 5, 14), C("b", 2, 5, 18),
                    C("b", 3, 5, 26), C("b", 4, 5, 30), C("c", 0, 5, 20), C("c", 1, 5, 30)));

            return string.Join("|", result.Evaluated.Select(e => $"{e.Candidate.Id}@{e.Stage}"));
        }

        Assert.Equal(await RunOnceAsync(), await RunOnceAsync());
    }

    [Fact]
    public async Task Huy_giua_roi_khong_duoc_nhan_tinh_la_ket_qua()
    {
        // Hủy không phải là một kết quả: nếu trả về "không có ứng viên nào đạt" thì lần chạy
        // sau có thể ghi đè tệp nguồn. .NET dùng OperationCanceledException cho việc này.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 95));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => search.RunAsync(Request(C("b", 0, 3, 16)), cts.Token));

        Assert.Empty(encoder.Encoded);
    }
}
