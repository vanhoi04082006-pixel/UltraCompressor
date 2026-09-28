using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Bảng tham số nén, chuyển từ bản gốc v12 sang dạng dữ liệu thuần để kiểm thử được.
/// Xem <c>docs/PHASE0-REFERENCE.md</c> mục 1 để đối chiếu.
///
/// Một mức nén là <b>một con số duy nhất</b> áp cho mọi loại media, nhưng mỗi loại đọc
/// tham số riêng từ đây: chọn "Cân bằng" là video CRF 23 <i>và</i> ảnh -q:v 5 <i>và</i>
/// audio 192k <i>và</i> PDF /ebook <i>và</i> GIF lossy 40/20fps. Không có mức nén riêng cho
/// từng loại, và cũng không chỉnh được tham số tay.
/// </summary>
public sealed record CompressionProfile
{
    public required int VideoCrf { get; init; }

    public required string VideoPreset { get; init; }

    public required int ImageQuality { get; init; }

    /// <summary>Chiều rộng tối đa sau khi nén. Chiều cao tự tính theo tỉ lệ, làm tròn chẵn 2.</summary>
    public required int MaxWidth { get; init; }

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
            ImageQuality = 3,
            MaxWidth = 3840,
            AudioBitrateKbps = 320,
            PdfPreset = "/prepress",
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
            ImageQuality = 5,
            MaxWidth = 1920,
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
            ImageQuality = 10,
            MaxWidth = 1080,
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
    /// Chuỗi <c>-vf</c> thu nhỏ: chỉ co khi ảnh rộng hơn mức tối đa.
    ///
    /// <see cref="MaxWidth"/> dùng chung cho ảnh và video — cùng một mức nén thì ảnh và
    /// video bị thu nhỏ về cùng một bề rộng. Tách riêng được thì phải tách trường.
    /// </summary>
    public string ScaleFilter => $"scale='min({MaxWidth},iw)':-2";

    public string GifFilter =>
        $"fps={GifFps},scale=iw*{GifWidthScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}:-1:flags=lanczos," +
        "split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";
}
