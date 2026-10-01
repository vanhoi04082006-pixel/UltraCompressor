namespace UltraCompressor.Core.Media;

/// <summary>
/// Đặc trưng thô của một mẫu thời gian, đọc thẳng từ metadata của một lượt ffmpeg.
///
/// <para>Tách "thô" và "đã chuẩn hoá" là cố ý. Số thô phụ thuộc bản dựng ffmpeg và phụ
/// thuộc nội dung; số đã chuẩn hoá mới dùng để so sánh được. Giữ cả hai thì khi một
/// bản dựng đổi hành vi, mình thấy được ngay ở tầng thô thay vì phải đoán từ tầng đã
/// chuẩn hoá.</para>
///
/// <param name="StartSeconds">Đầu mẫu, tính từ đầu tệp.</param>
/// <param name="SampleSeconds">Độ dài mẫu thực tế đã đo.</param>
/// <param name="Spatial">Entropy chuẩn hoá theo độ sáng, ffmpeg đã trả về trên [0,1].</param>
/// <param name="Motion">Mức thay đổi giữa các khung, thô.</param>
/// <param name="SceneScore">Điểm đổi cảnh cao nhất trong mẫu, thô.</param>
/// <param name="Blur">Mức mờ trung bình, thô. Nhỏ nghĩa là nhiều cạnh sắc.</param>
/// <param name="LumaAverage">Độ sáng trung bình, thô.</param>
/// <param name="Frames">Số khung thực sự đọc được trong mẫu.</param>
public sealed record WindowFeatures(
    double StartSeconds,
    double SampleSeconds,
    double Spatial,
    double Motion,
    double SceneScore,
    double Blur,
    double LumaAverage,
    int Frames)
{
    /// <summary>Mẫu không đọc được khung nào thì vô dụng cho mọi mục đích.</summary>
    public bool IsUsable => Frames > 0;
}

/// <summary>Vai trò của một đoạn được chọn.</summary>
public enum WindowRole
{
    /// <summary>Gần với hành vi điển hình của tệp — mốc để so sánh mọi thứ khác.</summary>
    Typical,

    /// <summary>Nhiều chi tiết, cạnh, chữ hoặc kết cấu.</summary>
    HighSpatial,

    /// <summary>Chuyển động nhiều. Không có nếu tệp gần như tĩnh.</summary>
    HighMotion,

    /// <summary>Ít chi tiết nhất. Không có nếu mọi cảnh đều khó như nhau.</summary>
    LowComplexity,
}

/// <summary>
/// Một đoạn nguồn được chọn để đo chất lượng, kèm lý do.
///
/// <para><see cref="Reason"/> là phần bắt buộc, không phải tiện nghi. Ở giai đoạn sau,
/// khi có nhiều ứng viên, phải trả lời được "vì sao đo đoạn này mà không đo đoạn kia";
/// một danh sách điểm số không trả lời được câu hỏi đó.</para>
/// </summary>
public sealed record RepresentativeWindow(
    double StartSeconds,
    double DurationSeconds,
    WindowRole Role,
    double SpatialScore,
    double MotionScore,
    double SceneScore,
    double SharpnessScore,
    double OverallComplexity,
    int SampleIndex,
    string Reason)
{
    public double EndSeconds => StartSeconds + DurationSeconds;

    public override string ToString() =>
        $"{Role} @ {StartSeconds:0.0}s–{EndSeconds:0.0}s (độ khó {OverallComplexity:0.00})";
}

/// <summary>Số liệu về một lần quét, để đo chi phí và chẩn đoán.</summary>
public sealed record ScanStats(
    int SamplesRequested,
    int SamplesMeasured,
    TimeSpan Duration,
    bool FellBack)
{
    public static ScanStats None { get; } = new(0, 0, TimeSpan.Zero, true);
}

/// <summary>Kết quả chọn đoạn: các đoạn đã chọn, toàn bộ mẫu đã đo, và số liệu.</summary>
public sealed record WindowSelection(
    IReadOnlyList<RepresentativeWindow> Windows,
    IReadOnlyList<WindowFeatures> Samples,
    ScanStats Stats)
{
    /// <summary>Rơi về chiến lược vị trí cố định vì quét thất bại.</summary>
    public bool UsedFallback => Stats.FellBack;

    public static WindowSelection Fallback(
        IReadOnlyList<RepresentativeWindow> windows, ScanStats stats)
        => new(windows, [], stats);
}

/// <summary>Một đoạn tham chiếu đã được cắt thành clip.</summary>
/// <param name="Role">Vai trò đoạn.</param>
/// <param name="Path">Tệp clip tham chiếu.</param>
/// <param name="OriginSeconds">Mốc đoạn trên tệp nguồn, để truy vết.</param>
/// <param name="LengthSeconds">Thời lượng đoạn.</param>
/// <param name="Bytes">Kích thước clip.</param>
/// <param name="Elapsed">Thời gian cắt.</param>
public sealed record WindowReference(
    WindowRole Role,
    string Path,
    double OriginSeconds,
    double LengthSeconds,
    long Bytes,
    TimeSpan Elapsed);

/// <summary>
/// Seam cho việc cắt đoạn tham chiếu thành clip.
///
/// <para>Clip tham chiếu là <b>điểm neo thời gian</b> cho mọi ứng viên của đoạn đó: mọi
/// ứng viên đều bắt đầu từ cùng khung hình đầu, nên phép so đo không phụ thuộc vào việc
/// hai bên có seek trùng nhau hay không.</para>
///
/// <para>Seam đặt ở tầng media, không phải tầng tìm kiếm, vì cả đo thử lẫn lưới an toàn
/// cuối cùng đều cần cùng một điểm neo. Nếu hai tầng dùng hai cách cắt khác nhau, một
/// ứng viên có thể đạt ở giai đoạn thử rồi rớt ở lưới cuối vì lý do căn khung hình —
/// đúng lỗi đã đo được trên tệp thật.</para>
/// </summary>
public interface IReferenceWindowSource
{
    Task<IReadOnlyList<WindowReference>> ExtractAsync(
        string sourcePath, IReadOnlyList<RepresentativeWindow> windows, CancellationToken token);

    void Release(IReadOnlyList<WindowReference> references);
}
