using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Storage;

/// <summary>Hoàn tác / duyệt / dọn bản sao lưu trên cả job.</summary>
public static class UndoService
{
    public sealed record BatchResult(int Restored, int Failed, IReadOnlyList<string> Errors);

    /// <summary>Khôi phục mọi tệp đã bị thay thế trong job về bản gốc.</summary>
    public static BatchResult RestoreAll(Job job)
    {
        var restored = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var item in job.Items.Where(i => i.IsApplied))
        {
            // Không dựa vào BackupPath đã lưu: job nạp từ phiên cũ có thể không có trường
            // này. Dựng lại từ đường dẫn gốc cho chắc, nếu không sẽ bỏ sót tệp cần hoàn tác.
            item.BackupPath ??= FileTransaction.BackupPathFor(item.FilePath);

            if (!File.Exists(item.BackupPath))
            {
                // Thiếu bản sao lưu phải được báo. Bỏ qua im lặng sẽ khiến người dùng
                // tưởng đã khôi phục xong trong khi tệp vẫn là bản đã nén.
                failed++;
                errors.Add($"{item.FileName}: không tìm thấy bản sao lưu để khôi phục.");
                continue;
            }

            var error = FileTransaction.Restore(item.FilePath);
            if (error is null)
            {
                restored++;
                item.IsApplied = false;
                item.NewSize = item.OldSize;
                item.Skip = SkipReason.None;
                item.QualityScore = null;
            }
            else
            {
                errors.Add($"{item.FileName}: {error}");
            }
        }

        job.Committed = false;
        job.Status = JobStatus.Waiting;
        job.CompletedAt = null;
        return new BatchResult(restored, errors.Count, errors);
    }

    /// <summary>Khôi phục một tệp đơn lẻ.</summary>
    public static string? RestoreOne(JobItem item)
    {
        var error = FileTransaction.Restore(item.FilePath);
        if (error is null)
        {
            item.IsApplied = false;
            item.NewSize = item.OldSize;
        }
        return error;
    }

    /// <summary>
    /// Duyệt kết quả: xoá bản sao lưu vì người dùng đã chấp nhận.
    /// Trả về các lỗi để báo — thường là tệp đang được mở bởi trình phát.
    /// </summary>
    public static BatchResult DiscardBackups(Job job, int keepDays)
    {
        var discarded = 0;
        var errors = new List<string>();

        foreach (var item in job.Items)
        {
            if (!item.IsApplied) continue;

            var backup = item.BackupPath ?? FileTransaction.BackupPathFor(item.FilePath);
            if (!File.Exists(backup)) continue;

            if (keepDays > 0)
            {
                // Giữ thêm keepDays ngày rồi dọn sau — để người dùng kịp hối tiếc.
                try
                {
                    var age = DateTime.Now - File.GetLastWriteTime(backup);
                    if (age.TotalDays < keepDays) continue;
                }
                catch
                {
                    // Không đọc được ngày sửa — coi như còn giữ.
                    continue;
                }
            }

            var error = FileTransaction.DiscardBackup(item.FilePath);
            if (error is null) discarded++;
            else errors.Add($"{item.FileName}: {error}");
        }

        if (errors.Count == 0 && keepDays == 0)
        {
            job.Status = JobStatus.Committed;
            job.Committed = true;
        }

        return new BatchResult(discarded, errors.Count, errors);
    }

    /// <summary>Xoá các tệp <c>.bak</c> quá hạn rải rác trong các thư mục đã từng nén.</summary>
    public static int PurgeExpiredBackups(IEnumerable<string> directories, int keepDays)
    {
        if (keepDays <= 0) return 0;

        var cutoff = DateTime.Now.AddDays(-keepDays);
        var removed = 0;

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*" + FileTransaction.BackupSuffix, SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) > cutoff) continue;
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // Đang bị khoá.
                    }
                }
            }
            catch
            {
                // Không quét được thư mục.
            }
        }

        return removed;
    }

    /// <summary>Tệp nào trong job còn giữ bản sao lưu (dùng để cảnh báo trước khi dọn).</summary>
    public static IReadOnlyList<JobItem> PendingBackups(Job job) =>
        [.. job.Items.Where(i =>
            i.IsApplied &&
            File.Exists(i.BackupPath ?? FileTransaction.BackupPathFor(i.FilePath)))];
}
