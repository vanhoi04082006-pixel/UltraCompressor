using UltraCompressor.Core.Models;
using UltraCompressor.Core.Scheduling;
using Xunit;

namespace UltraCompressor.Core.Tests;

public class EtaEstimatorTests
{
    [Fact]
    public void Chua_du_du_lieu_thi_khong_uoc_duoc()
    {
        var eta = new EtaEstimator();
        Assert.Equal(-1, eta.Estimate(1000));
    }

    [Fact]
    public void Khong_con_gi_de_xu_li_thi_tra_0()
    {
        var eta = new EtaEstimator();
        eta.AddProgress(1000, 0);
        Assert.Equal(0, eta.Estimate(0));
    }

    [Fact]
    public void Uoc_theo_toc_do_quan_sat()
    {
        var eta = new EtaEstimator();
        eta.AddProgress(0, 0);
        eta.AddProgress(1000, 10);   // 100 byte/giây
        eta.AddProgress(2000, 20);

        var seconds = eta.Estimate(1000);

        Assert.True(seconds > 0);
        Assert.Equal(10, seconds, 0);
    }

    [Fact]
    public void Toc_do_bang_zero_khi_du_lieu_qua_ngan()
    {
        var eta = new EtaEstimator();
        eta.AddProgress(0, 0);
        Assert.Equal(0, eta.BytesPerSecond());
    }

    [Fact]
    public void Reset_xoa_sach_so_do()
    {
        var eta = new EtaEstimator();
        eta.AddProgress(1000, 5);
        eta.Reset();

        Assert.Equal(-1, eta.Estimate(1000));
        Assert.Equal(0, eta.BytesPerSecond());
    }

    [Fact]
    public void Hai_moc_qua_gan_nhau_thi_chua_uoc_duoc()
    {
        // Hai lần ghi cách nhau chưa tới 1 giây thì tốc độ tính ra vô nghĩa, nên ước lượng
        // phải từ chối trả con số sai thay vì đoán bừa.
        var eta = new EtaEstimator();
        eta.AddProgress(500, 10.0);
        eta.AddProgress(1000, 10.1);

        Assert.Equal(-1, eta.Estimate(1000));
    }

    [Fact]
    public void Moc_qua_xa_hon_cua_so_thi_ra_toc_do_dung()
    {
        // Bản gốc lấy "đã xử lý" theo số byte chỉ cộng dồn *sau* khi một tệp xong, nên tốc
        // độ luôn bị trễ và ETA lúc đầu vô nghĩa. Ước lượng ở đây dùng mốc thời gian thật.
        var eta = new EtaEstimator();
        eta.AddProgress(0, 0);
        eta.AddProgress(5000, 5);
        eta.AddProgress(10000, 10);

        Assert.Equal(1000, eta.BytesPerSecond(), 0);
        Assert.Equal(5, eta.Estimate(5000), 1);
    }
}

public class PauseGateTests
{
    [Fact]
    public async Task Chay_binh_thuong_thi_khong_cho()
    {
        var gate = new PauseGate();
        await gate.WaitAsync(CancellationToken.None);
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task Tam_dung_thi_lenh_cho_bi_chan()
    {
        // Bug B15/B16 của bản gốc: vòng chờ dạng while (paused) await Task.Delay(500)
        // không nhìn thấy CancellationToken, nên hủy job lúc đang tạm dừng sẽ treo vô hạn.
        // Ở đây lệnh chờ phải bị hủy được.
        var gate = new PauseGate();
        gate.Pause();
        Assert.True(gate.IsPaused);

        using var cts = new CancellationTokenSource();
        var waiting = gate.WaitAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Tiep_tuc_thi_tha_cho_va_lenh_cho_thong_thoat()
    {
        var gate = new PauseGate();
        gate.Pause();

        var waiting = gate.WaitAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        gate.Resume();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Chi_dung_mot_lan()
    {
        var gate = new PauseGate();
        gate.Pause();
        gate.Pause();

        gate.Resume();
        gate.Resume();

        // Vẫn phải đi qua được: cổng chỉ có một chốt.
        await gate.WaitAsync(CancellationToken.None);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cho_duoc_nhieu_lan_lien_tiep_khong_treo()
    {
        // Bản đầu tiên lấy một permit của SemaphoreSlim ở WaitAsync rồi gọi Resume ở
        // cuối vòng lặp để "trả lại". Nhưng Resume chỉ trả khi cổng đang tạm dừng, nên
        // tệp đầu tiên xong là tệp thứ hai treo vĩnh viễn — job nhiều tệp không bao giờ
        // chạy nổi tới cuối. Chờ phải không tiêu tốn khoá nào.
        var gate = new PauseGate();

        for (var i = 0; i < 5; i++)
        {
            await gate.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Cho_xong_roi_tam_dung_van_chan_duoc()
    {
        // Sau khi đã đi qua cổng, bấm Tạm dừng phải có tác dụng với lượt chờ kế tiếp.
        var gate = new PauseGate();
        await gate.WaitAsync(CancellationToken.None);

        gate.Pause();

        using var cts = new CancellationTokenSource();
        var waiting = gate.WaitAsync(cts.Token);
        Assert.False(waiting.IsCompleted);

        gate.Resume();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Tam_dung_khong_chan_luong_goi()
    {
        // Pause() trước đây gọi _gate.Wait() nên khi có tệp đang chạy, lệnh Tạm dừng bị
        // treo tới khi tệp đó xong — tức là giao diện đứng hình. Pause phải trả về ngay.
        var gate = new PauseGate();
        var holder = gate.WaitAsync(CancellationToken.None);
        await holder;

        var pause = Task.Run(() => gate.Pause());
        await pause.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(gate.IsPaused);
    }
}

public class ConcurrencyTests
{
    [Fact]
    public void Khong_dat_thi_tu_dong_theo_so_nhan()
    {
        var config = new AppConfig { MaxConcurrent = 0, ConcurrencyScale = 0.5 };
        var value = CompressionEngine.ResolveConcurrency(config);

        Assert.InRange(value, 2, 8);
        Assert.Equal(
            Math.Clamp((int)Math.Round(Environment.ProcessorCount * 0.5), 2, 8),
            value);
    }

    [Fact]
    public void Dat_roi_thi_dung_so_da_chon()
    {
        Assert.Equal(3, CompressionEngine.ResolveConcurrency(new AppConfig { MaxConcurrent = 3 }));
    }

    [Fact]
    public void Tieu_chi_he_so_duoc_bi_gioi_han()
    {
        // Người dùng nhập điên rồi không được làm treo máy bằng 99999 luồng nén.
        var value = CompressionEngine.ResolveConcurrency(new AppConfig { MaxConcurrent = 0, ConcurrencyScale = 999 });
        Assert.InRange(value, 2, 8);
    }
}
