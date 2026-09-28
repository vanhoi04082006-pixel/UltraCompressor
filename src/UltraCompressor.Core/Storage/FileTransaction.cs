namespace UltraCompressor.Core.Storage;

/// <summary>
/// Thay thế tệp gốc bằng kết quả nén, theo cách không bao giờ để người dùng mất dữ liệu.
///
/// Bản gốc làm <c>File.Move(goc, .bak)</c> rồi <c>File.Move(tmp, goc)</c>. Nếu lệnh thứ hai
/// hỏng thì bản gốc nằm lại trong <c>.bak</c> và tệp chính biến mất (bug B6). Ở đây:
///
///   1. Sao chép (không di chuyển) bản gốc sang <c>.bak</c> — tệp gốc vẫn nguyên tại chỗ.
///   2. <c>File.Move(temp, goc, overwrite: true)</c> — thay thế nguyên tử trên cùng ổ đĩa.
///   3. Nếu bước 2 hỏng: bản gốc vẫn còn nguyên, chỉ cần dọn tệp tạm.
///
/// Ngoài ra không bao giờ ghi đè một <c>.bak</c> đã có (bug B7): bản <c>.bak</c> đầu tiên
/// chính là bản gốc nguyên vẹn, ghi đè nó đồng nghĩa mất khả năng hoàn tác về bản thật.
/// </summary>
public static class FileTransaction
{
    public static readonly string BackupSuffix = ".bak";

    public static string BackupPathFor(string filePath) => filePath + BackupSuffix;

    /// <summary>Ghi kết quả nén đè lên tệp gốc. Trả về null nếu thành công, lý do nếu thất bại.</summary>
    public static string? Commit(string originalPath, string tempPath)
    {
        if (!File.Exists(tempPath)) return "Tệp kết quả không tồn tại.";

        try
        {
            var newInfo = new FileInfo(tempPath);
            if (newInfo.Length == 0) return "Tệp kết quả rỗng.";

            var backup = BackupPathFor(originalPath);

            // Bước 1: bảo toàn bản gốc. Sao chép chứ không di chuyển.
            if (!File.Exists(backup))
            {
                File.Copy(originalPath, backup, overwrite: false);
            }

            // Bước 2: thay thế nguyên tử.
            File.Move(tempPath, originalPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            // Tệp gốc vẫn còn nguyên vì bước 2 là thao tác nguyên tử.
            TryDelete(tempPath);
            return $"Không ghi được tệp: {ex.Message}";
        }
    }

    /// <summary>Đưa kết quả nén sang một đường dẫn khác, không đụng tới tệp gốc (chế độ xuất / chạy thử).</summary>
    public static string? Export(string tempPath, string destinationPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Move(tempPath, destinationPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Không xuất được sang '{destinationPath}': {ex.Message}";
        }
    }

    /// <summary>Khôi phục bản gốc từ bản sao lưu. Trả về null nếu thành công.</summary>
    public static string? Restore(string originalPath)
    {
        var backup = BackupPathFor(originalPath);
        if (!File.Exists(backup)) return "Không có bản sao lưu.";

        try
        {
            File.Move(backup, originalPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Không khôi phục được: {ex.Message}";
        }
    }

    /// <summary>Xoá bản sao lưu — dùng sau khi người dùng duyệt kết quả.</summary>
    public static string? DiscardBackup(string originalPath)
    {
        var backup = BackupPathFor(originalPath);
        if (!File.Exists(backup)) return null;

        try
        {
            File.Delete(backup);
            return null;
        }
        catch (Exception ex)
        {
            return $"Không xoá được bản sao lưu (tệp đang mở?): {ex.Message}";
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Tệp tạm bị khoá — không ảnh hưởng dữ liệu người dùng.
        }
    }
}
