using UltraCompressor.Core;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Storage;
using UltraCompressor.Core.Toolchain;
using Xunit;

namespace UltraCompressor.Core.Tests;

public class MediaClassifierTests
{
    [Theory]
    [InlineData("a.mp4", MediaKind.Video)]
    [InlineData("a.MKV", MediaKind.Video)]
    [InlineData("a.mov", MediaKind.Video)]
    [InlineData("a.jpg", MediaKind.Image)]
    [InlineData("a.JPEG", MediaKind.Image)]
    [InlineData("a.png", MediaKind.Image)]
    [InlineData("a.gif", MediaKind.Gif)]
    [InlineData("a.mp3", MediaKind.Audio)]
    [InlineData("a.m4a", MediaKind.Audio)]
    [InlineData("a.pdf", MediaKind.Pdf)]
    [InlineData("a.txt", MediaKind.Unknown)]
    [InlineData("Khong co duoi tep", MediaKind.Unknown)]
    [InlineData("a.wav", MediaKind.Unknown)]
    [InlineData("a.flac", MediaKind.Unknown)]
    public void Phan_loai_theo_duoi_tep(string path, MediaKind expected)
        => Assert.Equal(expected, MediaClassifier.Classify(path));

    [Fact]
    public void Wav_va_flac_bi_bo_co_co_ly_do()
    {
        // WAV/FLAC thường là bản lưu trữ, nén lại bằng bitrate cố định chỉ làm tệp to
        // thêm mà chất lượng không giảm. Cần muốn nén thì tự chọn định dạng khác.
        Assert.False(MediaClassifier.IsSupported("ban-lau.wav"));
        Assert.False(MediaClassifier.IsSupported("ban-lau.flac"));
    }

    [Fact]
    public void Co_duoi_tep_ho_tro_khong_rong()
        => Assert.NotEmpty(MediaClassifier.AllSupportedExtensions);
}

public class DiskSpaceTests
{
    [Fact]
    public void Doc_duoc_dung_luong_o_thu_muc_ton_tai()
    {
        var available = DiskSpace.AvailableBytesFor(Path.GetTempPath());
        Assert.True(available > 0);
    }

    [Fact]
    public void Duong_dan_khong_hop_le_thi_khong_lam_hong()
    {
        Assert.Equal(long.MaxValue, DiskSpace.AvailableBytesFor("\0:khong-hop-le\\"));
    }

    [Fact]
    public void Khong_du_dia_chi_thi_bao_thieu()
    {
        var ok = DiskSpace.HasRoom(Path.GetTempPath(), long.MaxValue / 2, out _, out var needed);

        Assert.False(ok);
        Assert.True(needed > 0);
    }

    [Fact]
    public void Can_tam_dung_lon_hon_yeu_cau()
    {
        // Cần chỗ cho bản sao lưu + kết quả + biên an toàn.
        DiskSpace.HasRoom(Path.GetTempPath(), 1000, out _, out var needed);
        Assert.True(needed > 1000);
    }
}

public class JsonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "uc_json_" + Guid.NewGuid().ToString("N")[..8]);

    public JsonStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Luu_roi_doc_lai_giu_nguyen_noi_dung()
    {
        var path = PathFor("a.json");
        var config = new AppConfig { Level = CompressionLevel.Strong, MinSavingPercent = 2.5, MaxConcurrent = 6 };

        await JsonStore.SaveAsync(path, config);
        var (value, error) = await JsonStore.TryLoadAsync<AppConfig>(path);

        Assert.Null(error);
        Assert.NotNull(value);
        Assert.Equal(CompressionLevel.Strong, value!.Level);
        Assert.Equal(2.5, value.MinSavingPercent);
        Assert.Equal(6, value.MaxConcurrent);
    }

    [Fact]
    public async Task Tep_khong_ton_tai_thi_tra_null_va_khong_bao_loi()
    {
        var (value, error) = await JsonStore.TryLoadAsync<AppConfig>(PathFor("khong-co.json"));

        Assert.Null(value);
        Assert.Null(error);
    }

    [Fact]
    public async Task Tep_hong_thi_bao_loi_thay_vi_nuot_im_lang()
    {
        // Bản gốc nuốt mọi lỗi trong catch { }: session hỏng thì người dùng mất sạch
        // danh sách mà không ai biết vì sao.
        var path = PathFor("hong.json");
        await File.WriteAllTextAsync(path, "{ khong phai json");

        var (value, error) = await JsonStore.TryLoadAsync<AppConfig>(path);

        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Ghi_tam_hon_va_thay_the_nguyen_tuyen_tinh()
    {
        // Không có tệp tạm sót lại sau khi ghi: nếu treo máy giữa lúc ghi, phiên cũ vẫn
        // còn nguyên thay vì bị băm nát.
        var path = PathFor("a.json");
        await JsonStore.SaveAsync(path, new AppConfig { MaxConcurrent = 1 });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Enum_duoc_ghi_ten_chu_khong_phai_so()
    {
        // Người dùng sửa config.json bằng tay phải đọc được.
        var path = PathFor("enum.json");
        await JsonStore.SaveAsync(path, new AppConfig { Level = CompressionLevel.Strong });

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Strong\"", text);
    }
}

