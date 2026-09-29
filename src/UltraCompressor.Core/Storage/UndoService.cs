using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Storage;

/// <summary>Hoàn tác / duyệt / dọn bản sao lưu trên cả job.</summary>
public static class UndoService
{
    /// <summary>
    /// Kết quả hoàn tác: đã khôi phục bao nhiêu bản gốc, hỏng bao nhiêu.
    /// </summary>
    public sealed record BatchResult(int Restored, int Failed, IReadOnlyList<string> Errors);

    /// <summary>
    /// Kết quả duyệt. Tách khỏi <see cref="BatchResult"/> vì hai việc đếm khác nhau:
    /// hoàn tác thì <b>khôi phục</b> bản gốc, duyệt thì <b>xoá</b> bản gốc.
    ///
    /// <para><see cref="Applied"/> là số tệp đã đưa bản nén vào chỗ;
    /// <see cref="Released"/> là số tệp đã xoá bản gốc, tức là số chỗ đã giải phóng. Hai số
    /// này lệch nhau khi tệp đang được trình phát giữ nên xoá không được — đó là lý do
    /// phải báo riêng thay vì gộp vào "lỗi".</para>
    /// </summary>
    public sealed record CommitResult(int Applied, int Released, int Failed, IReadOnlyList<string> Errors);

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
    /// Duyệt xong: xoá bản gốc vì người dùng đã xem kết quả và chấp nhận.
    ///
    /// <para>Trước đây có tham số <c>keepDays</c> và mặc định cấu hình là 30, nên nhánh
    /// "còn trẻ hơn 30 ngày thì giữ" luôn đúng và hàm này **không bao giờ xoá gì**. Bấm
    /// Duyệt xong bản gốc vẫn nằm đó chiếm dung lượng, đúng như phản hồi của người dùng.
    /// Nay Duyệt là Duyệt: xoá hẳn, giải phóng chỗ.</para>
    ///
    /// <para>Trả về các lỗi để báo — thường là tệp đang được trình phát giữ. Lỗi xoá KHÔNG
    /// làm hỏng việc duyệt: bản đã nén vẫn ở chỗ, và bản gốc còn nằm trong
    /// <c>.bak</c> nên vẫn hoàn tác được.</para>
    /// </summary>
    public static CommitResult ReleaseBackups(Job job)
    {
        var released = 0;
        var errors = new List<string>();
        var applied = 0;

        foreach (var item in job.Items)
        {
            if (!item.IsApplied) continue;

            applied++;

            var backup = item.BackupPath ?? FileTransaction.BackupPathFor(item.FilePath);
            if (!File.Exists(backup)) continue;

            var error = FileTransaction.DiscardBackup(item.FilePath);
            if (error is null)
            {
                released++;
                // Không còn lối quay lui cho tệp này. Ghi lại đúng sự thật thay vì để
                // trỏ vào một .bak đã không tồn tại.
                item.BackupPath = null;
            }
            else
            {
                errors.Add($"{item.FileName}: {error}");
            }
        }

        // Đã duyệt xong thì job phải sang trạng thái "đã ghi", bất kể có xoá được .bak hay
        // không. Trước đây điều kiện là `errors.Count == 0 && keepDays == 0`; với
        // keepDays mặc định 30 thì nhánh này không bao giờ chạy, nên bấm Duyệt xong job
        // vẫn hiện "Chờ duyệt" và đếm 0 tệp — người dùng tưởng thao tác chưa xong và bấm
        // lại nhiều lần.
        job.Status = JobStatus.Committed;
        job.Committed = true;

        return new CommitResult(applied, released, errors.Count, errors);
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
