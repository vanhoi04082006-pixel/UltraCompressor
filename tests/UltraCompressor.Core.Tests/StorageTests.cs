using UltraCompressor.Core.Models;
using UltraCompressor.Core.Scheduling;
using UltraCompressor.Core.Storage;
using Xunit;

namespace UltraCompressor.Core.Tests;

public class FileTransactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "uc_tx_" + Guid.NewGuid().ToString("N")[..8]);

    public FileTransactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Ghi_de_giu_lai_ban_goc_trong_bak()
    {
        var original = Write("a.txt", "ban goc");
        var temp = Write("tmp.txt", "ban nen");

        var error = FileTransaction.Commit(original, temp);

        Assert.Null(error);
        Assert.Equal("ban nen", File.ReadAllText(original));
        Assert.Equal("ban goc", File.ReadAllText(original + ".bak"));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Khong_ghi_de_ban_sao_luu_da_co()
    {
        // Bản gốc xoá .bak cũ rồi mới backup. Chạy lần hai là mất khả năng quay về bản
        // gốc thật — và người dùng có thể còn tệp .bak do chính họ tạo.
        var original = Write("a.txt", "ban goc that");
        File.WriteAllText(original + ".bak", "ban sao luu da co");

        var temp = Write("tmp.txt", "ban nen");
        FileTransaction.Commit(original, temp);

        // Bản sao lưu có sẵn phải được giữ nguyên, không bị ghi đè bởi bản gốc lần này.
        Assert.Equal("ban sao luu da co", File.ReadAllText(original + ".bak"));
        Assert.Equal("ban nen", File.ReadAllText(original));
    }

    [Fact]
    public void Tep_result_rong_thi_bao_loi_va_giu_nguyen_tep_goc()
    {
        var original = Write("a.txt", "ban goc");
        var temp = Write("tmp.txt", "");

        var error = FileTransaction.Commit(original, temp);

        Assert.NotNull(error);
        Assert.Equal("ban goc", File.ReadAllText(original));
    }

    [Fact]
    public void Tep_result_khong_ton_tai_thi_bao_loi()
    {
        var original = Write("a.txt", "ban goc");
        Assert.NotNull(FileTransaction.Commit(original, Path.Combine(_dir, "khong-co.txt")));
        Assert.Equal("ban goc", File.ReadAllText(original));
    }

    [Fact]
    public void Khoi_phuc_tra_lai_nguyen_ban_goc()
    {
        var original = Write("a.txt", "ban goc");
        var temp = Write("tmp.txt", "ban nen");
        FileTransaction.Commit(original, temp);

        var error = FileTransaction.Restore(original);

        Assert.Null(error);
        Assert.Equal("ban goc", File.ReadAllText(original));
        Assert.False(File.Exists(original + ".bak"));
    }

    [Fact]
    public void Khoi_phuc_khong_co_ban_sao_luu_thi_bao_loi()
    {
        var original = Write("a.txt", "ban goc");
        Assert.NotNull(FileTransaction.Restore(original));
        Assert.Equal("ban goc", File.ReadAllText(original));
    }

    [Fact]
    public void Xuat_ra_duong_dan_khac_va_giu_nguyen_tep_goc()
    {
        var original = Write("a.txt", "ban goc");
        var temp = Write("tmp.txt", "ban nen");
        var destination = Path.Combine(_dir, "ket-qua", "sub", "a.txt");

        var error = FileTransaction.Export(temp, destination);

        Assert.Null(error);
        Assert.Equal("ban goc", File.ReadAllText(original));
        Assert.Equal("ban nen", File.ReadAllText(destination));
        Assert.False(File.Exists(original + ".bak"));
    }

    [Fact]
    public void Xoa_ban_sao_luu()
    {
        var original = Write("a.txt", "ban goc");
        File.WriteAllText(original + ".bak", "ban goc");

        Assert.Null(FileTransaction.DiscardBackup(original));
        Assert.False(File.Exists(original + ".bak"));

        // Xoá lần hai không phải lỗi.
        Assert.Null(FileTransaction.DiscardBackup(original));
    }
}

