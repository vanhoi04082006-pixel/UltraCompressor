using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Khóa cứng bảng tham số lấy từ bản gốc v12 (xem docs/PHASE0-REFERENCE.md mục 1).
/// Đây là hợp đồng với người dùng: đổi số này là đổi hành vi nén, nên phải có người canh.
/// </summary>
public class CompressionProfileTests
{
    [Fact]
    public void Bang_tham_so_khop_ban_goc()
    {
        var light = CompressionProfile.For(CompressionLevel.Light);
        Assert.Equal(20, light.VideoCrf);
        Assert.Equal("slow", light.VideoPreset);
        Assert.Equal(3, light.ImageQuality);
        Assert.Equal(3840, light.MaxWidth);
        Assert.Equal(320, light.AudioBitrateKbps);
        Assert.Equal("/prepress", light.PdfPreset);
        Assert.Equal(20, light.GifLossy);

        var balanced = CompressionProfile.For(CompressionLevel.Balanced);
        Assert.Equal(23, balanced.VideoCrf);
        Assert.Equal("medium", balanced.VideoPreset);
        Assert.Equal(5, balanced.ImageQuality);
        Assert.Equal(1920, balanced.MaxWidth);
        Assert.Equal(192, balanced.AudioBitrateKbps);
        Assert.Equal("/ebook", balanced.PdfPreset);
        Assert.Equal(40, balanced.GifLossy);

        var strong = CompressionProfile.For(CompressionLevel.Strong);
        Assert.Equal(28, strong.VideoCrf);
        Assert.Equal("veryfast", strong.VideoPreset);
        Assert.Equal(10, strong.ImageQuality);
        Assert.Equal(1080, strong.MaxWidth);
        Assert.Equal(128, strong.AudioBitrateKbps);
        Assert.Equal("/screen", strong.PdfPreset);
        Assert.Equal(80, strong.GifLossy);
    }

    [Fact]
    public void Muc_nang_hon_thi_quet_hon()
    {
        var levels = new[] { CompressionLevel.Light, CompressionLevel.Balanced, CompressionLevel.Strong }
            .Select(CompressionProfile.For).ToArray();

        for (var i = 1; i < levels.Length; i++)
        {
            // CRF cao hơn = nén mạnh hơn
            Assert.True(levels[i].VideoCrf > levels[i - 1].VideoCrf);
            // -q:v cao hơn = chất lượng ảnh thấp hơn = file nhỏ hơn
            Assert.True(levels[i].ImageQuality > levels[i - 1].ImageQuality);
            // giảm chiều rộng tối đa nhiều hơn
            Assert.True(levels[i].MaxWidth < levels[i - 1].MaxWidth);
            // bitrate thấp hơn
            Assert.True(levels[i].AudioBitrateKbps < levels[i - 1].AudioBitrateKbps);
            // PDF nén mạnh hơn
            Assert.True(levels[i].GifLossy > levels[i - 1].GifLossy);
        }
    }

    [Fact]
    public void Loc_anh_giong_ban_goc()
    {
        Assert.Equal("scale='min(1920,iw)':-2", CompressionProfile.For(CompressionLevel.Balanced).ScaleFilter);
        Assert.Equal("scale='min(3840,iw)':-2", CompressionProfile.For(CompressionLevel.Light).ScaleFilter);
        Assert.Equal("scale='min(1080,iw)':-2", CompressionProfile.For(CompressionLevel.Strong).ScaleFilter);
    }

    [Fact]
    public void Loc_gif_co_palettegen_va_giam_khung_hinh()
    {
        var strong = CompressionProfile.For(CompressionLevel.Strong);
        Assert.Contains("palettegen", strong.GifFilter);
        Assert.Contains("paletteuse", strong.GifFilter);
        Assert.Contains("fps=15", strong.GifFilter);
        // Mức Mạnh thu nhỏ thêm một bước so với hai mức kia.
        Assert.Contains("scale=iw*0.8", strong.GifFilter);

        var light = CompressionProfile.For(CompressionLevel.Light);
        Assert.Contains("fps=20", light.GifFilter);
        Assert.DoesNotContain("scale=iw*0.", light.GifFilter);
    }

    [Fact]
    public void Giu_nguyen_ten_muc_khi_serial_hoa()
    {
        foreach (var level in Enum.GetValues<CompressionLevel>())
        {
            Assert.Equal(level, CompressionProfile.For(level).Level);
        }
    }
}
