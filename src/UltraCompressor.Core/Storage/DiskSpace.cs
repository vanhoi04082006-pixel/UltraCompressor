namespace UltraCompressor.Core.Storage;

/// <summary>Kiểm tra dung lượng trước khi ghi, để không nén nửa chừng rồi hết chỗ.</summary>
public static class DiskSpace
{
    /// <summary>Số byte trống trên ổ đang chứa đường dẫn.</summary>
    public static long AvailableBytesFor(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Có đủ chỗ để ghi tệp tạm không. Cần đủ cho tệp nguồn (để tạo bản sao) cộng thêm
    /// dung lượng dự kiến của kết quả. Dự phòng thêm 10% cho chỗ trống hệ thống.
    /// </summary>
    public static bool HasRoom(string path, long requiredBytes, out long available, out long needed)
    {
        available = AvailableBytesFor(path);

        // Dự phòng 10%, tối thiểu 256 MB.
        var margin = Math.Max(requiredBytes / 10, 256L * 1024 * 1024);
        needed = requiredBytes + margin;

        return available >= needed;
    }

    /// <summary>Kiểm tra cho cả job, dựa trên tổng dung lượng tệp gốc chưa xử lý.</summary>
    public static bool HasRoomForJob(string folderPath, IReadOnlyCollection<long> pendingSizes, out long available, out long needed)
    {
        long total = 0;
        foreach (var size in pendingSizes)
        {
            // Nén tệp cần: 1 bản sao lưu (bằng size gốc) + 1 tệp kết quả (tối đa bằng size gốc).
            total += size * 2;
        }

        return HasRoom(folderPath, total, out available, out needed);
    }

    public static string Describe(long available, long needed) =>
        $"Cần khoảng {Format.Size(needed)} dung lượng trống, hiện chỉ còn {Format.Size(available)}.";
}
