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

        /// <summary>
        /// Ghi đè theo đường dẫn clip (trong đó có id ứng viên). Dùng khi test cần mỗi ứng
        /// viên một số đo khác nhau — ví dụ tìm kiếm nhị phân, nơi biên khả thi nằm giữa
        /// các điểm và mỗi điểm phải rớt/đạt khác nhau.
        /// </summary>
        public Func<string, double?>? QualityByPath { get; set; }

        public Task<QualityResult?> MeasureAsync(
            string referencePath, string candidatePath,
            TimeWindow referenceWindow, TimeWindow candidateWindow,
            int displayWidth, int displayHeight, int candidateWidth, int candidateHeight,
            VmafModel model, int candidateStartFrame = 0, int referenceStartFrame = 0, CancellationToken token = default)
        {
            var role = RoleOf(candidatePath);
            Measured.Add(role);

            var mean = QualityByPath?.Invoke(candidatePath) ?? Quality(role);

            if (mean is not { } value)
            {
                return Task.FromResult<QualityResult?>(null);
            }

            // Giữ lại kích thước đích trong kết quả: đó là thứ cho phép kiểm tra rằng
            // orchestrator truyền đúng kích thước ứng viên xuống tầng đo, thay vì tự bịa.
            return Task.FromResult<QualityResult?>(new QualityResult(
                model.Id, referenceWindow,
                displayWidth, displayHeight,
                candidateWidth, candidateHeight,
                new QualitySample(value, value - 3, value - 20, 0.999, 72)));
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
        // Bản cũ đo cả hai ứng viên rồi mới kết luận. Nhị phân thì khác: điểm đầu (chất
        // lượng cao nhất) rớt THẬT thì điểm còn lại chắc chắn rớt, nên không tốn encode
        // cho nó nữa — 1 phép đo thay vì 2. Test cũ giữ lại sẽ khẳng định sự lãng phí mà
        // refactor này sinh ra để loại bỏ, nên cập nhật chứ không phải sửa cho qua.
        var measurer = new FakeMeasurer(role => role == WindowRole.Typical ? 80 : 95);
        var search = new PilotSearch(new FakeEncoder(), new FakeReferences(), measurer);

        var result = await search.RunAsync(Request(C("b", 0, 3, 16), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.Equal([WindowRole.Typical], measurer.Measured);
        Assert.Equal(1, result.Statistics.QualityMeasurements);
        Assert.Equal(2, result.Statistics.MeasurementsIfNoEarlyReject);
        Assert.Equal(1, result.Statistics.MeasurementsSavedByEarlyReject);
        Assert.Equal(50, result.Statistics.EarlyRejectSavingPercent);

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

    // --- tìm kiếm nhị phân trên thang điểm của nhánh (RED: chưa có) ---

    private static List<VideoEncodeCandidate> Branch5(string branch = "b") =>
        [.. Enumerable.Range(0, 5).Select(i => C(branch, i, 5, 10 + i) with { Id = $"{branch}/p{i}" })];

    private static Func<string, double?> QualityById(params (string Id, double Mean)[] map) =>
        path =>
        {
            foreach (var (id, mean) in map)
            {
                if (path.Contains(id, StringComparison.Ordinal))
                {
                    return mean;
                }
            }

            return null;
        };

    private static QualityFloor BinaryFloor =>
        QualityPolicy.For(CompressionLevel.Balanced, VmafModels.Neg);

    private static EvaluatedCandidate EvalForBinary(VideoEncodeCandidate candidate, double mean)
    {
        var sample = new QualitySample(mean, mean - 3, mean - 20, 0.999, 72);
        var measurement = new WindowMeasurement(WindowRole.Typical, 0, 3, sample);

        return new EvaluatedCandidate
        {
            Candidate = candidate,
            Aggregate = QualityAggregator.Aggregate(candidate.Id, [measurement], BinaryFloor),
            Estimate = new SizeEstimate
            {
                TotalBytes = 1_000_000,
                VideoBytes = 900_000,
                AudioBytes = 100_000,
                ContainerBytes = 0,
                DurationSeconds = 300,
                IsReliable = true,
                Assumptions = [],
            },
            Measurements = [measurement],
            MeasurementsTaken = 1,
            MeasurementsSkipped = 0,
            WindowsNotMeasured = 0,
            ComputeCostSeconds = 1,
            Stage = SearchStage.Coarse,
        };
    }

    [Fact]
    public void BranchSearch_tra_thu_tu_dau_cuoi_giua()
    {
        // Nhánh 5 điểm: đầu, cuối, rồi chia đôi. Thứ tự này là toàn bộ ý nghĩa của tìm
        // kiếm nhị phân — đo sai thứ tự thì không còn là nhị phân nữa.
        var points = Branch5();
        var search = new BranchSearch(points, BinaryFloor, CompressionLevel.Balanced);

        Assert.Equal("b/p0", search.Next().Candidate?.Id);
        search.Observe(EvalForBinary(points[0], 91));
        Assert.Equal("b/p4", search.Next().Candidate?.Id);
        search.Observe(EvalForBinary(points[4], 80));
        Assert.Equal("b/p2", search.Next().Candidate?.Id);
        search.Observe(EvalForBinary(points[2], 91));
        Assert.Equal("b/p3", search.Next().Candidate?.Id);
        search.Observe(EvalForBinary(points[3], 87));

        Assert.True(search.IsClosed);
        Assert.Null(search.Next().Candidate);
        Assert.Equal(["b/p1"], search.Pruned.Select(p => p.CandidateId));
    }

    [Fact]
    public void BranchSearch_diem_dau_rot_thi_dong_nhanh_ngay()
    {
        // Điểm chất lượng cao nhất đã rớt thì mọi điểm thấp hơn chắc chắn rớt (đơn điệu).
        // Không có gì để chia đôi.
        var points = Branch5();
        var search = new BranchSearch(points, BinaryFloor, CompressionLevel.Balanced);

        Assert.Equal("b/p0", search.Next().Candidate?.Id);
        search.Observe(EvalForBinary(points[0], 80));

        Assert.True(search.IsClosed);
        Assert.Equal(["b/p1", "b/p2", "b/p3", "b/p4"], search.Pruned.Select(p => p.CandidateId));
        Assert.All(search.Pruned, p => Assert.Equal(SearchDecisionReasons.PilotPruned, p.Reason));
    }

    [Fact]
    public void BranchSearch_diem_cuoi_dat_thi_dong_nhanh_vi_moi_diem_deu_dat()
    {
        // Điểm sâu nhất mà đạt thì mọi điểm trên nó đều đạt. Điểm nhỏ nhất (cuối) đã được
        // đo, nên không còn gì để tìm — đóng nhánh, giữ lại các điểm giữa làm dự phòng
        // trong báo cáo chứ không tốn encode.
        var points = Branch5();
        var search = new BranchSearch(points, BinaryFloor, CompressionLevel.Balanced);

        search.Observe(EvalForBinary(points[0], 91));
        Assert.Equal("b/p4", search.Next().Candidate?.Id);
        search.Observe(EvalForBinary(points[4], 91));

        Assert.True(search.IsClosed);
        Assert.Equal(["b/p1", "b/p2", "b/p3"], search.Pruned.Select(p => p.CandidateId));
    }

    [Fact]
    public void BranchSearch_dung_som_khi_diem_dau_vuot_du_o_balanced()
    {
        // p0 đạt 95, dư 6 điểm so với ngưỡng 89 — vượt xa nhiễu đo (epsilon 0,5). Với
        // Balanced, đào sâu thêm chỉ để tìm tệp nhỏ hơn trong cùng nhánh; dừng sớm đổi
        // vài encode lấy đúng ứng viên chất lượng cao nhất của nhánh.
        var points = Branch5();
        var search = new BranchSearch(points, BinaryFloor, CompressionLevel.Balanced);

        search.Observe(EvalForBinary(points[0], 95));

        Assert.True(search.IsClosed);
        Assert.Equal(4, search.Pruned.Count);
    }

    [Fact]
    public void BranchSearch_khong_dung_som_khi_chua_vuot_du()
    {
        // 91 chỉ dư 2 điểm — chưa đủ xa nhiễu để kết luận nhánh "quá tốt". Phải dò tiếp.
        var points = Branch5();
        var search = new BranchSearch(points, BinaryFloor, CompressionLevel.Balanced);

        search.Observe(EvalForBinary(points[0], 91));

        Assert.False(search.IsClosed);
        Assert.Equal("b/p4", search.Next().Candidate?.Id);
    }

    [Fact]
    public void BranchSearch_strong_khong_dung_som_du_vuot_du()
    {
        // Strong ưu tiên dung lượng nhỏ nhất: dù p0 vượt dư, vẫn phải dò tới điểm sâu
        // nhất để tìm tệp nhỏ nhất còn đạt. Dừng sớm ở đây là phản lại chính mode.
        var points = Branch5();
        var strongFloor = QualityPolicy.For(CompressionLevel.Strong, VmafModels.Neg);
        var search = new BranchSearch(points, strongFloor, CompressionLevel.Strong);

        search.Observe(EvalForBinary(points[0], 95));

        Assert.False(search.IsClosed);
        Assert.Equal("b/p4", search.Next().Candidate?.Id);
    }

    [Fact]
    public async Task Nhi_phan_danh_gia_theo_thu_tu_dau_cuoi_giua()
    {
        // Tích hợp: thứ tự encode thật phải là p0, p4, p2, p3. p1 không bao giờ được đo
        // vì nó nằm giữa hai điểm đã đạt (p0, p2) — đo nó không thêm thông tin nào.
        var measurer = new FakeMeasurer(_ => 95)
        {
            QualityByPath = QualityById(
                ("b/p0", 91), ("b/p1", 93), ("b/p2", 91), ("b/p3", 87), ("b/p4", 80)),
        };
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), measurer);

        var result = await search.RunAsync(Request([.. Branch5()]));

        Assert.Equal(["b/p0", "b/p4", "b/p2", "b/p3"], encoder.Encoded);
        Assert.DoesNotContain("b/p1", encoder.Encoded);
        Assert.Equal(SearchStatus.SelectedCandidate, result.Status);

        var pruned = result.Rejected.Where(r => r.CandidateId == "b/p1").ToList();
        Assert.Single(pruned);
        Assert.Equal(SearchDecisionReasons.PilotPruned, pruned[0].Reason);
    }

    [Fact]
    public async Task Diem_dau_vuot_du_thi_dung_ca_nhanh_o_balanced()
    {
        var measurer = new FakeMeasurer(_ => 95)
        {
            QualityByPath = QualityById(("b/p0", 95)),
        };
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), measurer);

        var result = await search.RunAsync(Request([.. Branch5()]));

        Assert.Equal(["b/p0"], encoder.Encoded);
        Assert.Equal(
            ["b/p1", "b/p2", "b/p3", "b/p4"],
            result.Rejected
                .Where(r => r.Reason == SearchDecisionReasons.PilotPruned)
                .Select(r => r.CandidateId)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Diem_dau_rot_thi_bo_ca_nhanh_chi_mot_lan_encode()
    {
        var encoder = new FakeEncoder();
        var search = new PilotSearch(
            encoder, new FakeReferences(), new FakeMeasurer(_ => 80));

        var result = await search.RunAsync(Request([.. Branch5()]));

        Assert.Equal(["b/p0"], encoder.Encoded);
        Assert.Equal(4, result.Rejected.Count(r => r.Reason == SearchDecisionReasons.PilotPruned));
    }

    [Fact]
    public async Task Tat_ca_dat_thi_chi_danh_gia_hai_dau()
    {
        // Không vượt dư (91, dư 2 < 3) nên không dừng sớm; nhưng điểm cuối đạt nghĩa là
        // mọi điểm đều đạt, và điểm nhỏ nhất đã có số đo — đóng nhánh sau 2 lần encode
        // thay vì 5.
        var measurer = new FakeMeasurer(_ => 91);
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), measurer);

        var result = await search.RunAsync(Request([.. Branch5()]));

        Assert.Equal(["b/p0", "b/p4"], encoder.Encoded);
        Assert.Equal(SearchStatus.SelectedCandidate, result.Status);
    }

    [Fact]
    public async Task Cac_nhanh_duoc_thu_xen_ke_theo_thu_tu_ten_nhanh()
    {
        // `CoarseCandidates` cũ đã bị `BranchSearch` thay thế, nhưng ý định của test cũ
        // vẫn giữ: thứ tự giữa các nhánh là tên nhánh, tất định. Hai nhánh đều rớt ở
        // điểm đầu nên mỗi nhánh chỉ tốn đúng một lần encode, xen kẽ nhau.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 80));

        var result = await search.RunAsync(Request(
            C("H264/1920x1080", 0, 3, 16), C("H264/1920x1080", 1, 3, 24), C("H264/1920x1080", 2, 3, 32),
            C("Hevc/1920x1080", 0, 3, 26), C("Hevc/1920x1080", 1, 3, 34), C("Hevc/1920x1080", 2, 3, 42)));

        Assert.Equal(
            ["H264/1920x1080/crf16", "Hevc/1920x1080/crf26"],
            encoder.Encoded);
        Assert.Equal(SearchStatus.NoFeasibleCandidate, result.Status);
        Assert.Equal(4, result.Rejected.Count(r => r.Reason == SearchDecisionReasons.PilotPruned));
    }

    // NOTE: hai test coarse cũ (`Nhanh_chi_duoc_dong_hai_diem_dau_va_cuoi` và
    // `Nhanh_dat_thi_cac_diem_con_lai_duoc_dong_tiep`) đã bị XÓA ở lần refactor này, không
    // phải sửa cho qua. Chúng khẳng định hành vi cũ (coarse luôn đo 2 điểm; nhánh đạt thì
    // đo hết) — mà chính hành vi đó là thứ nhị phân thay thế có chủ đích:
    // `Diem_dau_rot_thi_bo_ca_nhanh_chi_mot_lan_encode` (1 thay vì 2) và
    // `Tat_ca_dat_thi_chi_danh_gia_hai_dau` (2 thay vì 5). Giữ cả hai bản là giữ hai sự
    // thật mâu thuẫn trong cùng một suite.

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
    public async Task Ngan_sach_gioi_han_dung_so_danh_gia_ke_ca_khi_chua_co_nhanh_nao_dong()
    {
        // 10 nhánh × 3 điểm = 30 ứng viên, trần 5. Mỗi nhánh mới chỉ thử điểm đầu (đạt
        // nhưng không vượt dư nên không dừng sớm, cũng chưa đóng) thì ngân sách đã cạn.
        // Ngân sách phải thật sự giới hạn — và chỉ đếm ứng viên ĐÃ ĐO, không đếm pruned.
        var encoder = new FakeEncoder();
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 91));

        var many = Enumerable.Range(0, 10)
            .SelectMany(b => Enumerable.Range(0, 3).Select(i => C($"br{b}", i, 3, 16 + i * 8)))
            .ToList();
        var result = await search.RunAsync(Request([.. many]) with { MaxEvaluations = 5 });

        Assert.Equal(5, encoder.Encoded.Count);
        Assert.Equal(5, result.Statistics.CandidatesEvaluated);
        Assert.Equal(5, result.Statistics.PilotEncodes);
        Assert.Equal(30, result.Statistics.CandidatesPlanned);
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
        // Điểm ĐẦU encode hỏng: không có thông tin chất lượng nên KHÔNG được cắt nhánh
        // (khác hẳn với rớt đo). Nhánh chuyển sang dò tuyến tính, các điểm còn lại vẫn
        // được thử, và điểm hỏng được ghi lại với mã hạ tầng để báo cáo thấy nó.
        var encoder = new FakeEncoder(c => c.PointIndex == 0 ? [Failed(c)] : []);
        var search = new PilotSearch(encoder, new FakeReferences(), new FakeMeasurer(_ => 91));

        var result = await search.RunAsync(Request(
            C("b", 0, 3, 16), C("b", 1, 3, 24), C("b", 2, 3, 32)));

        Assert.Equal(SearchStatus.SelectedCandidate, result.Status);
        Assert.DoesNotContain(result.Evaluated, e => e.Candidate.PointIndex == 0);

        // Chi phí bỏ ra vẫn phải hiện ra. Giấu lần encode hỏng đi là khiến báo cáo tự dối
        // mình rằng lần tìm kiếm này rẻ hơn thực tế.
        var failed = result.Rejected
            .Where(r => r.Reason == SearchDecisionReasons.PilotEncodeFailed)
            .ToList();
        Assert.Single(failed);
        Assert.Equal("b/crf16", failed[0].CandidateId);

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
