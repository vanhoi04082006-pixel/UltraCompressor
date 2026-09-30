using System.Globalization;
using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Chọn các đoạn nguồn đại diện cho độ khó nén.
///
/// <para>Đây là thuần toán: nhận đặc trưng đã đo, trả về các đoạn đã chọn. Không mở tệp,
/// không gọi ffmpeg, không biết ứng viên nào sẽ được encode. Nhờ vậy nó kiểm thử được
/// bằng dữ liệu tổng hợp, và giai đoạn sau có thể dùng lại nguyên vẹn cho vòng tìm
/// nghiệm mà không phải viết lại.</para>
///
/// <para>Ba nguyên tắc định hình thuật toán:</para>
/// <list type="number">
/// <item><b>Chuẩn hoá tương đối, trong chính tệp đó.</b> Mọi đặc trưng được chuẩn hoá
/// theo min/max của các mẫu của cùng một tệp. Không cần hằng số tuyệt đối nào, và
/// không suy ra ngưỡng từ tập hiệu chỉnh hiện có. Đánh đổi: một tệp đều khó sẽ trông
/// "đa dạng" — nhưng ta đang chọn đoạn khó <i>trong tệp này</i>, nên đó đúng là câu hỏi
/// cần trả lời. Vấn đề "tệp này khó đến đâu" thuộc ứng viên ở giai đoạn sau, không thuộc
/// đây.</item>
/// <item><b>Vai trò, không phải top-N.</b> Sắp xếp theo một điểm tổng rồi lấy 3 cái
/// trên cùng gần như chắc chắn rơi vào cùng một cảnh. Chọn theo vai trò buộc các đoạn
/// phải khác nhau về bản chất.</item>
/// <item><b>Không bịa vai trò không có thật.</b> Tệt gần như tĩnh không có đoạn
/// HIGH_MOTION; mọi cảnh đều dễ thì không có LOW_COMPLEXITY. Tạo ra chúng chỉ để lấp
/// đủ số lượng là bịa dữ liệu, và giai đoạn sau sẽ encode thừ mà không thu được thông
/// tin nào.</item>
/// </list>
/// </summary>
public static class RepresentativeWindowSelector
{
    /// <summary>Điểm khó tổng thể của một mẫu, sau khi chuẩn hoá tương đối.</summary>
    private readonly record struct Normalized(
        int Index,
        double Spatial,
        double Motion,
        double Scene,
        double Sharpness,
        double Overall);

    /// <summary>
    /// Chọn đoạn từ các mẫu đã đo.
    /// </summary>
    /// <param name="samples">Đặc trưng thô, theo thứ tự thời gian.</param>
    /// <param name="duration">Thời lượng tệp, dùng để giữ đoạn nằm trong tệp.</param>
    /// <param name="config">Chính sách: số đoạn, trọng số, khoảng cách tối thiểu.</param>
    /// <param name="stats">Số liệu quét để ghép vào kết quả.</param>
    public static WindowSelection Select(
        IReadOnlyList<WindowFeatures> samples,
        TimeSpan? duration,
        AppConfig config,
        ScanStats stats)
    {
        var usable = samples.Where(s => s.IsUsable).ToList();
        if (usable.Count == 0)
        {
            return Fallback(duration, config, stats with { FellBack = true });
        }

        var normalized = Normalize(usable, config);
        var seconds = duration is { } d && d > TimeSpan.Zero ? d.TotalSeconds : 0;
        var medianOverall = Median(normalized.Select(x => x.Overall));

        // Khoảng cách tối thiểu bị giới hạn bởi chiều dài tệp. Một tệp 60 giây không thể
        // chia ra ba đoạn cách nhau 30 giây — mốc cố định sẽ khiến bộ chọn âm thầm trả về
        // ít hơn số đoạn yêu cầu mà không nói lý do. Cắt theo mẫu: mỗi đoạn cần
        // (target + 1) khoảng trống để tồn tại.
        var separation = EffectiveSeparation(config, seconds);

        var chosen = new List<RepresentativeWindow>();
        var picked = new List<Normalized>();
        var pickedStarts = new List<double>();

        // Thứ tự chọn: CỰC ĐẠI TRƯỚC, TRUNG BÌNH SAU.
        //
        // Đây là điều hướng, không phải sở thích về thứ tự hiển thị. Một vai trò cực đại
        // có đúng MỘT ứng viên đáng chọn; vai trò "điển hình" thì có cả một mảng ứng viên
        // gần như ngang nhau quanh trung vị. Nếu cho Typical đi trước, nó có thể chiếm
        // mẫu nằm sát mẫu khó nhất, rồi HighMotion bị khoảng-cách-tối-thiểu loại ra và rơi
        // về một ứng viên tầm thường — tức là đo đoạn dễ thay vì đoạn khó.
        foreach (var role in SelectionOrder())
        {
            if (chosen.Count >= EffectiveTarget(config)) break;

            var candidate = BestCandidateFor(role, normalized, medianOverall, picked, pickedStarts, usable, separation, config);
            if (candidate is not { } hit) continue;

            picked.Add(hit);
            pickedStarts.Add(usable[hit.Index].StartSeconds);
            chosen.Add(Build(hit, usable, role, seconds, config, picked.Count - 1));
        }

        if (chosen.Count == 0)
        {
            return Fallback(duration, config, stats with { FellBack = true });
        }

        // Trả về theo thứ tự thời gian. Thứ tự chọn là thứ tự ưu tiên nội bộ; người đọc
        // nhật ký muốn thấy các đoạn xếp theo timeline.
        return new WindowSelection(
            [.. chosen.OrderBy(w => w.StartSeconds)],
            usable,
            stats with { FellBack = false });
    }

