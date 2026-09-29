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

/// <summary>Kết quả chạy tiến trình đọc stdout dạng byte thô.</summary>
public sealed class BinaryProcessResult
{
    public required int ExitCode { get; init; }

    /// <summary>Byte thô đọc được từ stdout, đã cắt còn tối đa <c>maxBytes</c>.</summary>
    public byte[] StandardOutputBytes { get; init; } = [];

    public IReadOnlyList<string> StandardErrorTail { get; init; } = [];

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }

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

    /// <summary>
    /// Chạy tiến trình và đọc stdout dạng **byte thô**, không phải văn bản theo dòng.
    ///
    /// <para>Dùng cho ffmpeg xuất khung hình raw ra stdout. Phải là đường riêng chứ không
    /// dùng chung với <see cref="RunAsync(string, IReadOnlyList{string}, Action{string}, Action{string}, CancellationToken)"/>:
    /// trình đọc theo dòng sẽ cắt dữ liệu nhị phân tại mọi ký tự xuống dòng, và ngược lại
    /// dữ liệu nhị phân không có gốc xuống dòng nên trình đọc theo dòng sẽ không bao giờ
    /// kịp phát sự kiện. Cả hai hướng đều hỏng âm thầm, không báo lỗi.</para>
    ///
    /// <para>Đọc tối đa <paramref name="maxBytes"/> rồi dừng, để một tệp hỏng không thể bắt
    /// ta giữ vô hạn byte trong bộ nhớ.</para>
    /// </summary>
    public static async Task<BinaryProcessResult> RunBinaryAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int maxBytes,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stderr = new Queue<string>(StderrTailLines);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stderr.Count == StderrTailLines) stderr.Dequeue();
            stderr.Enqueue(e.Data);
        };

        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new BinaryProcessResult { ExitCode = -1, StandardErrorTail = [ex.Message] };
        }

        process.BeginErrorReadLine();

        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // ignore
        }

        // Đọc stdout trên luồng riêng: nếu đọc tuần tự ở đây thì khi ffmpeg ghi ra nhiều hơn
        // rồi chờ ta đọc, nó sẽ kẹt ở đường ống và hai bên chờ nhau.
        //
        // Không truyền `token` vào đây: sau khi hết giờ ta giết tiến trình rồi vẫn cần đọc
        // nốt phần đang trong đường ống. Hủy giữa chừng sẽ vứt mất đúng phần dữ liệu cần, và
        // luồng chỉ tự kết thúc khi tiến trình đã chết.
        var buffer = new byte[64 * 1024];
        var collected = new MemoryStream();
        var pump = Task.Run(async () =>
        {
            var stream = process.StandardOutput.BaseStream;
            while (collected.Length < maxBytes)
            {
                var want = (int)Math.Min(buffer.Length, maxBytes - collected.Length);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, want), CancellationToken.None);
                }
                catch
                {
                    break;
                }

                if (read <= 0) break;
                await collected.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
            }
        }, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

        var timedOut = false;
        var cancelled = false;
        try
        {
            await exited.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            cancelled = !timedOut;
            TryKillTree(process);

            try
            {
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        // Chờ đọc nốt phần còn lại rồi mới kết luận, nếu không sẽ mất đúng phần ta cần.
        await pump.ConfigureAwait(false);

        return new BinaryProcessResult
        {
            ExitCode = process.HasExited ? process.ExitCode : -1,
            StandardOutputBytes = collected.ToArray(),
            StandardErrorTail = [.. stderr],
            TimedOut = timedOut,
            Cancelled = cancelled,
        };
    }

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
