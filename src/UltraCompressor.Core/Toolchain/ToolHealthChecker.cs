using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Toolchain;

/// <summary>
/// Kiểm tra công cụ ngoài bằng cách <b>chạy thật một việc nhỏ</b>, không chỉ đọc <c>--version</c>.
///
/// Lý do: trên chính máy này, <c>gifsicle.exe</c> in "Can't load DLL, LoadLibrary error 126"
/// rồi vẫn chạy tốt, còn <c>gswin64c.exe</c> in "Can't load Ghostscript DLL" và hỏng thật.
/// Chuỗi cảnh báo vì vậy không đáng tin — chỉ exit code + kiểm tra tệp đầu ra mới là kết luận.
/// </summary>
public sealed class ToolHealthChecker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] BrokenMarkers =
    [
        "Can't load", "LoadLibrary error", "not recognized", "cannot find",
        "No such file", "not a valid Win32", "Bad EXE format",
    ];

    private readonly ToolLocator _locator;

    public ToolHealthChecker(ToolLocator locator) => _locator = locator;


    public async Task<ToolReport> CheckAsync(ToolKind kind, CancellationToken token = default)
    {
        var name = kind switch
        {
            ToolKind.FFmpeg => "FFmpeg",
            ToolKind.FFplay => "FFplay",
            ToolKind.Gifsicle => "Gifsicle",
            ToolKind.Ghostscript => "Ghostscript",
            _ => kind.ToString(),
        };

        var required = kind != ToolKind.FFplay;
        var path = _locator.Locate(kind);

        if (path is null)
        {
            return new ToolReport
            {
                Kind = kind,
                DisplayName = name,
                Required = required,
                Health = ToolHealth.Missing,
                Message = kind == ToolKind.Ghostscript
                    ? "Chưa tìm thấy. Cài Ghostscript tại https://ghostscript.com/releases/ rồi khai báo đường dẫn trong Cài đặt."
                    : "Chưa tìm thấy. Đặt tệp thực thi cạnh chương trình hoặc khai báo đường dẫn trong Cài đặt.",
            };
        }

        return kind switch
        {
            ToolKind.FFmpeg => await CheckFFmpegAsync(kind, path, name, required, token),
            ToolKind.FFplay => await CheckVersionOnlyAsync(kind, path, name, required, ["-version"], token),
            ToolKind.Gifsicle => await CheckGifsicleAsync(kind, path, name, required, token),
            ToolKind.Ghostscript => await CheckGhostscriptAsync(kind, path, name, required, token),
            _ => new ToolReport { Kind = kind, DisplayName = name, Required = required, Path = path },
        };
    }

    private static async Task<ToolReport> CheckFFmpegAsync(ToolKind kind, string path, string name, bool required, CancellationToken token)
    {
        var version = await ReadFirstLineAsync(path, ["-version"], token);
        if (version is null)
        {
            return Broken(kind, path, name, required, "Không chạy được. Kiểm tra lại tệp ffmpeg.exe.");
        }

        // Thử thật: dựng 1 frame 16x16 rồi xuất ra null. Rất nhanh, không cần tệp nguồn.
        var probe = await ProcessRunner.RunAsync(
            path,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=16x16:d=0.1",
             "-frames:v", "1", "-f", "null", "-"],
            ProbeTimeout,
            token);

        return probe.Succeeded
            ? Ok(kind, path, name, required, version)
            : Broken(kind, path, name, required, $"Chạy được nhưng thử encode thất bại (exit {probe.ExitCode}). {Tail(probe)}");
    }

    private async Task<ToolReport> CheckGifsicleAsync(ToolKind kind, string path, string name, bool required, CancellationToken token)
    {
        var version = await ReadFirstLineAsync(path, ["--version"], token);
        if (version is null)
        {
            return Broken(kind, path, name, required, "Không chạy được. Kiểm tra lại tệp gifsicle.exe.");
        }

        // Không thể thử gifsicle nếu chưa có ffmpeg để tạo ảnh GIF thử.
        var ffmpeg = _locator.Locate(ToolKind.FFmpeg);
        if (ffmpeg is null)
        {
            return new ToolReport
            {
                Kind = kind,
                DisplayName = name,
                Required = required,
                Path = path,
                Health = ToolHealth.Ok,
                Version = version,
                Message = "Chưa kiểm tra chức năng vì thiếu FFmpeg.",
            };
        }

        var dir = Path.Combine(Path.GetTempPath(), "uc_probe_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "probe.gif");
            var result = Path.Combine(dir, "probe_opt.gif");

            var make = await ProcessRunner.RunAsync(
                ffmpeg,
                ["-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=s=32x32:r=5:d=0.4", source],
                ProbeTimeout,
                token);

            if (!make.Succeeded || !File.Exists(source))
            {
                return Broken(kind, path, name, required, "Không tạo được tệp GIF thử để kiểm tra.");
            }

            var run = await ProcessRunner.RunAsync(
                path,
                ["-O3", "--colors", "256", source, "-o", result],
                ProbeTimeout,
                token);

            if (!run.Succeeded || !File.Exists(result) || new FileInfo(result).Length == 0)
            {
                return Broken(kind, path, name, required,
                    $"Tệp có mặt nhưng không tối ưu được (exit {run.ExitCode}). Thường là thiếu DLL runtime 32-bit. {Tail(run)}");
            }

            return Ok(kind, path, name, required, version);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task<ToolReport> CheckGhostscriptAsync(ToolKind kind, string path, string name, bool required, CancellationToken token)
    {
        var version = await ReadFirstLineAsync(path, ["-version"], token);

        // Chạy một chương trình PostScript tối thiểu. Chỉ cần nạp được DLL là đạt.
        var probe = await ProcessRunner.RunAsync(
            path,
            ["-dQUIET", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=nullpage", "-c", "quit"],
            ProbeTimeout,
            token);

        if (probe.Succeeded)
        {
            return Ok(kind, path, name, required, version ?? "Ghostscript");
        }

        var detail = Tail(probe);
        var looksLikeStub = BrokenMarkers.Any(m => detail.Contains(m, StringComparison.OrdinalIgnoreCase));

        return Broken(kind, path, name, required, looksLikeStub
            ? "Tệp tồn tại nhưng không phải bộ cài Ghostscript đầy đủ — thường là tệp stub thiếu DLL. " +
              "Cài lại từ https://ghostscript.com/releases/ rồi khai báo đường dẫn gswin64c.exe trong Cài đặt."
            : $"Không khởi động được (exit {probe.ExitCode}). {detail}");
    }

    private static async Task<ToolReport> CheckVersionOnlyAsync(
        ToolKind kind, string path, string name, bool required, string[] args, CancellationToken token)
    {
        var version = await ReadFirstLineAsync(path, args, token);
        return version is null
            ? Broken(kind, path, name, required, "Không chạy được.")
            : new ToolReport
            {
                Kind = kind,
                DisplayName = name,
                Required = required,
                Path = path,
                Health = required ? ToolHealth.Ok : ToolHealth.Optional,
                Version = version,
            };
    }

    /// <summary>
    /// Đọc dòng phiên bản đầu tiên. Luôn có giới hạn thời gian.
    ///
    /// Bản đầu tiên gọi <c>RunAsync(path, args, token)</c> — bản không có timeout. Với
    /// <c>ffplay -version</c> thì đôi khi tiến trình không tự thoát, làm treo
    /// <c>CheckAllAsync</c>, và vì cửa sổ chỉ mở sau bước này nên ứng dụng không hiện gì cả.
    /// </summary>
    private static async Task<string?> ReadFirstLineAsync(string path, string[] args, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync(path, args, ProbeTimeout, token);

        var text = result.StandardOutput.Trim();
        if (text.Length == 0)
        {
            var line = result.StandardErrorTail.FirstOrDefault(
                l => !BrokenMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
            text = line?.Trim() ?? string.Empty;
        }

        if (result.TimedOut)
        {
            // Vẫn trả về phần đã kịp in ra, nhưng đánh dấu để lớp gọi biết là không chắc chắn.
            return text.Length == 0 ? null : text;
        }

        return result.Cancelled ? null : text.Split('\n').FirstOrDefault()?.Trim() ?? null;
    }

    private static ToolReport Ok(ToolKind kind, string path, string name, bool required, string? version) => new()
    {
        Kind = kind,
        DisplayName = name,
        Required = required,
        Path = path,
        Health = ToolHealth.Ok,
        Version = version,
    };

    private static ToolReport Broken(ToolKind kind, string path, string name, bool required, string message) => new()
    {
        Kind = kind,
        DisplayName = name,
        Required = required,
        Path = path,
        Health = ToolHealth.Broken,
        Message = message,
    };

    private static string Tail(ProcessResult result)
    {
        var lines = result.StandardErrorTail
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Reverse()
            .Take(3)
            .Reverse()
            .ToArray();
        return lines.Length == 0 ? string.Empty : string.Join(" | ", lines);
    }
}