    /// <summary>
    /// Chiến lược dự phòng: các vị trí chia đều.
    ///
    /// <para>Được phép dùng vị trí cố định — nhưng chỉ ở đây, khi quét thất bại. Trước đây
    /// đây là chiến lược <i>duy nhất</i>, và đó là lý do nó không phản ánh nội dung: một
    /// tệp mà 80% đầu là cảnh tĩnh vẫn ra một đoạn "đại diện" nằm ngay trong cảnh tĩnh.</para>
    /// </summary>
    public static WindowSelection Fallback(TimeSpan? duration, AppConfig config, ScanStats stats)
    {
        var length = Math.Max(0.5, config.WindowDurationSeconds);
        var seconds = duration is { } d && d > TimeSpan.Zero ? d.TotalSeconds : 0;
        var wanted = Math.Clamp(config.TargetWindowCount, 1, Math.Max(1, config.MaxWindowCount));

        var windows = new List<RepresentativeWindow>(wanted);
        for (var i = 0; i < wanted; i++)
        {
            var fraction = wanted <= 1 ? 0.5 : i / (double)(wanted - 1);
            var start = seconds > 0
                ? Math.Clamp(fraction * seconds, 0, Math.Max(0, seconds - length))
                : 0;

            windows.Add(new RepresentativeWindow(
                start, Math.Min(length, seconds > 0 ? seconds : length),
                WindowRole.Typical,
                SpatialScore: 0, MotionScore: 0, SceneScore: 0, SharpnessScore: 0,
                OverallComplexity: 0, SampleIndex: -1,
                Reason: "Quét đặc tính thất bại nên dùng vị trí chia đều; không có số đo đi kèm."));
        }

        return WindowSelection.Fallback(windows, stats);
    }

    /// <summary>
    /// Thứ tự <b>chọn</b>. Cực đại trước, trung bình sau — xem chỗ gọi để rõ lý do.
    /// </summary>
    private static IEnumerable<WindowRole> SelectionOrder() =>
    [
        WindowRole.HighMotion,
        WindowRole.HighSpatial,
        WindowRole.Typical,
        WindowRole.LowComplexity,
    ];

    private static int EffectiveTarget(AppConfig config) =>
        Math.Clamp(config.TargetWindowCount, 1, Math.Max(1, config.MaxWindowCount));

    /// <summary>
    /// Khoảng cách tối thiểu thực tế: không vượt quá giá trị cấu hình, và không vượt quá
    /// mức mà thời lượng tệp cho phép.
    ///
    /// <para>Không cắt thì một tệp ngắn không bao giờ ra đủ số đoạn yêu cầu, và số đoạn
    /// thiếu đi lại không kèm lý do nào để đọc. Không giới hạn thì hai đoạn có thể rơi
    /// sát nhau trong một tệp dài và trở thành hai cách nhìn cùng một cảnh.</para>
    /// </summary>
    private static double EffectiveSeparation(AppConfig config, double durationSeconds)
    {
        var wanted = Math.Max(0, config.MinWindowSeparationSeconds);
        if (durationSeconds <= 0) return wanted;

        var slots = EffectiveTarget(config) + 1;
        return Math.Min(wanted, durationSeconds / slots);
    }

    // ---------------------------------------------------------------- chuẩn hoá

