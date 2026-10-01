using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Quy tắc chép cấu hình: trường nào giao diện được gửi lên, và trường nào không.
///
/// <para><b>Lỗi đã mắc phải.</b> `EnableAdaptiveSearch` không có trong danh sách trường được
/// chép, mà giao diện lại không có ô bật cho nó — nên giá trị luôn là mặc định <c>false</c>. Người
/// dùng bật cờ bằng tay trong tệp cấu hình rồi bấm "Lưu" sẽ thấy cờ bị tắt, và vì thao tác lưu
/// ghi lại chính đối tượng đó, giá trị tắt còn nằm trong tệp. Không có dấu hiệu gì cho biết.</para>
///
/// <para>Hàm chép nằm ở <see cref="AppConfig"/> trong Core chứ không ở tầng WinForms, nên nó
/// kiểm thử được mà không cần đổi TFM của cả bộ kiểm thử — điều mà bản sửa trước đó không làm
/// được và phải ghi nợ.</para>
/// </remarks>
public class AppConfigCopyTests
{
    [Fact]
    public void Luu_cau_hinh_khong_duoc_tat_co_thu_nghiem_dang_bat()
    {
        var running = new AppConfig { EnableAdaptiveSearch = true };

        // Bản giao diện gửi lên không có trường này, nên giá trị của nó là mặc định (tắt).
        var fromUi = new AppConfig { Level = CompressionLevel.Strong };

        running.CopyRuntimeSettingsFrom(fromUi);

        Assert.True(running.EnableAdaptiveSearch);
        Assert.Equal(CompressionLevel.Strong, running.Level);
    }

    [Fact]
    public void Co_that_bat_thi_vao_cau_hinh_thi_van_giu_nguyen()
    {
        // Cùng lập luận: người dùng tắt cờ rồi lưu một thứ khác, cờ phải vẫn tắt.
        var running = new AppConfig { EnableAdaptiveSearch = false };

        running.CopyRuntimeSettingsFrom(new AppConfig { EnableAdaptiveSearch = false });

        Assert.False(running.EnableAdaptiveSearch);
    }

    [Fact]
    public void Chep_tat_ca_cac_truong_giao_dien_quan_ly()
    {
        // Danh sách trường phải giữ được ý nghĩa: mọi thứ người dùng chỉnh trên giao diện đều
        // phải tới đích. Nếu thêm một trường vào giao diện mà quên ở đây, nó sẽ "lưu được nhưng
        // không có tác dụng" — và đó là loại lỗi người dùng không báo được vì không có dấu hiệu.
        var from = new AppConfig
        {
            Level = CompressionLevel.Light,
            DryRunDefault = true,
            MaxConcurrent = 7,
            MinSavingPercent = 12.5,
            MinFileSizeBytes = 123_456,
            IncludeSubfolders = true,
            ExcludePatterns = ["*.tmp"],
            MeasureQuality = false,
            CheckFreeSpace = false,
            ConcurrencyScale = 3,
            LogLevel = "Debug",
            Theme = "dark",
        };

        var to = new AppConfig();
        to.CopyRuntimeSettingsFrom(from);

        Assert.Equal(CompressionLevel.Light, to.Level);
        Assert.True(to.DryRunDefault);
        Assert.Equal(7, to.MaxConcurrent);
        Assert.Equal(12.5, to.MinSavingPercent);
        Assert.Equal(123_456, to.MinFileSizeBytes);
        Assert.True(to.IncludeSubfolders);
        Assert.Equal(["*.tmp"], to.ExcludePatterns);
        Assert.False(to.MeasureQuality);
        Assert.False(to.CheckFreeSpace);
        Assert.Equal(3, to.ConcurrencyScale);
        Assert.Equal("Debug", to.LogLevel);
        Assert.Equal("dark", to.Theme);
    }

    [Fact]
    public void Cac_truong_khong_thuoc_giao_dien_thi_giu_nguyen_gia_tri_dang_chay()
    {
        // Cờ thử nghiệm và các trường tinh vi khác không đi qua giao diện, nên lưu cấu hình
        // không được đụng tới.
        var running = new AppConfig
        {
            EnableAdaptiveSearch = true,
            EnableAv1Search = true,
            MaxSearchEvaluations = 31,
            MaxInitialCandidates = 7,
            VideoCodec = "av1",
        };

        running.CopyRuntimeSettingsFrom(new AppConfig());

        Assert.True(running.EnableAdaptiveSearch);
        Assert.True(running.EnableAv1Search);
        Assert.Equal(31, running.MaxSearchEvaluations);
        Assert.Equal(7, running.MaxInitialCandidates);
        Assert.Equal("av1", running.VideoCodec);
    }

    [Fact]
    public void Co_the_tat_co_thu_nghiem_bang_cach_rang_no()
    {
        // Đường tắt tường minh: một ai đó muốn buộc cờ xuống thì gọi với
        // `preserveExperimentalFlags: false`. Phải tồn tại để lựa chọn này không bị cấm ngầm.
        var running = new AppConfig { EnableAdaptiveSearch = true };

        running.CopyRuntimeSettingsFrom(new AppConfig(), preserveExperimentalFlags: false);

        Assert.False(running.EnableAdaptiveSearch);
    }

    [Fact]
    public void Danh_sach_co_flag_phai_dung_va_khong_rong()
    {
        // Danh sách phải liệt kê tường minh để thêm một cờ mới bắt buộc phải quyết định có đi
        // qua giao diện hay không — thay vì âm thầm trở thành cờ "bị reset mỗi lần lưu".
        Assert.NotEmpty(AppConfig.ExperimentalFlags);
        Assert.Contains(nameof(AppConfig.EnableAdaptiveSearch), AppConfig.ExperimentalFlags);

        // Mỗi tên trong danh sách phải là một thành viên thật của AppConfig.
        foreach (var name in AppConfig.ExperimentalFlags)
        {
            Assert.NotNull(typeof(AppConfig).GetProperty(name));
        }
    }

    [Fact]
    public void Khong_chep_vao_chinh_no()
    {
        // Không phải lỗi thường gặp, nhưng rẻ mà đắt: `CopyRuntimeSettingsFrom(this)` sẽ khóa
        // object vô ích và làm mất giá trị cờ thử nghiệm.
        var config = new AppConfig { EnableAdaptiveSearch = true, MinSavingPercent = 9.5 };

        Assert.Throws<ArgumentNullException>(() => config.CopyRuntimeSettingsFrom(null!));
    }
}
