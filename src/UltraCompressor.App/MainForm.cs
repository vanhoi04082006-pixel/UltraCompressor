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

        // Nhận thả tệp. Chỉ làm được từ khi cửa sổ đã có HWND, và phải bật SAU khi
        // AllowExternalDrop = false (xem InitializeBrowserAsync) — nếu bật trước thì
        // WebView2 kịp đăng ký làm OLE drop target và ta không bao giờ nhận thông báo.
        EnableDropOnForm();

        await InitializeBrowserAsync();
    }

    /// <summary>
    /// Bật cờ <c>WS_EX_ACCEPTFILES</c> trên chính Form. Khi thả, hệ điều hành tìm cửa sổ con
    /// sâu nhất dưới con trỏ; cửa sổ con của Chromium không có cờ đó nên nó đi lên chuỗi
    /// cha và dừng ở Form.
    /// </summary>
    private void EnableDropOnForm()
    {
        try
        {
            if (!NativeMethods.AcceptDrop(Handle, true))
            {
                // Không nuốt: nếu cờ không bật thì kéo-thả im lặng không hoạt động, mà
                // người dùng không có cách nào biết vì sao.
                Diagnostic.Log("Khong bat duoc WS_EX_ACCEPTFILES - keo tha se khong hoat dong.");
                return;
            }

            var exStyle = NativeMethods.GetExtendedWindowStyle(Handle);
            var ok = (exStyle & NativeMethods.WSExAcceptFiles) != 0;

            Diagnostic.Log(
                $"Da bat nhan tha tren cua so chinh: exstyle=0x{exStyle:X}, " +
                $"WS_EX_ACCEPTFILES={(ok ? "co" : "KHONG")}.");
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khong bat duoc nhan tha: {ex.GetType().Name}: {ex.Message}");
        }
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

            // KÉO-THẢ: đăng ký WM_DROPFILES trên chính HWND của form.
            //
            // Vì sao không dùng sự kiện DragOver/DragDrop của WebView2: bản SDK này
            // (Microsoft.Web.WebView2 1.0.4191.47) đã bỏ hẳn — đã tra bằng reflection,
            // trong 497 kiểu xuất ra không có `CoreWebView2DragDropEventArgs` cũng không có
            // thành viên nào chứa "Drop" ngoài `AllowExternalDrop`. Nên không có API sẵn.
            //
            // Vì sao cách này chạy được: AllowExternalDrop = false khiến WebView2 thôi là
            // OLE drop target, nên nó KHÔNG có WS_EX_ACCEPTFILES. Khi thả, hệ điều hành tìm
            // cửa sổ con sâu nhất dưới con trỏ, thấy không có cờ đó thì đi lên chuỗi cha —
            // và tới Form (đã bật cờ) thì dừng. Đó là lý do phải đăng ký trên Form chứ
            // không phải trên `_browser.Handle`: HWND đó là control WebView2, còn tệp thật
            // nằm ở cửa sổ con của Chromium, là cháu chứ không phải con.
            //
            // Bắt buộc giữ AllowExternalDrop = false. Bật lên thì WebView2 nhận thả trước,
            // ta không bao giờ nhận được thông báo, và trình duyệt còn điều hướng tới tệp
            // vừa thả làm màn hình trắng trơn.
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

            LogDropTargetChain();
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khong doc duoc hinh hoc: {ex.Message}");
        }
    }

    /// <summary>
    /// Ghi lại trạng thái nhận thả của từng cửa sổ trong chuỗi: form và cửa sổ con WebView2.
    ///
    /// Cơ chế kéo-thả dựa trên tiền đề rằng khi thả, hệ điều hành tìm cửa sổ con sâu nhất
    /// dưới con trỏ rồi **đi lên chuỗi cha** cho tới khi gặp cửa sổ có cờ
    /// <c>WS_EX_ACCEPTFILES</c>. Nếu cửa sổ con của Chromium cũng có cờ đó thì thông báo
    /// không bao giờ tới form, và con trỏ hiện dấu cấm — đúng triệu chứng mà lần thử trước
    /// gặp. Chỉ đọc từ log này mới phân biệt được "sai thứ tự bật cờ" với "WebView2 vẫn
    /// là drop target".
    /// </summary>
    private void LogDropTargetChain()
    {
        try
        {
            var form = DescribeDropStyle(Handle, "form");
            var browser = DescribeDropStyle(_browser.Handle, "control WebView2");

            // Cửa sổ con thật của Chromium là HWND cháu; đi vào nhánh con để tìm nó ra.
            var grandChild = NativeMethods.FindFirstChild(_browser.Handle);
            var webview = DescribeDropStyle(grandChild, "cua so con cua Chromium");

            Diagnostic.Log($"Kha nang nhan tha: {form} | {browser} | {webview}");
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Khong doc duoc trang thai nhan tha: {ex.Message}");
        }
    }

    private static string DescribeDropStyle(IntPtr hwnd, string label)
    {
        if (hwnd == IntPtr.Zero) return $"{label}=khong co";

        var exStyle = NativeMethods.GetExtendedWindowStyle(hwnd);
        var accepts = (exStyle & NativeMethods.WSExAcceptFiles) != 0;

        return $"{label}=0x{exStyle:X} (nhan tha: {(accepts ? "CO" : "khong")})";
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

    /// <summary>
    /// Nhận thả tệp / thư mục. Cửa sổ phải có <c>WS_EX_ACCEPTFILES</c> — xem
    /// <see cref="InitializeBrowserAsync"/> để biết vì sao nó nằm trên Form chứ không
    /// phải trên control WebView2.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WMDragEnter:
            case NativeMethods.WMDragOver:
                // Ta chỉ đọc tệp để nén, không hề di chuyển — nên hiện con trỏ "sao chép".
                // Gọi DragQueryFile với chỉ số -1 để hệ điều hành chấp nhận lời gọi rồi báo
                // số mục; số mục bằng 0 thì không có gì để thả.
                var dropped = NativeMethods.DragQueryFile(new NativeMethods.HDROP(m.WParam), 0xFFFFFFFF, null, 0);
                m.Result = dropped > 0 ? (nint)NativeMethods.CopyEffect : 0;
                NotifyDropHover(true);
                return;

            case NativeMethods.WMDragLeave:
                NotifyDropHover(false);
                m.Result = 0;
                return;

            case NativeMethods.WMDropFiles:
                HandleDrop(new NativeMethods.HDROP(m.WParam));
                return;
        }

        base.WndProc(ref m);
    }

    private void HandleDrop(NativeMethods.HDROP drop)
    {
        try
        {
            var paths = new List<string>();
            var count = NativeMethods.DragQueryFile(drop, 0xFFFFFFFF, null, 0);

            for (var i = 0u; i < count; i++)
            {
                var length = NativeMethods.DragQueryFile(drop, i, null, 0);
                if (length <= 0) continue;

                // length là số ký tự, chưa tính NUL kết thúc.
                var buffer = new char[length + 1];
                if (NativeMethods.DragQueryFile(drop, i, buffer, buffer.Length) == 0) continue;

                var path = new string(buffer).TrimEnd('\0').Trim();
                if (path.Length > 0) paths.Add(path);
            }

            NotifyDropHover(false);

            // Ghi cả trường hợp 0 mục: đó là dấu hiệu thả vào sai chỗ hoặc cờ chưa bật, và
            // im lặng khiến rất khó chẩn đoán.
            Diagnostic.Log($"Keo tha: WM_DROPFILES den, {count} muc, doc duoc {paths.Count} duong dan.");

            if (paths.Count == 0) return;

            Diagnostic.Log($"Keo tha {paths.Count} muc: {string.Join("; ", paths)}");

            _ = SendAsync(BridgeJson.Serialize(new BridgeMessage
            {
                Event = "pathsDropped",
                Data = JsonSerializer.SerializeToNode(new { paths }, BridgeJson.Options),
            }));
        }
        catch (Exception ex)
        {
            Diagnostic.Log($"Không xử lý được kéo thả: {ex.Message}");
        }
        finally
        {
            // Bắt buộc giải phóng, nếu không con trỏ sẽ bị kẹt ở dấu cấm.
            NativeMethods.DragFinish(drop);
        }
    }
    private void NotifyDropHover(bool active) => _ = SendAsync(BridgeJson.Serialize(new BridgeMessage
    {
        Event = "dropHover",
        Data = JsonSerializer.SerializeToNode(new { active }, BridgeJson.Options),
    }));


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
    /// <summary>Handle của lớp cửa sổ con tạo bởi DragQueryFile.</summary>
    public readonly record struct HDROP(nint Value);

    public const int WMDragEnter = 0x02C3;
    public const int WMDragOver = 0x02C2;
    public const int WMDragLeave = 0x02C5;
    public const int WMDropFiles = 0x0233;

    private const uint DragDropEffectCopy = 1;

    /// <summary>
    /// Cờ <c>WS_EX_ACCEPTFILES</c> của <c>GetWindowLong(GWL_EXSTYLE)</c>.
    ///
    /// Là <b>0x00000010</b>. Dễ nhầm với <c>WS_EX_LAYERED</c> (0x00080000) — nhầm thì phần
    /// đo báo "không nhận thả" trong khi cờ đã bật đúng, và ta đi tìm một lỗi không có.
    /// </summary>
    public const int WSExAcceptFiles = 0x00000010;

    private const int GwlExStyle = -20;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    /// <summary>Đọc kiểu mở rộng của cửa sổ, dùng để kiểm tra cờ nhận thả.</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    public static long GetExtendedWindowStyle(IntPtr hWnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GwlExStyle).ToInt64() : GetWindowLong32(hWnd, GwlExStyle);

    /// <summary>
    /// Cửa sổ con đầu tiên, dùng để lần xuống HWND cháu của Chromium.
    /// <c>GW_CHILD</c> = 5: trả về cửa sổ con đầu tiên theo thứ tự Z, hoặc 0 nếu không có.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GWChild = 5;

    public static IntPtr FindFirstChild(IntPtr parent) =>
        parent == IntPtr.Zero ? IntPtr.Zero : GetWindow(parent, GWChild);

    /// <summary>
    /// Bật/tắt nhận thả cho một cửa sổ. Bật lên tức là thêm cờ <c>WS_EX_ACCEPTFILES</c>.
    ///
    /// <b>Nằm ở shell32.dll, không phải user32.dll.</b> Khai báo nhầm sang user32 sẽ ném
    /// <c>EntryPointNotFoundException</c> ngay lúc P/Invoke — và vì lời gọi nằm trong
    /// try/catch nên ứng dụng vẫn chạy bình thường, chỉ có cờ không được bật và kéo-thả
    /// im lặng không hoạt động. Đây đúng là loại lỗi không có triệu chứng nhìn thấy.
    /// </summary>
    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

    /// <summary>
    /// Đọc đường dẫn từ handle thả. <paramref name="index"/> = 0xFFFFFFFF (-1) trả về
    /// <b>số</b> mục thay vì độ dài chuỗi. Dùng mảng <c>char</c> chứ không dùng
    /// <c>StringBuilder</c>: P/Invoke với StringBuilder bị cảnh báo CA1838.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint DragQueryFile(HDROP hDrop, uint index, [Out] char[]? file, int size);

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern void DragFinish(HDROP hDrop);

    public static bool AcceptDrop(IntPtr hWnd, bool accept) => DragAcceptFiles(hWnd, accept);

    public static DragDropEffect CopyEffect => (DragDropEffect)DragDropEffectCopy;
}

/// <summary>Hiệu ứng thả tệp, theo đúng giá trị DROPEFFECT của Windows.</summary>
public enum DragDropEffect
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