    private static List<Normalized> Normalize(List<WindowFeatures> samples, AppConfig config)
    {
        // Độ mờ nhỏ nghĩa là nhiều cạnh sắc, nên đảo chiều trước khi chuẩn hoá.
        var sharpnessRaw = samples.Select(s => -s.Blur).ToList();

        var spatial = Scale(samples.Select(s => s.Spatial));
        var motion = Scale(samples.Select(s => s.Motion));
        var scene = Scale(samples.Select(s => s.SceneScore));
        var sharpness = Scale(sharpnessRaw);

        var result = new List<Normalized>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var overall =
                config.ComplexityWeightSpatial * spatial[i] +
                config.ComplexityWeightMotion * motion[i] +
                config.ComplexityWeightScene * scene[i] +
                config.ComplexityWeightSharpness * sharpness[i];

            result.Add(new Normalized(i, spatial[i], motion[i], scene[i], sharpness[i], Clamp01(overall)));
        }

        return result;
    }

    /// <summary>
    /// Đưa một đặc trưng về [0,1] bằng min/max của chính các mẫu trong tệp.
    ///
    /// <para>Mẫu nào không đo được (NaN) thì lấy 0 — thiếu dữ liệu không được làm hỏng
    /// cả lượt chọn, và 0 là giá trị trung tính an toàn cho một điểm khó.</para>
    /// </summary>
    private static double[] Scale(IEnumerable<double> values)
    {
        var list = values.Select(v => double.IsFinite(v) ? v : 0).ToList();
        if (list.Count == 0) return [];

        var min = list.Min();
        var max = list.Max();
        var span = max - min;

        // Tệp mà mọi mẫu giống hệt nhau: không có gì để phân biệt, trả về giữa để
        // không đẩy cả đoạn lên đỉnh cũng không đẩy xuống đáy.
        if (span <= 0) return [.. list.Select(_ => 0.5)];

        return [.. list.Select(v => Clamp01((v - min) / span))];
    }

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    // ---------------------------------------------------------------- chọn theo vai trò

    private static Normalized? BestCandidateFor(
        WindowRole role,
        List<Normalized> all,
        double medianOverall,
        List<Normalized> alreadyPicked,
        List<double> pickedStarts,
        List<WindowFeatures> samples,
        double separation,
        AppConfig config)
    {
        // Khoảng cách tối thiểu: hai đoạn cách nhau dưới ngưỡng là hai cách nhìn cùng
        // một cảnh, đo chừng đó là lãng phí. So trên THỜI GIAN thực, không so chỉ số
        // mẫu — mẫu cách nhau đều nhau nên hai chỉ số cạnh nhau vẫn có thể là hai cảnh khác.
        // Bỏ đi một vai trò nếu vai trò đó không tồn tại trong tệp này: ràng buộc này
        // không phụ thuộc vào ứng viên nào đã chọn, nên kiểm một lần trước cả vòng lặp.
        if (!IsRolePlausible(role, all, samples, config)) return null;

        // ThenBy theo chỉ số mẫu ở mọi nhánh: khi hai mẫu có điểm bằng nhau, phải chọn
        // cái nào sớm hơn, nếu không kết quả phụ thuộc thứ tự mà LINQ trả về.
        var ordered = role switch
        {
            WindowRole.Typical => all
                .OrderBy(n => Math.Abs(n.Overall - medianOverall))
                .ThenBy(n => n.Index),

            WindowRole.HighSpatial => all
                .OrderByDescending(n => n.Spatial)
                .ThenByDescending(n => n.Overall)
                .ThenBy(n => n.Index),

            WindowRole.HighMotion => all
                .OrderByDescending(n => n.Motion)
                .ThenByDescending(n => n.Overall)
                .ThenBy(n => n.Index),

            WindowRole.LowComplexity => all
                .OrderBy(n => n.Overall)
                .ThenBy(n => n.Index),

            _ => all.OrderBy(n => n.Index),
        };

        foreach (var candidate in ordered)
        {
            if (alreadyPicked.Any(p => p.Index == candidate.Index)) continue;

            var start = samples[candidate.Index].StartSeconds;
            if (pickedStarts.Any(s => Math.Abs(s - start) < separation)) continue;

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Vai trò này có thật sự tồn tại trong tệp này không.
    ///
    /// <para>Đây là chỗ chống bịa. Tệt gần như tĩnh có độ chuyển động trải rất hẹp, nên
    /// không có "đoạn chuyển động cao" — tạo ra một cái chỉ là chọn ngẫu nhiên rồi gọi
    /// tên là chuyển động. Ngưỡng nằm trong cấu hình, không rải trong mã.</para>
    /// </summary>
    /// <summary>
    /// Vai trò này có thật sự tồn tại trong tệp này không.
    ///
    /// <para>Đây là chỗ chống bịa. Hai điều kiện phải cùng đúng:</para>
    /// <list type="bullet">
    /// <item><b>Tuyệt đối</b> — tệp phải thật sự có chuyển động (hoặc chi tiết) ở mức đáng
    /// kể. Chuẩn hoá tương đối luôn khuếch đại nhiễu thành [0,1], nên tệp gần như tĩnh
    /// cũng "có biến thiên" nếu chỉ nhìn phần tương đối.</item>
    /// <item><b>Tương đối</b> — và phải khác biệt đủ so với phần trung vị, nếu không mọi
    /// tệp đều ra một đoạn "chuyển động cao" ở đúng vị trí trung bình.</item>
    /// </list>
    /// </summary>
    private static bool IsRolePlausible(
        WindowRole role,
        List<Normalized> all,
        List<WindowFeatures> samples,
        AppConfig config)
    {
        switch (role)
        {
            case WindowRole.HighMotion:
                {
                    var rawMax = samples.Max(s => s.Motion);
                    if (rawMax < config.MotionAbsoluteFloor) return false;

                    var spread = all.Max(n => n.Motion) - Median(all.Select(n => n.Motion));
                    return spread >= config.MotionSpreadThreshold;
                }

            case WindowRole.HighSpatial:
                {
                    if (samples.Max(s => s.Spatial) < config.SpatialAbsoluteFloor) return false;

                    var spread = all.Max(n => n.Spatial) - all.Min(n => n.Spatial);
                    return spread > 0.10;
                }

            case WindowRole.LowComplexity:
                {
                    var spread = all.Max(n => n.Overall) - all.Min(n => n.Overall);
                    // Không có cảnh nào dễ hơn đáng kể thì không có đoạn "dễ".
                    return spread > 0.25;
                }

            default:
                return true;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        var mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }

    private static RepresentativeWindow Build(
        Normalized chosen,
        List<WindowFeatures> samples,
        WindowRole role,
        double durationSeconds,
        AppConfig config,
        int ordinal)
    {
        var sample = samples[chosen.Index];
        var length = Math.Max(0.5, config.WindowDurationSeconds);

        // Cửa sổ đo lấy từ đầu mẫu đã quét, và không vượt ra ngoài tệp. Với tệp ngắn hơn
        // cửa sổ thì bịt lại cho vừa.
        var start = Math.Max(0, sample.StartSeconds);
        if (durationSeconds > 0)
        {
            start = Math.Min(start, Math.Max(0, durationSeconds - length));
            length = Math.Min(length, durationSeconds);
        }

        return new RepresentativeWindow(
            start,
            length,
            role,
            SpatialScore: chosen.Spatial,
            MotionScore: chosen.Motion,
            SceneScore: chosen.Scene,
            SharpnessScore: chosen.Sharpness,
            OverallComplexity: chosen.Overall,
            SampleIndex: chosen.Index,
            Reason: Explain(role, chosen, ordinal));
    }

    /// <summary>
    /// Câu giải thích bằng tiếng Việt, có số đi kèm.
    ///
    /// <para>Giai đoạn sau có nhiều ứng viên cạnh tranh; nếu không giải thích được vì sao
    /// chọn đoạn này thì không có cách nào biết khi nào kết quả đo là đáng tin. Số trong
    /// câu giải thích là điểm đã chuẩn hoá, không phải điểm thô của ffmpeg.</para>
    /// </summary>
    private static string Explain(WindowRole chosen, Normalized role, int ordinal)
    {
        var label = chosen switch
        {
            WindowRole.Typical => "điển hình",
            WindowRole.HighSpatial => "nhiều chi tiết",
            WindowRole.HighMotion => "chuyển động mạnh",
            WindowRole.LowComplexity => "đơn giản nhất",
            _ => chosen.ToString(),
        };

        return string.Format(
            CultureInfo.InvariantCulture,
            "Chọn đoạn {0} #{1}: không gian {2:0.00}, chuyển động {3:0.00}, đổi cảnh {4:0.00}, "
            + "độ sắc {5:0.00}, độ khó tổng {6:0.00}.",
            label,
            ordinal + 1,
            role.Spatial,
            role.Motion,
            role.Scene,
            role.Sharpness,
            role.Overall);
    }
}
