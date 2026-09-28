using System.IO;
using UltraCompressor.Core.Diagnostics;
using UltraCompressor.Core.Media;
using UltraCompressor.Core.Models;
using UltraCompressor.Core.Processes;
using UltraCompressor.Core.Storage;
using UltraCompressor.Core.Toolchain;

namespace UltraCompressor.Core.Compare;

/// <summary>Một tệp trong màn hình so sánh.</summary>
public sealed record CompareSide
{
    public required string Path { get; init; }

    public required string FileName { get; init; }

    /// <summary>Loại media. Giao diện đọc trường này để chọn thẻ hiển thị: video, ảnh hay âm thanh.</summary>
    public required string Kind { get; init; }

    public long Size { get; init; }

    public string SizeText { get; init; } = string.Empty;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string ResolutionText { get; init; } = string.Empty;

    public string DurationText { get; init; } = string.Empty;

    public double DurationSeconds { get; init; }

    public int? BitrateKbps { get; init; }

    public string BitrateText { get; init; } = string.Empty;

    public bool Exists { get; init; }

    /// <summary>Ảnh xem trước dạng data URI, hoặc null nếu không dựng được (ví dụ tệp âm thanh).</summary>
    public string? Thumbnail { get; init; }

    /// <summary>URL để nhúng trực tiếp vào trang (video/âm thanh/ảnh). Xem <c>MediaHost</c>.</summary>
    public string? Url { get; init; }

    public string? Error { get; init; }
}

public sealed record CompareResult
{
    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    /// <summary>Loại media, để giao diện chọn đúng thẻ hiển thị (video, ảnh, âm thanh).</summary>
    public required string Kind { get; init; }

    public required CompareSide Original { get; init; }

    /// <summary>Null khi chưa có bản nén thật trên đĩa (chạy thử, hoặc tệp bị giữ nguyên).</summary>
    public CompareSide? Compressed { get; init; }

    public long SavedBytes { get; init; }

    public string SavedText { get; init; } = string.Empty;

    public string SavedPercentText { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    /// <summary>Có nên hiện nút mở trình phát cho cả hai bên không.</summary>
    public bool CanPlay { get; init; }
}

/// <summary>
/// Dựng dữ liệu cho màn hình so sánh: bản gốc đứng cạnh bản đã nén.
///
/// Cặp tệp được đoán như sau:
///  - sau khi nén thật, tệp trên đĩa là bản nén và <c>.bak</c> là bản gốc → so <c>.bak</c>
///    với tệp hiện tại;
///  - khi xuất ra thư mục khác, tệp nén nằm ở thư mục đích → so tệp gốc với tệp ở đích;
///  - còn lại (chạy thử, hoặc tệp bị giữ nguyên) thì không có bản nén trên đĩa, và
///    <see cref="CompareResult.Compressed"/> là null.
///
/// Ảnh xem trước được thu nhỏ về 640px để giao diện không phải tải một ảnh 4K cho mỗi lần
/// bấm. Không gửi cả tệp gốc về giao diện — chỉ gửi ảnh đã thu nhỏ và vài chữ số.
/// </summary>
public sealed class CompareService(ToolLocator locator, TempWorkspace workspace, FileLogger? log = null)
{
    private const int ThumbWidth = 640;
    private const long MaxThumbnailBytes = 8 * 1024 * 1024;

