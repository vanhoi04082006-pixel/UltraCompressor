using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Toolchain;

/// <summary>Tìm đường dẫn tới các công cụ ngoài theo thứ tự ưu tiên.</summary>
public sealed class ToolLocator
{
    private readonly string _appDirectory;
    private readonly List<string> _extraDirectories = [];

    public ToolLocator(string appDirectory, ToolPaths? configured = null)
    {
        _appDirectory = appDirectory;
        Configured = configured ?? new ToolPaths();
    }

    public ToolPaths Configured { get; }

    /// <summary>Thư mục Ghostscript tự động phát hiện thêm.</summary>
    public void AddSearchDirectory(string directory) => _extraDirectories.Add(directory);

    public string? Locate(ToolKind kind) => kind switch
    {
        ToolKind.FFmpeg => LocateOne(Configured.FFmpeg, "ffmpeg.exe"),
        ToolKind.FFplay => LocateOne(Configured.FFplay, "ffplay.exe"),
        ToolKind.Gifsicle => LocateOne(Configured.Gifsicle, "gifsicle.exe"),
        ToolKind.Ghostscript => LocateOne(Configured.Ghostscript, "gswin64c.exe") ?? LocateGhostscriptInProgramFiles(),
        _ => null,
    };

    public string? LocateOne(string? configuredPath, string fileName)
    {
        // 1. Đường dẫn người dùng chỉ định.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        // 2. Cạnh tệp thực thi. Dùng đường dẫn tuyệt đối — bản gốc dùng đường dẫn
        //    tương đối nên phụ thuộc current working directory (bug B2, B23).
        var beside = Path.Combine(_appDirectory, fileName);
        if (File.Exists(beside)) return Path.GetFullPath(beside);

        // 3. Các thư mục đã biết (ví dụ nơi cài Ghostscript).
        foreach (var dir in _extraDirectories)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        // 4. PATH.
        return SearchPath(fileName);
    }

    private static string? SearchPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir;
            try
            {
                dir = raw.Trim().Trim('"');
            }
            catch
            {
                continue;
            }

            if (dir.Length == 0) continue;

            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
                // Mục PATH không hợp lệ — bỏ qua.
            }
        }

        return null;
    }

    /// <summary>Dò Ghostscript trong <c>C:\Program Files\gs\gs*\bin\gswin64c.exe</c>.</summary>
    private static string? LocateGhostscriptInProgramFiles()
    {
        foreach (var root in new[] { @"C:\Program Files\gs", @"C:\Program Files (x86)\gs" })
        {
            if (!Directory.Exists(root)) continue;

            string? best = null;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "gs*"))
                {
                    var candidate = Path.Combine(dir, "bin", "gswin64c.exe");
                    if (File.Exists(candidate)) best = candidate;
                }
            }
            catch
            {
                // Bỏ qua lỗi quyền truy cập.
            }

            if (best is not null) return Path.GetFullPath(best);
        }

        return null;
    }
}
