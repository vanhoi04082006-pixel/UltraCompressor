namespace UltraCompressor.Core.Storage;

/// <summary>
/// Quản lý tệp tạm.
///
/// Bản gốc ghi tệp tạm <c>tmp_&lt;guid&gt;&lt;ext&gt;</c> nằm ngay trong thư mục nguồn. Điều đó
/// giữ được ổ đĩa đích nhanh nhưng làm thư mục nguồn bị lấm nhầm, và tệp tạm sót lại khi
/// ứng dụng bị tắt đột ngột. Ở đây dùng thư mục tạm riêng theo từng job, tự dọn khi xong,
/// và quét lại các tệp tạm cũ khi khởi động.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private readonly string _root;
    private readonly List<string> _created = [];
    private readonly Lock _gate = new();

    public TempWorkspace(string? rootOverride = null)
    {
        _root = rootOverride ?? Path.Combine(Path.GetTempPath(), "UltraCompressor", "work");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Đường dẫn tệp tạm giữ đúng phần mở rộng của nguồn (một số bộ lọc ffmpeg dựa vào nó).</summary>
    public string CreatePath(string sourcePath, string? suffix = null)
    {
        var ext = Path.GetExtension(sourcePath);
        var name = $"{Guid.NewGuid():N}{suffix}{ext}";
        var path = Path.Combine(_root, name);
        lock (_gate) _created.Add(path);
        return path;
    }

    public void Release(string path)
    {
        lock (_gate) _created.Remove(path);
        FileTransaction.TryDelete(path);
    }

    /// <summary>
    /// Dọn các tệp tạm còn sót từ lần chạy trước bị gián đoạn.
    ///
    /// Gọi lúc khởi động thì truyền <see cref="TimeSpan.Zero"/> để xoá hết: lúc đó không có
    /// công việc nào chạy nên thứ gì còn nằm trong thư mục tạm chắc chắn là rác. Trước đây
    /// lọc theo tuổi 6 giờ, nên một lần đóng cưỡng bức (task manager, mất điện) để lại tệp
    /// tạm nặng hàng trăm MB trong suốt 6 tiếng đó.
    /// </summary>
    public int RemoveStale(TimeSpan olderThan)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - olderThan;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // Đang bị khoá — thử lại lần sau.
                }
            }
        }
        catch
        {
            // Không quét được thư mục tạm.
        }
        return removed;
    }

    public void Dispose()
    {
        List<string> paths;
        lock (_gate)
        {
            paths = [.. _created];
            _created.Clear();
        }

        foreach (var path in paths)
        {
            FileTransaction.TryDelete(path);
        }
    }
}