    private readonly Dictionary<string, CompareResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<CompareResult?> BuildAsync(
        JobItem item,
        string? outputFolder,
        CancellationToken token = default)
    {
        var key = item.FilePath + "|" + (outputFolder ?? string.Empty);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var currentExists = File.Exists(item.FilePath);
        if (!currentExists) return null;

        var backup = FileTransaction.BackupPathFor(item.FilePath);
        var backupExists = File.Exists(backup);

        string? originalPath;
        string? compressedPath;
        string note;

        if (backupExists)
        {
            originalPath = backup;
            compressedPath = item.FilePath;
            note = string.Empty;
        }
        else if (!string.IsNullOrEmpty(outputFolder) && item.OutputPath is { } exported && File.Exists(exported))
        {
            originalPath = item.FilePath;
            compressedPath = exported;
            note = string.Empty;
        }
        else
        {
            originalPath = item.FilePath;
            compressedPath = null;
            note = item.IsApplied
                ? string.Empty
                : "Chưa có bản nén trên đĩa. Chạy thử chỉ đo, không ghi tệp — bấm Duyệt hoặc dùng Nén thật.";
        }

        var original = await DescribeAsync(originalPath, item.Kind, token);
        var compressed = compressedPath is null
            ? null
            : await DescribeAsync(compressedPath, item.Kind, token);

        var saved = compressed is null ? 0 : Math.Max(0, original.Size - compressed.Size);
        var percent = original.Size > 0 && compressed is not null
            ? (double)saved * 100.0 / original.Size
            : 0;

        var result = new CompareResult
        {
            FilePath = item.FilePath,
            FileName = item.FileName,
            Kind = item.Kind.ToString(),
            Original = original,
            Compressed = compressed,
            SavedBytes = saved,
            SavedText = Format.Size(saved),
            SavedPercentText = compressed is null ? string.Empty : Format.Percent(percent),
            Note = note,
            CanPlay = item.Kind is MediaKind.Video or MediaKind.Audio,
        };

        _cache[key] = result;
        return result;
    }

    private async Task<CompareSide> DescribeAsync(string path, MediaKind kind, CancellationToken token)
    {
        var fileName = Path.GetFileName(path);
        var exists = File.Exists(path);
        if (!exists)
        {
            return new CompareSide
            {
                Path = path,
                FileName = fileName,
                Kind = kind.ToString(),
                Exists = false,
                SizeText = "—",
                ResolutionText = "—",
                DurationText = "—",
                BitrateText = "—",
                Error = "Không tìm thấy tệp.",
            };
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            return new CompareSide
            {
                Path = path,
                FileName = fileName,
                Kind = kind.ToString(),
                Exists = true,
                SizeText = "—",
                ResolutionText = "—",
                DurationText = "—",
                BitrateText = "—",
                Error = ex.Message,
            };
        }

        MediaInfo? info = null;
        if (kind != MediaKind.Image)
        {
            try
            {
                var ffmpeg = locator.Locate(ToolKind.FFmpeg);
                if (ffmpeg is not null) info = await new MediaProbe(ffmpeg).ProbeAsync(path, token);
            }
            catch (Exception ex)
            {
                log?.LogDebug("compare", $"Không probe được '{fileName}': {ex.Message}");
            }
        }

        string? thumbnail = null;
        try
        {
            thumbnail = await MakeThumbnailAsync(path, kind, info, token);
        }
        catch (Exception ex)
        {
            log?.LogDebug("compare", $"Không dựng được ảnh xem trước cho '{fileName}': {ex.Message}");
        }

        return new CompareSide
        {
            Path = path,
            FileName = fileName,
            Kind = kind.ToString(),
            Exists = true,
            Size = size,
            SizeText = Format.Size(size),
            Width = info?.Width,
            Height = info?.Height,
            ResolutionText = info?.Width is { } w && info.Height is { } h ? $"{w}×{h}" : "—",
            DurationText = info?.Duration is { } d ? Format.Time(d.TotalSeconds) : "—",
            DurationSeconds = info?.Duration?.TotalSeconds ?? 0,
            BitrateKbps = info?.BitrateKbps is { } b ? (int)Math.Round(b) : null,
            BitrateText = info?.BitrateKbps is { } br ? $"{Math.Round(br, 0):0} kb/s" : "—",
            Thumbnail = thumbnail,
        };
    }

