using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using UltraCompressor.App.Bridge;

namespace UltraCompressor.App;

/// <summary>
/// Khung chủ: đặt WebView2 vào cửa sổ và nối cầu hai chiều với lõi .NET.
/// Toàn bộ giao diện nằm ở <c>wwwroot</c>.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppHost _host;
    private readonly WebView2 _browser = new();
    private readonly Label _status = new();
    private readonly Label _version = new();
    private string? _webRoot;

    public MainForm(AppHost host)
    {
        _host = host;

        Text = "UltraCompressor — Nén media hàng loạt";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x0F, 0x11, 0x15);
        ForeColor = Color.FromArgb(0xE6, 0xEA, 0xF0);
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(760, 480);

        // Rất quan trọng. Mặc định của WinForms là AutoScaleMode.Font: nó nhân lại kích
        // thước form theo tỉ lệ phóng của phông chữ so với phông lúc thiết kế. Ở màn hình
        // 125%, kích thước logic bị nhân thêm ~1,23 lần, cửa sổ con WebView2 cũng được cấp
        // theo số đó — trong khi cửa sổ thật thì nhỏ hơn. Kết quả: bề mặt WebView2 rộng
        // hơn khung cửa sổ và toàn bộ mép phải giao diện bị cắt mất.
        AutoScaleMode = AutoScaleMode.None;

        ClientSize = new Size(1280, 800);
        AllowDrop = true;
        KeyPreview = true;

        // WebView2 chiếm toàn bộ vùng client, trừ thanh trạng thái cuối cửa sổ.
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor,
            Margin = Padding.Empty,
        };
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        split.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

        _browser.Dock = DockStyle.Fill;
        _browser.BackColor = BackColor;
        _browser.DefaultBackgroundColor = Color.FromArgb(0x0F, 0x11, 0x15);

        var statusBar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(0x0B, 0x0D, 0x10),
            Padding = new Padding(12, 0, 12, 0),
        };
        _status.Dock = DockStyle.Left;
        _status.AutoSize = false;
        _status.Width = 700;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(0x8A, 0x93, 0xA0);
        _status.Text = "Đang khởi tạo giao diện…";

        _version.Dock = DockStyle.Right;
        _version.AutoSize = false;
        _version.Width = 160;
        _version.TextAlign = ContentAlignment.MiddleRight;
        _version.ForeColor = Color.FromArgb(0x5B, 0x64, 0x72);
        _version.Text = $"v{typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"}";

        statusBar.Controls.Add(_status);
        statusBar.Controls.Add(_version);

        split.Controls.Add(_browser, 0, 0);
        split.Controls.Add(statusBar, 0, 1);
        Controls.Add(split);

        _host.PickFolders = BrowseForFolders;
        _host.PickToolFile = BrowseForToolFile;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosed += OnFormClosed;
        Shown += OnShown;
    }

    /// <summary>Cho phép chạy thử mà không cần publish.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? WebRoot
    {
        get => _webRoot;
        init => _webRoot = value;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        // Đặt kích thước sau khi đã có handle, khi đó DPI mới đúng.
        FitToWorkArea();
        await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var webRoot = _webRoot ?? Core.AppPaths.WebRoot;

            if (!File.Exists(Path.Combine(webRoot, "index.html")))
            {
                Diagnostic.Log($"Thiếu thư mục giao diện: {webRoot}");
                _status.Text = "Thiếu thư mục giao diện (wwwroot)";
                MessageBox.Show(
                    $"Không tìm thấy thư mục giao diện.\n\nĐã tìm tại: {webRoot}\n\n" +
                    "Hãy chạy `dotnet publish` để đóng gói đầy đủ.",
                    "UltraCompressor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var userData = Path.Combine(Core.AppPaths.DataDirectory, "webview");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _browser.EnsureCoreWebView2Async(env);

            var core = _browser.CoreWebView2;
            if (core is null)
            {
                _status.Text = "Không tạo được đối tượng WebView2";
                return;
            }

            // WebView2 cache tài nguyên tĩnh trong thư mục dữ liệu người dùng. Sau khi sửa
            // styles.css hoặc app.js, bản mới sẽ không bao giờ được nạp — triệu chứng là
            // "sửa giao diện mà không có gì thay đổi". Xoá cache mỗi lần mở ứng dụng:
            // đây là app chạy cục bộ nên không tốn thời gian đáng kể.
            try
            {
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            }
            catch (Exception ex)
            {
                Diagnostic.Log($"Không xoá được cache WebView2: {ex.Message}");
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            // Cho phép thả tệp/thư mục từ Windows Explorer vào cửa sổ. Bản WebView2
            // không có sự kiện "đã thả" ở phía .NET, nên sự kiện drop đi tới trang web
            // dưới dạng DataTransfer HTML5 và xử lý trong wwwroot/app.js.
            _browser.AllowExternalDrop = true;

            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
            core.NewWindowRequested += (_, args) => args.Handled = true;

            core.SetVirtualHostNameToFolderMapping(
                "app.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            Diagnostic.Log($"WebView2: mapping 'app.local' -> {webRoot}");

            _host.Attach(SendAsync);
            core.Navigate("https://app.local/index.html");
            _ = LogGeometryAsync();
            _ = CaptureIfRequestedAsync();
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Lỗi khởi tạo: {ex}");
            _status.Text = $"Lỗi khởi tạo: {ex.Message}";
            MessageBox.Show(
                $"Không khởi tạo được giao diện:\n\n{ex.Message}",
                "UltraCompressor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
    /// <summary>
    /// Kích thước vùng client khởi tạo: lấn hết vùng làm việc của màn hình.
    ///
    /// Giao diện là nội dung web nên cứ lấn hết vùng làm việc, đừng để dải trống.
    ///
    /// Lưu ý: <see cref="Screen.WorkingArea"/> và <see cref="Control.ClientSize"/> dùng
    /// cùng một hệ đơn vị, nên lấy nguyên giá trị, không chia thêm cho tỉ lệ phóng. Lần đầu
    /// mình chia thêm một lần nữa theo DPI, cửa sổ chỉ còn 1534px trong khi màn hình rộng
    /// 1920px — mất gần 20% bề ngang.
    ///
    /// Chỉ gọi được sau khi cửa sổ đã có handle: trước đó <see cref="Form.DeviceDpi"/> luôn
    /// trả về 96 bất kể màn hình thật.
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var area = Screen.FromControl(this).WorkingArea;
            var width = Math.Max(area.Width - 2, MinimumSize.Width);
            var height = Math.Max(area.Height - 2, MinimumSize.Height);

            if (Math.Abs(ClientSize.Width - width) < 2 && Math.Abs(ClientSize.Height - height) < 2)
            {
                return;
            }

            ClientSize = new Size(width, height);
            Diagnostic.Log($"Kich thuoc cua so: {width}x{height}, DPI={DeviceDpi}, vung lam viec={area.Width}x{area.Height}");
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khong chinh duoc kich thuoc cua so: {ex.Message}");
        }
    }

    /// <summary>
    /// Chụp ảnh giao diện khi được yêu cầu, phục vụ kiểm thử.
    ///
    /// Chụp màn hình từ bên ngoài (PowerShell, công cụ khác) sẽ bị Windows ảo hóa theo DPI
    /// và chỉ lấy được một phần cửa sổ, rất dễ khiến ta tưởng giao diện bị cắt mép phải
    /// trong khi thực tế không. <c>CapturePreviewAsync</c> của WebView2 cho ảnh đúng bằng
    /// những gì người dùng nhìn thấy.
    ///
    /// Bật bằng biến môi trường <c>UC_CAPTURE=D:\duong\dan\ten.png</c>.
    /// </summary>
    private async Task CaptureIfRequestedAsync()
    {
        var target = Environment.GetEnvironmentVariable("UC_CAPTURE");
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            var core = _browser.CoreWebView2;
            if (core is null) return;

            // Chờ giao diện vẽ xong một nhịp rồi mới chụp. Trễ chụp chỉnh được bằng
            // UC_CAPTURE_DELAY (giây) để chụp được trạng thái sau khi nén xong.
            var delaySeconds = 5;
            if (int.TryParse(Environment.GetEnvironmentVariable("UC_CAPTURE_DELAY"), out var d))
            {
                delaySeconds = Math.Clamp(d, 1, 600);
            }
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            await core.ExecuteScriptAsync("document.body && window.scrollTo(0, 0)");
            await Task.Delay(600);

            // Chạy đoạn script thử nghiệm trước khi chụp, để kiểm tra được trạng thái mà
            // không thể bấm chuột (ví dụ mở bảng chi tiết). Biến môi trường UC_EVAL_JS.
            var script = Environment.GetEnvironmentVariable("UC_EVAL_JS");
            if (!string.IsNullOrWhiteSpace(script))
            {
                var result = await core.ExecuteScriptAsync(script);
                Diagnostic.Log($"UC_EVAL_JS -> {result}");
                await Task.Delay(1200);
            }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var file = File.Create(target);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);

            Diagnostic.Log($"Da chup giao dien: {target}");
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khong chup duoc giao dien: {ex.Message}");
        }
    }
    /// <summary>
    /// Ghi lại chuỗi kích thước từ cửa sổ xuống tận trang: kích thước form, control, cửa sổ con
    /// HWND, và bề rộng bố cục của trang. Khi mép phải giao diện bị cắt thì chỉ nhìn ảnh
    /// chụp không định được vấn đề nằm ở tầng nào — có bốn số này thì thấy ngay.
    /// </summary>
    private async Task LogGeometryAsync()
    {
        await Task.Delay(2500);

        try
        {
            var core = _browser.CoreWebView2;
            if (core is null) return;

            var page = await core.ExecuteScriptAsync(
                "JSON.stringify({ inner: window.innerWidth, dpr: window.devicePixelRatio, sw: document.documentElement.scrollWidth })");

            // Đo bằng GetClientRect ngay trong tiến trình này: tiến trình đã khai báo
            // DPI-aware nên kết quả là pixel vật lý thật. Đo từ bên ngoài (PowerShell) thì
            // bị Windows ảo hóa theo DPI và nhìn sẽ ra sai lệch, dễ khiến ta chẩn đoán sai.
            var child = "khong co";
            if (NativeMethods.GetClientRect(_browser.Handle, out var r))
            {
                child = $"{r.Right}x{r.Bottom}";
            }

            var formClient = "khong co";
            if (NativeMethods.GetClientRect(Handle, out var fr))
            {
                formClient = $"{fr.Right}x{fr.Bottom}";
            }

            Diagnostic.Log(
                $"Hinh hoc (viet ly): vung client cua so={formClient}, " +
                $"cua so con WebView2={child}, DPI={DeviceDpi}, " +
                $"ClientSize (logic)={ClientSize.Width}x{ClientSize.Height}, trang={page}");
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khong doc duoc hinh hoc: {ex.Message}");
        }
    }
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        Diagnostic.Log($"WebView2: điều hướng xong, thành công={args.IsSuccess}");
        _status.Text = args.IsSuccess ? "Sẵn sàng" : "Không tải được giao diện";
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        Diagnostic.Log($"WebView2 tiến trình lỗi: {args.ProcessFailedKind} (mã {args.ExitCode})");
        _status.Text = "Lỗi tiến trình giao diện";
    }

    private Task SendAsync(string json)
    {
        var core = _browser.CoreWebView2;
        if (core is null) return Task.CompletedTask;

        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Không gửi được tin nhắn tới giao diện: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = BridgeJson.Parse(e.WebMessageAsJson);
        if (message is null)
        {
            Diagnostic.Log("Nhận được tin nhắn không đọc được từ giao diện.");
            return;
        }

        // Sự kiện do trang web phát ra, không phải lệnh gọi vào lõi.
        if (message.Cmd is null)
        {
            if (message.Event == "jsError")
            {
                Diagnostic.Log($"Giao diện báo lỗi: {message.Data?.ToString() ?? "{}"}");
            }
            return;
        }

        _ = DispatchAsync(message);
    }

    private async Task DispatchAsync(BridgeMessage message)
    {
        try
        {
            var response = await _host.DispatchAsync(message);
            await SendAsync(BridgeJson.Serialize(response));
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Lệnh '{message.Cmd}' lỗi: {ex}");
            await SendAsync(BridgeJson.Serialize(new BridgeMessage { Id = message.Id, Error = ex.Message }));
        }
    }

    /// <summary>Chọn nhiều thư mục. Dùng hộp thoại hệ điều hành, xử lý ổ mạng ổn hơn web.</summary>
    private IReadOnlyList<string> BrowseForFolders()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục cần nén",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? [dialog.SelectedPath] : [];
    }

    private string? BrowseForToolFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Chọn tệp thực thi của công cụ",
            Filter = "Tệp thực thi (*.exe)|*.exe|Tất cả tệp (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private void OnDragEnter(object? sender, System.Windows.Forms.DragEventArgs e)
    {
        // Ch? nh?n th� m?c: k�o t?p l? l�n th? kh�ng b�o "sao ch�p" r?i l?i b? qua.
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, System.Windows.Forms.DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;

        // Chỉ nhận thư mục. Kéo tệp lẻ lên thì báo rõ thay vì im lặng bỏ qua.
        var folders = paths.Where(Directory.Exists).ToArray();
        var rejected = paths.Count(p => !Directory.Exists(p));

        if (folders.Length > 0)
        {
            _ = SendAsync(BridgeJson.Serialize(new BridgeMessage
            {
                Event = "foldersDropped",
                Data = System.Text.Json.JsonSerializer.SerializeToNode(
                    new { paths = folders }, BridgeJson.Options),
            }));
        }

        if (rejected > 0)
        {
            _ = SendAsync(BridgeJson.Serialize(new BridgeMessage
            {
                Event = "notice",
                Data = System.Text.Json.JsonSerializer.SerializeToNode(
                    new { level = "warn", message = $"Chỉ nhận thư mục. Bỏ qua {rejected} mục không phải thư mục." },
                    BridgeJson.Options),
            }));
        }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        try
        {
            _host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Lỗi khi đóng: {ex.Message}");
        }

        _browser.Dispose();
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
}