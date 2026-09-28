namespace UltraCompressor.Core.Scheduling;

/// <summary>
/// Ước lượng thời gian còn lại từ tốc độ quan sát gần đây.
///
/// Bản gốc tính tốc độ = <c>BytesOriginal / elapsed</c> nhưng <c>BytesOriginal</c> chỉ được
/// cộng dồn <i>sau</i> khi một tệp xong, nên tốc độ luôn bị trễ và ETA lúc đầu vô nghĩa
/// (bug B18). Ở đây theo dõi mốc (thời gian, dung lượng đã nén) trong một cửa sổ trượt,
/// nên ETA ổn định ngay từ đầu và không bị giật khi gặp tệp lớn nhỏ xen kẽ.
/// </summary>
public sealed class EtaEstimator
{
    private readonly Queue<(double Seconds, long Bytes)> _samples = new();
    private readonly double _windowSeconds;

    public EtaEstimator(double windowSeconds = 20)
    {
        _windowSeconds = Math.Max(5, windowSeconds);
    }

    public void Reset()
    {
        _samples.Clear();
    }

    public void Start() => Reset();

    public void AddProgress(long bytesProcessed, double? elapsedSeconds = null)
    {
        var now = elapsedSeconds ?? System.Diagnostics.Stopwatch.GetTimestamp()
            / (double)System.Diagnostics.Stopwatch.Frequency;

        _samples.Enqueue((now, bytesProcessed));
        while (_samples.Count > 2 && now - _samples.Peek().Seconds > _windowSeconds)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Cửa sổ thời gian tối thiểu để coi là đủ dữ liệu, tính bằng giây.</summary>
    private const double MinSampleSeconds = 1.0;

    /// <summary>
    /// Giây còn lại ước tính, hoặc -1 nếu chưa đủ dữ liệu để nói một cách có căn cứ.
    ///
    /// Cố tình trả -1 thay vì đoán bừa. Nhánh dự phòng từng pha trộn đồng hồ hệ thống vào
    /// mốc đã truyền vào, nên với hai mẫu cách nhau 0,1 giây nó ra con số hàng chục nghìn
    /// giây — tệ hơn cả là không có ước lượng nào.
    /// </summary>
    public double Estimate(long bytesRemaining)
    {
        if (bytesRemaining <= 0) return 0;
        if (_samples.Count < 2) return -1;

        var first = _samples.Peek();
        var last = _samples.Last();

        var span = last.Seconds - first.Seconds;
        var delta = last.Bytes - first.Bytes;

        if (span < MinSampleSeconds || delta <= 0) return -1;

        return bytesRemaining / (delta / span);
    }

    /// <summary>Byte mỗi giây, để hiển thị tốc độ. 0 khi chưa đủ dữ liệu.</summary>
    public double BytesPerSecond()
    {
        if (_samples.Count < 2) return 0;
        var first = _samples.Peek();
        var last = _samples.Last();
        var span = last.Seconds - first.Seconds;
        if (span < MinSampleSeconds) return 0;

        var delta = last.Bytes - first.Bytes;
        return delta <= 0 ? 0 : delta / span;
    }
}
