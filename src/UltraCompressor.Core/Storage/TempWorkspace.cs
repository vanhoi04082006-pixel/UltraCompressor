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

    /// <summary>
    /// Đường dẫn tệp tạm cho một tệp ĐẦU RA.
    /// </summary>
    /// <remarks>
    /// <para><b>Phần mở rộng phải là của tệp đầu ra, không phải của tệp nguồn.</b> Bản trước
    /// nhận <c>sourcePath</c> và sao chép phần mở rộng của nó — tức để nguồn quyết định
    /// container mà ffmpeg sẽ ghi ra. Đo được: cùng một lệnh, chỉ khác phần mở rộng đầu ra,
    /// thì <c>.mkv</c> ra Matroska và <c>.ts</c> ra MPEG-TS, còn <c>-movflags +faststart</c>
    /// bị bỏ qua lặng lẽ. Nguyên tắc và số đo: <see cref="OutputContainer"/>.</para>
    ///
    /// <para>Hàm <b>không</b> chấp nhận phần mở rộng không có dấu chấm. Đó là lỗi gọi sai ở
    /// chỗ chọn container, và im lặng chấp nhận nó thì lỗi đó chỉ lộ ra ở tệp đầu ra hỏng
    /// hàng giờ sau — lúc đó rất khó truy ngược.</para>
    /// </remarks>
    /// <param name="outputExtension">Phần mở rộng đầu ra, lấy từ <see cref="OutputContainer"/>.</param>
    /// <param name="suffix">Chèn thêm trước phần mở rộng, để phân biệt các giai đoạn.</param>
    public string CreatePath(string outputExtension, string? suffix = null)
    {
        if (string.IsNullOrEmpty(outputExtension)
            || outputExtension[0] != '.'
            || outputExtension.Length < 2)
        {
            throw new ArgumentException(
                $"Phần mở rộng đầu ra phải có dấu chấm, ví dụ \"{OutputContainer.Mp4}\"; "
                + $"nhận được \"{outputExtension}\". Lấy từ OutputContainer, đừng chép từ tệp nguồn.",
                nameof(outputExtension));
        }

        var name = $"{Guid.NewGuid():N}{suffix}{outputExtension}";
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
