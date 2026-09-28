using System.IO;
using System.Windows.Forms;
using UltraCompressor.App.Bridge;
using UltraCompressor.Core;
using UltraCompressor.Core.Diagnostics;
using UltraCompressor.Core.Storage;
using UltraCompressor.Core.Toolchain;

namespace UltraCompressor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        AppHost? host = null;
        MainForm? form = null;

        try
        {
            AppPaths.EnsureDirectories();

            var logger = new FileLogger(AppPaths.LogDirectory, LogLevel.Info);
            Diagnostic.Attach(logger);

            // Ở đây chưa có vòng lặp thông điệp nào chạy, nên chặn bằng GetResult là an toàn.
            // (Trong một sự kiện của WPF thì việc này sẽ treo: phần tiếp nối của tác vụ async
            // cần dispatcher mà dispatcher lại đang bị chính lệnh chặn đó giữ.)
            var config = new ConfigStore(AppPaths.ConfigFile).LoadAsync().GetAwaiter().GetResult();
            logger.MinimumLevel = ParseLogLevel(config.LogLevel);
            logger.LogInfo("startup", $"Khởi động. Dữ liệu tại {AppPaths.DataDirectory}");

            using (var workspace = new TempWorkspace(AppPaths.TempRoot))
            {
                var removed = workspace.RemoveStale(TimeSpan.FromHours(6));
                if (removed > 0) logger.LogInfo("startup", $"Đã dọn {removed} tệp tạm cũ.");
            }

            var locator = new ToolLocator(AppPaths.BaseDirectory, config.Tools);
            foreach (var dir in FindDevToolDirectories())
            {
                locator.AddSearchDirectory(dir);
            }

            var tools = new ToolChain(locator, new ToolHealthChecker(locator));
            var session = new SessionStore(AppPaths.SessionFile);
            host = new AppHost(config, tools, logger, session);

            var (jobs, sessionError) = host.Engine.LoadSessionAsync().GetAwaiter().GetResult();
            if (sessionError is not null) logger.LogWarning("startup", sessionError);
            if (jobs.Count > 0) logger.LogInfo("startup", $"Khôi phục {jobs.Count} job từ phiên trước.");

            form = new MainForm(host) { WebRoot = ResolveWebRoot() };
        }
        catch (Exception ex)
        {
            // Lỗi khởi động phải thấy được, nếu không người dùng chỉ thấy cửa sổ trắng.
            Diagnostic.Log($"Khởi động thất bại: {ex}");
            MessageBox.Show(
                $"Khởi động thất bại:\n\n{ex.Message}\n\n{ex.GetType().Name}",
                "UltraCompressor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            host?.DisposeAsync().AsTask().Wait(3_000);
            return;
        }

        var window = form;

        /*
          Kiểm tra công cụ phải chạy TRƯỚC Application.Run.

          Application.Run chỉ quay lại khi ứng dụng kết thúc, nên nếu khởi động kiểm tra
          sau đó thì nó chạy duy nhất lúc thoát — cả phiên người dùng thấy trạng thái
          "đang kiểm tra" và tưởng công cụ hỏng. Hàm này chỉ chờ I/O tiến trình ngoài nên
          chạy song song với vòng lặp thông điệp, không cần UI thread.
        */
        if (host is not null)
        {
            _ = CheckToolsInBackgroundAsync(host);
        }

        Application.Run(window);
    }

    private static async Task CheckToolsInBackgroundAsync(AppHost host)
    {
        try
        {
            foreach (var report in await host.Engine.Tools.CheckAllAsync())
            {
                Diagnostic.Log($"{report.DisplayName}: {report.Health}{(report.Message is null ? "" : " — " + report.Message)}");
            }

            host.Invalidate();
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Kiểm tra công cụ lỗi: {ex.Message}");
        }
    }

    /// <summary>
    /// Tìm thư mục <c>tools\</c> của dự án khi chạy thử bằng <c>dotnet run</c>.
    ///
    /// Cài thật thì ffmpeg và gifsicle nằm cạnh tệp thực thi. Nhưng khi chạy thử, tệp thực
    /// thi nằm ở <c>bin\Debug\net10.0-windows\</c> — không có công cụ nào cạnh bên, nên ứng
    /// dụng báo nhầm thiếu Gifsicle trong khi người dùng đã đặt nó vào <c>tools\</c>.
    /// </summary>
    private static List<string> FindDevToolDirectories()
    {
        var found = new List<string>();
        var dir = new DirectoryInfo(AppPaths.BaseDirectory);

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            foreach (var relative in new[] { "tools", Path.Combine("src", "UltraCompressor.App", "tools") })
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (found.Contains(candidate)) continue;
                if (Directory.Exists(candidate)) found.Add(candidate);
            }

            dir = dir.Parent;
        }

        return found;
    }

    private static LogLevel ParseLogLevel(string? value) => value?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>
    /// Tìm thư mục giao diện: cạnh tệp thực thi trước, rồi lên cấp để hỗ trợ chạy thử
    /// (bin\Debug\net10.0-windows) mà không cần publish.
    /// </summary>
    private static string? ResolveWebRoot()
    {
        var direct = Path.Combine(AppPaths.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(direct, "index.html"))) return direct;

        var dir = new DirectoryInfo(AppPaths.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;

            var sibling = Path.Combine(dir.FullName, "src", "UltraCompressor.App", "wwwroot");
            if (File.Exists(Path.Combine(sibling, "index.html"))) return sibling;

            dir = dir.Parent;
        }

        return direct;
    }
}
