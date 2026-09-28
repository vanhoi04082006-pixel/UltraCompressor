using System.Text;


namespace UltraCompressor.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public sealed record LogEntry(
    DateTimeOffset At,
    LogLevel Level,
    string Category,
    string Message,
    string? JobId = null,
    string? FilePath = null);

/// <summary>Nhật ký dạng text, có phân loại để bật/tắt mức chi tiết.</summary>
public interface ILogger
{
    LogLevel MinimumLevel { get; set; }

    void Write(LogLevel level, string category, string message, string? jobId = null, string? filePath = null);
}

/// <summary>Phương thức tiện ích dùng chung cho mọi hiện thực <see cref="ILogger"/>.</summary>
public static class LoggerExtensions
{
    public static void LogDebug(this ILogger log, string category, string message)
        => log.Write(LogLevel.Debug, category, message);

    public static void LogInfo(this ILogger log, string category, string message)
        => log.Write(LogLevel.Info, category, message);

    public static void LogWarning(this ILogger log, string category, string message, string? jobId = null)
        => log.Write(LogLevel.Warning, category, message, jobId);

    public static void LogError(this ILogger log, string category, string message, Exception? ex = null)
        => log.Write(
            LogLevel.Error,
            category,
            ex is null ? message : $"{message} — {ex.Message}",
            filePath: ex?.StackTrace);

    public static void LogSkipped(this ILogger log, string reason, string filePath, string? jobId)
        => log.Write(LogLevel.Info, "skip", reason, jobId, filePath);

    public static void LogToolFailure(this ILogger log, string tool, string message)
        => log.Write(LogLevel.Error, "tools", $"{tool}: {message}");
}

/// <summary>Bỏ qua mọi thứ — dùng khi cần code không phụ thuộc log.</summary>
public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    public LogLevel MinimumLevel { get; set; } = LogLevel.Error;

    public void Write(LogLevel level, string category, string message, string? jobId = null, string? filePath = null)
    {
    }
}

/// <summary>
/// Ghi nhật ký ra tệp, theo ngày, tự xoay vòng.
///
/// Trọng tâm là <b>ghi lại nguyên văn lệnh đã chạy</b>. Khi kết quả nén kỳ lạ, nguyên nhân
/// gần như luôn nằm ở lệnh đó — bản gốc không lưu lại gì nên không chẩn đoán được.
/// </summary>
public sealed class FileLogger : ILogger
{
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly System.Text.UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly int _recentCapacity;

    public FileLogger(string directory, LogLevel minimumLevel = LogLevel.Info, int recentCapacity = 500)
    {
        _directory = directory;
        MinimumLevel = minimumLevel;
        _recentCapacity = recentCapacity;
        Directory.CreateDirectory(directory);
    }

    public LogLevel MinimumLevel { get; set; }

    public string CurrentFile => Path.Combine(_directory, $"ultra-{DateTime.Now:yyyyMMdd}.log");

    public void Write(LogLevel level, string category, string message, string? jobId = null, string? filePath = null)
    {
        if (level < MinimumLevel) return;

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message, jobId, filePath);

        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > _recentCapacity) _recent.Dequeue();
        }

        try
        {
            var line = Format(entry);
            lock (_gate)
            {
                RollIfNeeded();

                // BOM để Notepad và các trình soạn text kiểu cũ đọc đúng tiếng Việt.
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Utf8WithBom);
            }
        }
        catch
        {
            // Nhật ký hỏng không được làm hỏng việc nén.
        }
    }

    public IReadOnlyList<LogEntry> Recent(int max = 200)
    {
        lock (_gate)
        {
            return [.. _recent.Reverse().Take(max)];
        }
    }

    /// <summary>Ghi lại đúng một lần chạy công cụ ngoài, đủ để tái hiện sự cố.</summary>
    public void LogCommand(
        string tool,
        IReadOnlyList<string> arguments,
        int exitCode,
        IReadOnlyList<string> errorTail,
        double elapsedSeconds,
        string? jobId = null,
        string? filePath = null)
    {
        var quoted = string.Join(' ', arguments.Select(a =>
            a.Contains(' ') ? $"\"{a}\"" : a));

        var message = new StringBuilder()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{tool} {quoted}")
            .Append(System.Globalization.CultureInfo.InvariantCulture, $"  -> exit {exitCode}, {elapsedSeconds:F1}s");

        if (errorTail.Count > 0)
        {
            message.AppendLine();
            foreach (var line in errorTail.TakeLast(20))
            {
                message.AppendLine("  | " + line);
            }
        }

        Write(
            exitCode == 0 ? LogLevel.Info : LogLevel.Warning,
            "command",
            message.ToString(),
            jobId,
            filePath);
    }

    public void LogToolFailure(string tool, string message)
        => Write(LogLevel.Error, "tools", $"{tool}: {message}");

    public void LogSkipped(string reason, string filePath, string? jobId)
        => Write(LogLevel.Info, "skip", reason, jobId, filePath);

    private static string Format(LogEntry e)
    {
        var level = e.Level switch
        {
            LogLevel.Error => "ERR ",
            LogLevel.Warning => "WARN",
            LogLevel.Info => "INFO",
            _ => "DBUG",
        };

        var scope = e.FilePath is null ? e.Category : $"{e.Category} | {e.FilePath}";
        return $"{e.At:yyyy-MM-dd HH:mm:ss.fff} {level} {scope,-70} {e.Message}";
    }

    private void RollIfNeeded()
    {
        var file = CurrentFile;
        if (!File.Exists(file)) return;

        var info = new FileInfo(file);
        if (info.Length < MaxFileBytes) return;

        var archive = Path.Combine(_directory, $"ultra-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            File.Move(file, archive, overwrite: true);
        }
        catch
        {
            // Không xoay được thì cứ ghi tiếp vào tệp cũ.
        }
    }
}
