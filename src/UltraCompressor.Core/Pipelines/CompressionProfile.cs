using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Bảng tham số nén, chuyển từ bản gốc v12 sang dạng dữ liệu thuần để kiểm thử được.
///
/// <para><b>Một mức nén là một con số duy nhất</b> áp cho mọi loại media — không có mức
/// riêng cho ảnh, cho video hay cho GIF. Nhưng <b>mỗi loại đọc thông số riêng</b> từ đây:
/// chọn "Cân bằng" là đồng thời video CRF 23 <i>và</i> ảnh <c>-q:v 5</c> <i>và</i> audio
/// 192k <i>và</i> PDF <c>/ebook</c> <i>và</i> GIF lossy 40/20 fps. Không chỉnh được tham số
/// tay.</para>
///
/// <para><b>Bề rộng tối đa tách riêng cho ảnh và video.</b> Trước đây cả hai dùng chung
/// một trường <c>MaxWidth</c>, nên chọn mức "Mạnh" thì cả ảnh lẫn video đều bị bóp về
/// 1080px. Đó là sai về bản chất: ảnh nhìn toàn màn hình và có thể phóng to, còn video đã
/// được ràng buộc bởi khung hình mà mắt theo kịp. Nay mỗi loại một trần riêng.</para>
/// </summary>
public sealed record CompressionProfile
{
    public required int VideoCrf { get; init; }

    public required string VideoPreset { get; init; }

    /// <summary>Trần bề rộng cho video. Không co nếu video gốc hẹp hơn.</summary>
    public required int VideoMaxWidth { get; init; }

    public required int ImageQuality { get; init; }

    /// <summary>Trần bề rộng cho ảnh. Rộng hơn trần của video ở mức "Nhẹ" là có chủ ý.</summary>
    public required int ImageMaxWidth { get; init; }

    public required int AudioBitrateKbps { get; init; }

    public required string PdfPreset { get; init; }

    public required int GifLossy { get; init; }

    public required int GifFps { get; init; }

    public required double GifWidthScale { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Mức nén tương ứng, để giao diện đúng nhận thuộc tính này.</summary>
    public required CompressionLevel Level { get; init; }

    public static CompressionProfile For(CompressionLevel level) => level switch
    {
        // Light: chất lượng cao, file lớn. Không giảm khung hình, không thu nhỏ quá 4K.
        CompressionLevel.Light => new CompressionProfile
        {
            Level = CompressionLevel.Light,
            VideoCrf = 20,
            VideoPreset = "slow",
            VideoMaxWidth = 3840,
            ImageQuality = 3,
            ImageMaxWidth = 2560,
            AudioBitrateKbps = 320,
            // /prepress là thiết lập cho quy trình in offset — nó giữ ảnh ở 300dpi và
            // sinh ra tệp rất lớn, hoàn toàn không đúng ý nghĩa "chất lượng cao" trong
            // ngữ cảnh nén cho xem. /default giữ nguyên chất lượng gốc.
            PdfPreset = "/default",
            GifLossy = 20,
            GifFps = 20,
            GifWidthScale = 1.0,
            DisplayName = "Nhẹ (chất lượng cao)",
        },

        // Balanced: mức khuyến nghị.
        CompressionLevel.Balanced => new CompressionProfile
        {
            Level = CompressionLevel.Balanced,
            VideoCrf = 23,
            VideoPreset = "medium",
            VideoMaxWidth = 1920,
            ImageQuality = 5,
            ImageMaxWidth = 1920,
            AudioBitrateKbps = 192,
            PdfPreset = "/ebook",
            GifLossy = 40,
            GifFps = 20,
            GifWidthScale = 1.0,
            DisplayName = "Cân bằng (khuyến nghị)",
        },

        // Strong: nhỏ nhất, kéo giảm fps và thu nhỏ GIF thêm một bước.
        CompressionLevel.Strong => new CompressionProfile
        {
            Level = CompressionLevel.Strong,
            VideoCrf = 28,
            VideoPreset = "veryfast",
            // Không hạ dưới 1920. CRF 28 đã đủ để nhỏ file; hạ thêm bề rộng nữa là cắt
            // hai lần vào cùng một tệp, mất chất lượng mà không thêm lợi ích gì.
            VideoMaxWidth = 1920,
            ImageQuality = 10,
            // ảnh vẫn rộng hơn 1440 (màn hình phổ biến nhất), nên nhìn vẫn sắc nét.
            ImageMaxWidth = 1600,
            AudioBitrateKbps = 128,
            PdfPreset = "/screen",
            GifLossy = 80,
            GifFps = 15,
            GifWidthScale = 0.8,
            DisplayName = "Mạnh (size nhỏ nhất)",
        },

        // Enum đọc từ JSON có thể mang giá trị lạ.
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public static IReadOnlyList<CompressionProfile> All { get; } =
        [.. Enum.GetValues<CompressionLevel>().Select(For)];

    /// <summary>
    /// Chuỗi <c>-vf</c> thu nhỏ video: chỉ co khi nguồn rộng hơn trần.
    /// <c>min(...)</c> thay vì so sánh trước để không phải biết chiều rộng nguồn.
    /// </summary>
    public string VideoFilter => BuildScale(VideoMaxWidth);

    /// <summary>Chuỗi <c>-vf</c> thu nhỏ ảnh, trần riêng với video.</summary>
    public string ImageFilter => BuildScale(ImageMaxWidth);

    private static string BuildScale(int maxWidth) => $"scale='min({maxWidth},iw)':-2";

    public string GifFilter =>
        $"fps={GifFps},scale=iw*{GifWidthScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}:-1:flags=lanczos," +
        "split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";
}
