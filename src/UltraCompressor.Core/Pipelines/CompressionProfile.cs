using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Pipelines;

/// <summary>
/// Bảng tham số nén, chuyển từ bản gốc v12 sang dạng dữ liệu thuần để kiểm thử được.
/// Xem <c>docs/PHASE0-REFERENCE.md</c> mục 1 để đối chiếu.
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

    /// <summary>M?c n�n t��ng ?ng, �? giao di?n d�ng l?i ch�nh thu?c t�nh n�y.</summary>
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
        // Enum �?c t? JSON c� th? mang gi� tr? l?.
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public static IReadOnlyList<CompressionProfile> All { get; } =
        [.. Enum.GetValues<CompressionLevel>().Select(For)];

    /// <summary>Chuỗi <c>-vf</c> thu nhỏ: chỉ co khi ảnh rộng hơn mức tối đa.</summary>
    public string ScaleFilter => $"scale='min({MaxWidth},iw)':-2";

    public string GifFilter =>
        $"fps={GifFps},scale=iw*{GifWidthScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}:-1:flags=lanczos," +
        "split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";
}