    /// <summary>
    /// Dựng ảnh xem trước. Ảnh thì thu nhỏ bằng chính ffmpeg, video/GIF thì lấy một khung
    /// hình ở 10% thời lượng, PDF thì để Ghostscript lo. Không dựng được thì trả null —
    /// giao diện hiện ô trống thay vì làm hỏng cả màn hình.
    /// </summary>
    /// <summary>
    /// Chọn thời điểm lấy khung hình: 10% thời lượng, tối thiểu 3 giây.
    ///
    /// Không lấy ở đầu tệp vì mở đầu phim thường là logo hoặc màn đen — so sánh hai bên
    /// bằng khung gần như đen thì vô nghĩa. Dùng tỉ lệ thay vì số giây cố định để một tập
    /// 20 phút và một clip 45 giây lấy được một khoảnh đặc trưng thay vì một khoảnh mở đầu.
    /// </summary>
    private static double FramePosition(MediaInfo? info)
    {
        var total = info?.Duration?.TotalSeconds ?? 0;
        if (total <= 0) return 3;
        return Math.Clamp(total * 0.10, 3, 600);
    }

    private async Task<string?> MakeThumbnailAsync(string path, MediaKind kind, MediaInfo? info, CancellationToken token)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tif" or ".tiff")
        {
            var ffmpeg = locator.Locate(ToolKind.FFmpeg);
            if (ffmpeg is null) return null;
            if (new FileInfo(path).Length > MaxThumbnailBytes) return null;
            return await RenderWithFfmpegAsync(ffmpeg, path, seconds: null, token);
        }

        if (kind is MediaKind.Video or MediaKind.Gif)
        {
            var ffmpeg = locator.Locate(ToolKind.FFmpeg);
            if (ffmpeg is null) return null;
            return await RenderWithFfmpegAsync(ffmpeg, path, seconds: FramePosition(info), token);
        }

        if (kind is MediaKind.Pdf)
        {
            var gs = locator.Locate(ToolKind.Ghostscript);
            if (gs is null) return null;
            return await RenderWithGhostscriptAsync(gs, path, token);
        }

        return null;
    }

    private async Task<string?> RenderWithFfmpegAsync(
        string ffmpeg,
        string inputPath,
        double? seconds,
        CancellationToken token)
    {
        var temp = workspace.CreatePath("thumb", ".jpg");

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

        if (seconds is { } target)
        {
            // Dò hai lần: nhảy nhanh tới gần đích rồi mới tinh chỉnh.
            //
            // Chỉ `-ss` trước `-i` thì nhanh nhưng dừng ở keyframe gần nhất, mà bản gốc và
            // bản nén có cấu trúc GOP khác nhau → hai bên rơi vào hai thời điểm khác nhau và
            // không so được. Chỉ `-ss` sau `-i` thì chuẩn tới từng khung nhưng phải giải mã
            // từ đầu, tệ cho phim dài. Nhảy trước rồi tinh chỉnh sau là cách nhanh và chính xác.
            var coarse = Math.Max(0, target - 3);
            args.AddRange(["-ss", coarse.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
            args.AddRange(["-i", inputPath, "-ss", (target - coarse).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        }
        else
        {
            args.AddRange(["-i", inputPath]);
        }

        args.AddRange(["-frames:v", "1"]);
        args.AddRange(["-vf", $"scale={ThumbWidth}:-2:flags=lanczos", "-q:v", "4", "-y", temp]);

        return await FinishAsync(ffmpeg, args, temp, token);
    }

    private async Task<string?> RenderWithGhostscriptAsync(string gs, string inputPath, CancellationToken token)
    {
        var temp = workspace.CreatePath("thumb", ".jpg");

        // Ghostscript đặt tệp đích bằng -sOutputFile và không có tùy chọn resize như ffmpeg.
        // -r72 cho trang A4 khoảng 595px, vừa đủ cho một nửa màn hình.
        var args = new List<string>
        {
            "-dQUIET", "-dNOPAUSE", "-dBATCH", "-dSAFER",
            "-sDEVICE=jpeg", "-r72", "-dFirstPage=true", "-dLastPage=true",
            $"-sOutputFile={temp}",
            inputPath,
        };

        return await FinishAsync(gs, args, temp, token);
    }

    private async Task<string?> FinishAsync(
        string tool,
        IReadOnlyList<string> args,
        string temp,
        CancellationToken token)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(tool, args, token: token);
            if (!result.Succeeded || !File.Exists(temp)) return null;

            var bytes = await File.ReadAllBytesAsync(temp, token);
            return bytes.Length == 0 ? null : "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        finally
        {
            workspace.Release(temp);
        }
    }
}
