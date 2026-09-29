using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using UltraCompressor.App.Bridge;
using UltraCompressor.Core;

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
    private bool _allowClose;
    private bool _closing;

    public MainForm(AppHost host)
    {
        _host = host;

        Text = "UltraCompressor — Nén media hàng loạt";

        // ApplicationIcon trong csproj chỉ gắn icon vào resource của tệp thực thi, nên
        // Explorer đọc được. Nhưng WinForms vẫn tự tạo icon mặc định cho Handle nếu không
        // gán ở đây — biểu hiện là biểu tượng trên thanh tiêu đề và trong Alt+Tab là icon
        // .NET mặc định, không phải icon ứng dụng. Gán rõ để hai nơi khớp nhau.
        Icon = AppIcon.Load();

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
        _host.PickFiles = BrowseForFiles;
        _host.PickExportFolder = BrowseForExportFolder;
        _host.PickToolFile = BrowseForToolFile;

        FormClosed += OnFormClosed;
        FormClosing += OnFormClosing;
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

        // Nạp phiên lần trước trước khi giao diện vẽ lần đầu, để lời mời "Tiếp tục"
        // hiện ngay thay vì phải chờ tới lần đẩy trạng thái kế tiếp.
        try
        {
            var restored = await _host.RestoreSessionAsync();
            if (restored > 0) _status.Text = $"Đã khôi phục {restored} job từ lần chạy trước.";
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khôi phục phiên lỗi: {ex.Message}");
        }

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

            var userData = Core.AppPaths.WebViewProfileDirectory;
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

            // Chặn WebView2 tự mở tệp vừa thả vào. Không có tính năng kéo-thả nào ở đây:
            // nếu để mặc định, thả một tệp vào cửa sổ sẽ khiến trình duyệt điều hướng tới
            // tệp đó và giao diện trắng trơn. Giữ nguyên cờ này còn là cách rẻ nhất để
            // chặn, và nó cũng làm WebView2 thôi giữ vai trò drop target.
            _browser.AllowExternalDrop = false;


            var media = new MediaHost();
            _host.RegisterMedia = media.Register;
            core.AddWebResourceRequestedFilter(
                MediaHost.Origin + "/*",
                CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var response = media.Respond(env, args);
                if (response is not null) args.Response = response;
            };

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
    /// <b>Phải trừ khung cửa sổ và phải canh lại vị trí.</b> Trước đây hàm này lấy nguyên
    /// vùng làm việc làm <see cref="Control.ClientSize"/>. Hai lỗi cộng lại làm mép phải
    /// và mép dưới của cửa sổ nằm ngoài màn hình:
    ///
    ///  1. vùng làm việc 1920x1020 là kích thước <b>ngoài</b> của cửa sổ; đặt ClientSize
    ///     bằng đúng nó rồi cộng thêm thanh tiêu đề và viền (~34px) thì chiều cao vượt
    ///     vùng làm việc, status bar bị cắt;
    ///  2. đổi kích thước <b>không</b> khiến WinForms canh lại vị trí. Cửa sổ được canh
    ///     giữa khi còn 1280px bề rộng (mép trái = 320), rồi nới ra 1918px mà mép trái
    ///     giữ nguyên → mép phải = 2238, tràn 318px ra ngoài màn hình 1920. Đó là lý do
    ///     cột thao tác, khối "Tốc độ / Còn lại / Luồng nén" và nút cuối thanh trên cùng
    ///     biến mất.
    ///
    /// Cách sửa: đặt <see cref="Form.Size"/> (kích thước ngoài) cho vừa vùng làm việc, để
    /// WinForms tự trừ khung, rồi canh lại giữa màn hình.
    ///
    /// <see cref="Screen.WorkingArea"/> và <see cref="Control.ClientSize"/> dùng cùng một
    /// hệ đơn vị khi tiến trình khai báo DPI-aware, nên lấy nguyên giá trị, không chia thêm
    /// cho tỉ lệ phóng.
    ///
    /// Chỉ gọi được sau khi cửa sổ đã có handle: trước đó <see cref="Form.DeviceDpi"/> luôn
    /// trả về 96 bất kể màn hình thật.
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var area = Screen.FromControl(this).WorkingArea;

            // -2 để mép cửa sổ không dính sát mép màn hình (Windows 11 có thanh tác vụ).
            var target = new Size(
                Math.Max(area.Width - 2, MinimumSize.Width),
                Math.Max(area.Height - 2, MinimumSize.Height));

            var frame = Size - ClientSize;
            var wanted = new Size(target.Width + frame.Width, target.Height + frame.Height);

            if (Size != wanted)
            {
                Size = wanted;
            }

            if (Location != new Point(area.Left, area.Top))
            {
                Location = new Point(area.Left, area.Top);
            }

            Diagnostic.Log(
                $"Kich thuoc cua so: client={ClientSize.Width}x{ClientSize.Height}, " +
                $"ngoai={Size.Width}x{Size.Height}, DPI={DeviceDpi}, " +
                $"vung lam viec={area.Width}x{area.Height}, vi tri={Location.X},{Location.Y}");
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

                // ExecuteScriptAsync trả về ngay khi script trả về, KHÔNG đợi promise bên
                // trong. Nên script kiểm thử kiểu `async () => { ... await sleep ... }` vẫn
                // đang chạy ở trong khi ta đã chụp. UC_EVAL_SETTLE (mili giây) là khoảng
                // chờ để lấy đúng trạng thái muốn chụp, ví dụ lúc job đang nén dở một tệp.
                var settle = 1200;
                if (int.TryParse(Environment.GetEnvironmentVariable("UC_EVAL_SETTLE"), out var ms))
                {
                    settle = Math.Clamp(ms, 0, 600_000);
                }

                await Task.Delay(settle);
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

    /// <summary>
    /// Chọn nhiều tệp lẻ. Bộ lọc dựng từ chính danh sách đuôi mà lõi hỗ trợ
    /// (<c>MediaClassifier</c>), nên hộp thoại không bao giờ lệch với những gì ứng dụng
    /// thực sự nén được.
    /// </summary>
    private string[] BrowseForFiles()
    {
        var extensions = MediaClassifier.AllSupportedExtensions
            .Select(e => e.TrimStart('*'))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var dialog = new OpenFileDialog
        {
            Title = "Chọn tệp cần nén (có thể chọn nhiều)",
            Multiselect = true,
            CheckFileExists = true,
            Filter = $"Media được hỗ trợ ({string.Join(';', extensions)})|{string.Join(';', extensions)}|" +
                     "Tất cả tệp (*.*)|*.*",
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : [];
    }


    /// <summary>Chọn thư mục đích khi xuất kết quả ra nơi khác.</summary>
    private string? BrowseForExportFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục nhận kết quả nén",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
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

    // ---------------------------------------------------------------- đóng cửa sổ

    /// <summary>
    /// Chặn đóng cửa sổ, rồi mới hỏi. Không hỏi ngay trong sự kiện này vì việc hoàn tác
    /// hoặc lưu phiên mất vài giây — làm trong <c>FormClosing</c> sẽ treo cửa sổ trông
    /// như treo máy.
    /// </summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;

        e.Cancel = true;
        if (_closing) return;
        _closing = true;

        _ = ShutdownAsync(e.CloseReason);
    }

    private async Task ShutdownAsync(CloseReason reason)
    {
        try
        {
            var plan = _host.PlanExit();

            // Rảnh thì thoát luôn, không vướng người dùng bằng câu hỏi.
            if (!plan.IsBusy && plan.AppliedFiles == 0)
            {
                FinishClose();
                return;
            }

            var choice = await AskOnUiThreadAsync(plan);
            if (choice == AppHost.ExitChoice.Stay)
            {
                _host.AbandonExitPreparation();
                _status.Text = "Đã huỷ đóng.";
                return;
            }

            _status.Text = choice == AppHost.ExitChoice.UndoAndExit
                ? "Đang khôi phục bản gốc…"
                : "Đang lưu phiên để chạy tiếp…";

            if (choice == AppHost.ExitChoice.UndoAndExit)
            {
                var result = await Task.Run(() => _host.UndoEverythingAsync());
                Diagnostic.Log($"Thoát sau khi hoàn tác: {result.Restored} tệp, {result.Failed} lỗi.");
            }
            else
            {
                await Task.Run(() => _host.SaveForResumeAsync());
            }
        }
        catch (Exception ex)
        {
            // Hỏng thì cứ thoát, không giữ người dùng ở cửa sổ không đóng được.
            Diagnostic.Log($"Lỗi khi đóng ({reason}): {ex.Message}");
        }
        finally
        {
            FinishClose();
        }
    }

    private void FinishClose()
    {
        void Done()
        {
            if (IsDisposed) return;
            _allowClose = true;
            _closing = false;
            Close();
        }

        if (InvokeRequired) BeginInvoke(Done);
        else Done();
    }

    /// <summary>Hiện hộp thoại trên luồng giao diện rồi chờ người dùng chọn.</summary>
    private Task<AppHost.ExitChoice> AskOnUiThreadAsync(AppHost.ExitPlan plan)
    {
        var tcs = new TaskCompletionSource<AppHost.ExitChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            try
            {
                using var dialog = new ExitConfirmForm(plan);
                dialog.ShowDialog(this);
                tcs.TrySetResult(dialog.Choice);
            }
            catch (Exception ex)
            {
                Diagnostic.Log($"Hộp thoại đóng cửa sổ lỗi: {ex.Message}");
                tcs.TrySetResult(AppHost.ExitChoice.Stay);
            }
        }

        if (InvokeRequired) BeginInvoke(Show);
        else Show();

        return tcs.Task;
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
    /// <summary>
    /// Đặt AppUserModelID cho tiến trình. Phải gọi trước khi tạo cửa sổ đầu tiên.
    /// Windows 10 trở đi hỗ trợ; bản cũ hơn trả về HRESULT lỗi — bỏ qua được.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
}
