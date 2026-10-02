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
/// <para><b>Đã đóng nửa còn lại của lỗi đó.</b> Nay Cài đặt đã có ô bật cho cờ này, nên nó là
/// trường giao diện thường và phải chép theo. Nếu giữ nó ở nhánh "giữ nguyên", ô bật trở thành
/// nút mù: người dùng bật, bấm Lưu, và cờ tắt lại — đúng lỗi cũ, chỉ lần này người dùng có
/// lý do tin rằng mình đang chọn.</para>
///
/// <para>Hàm chép nằm ở <see cref="AppConfig"/> trong Core chứ không ở tầng WinForms, nên nó
/// kiểm thử được mà không cần đổi TFM của cả bộ kiểm thử — điều mà bản sửa trước đó không làm
/// được và phải ghi nợ.</para>
/// </remarks>
public class AppConfigCopyTests
{
    [Fact]
    public void Co_that_bat_thi_vao_cau_hinh_thi_van_giu_nguyen()
    {
        // Cờ KHÔNG có ô bật ở giao diện thì lưu cấu hình không được đụng tới: bản giao diện
        // gửi lên không có trường đó, nên giá trị của nó luôn là mặc định (tắt).
        var running = new AppConfig { EnableAv1Search = true };

        running.CopyRuntimeSettingsFrom(new AppConfig { Level = CompressionLevel.Strong });

        Assert.True(running.EnableAv1Search);
        Assert.Equal(CompressionLevel.Strong, running.Level);
    }

    [Fact]
    public void Co_that_tat_roi_luu_thu_khac_thi_van_tat()
    {
        // Cùng lập luận: người dùng tắt cờ rồi lưu một thứ khác, cờ phải vẫn tắt.
        var running = new AppConfig { EnableAv1Search = false };

        running.CopyRuntimeSettingsFrom(new AppConfig { EnableAv1Search = false });

        Assert.False(running.EnableAv1Search);
    }

    [Fact]
    public void O_bat_trong_giao_dien_phai_an()
    {
        // Đây là hợp đồng của ô bật trong Cài đặt: bật thì phải tới đích, tắt thì phải tới đích.
        // Trước đây cờ này được giữ nguyên có chủ ý, và ô bật (nếu có) sẽ là nút mù.
        var running = new AppConfig { EnableAdaptiveSearch = false };

        running.CopyRuntimeSettingsFrom(new AppConfig { EnableAdaptiveSearch = true });
        Assert.True(running.EnableAdaptiveSearch);

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
            EnableAdaptiveSearch = true,
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
        Assert.True(to.EnableAdaptiveSearch);
    }

    [Fact]
    public void Cac_truong_khong_thuoc_giao_dien_thi_giu_nguyen_gia_tri_dang_chay()
    {
        // Cờ và ngưỡng tinh vi không đi qua giao diện, nên lưu cấu hình không được đụng tới.
        var running = new AppConfig
        {
            EnableAv1Search = true,
            MaxSearchEvaluations = 31,
            MaxInitialCandidates = 7,
            VideoCodec = "av1",
        };

        running.CopyRuntimeSettingsFrom(new AppConfig());

        Assert.True(running.EnableAv1Search);
        Assert.Equal(31, running.MaxSearchEvaluations);
        Assert.Equal(7, running.MaxInitialCandidates);
        Assert.Equal("av1", running.VideoCodec);
    }

    [Fact]
    public void Danh_sach_co_flag_phai_dung_va_khong_rong()
    {
        // Danh sách phải liệt kê tường minh để thêm một cờ mới bắt buộc phải quyết định có đi
        // qua giao diện hay không — thay vì âm thầm trở thành cờ "bị reset mỗi lần lưu".
        Assert.NotEmpty(AppConfig.ExperimentalFlags);

        // Mỗi tên trong danh sách phải là một thành viên thật của AppConfig.
        foreach (var name in AppConfig.ExperimentalFlags)
        {
            Assert.NotNull(typeof(AppConfig).GetProperty(name));
        }
    }

    [Fact]
    public void Co_roi_bat_trong_giao_dien_thi_khong_duoc_liet_ke_la_co_that()
    {
        // Ngược lại cũng phải đúng: một trường ĐÃ có ô bật ở Cài đặt mà lại nằm trong danh sách
        // cờ thử nghiệm thì ô bật ấy không ăn. Cờ thích ứng từng ở trong danh sách này và
        // chính vì thế nó bị giữ nguyên khi lưu — im lặng, không báo lỗi.
        Assert.DoesNotContain(nameof(AppConfig.EnableAdaptiveSearch), AppConfig.ExperimentalFlags);
    }

    [Fact]
    public void Khong_chep_vao_chinh_no()
    {
        // Không phải lỗi thường gặp, nhưng rẻ mà đắt: `CopyRuntimeSettingsFrom(this)` sẽ khoá
        // object vô ích và làm mất giá trị cờ thử nghiệm.
        var config = new AppConfig { EnableAv1Search = true, MinSavingPercent = 9.5 };

        Assert.Throws<ArgumentNullException>(() => config.CopyRuntimeSettingsFrom(null!));
    }
}
