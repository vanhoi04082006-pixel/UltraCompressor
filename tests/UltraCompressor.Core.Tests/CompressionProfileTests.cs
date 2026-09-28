using UltraCompressor.Core.Pipelines;
using UltraCompressor.Core.Models;
using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Khóa cứng bảng tham số nén. Đây là hợp đồng với người dùng: đổi số này là đổi hành vi
/// nén, nên phải có người canh. Xem docs/PHASE0-REFERENCE.md mục 1 cho bản gốc.
/// </summary>
public class CompressionProfileTests
{
    [Fact]
    public void Bang_tham_so_dung_nghia_da_doi()
    {
        var light = CompressionProfile.For(CompressionLevel.Light);
        Assert.Equal(20, light.VideoCrf);
        Assert.Equal("slow", light.VideoPreset);
        Assert.Equal(3840, light.VideoMaxWidth);
        Assert.Equal(3, light.ImageQuality);
        Assert.Equal(2560, light.ImageMaxWidth);
        Assert.Equal(320, light.AudioBitrateKbps);
        Assert.Equal(20, light.GifLossy);

        var balanced = CompressionProfile.For(CompressionLevel.Balanced);
        Assert.Equal(23, balanced.VideoCrf);
        Assert.Equal("medium", balanced.VideoPreset);
        Assert.Equal(1920, balanced.VideoMaxWidth);
        Assert.Equal(5, balanced.ImageQuality);
        Assert.Equal(1920, balanced.ImageMaxWidth);
        Assert.Equal(192, balanced.AudioBitrateKbps);
        Assert.Equal("/ebook", balanced.PdfPreset);
        Assert.Equal(40, balanced.GifLossy);

        var strong = CompressionProfile.For(CompressionLevel.Strong);
        Assert.Equal(28, strong.VideoCrf);
        Assert.Equal("veryfast", strong.VideoPreset);
        Assert.Equal(1920, strong.VideoMaxWidth);
        Assert.Equal(10, strong.ImageQuality);
        Assert.Equal(1600, strong.ImageMaxWidth);
        Assert.Equal(128, strong.AudioBitrateKbps);
        Assert.Equal("/screen", strong.PdfPreset);
        Assert.Equal(80, strong.GifLossy);
    }

    [Fact]
    public void Muc_nhe_khong_dung_preset_prepress_cho_pdf()
    {
        // /prepress là thiết lập cho in offset: giữ ảnh 300dpi và sinh tệp rất lớn. Người
        // dùng chọn "Nhẹ" là muốn giữ chất lượng, không phải muốn chuẩn bị in.
        var light = CompressionProfile.For(CompressionLevel.Light);

        Assert.Equal("/default", light.PdfPreset);
        Assert.DoesNotContain("prepress", light.PdfPreset);
    }

    [Fact]
    public void Be_rong_anh_va_video_tach_biet()
    {
        // Trước đây cả hai dùng chung MaxWidth, nên chọn "Mạnh" là cả ảnh lẫn video đều bị
        // bóp về 1080px. Ảnh vẫn phải sắc nét trên màn hình 1440px.
        foreach (var level in Enum.GetValues<CompressionLevel>())
        {
            var profile = CompressionProfile.For(level);
            Assert.True(profile.ImageMaxWidth >= 1440, $"{level}: ảnh bị hạ dưới 1440px");
        }

        // Mức "Nhẹ" cho phép video rộng hơn ảnh — đúng tinh thần bảo toàn chi tiết.
        var light = CompressionProfile.For(CompressionLevel.Light);
        Assert.True(light.VideoMaxWidth > light.ImageMaxWidth);
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
            // bitrate thấp hơn
            Assert.True(levels[i].AudioBitrateKbps < levels[i - 1].AudioBitrateKbps);
            // GIF nén mạnh hơn
            Assert.True(levels[i].GifLossy > levels[i - 1].GifLossy);

            // Bề rộng chỉ được phép giảm, không bắt buộc giảm: video ở cả "Cân bằng" và
            // "Mạnh" đều giữ 1920px, đẩy việc nhỏ file xuống CRF chứ không cắt thêm kích
            // thước — cắt cả hai cùng lúc là mất chất lượng mà không thêm lợi ích.
            Assert.True(levels[i].VideoMaxWidth <= levels[i - 1].VideoMaxWidth);
            Assert.True(levels[i].ImageMaxWidth <= levels[i - 1].ImageMaxWidth);
        }
    }

    [Fact]
    public void Loc_anh_va_video_dung_tan_rieng()
    {
        Assert.Equal("scale='min(1920,iw)':-2", CompressionProfile.For(CompressionLevel.Balanced).VideoFilter);
        Assert.Equal("scale='min(1920,iw)':-2", CompressionProfile.For(CompressionLevel.Balanced).ImageFilter);

        Assert.Equal("scale='min(3840,iw)':-2", CompressionProfile.For(CompressionLevel.Light).VideoFilter);
        Assert.Equal("scale='min(2560,iw)':-2", CompressionProfile.For(CompressionLevel.Light).ImageFilter);

        Assert.Equal("scale='min(1920,iw)':-2", CompressionProfile.For(CompressionLevel.Strong).VideoFilter);
        Assert.Equal("scale='min(1600,iw)':-2", CompressionProfile.For(CompressionLevel.Strong).ImageFilter);
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
