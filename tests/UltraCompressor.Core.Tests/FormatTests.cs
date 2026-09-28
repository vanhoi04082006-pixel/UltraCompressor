using UltraCompressor.Core;
using Xunit;

namespace UltraCompressor.Core.Tests;

public class FormatTests
{
    [Theory]
    // Cùng cách hiển thị như bản gốc v12: bản gốc hiện "4.47 GB" cho 4 803 868 881 byte
    // và ảnh chụp màn hình của bản gốc cũng ghi đúng con số đó.
    [InlineData(0, "0 bytes")]
    [InlineData(512, "512 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048575, "1024.0 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(4803868881, "4.47 GB")]
    public void Size_dung_dinh_dang(long bytes, string expected)
        => Assert.Equal(expected, Format.Size(bytes));

    [Fact]
    public void Size_am_ban_ve_khong_va_so_am()
    {
        Assert.Equal("0 bytes", Format.Size(-1));
    }

    [Fact]
    public void Size_luon_dung_dau_cham_thap_phan()
    {
        // Máy đang dùng vùng cũ "tiếng Việt" dùng dấu phẩy thập phân. Bản gốc dùng
        // ToString() không chỉ định văn hoá nên có thể ra "4,47 GB" — sai với mọi ảnh chụp
        // trước đó. Ở đây phải luôn là dấu chấm.
        var result = Format.Size(4803868881);
        Assert.Contains('.', result);
        Assert.DoesNotContain(',', result);
    }

    [Theory]
    [InlineData(-1, "--:--")]
    [InlineData(double.NaN, "--:--")]
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(599, "09:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3725, "01:02:05")]
    [InlineData(86401, "> 1 ngày")]
    public void Time_dung_dinh_dang(double seconds, string expected)
        => Assert.Equal(expected, Format.Time(seconds));

    [Fact]
    public void SizeSigned_co_dau()
    {
        Assert.Equal("+1.0 KB", Format.SizeSigned(1024));
        Assert.Equal("-1.0 KB", Format.SizeSigned(-1024));
    }
}