public class FolderScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "uc_scan_" + Guid.NewGuid().ToString("N")[..8]);

    public FolderScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private void Touch(string relative, int size = 100)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    [Fact]
    public void Quet_duoc_thu_muc_con_va_danh_dau_dinh_dang()
    {
        Touch("a.mp4");
        Touch("b.jpg");
        Touch("sub/c.mp3");
        Touch("sub/deep/d.gif");
        Touch("ghi-chu.txt");
        Touch("cu.bak");

        var result = new FolderScanner(new AppConfig { IncludeSubfolders = true })
            .Scan(_dir, CompressionLevel.Balanced, true, null);

        Assert.Empty(result.Errors);
        Assert.Equal(6, result.Job.Items.Count);

        var byName = result.Job.Items.ToDictionary(i => Path.GetFileName(i.FilePath), i => i);

        Assert.Equal(MediaKind.Video, byName["a.mp4"].Kind);
        Assert.Equal(MediaKind.Image, byName["b.jpg"].Kind);
        Assert.Equal(MediaKind.Audio, byName["c.mp3"].Kind);
        Assert.Equal(MediaKind.Gif, byName["d.gif"].Kind);
        Assert.Equal(SkipReason.UnsupportedFormat, byName["ghi-chu.txt"].Skip);
    }

    [Fact]
    public void Khong_quet_thu_muc_con_khi_tat()
    {
        Touch("a.mp4");
        Touch("sub/b.mp4");

        var result = new FolderScanner(new AppConfig { IncludeSubfolders = false })
            .Scan(_dir, CompressionLevel.Balanced, true, null);

        Assert.Single(result.Job.Items);
    }

    [Fact]
    public void Quet_duoc_t_le()
    {
        Touch("a.mp4");

        var result = new FolderScanner(new AppConfig())
            .ScanFile(Path.Combine(_dir, "a.mp4"), CompressionLevel.Strong, true, null);

        Assert.Empty(result.Errors);
        var item = Assert.Single(result.Job.Items);

        Assert.Equal(MediaKind.Video, item.Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "a.mp4")), item.FilePath);
        Assert.False(item.IsComplete);

        // Job tệp lẻ đặt FolderPath là thư mục chứa tệp: cần cho đường dẫn tương đối khi
        // xuất kết quả và cho tệp .bak, cả hai đều phải khớp với tệp gốc.
        Assert.True(result.Job.IsFileJob);
        Assert.Equal(Path.GetFullPath(_dir), result.Job.FolderPath);
        Assert.Equal("a.mp4", result.Job.DisplayName);
    }

    [Fact]
    public void Tep_le_khong_hop_tri_roi_van_hien_voi_ly_do()
    {
        // Tệp lẻ sai định dạng phải hiện trong bảng kèm lý do, giống hệt quét thư mục —
        // không được im lặng biến mất, người dùng không hiểu vì sao không thấy gì.
        Touch("ghi-chu.txt");

        var result = new FolderScanner(new AppConfig())
            .ScanFile(Path.Combine(_dir, "ghi-chu.txt"), CompressionLevel.Balanced, true, null);

        var item = Assert.Single(result.Job.Items);
        Assert.Equal(SkipReason.UnsupportedFormat, item.Skip);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Tep_le_bi_bo_loc_va_khong_du_kich_thuoc()
    {
        Touch("a.mp4", 10);
        Touch("a.mp4.bak", 10);
        var scanner = new FolderScanner(new AppConfig { MinFileSizeBytes = 1024 });

        var filtered = scanner.ScanFile(Path.Combine(_dir, "a.mp4.bak"), CompressionLevel.Balanced, true, null);
        Assert.Equal(SkipReason.ExcludedByFilter, Assert.Single(filtered.Job.Items).Skip);

        var tooSmall = scanner.ScanFile(Path.Combine(_dir, "a.mp4"), CompressionLevel.Balanced, true, null);
        Assert.Equal(SkipReason.ExcludedByFilter, Assert.Single(tooSmall.Job.Items).Skip);
    }

    [Fact]
    public void Tep_le_khong_ton_tai_thi_bao_loi()
    {
        var result = new FolderScanner(new AppConfig())
            .ScanFile(Path.Combine(_dir, "khong-co.mp4"), CompressionLevel.Balanced, true, null);

        Assert.Empty(result.Job.Items);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Job_thu_muc_hien_ten_thu_muc_khong_phai_ten_tep()
    {
        var job = new Job { FolderPath = _dir, IsFileJob = true, SingleFilePath = Path.Combine(_dir, "phim.mp4") };

        Assert.Equal("phim.mp4", job.DisplayName);

        job.IsFileJob = false;
        job.SingleFilePath = null;
        Assert.Equal(new DirectoryInfo(_dir).Name, job.DisplayName);
    }


    [Fact]
    public void Tep_bak_va_tam_bi_lo_ngay_tu_dau()
    {
        // Bản gốc không lọc gì: chạy lần hai sẽ quét lại chính tệp .bak mà nó vừa tạo
        // rồi nén tiếp vào tệp .bak mới. Đây là lý do phải có bộ lọc.
        Touch("a.mp4");
        Touch("a.mp4.bak");
        Touch("ghi-chu.tmp");
        Touch("Thumbs.db");

        var result = new FolderScanner(new AppConfig())
            .Scan(_dir, CompressionLevel.Balanced, true, null);

        var skipped = result.Job.Items.Where(i => i.IsComplete).ToList();
        Assert.Equal(3, skipped.Count);
        Assert.All(skipped, i => Assert.Equal(SkipReason.ExcludedByFilter, i.Skip));
    }

    [Fact]
    public void Bo_loc_ho_tro_khoang_trang_va_dau_hoi_thuong()
    {
        Touch("video 01.mp4");
        Touch("VIDEO 02.MP4");
        Touch("khac.mp3");

        var result = new FolderScanner(new AppConfig { ExcludePatterns = ["video *.mp4"] })
            .Scan(_dir, CompressionLevel.Balanced, true, null);

        var excluded = result.Job.Items
            .Where(i => i.Skip == SkipReason.ExcludedByFilter)
            .Select(i => i.FileName)
            .ToList();

        Assert.Equal(2, excluded.Count);
        Assert.Contains("VIDEO 02.MP4", excluded);
    }

    [Fact]
    public void Bo_qua_tep_nho_hon_nguong()
    {
        Touch("nho.jpg", 10);
        Touch("lon.jpg", 5 * 1024 * 1024);

        var result = new FolderScanner(new AppConfig { MinFileSizeBytes = 1024 * 1024 })
            .Scan(_dir, CompressionLevel.Balanced, true, null);

        var small = result.Job.Items.Single(i => i.FileName == "nho.jpg");
        Assert.True(small.IsComplete);
        Assert.Equal(SkipReason.ExcludedByFilter, small.Skip);
        Assert.False(result.Job.Items.Single(i => i.FileName == "lon.jpg").IsComplete);
    }

    [Fact]
    public void Thu_muc_khong_co_tep_phu_hop_thi_bao_ro()
    {
        Touch("a.txt");

        var result = new FolderScanner(new AppConfig()).Scan(_dir, CompressionLevel.Balanced, true, null);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Thu_muc_khong_ton_tai_thi_bao_loi()
    {
        var result = new FolderScanner(new AppConfig())
            .Scan(Path.Combine(_dir, "khong-co"), CompressionLevel.Balanced, true, null);

        Assert.NotEmpty(result.Errors);
    }
}

public class FolderScannerWildcardTests
{
    [Theory]
    [InlineData("a.mp4", "*.mp4", true)]
    [InlineData("A.MP4", "*.mp4", true)]
    [InlineData("a.mp4", "*.MP4", true)]
    [InlineData("a.bak", "*.bak", true)]
    [InlineData("a.mp4.bak", "*.bak", true)]
    [InlineData("a.txt", "*.mp4", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("ac", "a?c", false)]
    [InlineData("abc", "a*c", true)]
    [InlineData("ac", "a*c", true)]
    [InlineData("anything", "*", true)]
    [InlineData("Thumbs.db", "thumbs.db", true)]
    public void Khop_mau_theo_ten(string name, string pattern, bool expected)
        => Assert.Equal(expected, FolderScanner.MatchesWildcard(name, pattern));
}

public class JobItemTests
{
    [Fact]
    public void Tiet_kiem_chi_tinh_khi_nen_thanh_cong()
    {
        var skipped = new JobItem { OldSize = 100, NewSize = 90, IsComplete = true, Skip = SkipReason.NoSizeGain };
        Assert.Equal(0, skipped.SavedBytes);
        Assert.False(skipped.Succeeded);

        var done = new JobItem { OldSize = 100, NewSize = 60, IsComplete = true, IsApplied = true };
        Assert.Equal(40, done.SavedBytes);
        Assert.Equal(40.0, done.SavedPercent, 1);

        // Chế độ thử: đã đo được nhưng chưa ghi vào tệp gốc — vẫn phải hiện mức tiết kiệm.
        var predicted = new JobItem { OldSize = 100, NewSize = 60, IsComplete = true, IsPredicted = true };
        Assert.Equal(40, predicted.SavedBytes);
    }
}
