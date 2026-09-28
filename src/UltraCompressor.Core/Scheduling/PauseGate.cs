namespace UltraCompressor.Core.Scheduling;

/// <summary>
/// Cổng chặn cho Tạm dừng. Sửa bug B15/B16 của bản gốc: vòng chờ dạng
/// <c>while (paused) await Task.Delay(500)</c> không nhìn thấy CancellationToken nên
/// hủy job lúc đang tạm dừng sẽ treo vô hạn.
///
/// Bản đầu tiên dùng <see cref="SemaphoreSlim"/> làm cả tín hiệu tạm dừng lẫn khoá loại trừ,
/// rồi ở cuối mỗi vòng lặp gọi <c>Resume()</c> để "trả permit". Hai chỗ đó không tương
/// ứng với nhau nên permit bị nuốt: tệp đầu tiên xong thì tệp thứ hai treo vĩnh viễn ở
/// <c>WaitAsync</c>, và <c>Pause()</c> lại chặn cả luồng gọi nên bấm Tạm dừng có thể treo
/// giao diện tới hết tệp đang chạy.
///
/// Nay tách hai ý nghĩa: cổng chỉ trả lời "được chạy chưa", việc loại trừ thao tác song
/// song do <c>SemaphoreSlim</c> riêng của engine đảm nhiệm.
/// </summary>
public sealed class PauseGate
{
    private readonly object _lock = new();

    /// <summary>Được hoàn tất để mở cổng cho tất cả người chờ.</summary>
    private TaskCompletionSource _open = Create();

    private static TaskCompletionSource Create() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsPaused { get; private set; }

    /// <summary>Đóng cổng. Không bao giờ chặn luồng gọi, nên bấm Tạm dừng không làm treo giao diện.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused) return;
            IsPaused = true;
        }
    }

    /// <summary>Mở cổng, đánh thức mọi người đang chờ.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!IsPaused) return;
            IsPaused = false;

            // Tạo cổng mới cho lần tạm dừng sau, nếu không thì người chờ sau đó sẽ
            // thấy Task đã hoàn tất và đi qua kể cả khi cổng đang đóng.
            _open.TrySetResult();
            _open = Create();
        }
    }

    /// <summary>
    /// Chờ cho tới khi được phép chạy. Trả về ngay nếu cổng đang mở, nên vòng lặp xử lý
    /// không phải giữ hay trả bất cứ khoá nào. Ném <see cref="OperationCanceledException"/>
    /// nếu bị hủy lúc đang chờ.
    /// </summary>
    public Task WaitAsync(CancellationToken token)
    {
        Task wait;
        lock (_lock)
        {
            if (!IsPaused) return Task.CompletedTask;
            wait = _open.Task;
        }

        return wait.WaitAsync(token);
    }
}