public class ToolLocatorTests
{
    [Fact]
    public void Uu_tien_duong_dan_nguoi_dung_dan()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uc_tools_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "ffmpeg.exe");
        File.WriteAllText(exe, "x");

        try
        {
            var locator = new ToolLocator(dir, new ToolPaths { FFmpeg = exe });
            Assert.Equal(Path.GetFullPath(exe), locator.Locate(ToolKind.FFmpeg));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Duong_dan_chi_dinh_khong_ton_tai_thi_bo_qua()
    {
        var locator = new ToolLocator(AppContext.BaseDirectory, new ToolPaths
        {
            FFmpeg = @"C:\khong\co\ffmpeg.exe",
        });

        // Không ném lỗi, chỉ coi như chưa chỉ định — người dùng có thể xoá nhầm rồi
        // định cấu hình lại.
        Assert.NotEqual(@"C:\khong\co\ffmpeg.exe", locator.Locate(ToolKind.FFmpeg));
    }

    [Fact]
    public void Tim_that_ben_tep_thuc_thi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uc_tools2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gifsicle.exe"), "x");

        try
        {
            var locator = new ToolLocator(dir);
            Assert.NotNull(locator.Locate(ToolKind.Gifsicle));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}

public class ToolChainTests
{
    [Fact]
    public void Cong_cu_can_thiet_theo_loai_media()
    {
        Assert.Equal([ToolKind.FFmpeg], ToolChain.RequiredToolsFor([MediaKind.Image, MediaKind.Video]));
        Assert.Equal([ToolKind.FFmpeg, ToolKind.Gifsicle], ToolChain.RequiredToolsFor([MediaKind.Gif]));
        Assert.Equal([ToolKind.Ghostscript], ToolChain.RequiredToolsFor([MediaKind.Pdf]));

        // Chỉ toàn PDF thì không cần ffmpeg.
        Assert.DoesNotContain(ToolKind.FFmpeg, ToolChain.RequiredToolsFor([MediaKind.Pdf]));
    }

    [Fact]
    public void Khong_chan_job_khi_cong_cu_chenh_va_khong_can()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uc_tools3_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var locator = new ToolLocator(dir, new ToolPaths
            {
                FFmpeg = @"C:\khong\co\ffmpeg.exe",
                Ghostscript = @"C:\khong\co\gswin64c.exe",
            });

            var chain = new ToolChain(locator, new ToolHealthChecker(locator));

            // Không có ffmpeg thì chặn job ảnh...
            Assert.NotEmpty(chain.BlockingProblemsFor([MediaKind.Image]));

            // ...nhưng job toàn PDF chỉ cần Ghostscript.
            var pdfProblems = chain.BlockingProblemsFor([MediaKind.Pdf]);
            Assert.NotEmpty(pdfProblems);
            Assert.DoesNotContain(ToolKind.FFmpeg, pdfProblems.Select(r => r.Kind));

            // Công cụ chưa kiểm tra cũng phải chặn: không có bằng chứng nó chạy được.
            Assert.All(chain.BlockingProblemsFor([MediaKind.Video]), r => Assert.Equal(ToolKind.FFmpeg, r.Kind));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Khong_bao_chung_toan_bo_khi_mot_cong_cu_hong()
    {
        // Bản gốc chặn toàn bộ ứng dụng vì Ghostscript hỏng, dù job video không cần tới nó.
        var dir = Path.Combine(Path.GetTempPath(), "uc_tools4_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var locator = new ToolLocator(dir, new ToolPaths { Ghostscript = @"C:\khong\co\gs.exe" });
            var chain = new ToolChain(locator, new ToolHealthChecker(locator));

            // Chưa kiểm tra thì danh sách công cụ hỏng còn trống — tránh báo nhầm cho người dùng.
            Assert.Empty(chain.BrokenTools());

            // Còn job cần Ghostscript thì bị chặn ngay.
            Assert.Contains(chain.BlockingProblemsFor([MediaKind.Pdf]), r => r.Kind == ToolKind.Ghostscript);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}

public class UndoServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "uc_undo_" + Guid.NewGuid().ToString("N")[..8]);

    public UndoServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private JobItem AppliedItem(string name, string original, string compressed)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, compressed);
        File.WriteAllText(path + FileTransaction.BackupSuffix, original);
        return new JobItem
        {
            FilePath = path,
            OldSize = original.Length,
            NewSize = compressed.Length,
            IsComplete = true,
            IsApplied = true,
        };
    }

    [Fact]
    public void Hoan_tac_toan_bo_job()
    {
        var job = new Job
        {
            FolderPath = _dir,
            Items =
            [
                AppliedItem("a.bin", "ban goc A", "ban nen A"),
                AppliedItem("b.bin", "ban goc B", "ban nen B"),
            ],
        };

        var result = UndoService.RestoreAll(job);

        Assert.Equal(2, result.Restored);
        Assert.Equal(0, result.Failed);
        Assert.Equal("ban goc A", File.ReadAllText(Path.Combine(_dir, "a.bin")));
        Assert.Equal("ban goc B", File.ReadAllText(Path.Combine(_dir, "b.bin")));
        Assert.False(job.Committed);
    }

    [Fact]
    public void Hoan_tac_bo_qua_tep_da_luu_nen()
    {
        // Có 2 tệp đã ghi đè nhưng chỉ 1 tệp còn .bak — tệp còn lại phải được báo lỗi
        // chứ không âm thầm bỏ qua.
        var good = AppliedItem("a.bin", "goc A", "nen A");
        var bad = AppliedItem("b.bin", "goc B", "nen B");
        File.Delete(bad.FilePath + FileTransaction.BackupSuffix);

        var job = new Job { FolderPath = _dir, Items = [good, bad] };
        var result = UndoService.RestoreAll(job);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Duyet_xoa_bao_luu()
    {
        var job = new Job
        {
            FolderPath = _dir,
            Items = [AppliedItem("a.bin", "goc", "nen")],
        };

        var result = UndoService.DiscardBackups(job, keepDays: 0);

        Assert.Equal(1, result.Restored);
        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(Path.Combine(_dir, "a.bin.bak")));
        Assert.Equal(JobStatus.Committed, job.Status);
    }

    [Fact]
    public void Duyet_giu_luu_vai_ngay()
    {
        var item = AppliedItem("a.bin", "goc", "nen");
        var job = new Job { FolderPath = _dir, Items = [item] };

        var result = UndoService.DiscardBackups(job, keepDays: 30);

        Assert.Equal(0, result.Restored);
        Assert.True(File.Exists(Path.Combine(_dir, "a.bin.bak")));
    }

    [Fact]
    public void Don_bao_luu_qua_han()
    {
        var item = AppliedItem("a.bin", "goc", "nen");
        var backup = item.FilePath + FileTransaction.BackupSuffix;
        File.SetLastWriteTime(backup, DateTime.Now.AddDays(-90));

        var removed = UndoService.PurgeExpiredBackups([_dir], keepDays: 30);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Khong_don_gi_khi_giu_vinh_vien()
    {
        AppliedItem("a.bin", "goc", "nen");
        Assert.Equal(0, UndoService.PurgeExpiredBackups([_dir], keepDays: 0));
    }
}
