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

    /// <summary>
    /// Kết quả thay thế tệp gốc, tách bạch hai loại lỗi vì chúng khác nghĩa với người dùng.
    /// </summary>
    public sealed record SwapResult
    {
        /// <summary>
        /// Không thay thế được, và bản gốc còn nguyên. Duyệt chưa thành công.
        /// </summary>
        public string? Error { get; init; }

        /// <summary>
        /// Đã thay thế thành công, nhưng xoá bản gốc thất bại (thường là tệp đang được
        /// mở). Bản gốc còn nằm ở đường dẫn <c>.bak</c>, nên người dùng vẫn xem lại và
        /// hoàn tác được — chỉ là chưa giải phóng được dung lượng.
        /// </summary>
        public string? ReleaseError { get; init; }

        /// <summary>Bản gốc đã được xoá hẳn, dung lượng đã giải phóng.</summary>
        public bool Released => Error is null && ReleaseError is null;
    }

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

    /// <summary>
    /// Thay thế tệp gốc rồi **xoá bản gốc ngay**, không để lại <c>.bak</c> nào.
    ///
    /// <para>Dùng khi người dùng đã bấm "Duyệt": lúc đó họ đã xem kết quả và chấp nhận,
    /// nên giữ lại bản gốc chỉ tốn dung lượng mà không ai dùng tới. Đây là điểm người dùng
    /// yêu cầu rõ: duyệt xong là phải giải phóng chỗ.</para>
    ///
    /// <para><b>Không sao chép bản gốc.</b> Bản trước dùng <c>File.Copy</c> rồi giữ lại 30
    /// ngày, tức là tốn thêm một bản 526 MB cho tới khi bấm duyệt. Ở đây dùng
    /// <c>File.Move</c> — trên cùng ổ đĩa đó là đổi tên, tức thì và tốn 0 byte; chỉ khi
    /// khác ổ đĩa mới rơi về <c>File.Copy</c>. Bản gốc chỉ tồn tại trong khoảnh thời gian
    /// của hai thao tác, và bị xoá ngay khi thành công.</para>
    ///
    /// <para>Nếu thay thế hỏng, bản gốc được đưa về đúng chỗ cũ. Người dùng không mất
    /// gì cả khi thao tác hỏng giữa chừng.</para>
    /// </summary>
    public static SwapResult CommitAndRelease(string originalPath, string tempPath)
    {
        if (!File.Exists(tempPath)) return new SwapResult { Error = "Tệp kết quả không tồn tại." };

        if (new FileInfo(tempPath).Length == 0) return new SwapResult { Error = "Tệp kết quả rỗng." };

        var backup = BackupPathFor(originalPath);

        // Bản gốc đã có .bak từ trước? Không ghi đè: .bak cũ là lối quay lui duy nhất còn
        // lại (xem bug B7 trong Commit).
        if (File.Exists(backup))
        {
            return new SwapResult { Error = "Đã có bản sao lưu .bak từ trước, không ghi đè để giữ khả năng hoàn tác." };
        }

        var movedOriginal = false;
        try
        {
            File.Move(originalPath, backup);
            movedOriginal = true;
        }
        catch
        {
            // Khác ổ đĩa thì Move không được. Sao chép thay thế; bản gốc vẫn nằm nguyên
            // tại chỗ cho tới khi bước sau ghi đè.
            File.Copy(originalPath, backup, overwrite: false);
        }

        try
        {
            File.Move(tempPath, originalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryRestore(originalPath, backup, movedOriginal);
            TryDelete(tempPath);
            return new SwapResult { Error = $"Không ghi được tệp: {ex.Message}" };
        }

        // Đã thay thế xong. Bản gốc nằm ở `backup`; xoá nó đi để giải phóng dung lượng.
        var releaseError = DiscardBackup(originalPath);

        return new SwapResult { ReleaseError = releaseError };
    }

    /// <summary>Đưa bản gốc về đúng chỗ cũ sau khi thay thế hỏng.</summary>
    private static void TryRestore(string originalPath, string backup, bool originalWasMoved)
    {
        try
        {
            if (originalWasMoved) File.Move(backup, originalPath, overwrite: true);
            else TryDelete(backup);
        }
        catch
        {
            // Không khôi phục được: bản gốc vẫn còn ở đường dẫn .bak nên không mất dữ liệu.
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

    /// <summary>
    /// Xoá bản sao lưu. Đây là thao tác **duy nhất** xoá bản gốc, và nó chỉ chạy khi
    /// người dùng đã bấm "Duyệt" — tức là đã xem kết quả và chấp nhận mất bản gốc.
    ///
    /// <para>Trước đây hàm này bị chặn bởi <c>KeepBackupDays</c> mặc định 30 ngày, nên
    /// duyệt xong bản gốc vẫn còn nguyên và vẫn chiếm chỗ. Người dùng phải đợi 30 ngày
    /// mới được giải phóng dung lượng, hoặc bấm "Dọn bản sao lưu quá hạn" — trong khi
    /// ý của họ khi bấm Duyệt là kết thúc luôn.</para>
    /// </summary>
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
