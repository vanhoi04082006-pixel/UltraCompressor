using System.Text;

namespace UltraCompressor.Core.Processes;

/// <summary>Kết quả chạy một tiến trình ngoài.</summary>
public sealed class ProcessResult
{
    public required int ExitCode { get; init; }

    /// <summary>Toàn bộ stdout (thường rỗng với ffmpeg).</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Những dòng cuối cùng của stderr — chỗ ffmpeg/gs ghi lỗi.</summary>
    public IReadOnlyList<string> StandardErrorTail { get; init; } = [];

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }

    public string StandardErrorText => string.Join(Environment.NewLine, StandardErrorTail);

    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>Chạy tiến trình ngoài, đọc output theo dòng, không bao giờ treo.</summary>
public static class ProcessRunner
{
    /// <summary>Số dòng stderr giữ lại để chẩn đoán.</summary>
    private const int StderrTailLines = 60;


    /// <summary>
    /// Chạy tiến trình ngoài. Không đặt timeout toàn cục có chủ ý: nén một video 2 GB có thể
    /// mất hàng giờ, nên thời hạn thuộc về từng pipeline chứ không phải ở tầng runner.
    /// Dùng <see cref="RunAsync(string, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>
    /// cho các lệnh kiểm tra cần chặn trên thời gian.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken token = default)
        => await RunCoreAsync(fileName, arguments, onStdoutLine, onStderrLine, timeout: null, token);

    /// <summary>Chạy lệnh ngắn có giới hạn thời gian — dùng cho kiểm tra công cụ.</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken token = default)
        => await RunCoreAsync(fileName, arguments, null, null, timeout, token);

    /// <summary>Chạy không cần đọc output — dùng cho các lệnh kiểm tra nhanh.</summary>
    public static Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken token = default)
        => RunAsync(fileName, SplitArguments(arguments), token: token);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        Action<string>? onStderrLine,
        TimeSpan? timeout,
        CancellationToken token)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // ArgumentList tự escape — tránh lỗi quote tay như bản gốc.
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new Queue<string>(StderrTailLines);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // Giữ N dòng cuối, vòng tròn.
            if (stderr.Count == StderrTailLines) stderr.Dequeue();
            stderr.Enqueue(e.Data);
            onStderrLine?.Invoke(e.Data);
        };

        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult { ExitCode = -1, StandardErrorTail = [ex.Message] };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // ffmpeg/gs đọc stdin có thể treo vô hạn nếu stdin mở. Đóng ngay.
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // ignore
        }

        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

        var waitToken = linked?.Token ?? token;

        try
        {
            await exited.Task.WaitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeoutCts?.IsCancellationRequested == true;
            TryKillTree(process);

            // Cho tiến trình có thời gian thoát êm; nếu không thì bỏ.
            try
            {
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            return new ProcessResult
            {
                ExitCode = process.HasExited ? process.ExitCode : -1,
                StandardOutput = stdout.ToString(),
                StandardErrorTail = [.. stderr],
                TimedOut = timedOut,
                Cancelled = !timedOut,
            };
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardErrorTail = [.. stderr],
        };
    }

    private static void TryKillTree(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Tiến trình đã thoát — không sao.
        }
    }

    /// <summary>Tách chuỗi tham số, có tôn trọng dấu nháy kép.</summary>
    public static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
