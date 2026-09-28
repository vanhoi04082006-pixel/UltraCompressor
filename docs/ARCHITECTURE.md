# Kiến trúc

## Tổng quan

```
┌─────────────────────────────────────────────────────────┐
│  wwwroot/  (index.html · styles.css · app.js)            │
│  Không framework, không bước build                        │
└───────────────────────┬─────────────────────────────────┘
                        │  window.chrome.webview
                        │  postMessage(JSON) hai chiều
┌───────────────────────▼─────────────────────────────────┐
│  MainForm.cs — WebView2, hộp thoại, DPI, chụp màn hình   │
│  Bridge/AppHost.cs — bảng lệnh, tuần tự hoá             │
└───────────────────────┬─────────────────────────────────┘
                        │  gọi hàm trực tiếp trong tiến trình
┌───────────────────────▼─────────────────────────────────┐
│  CompressionEngine — hàng đợi job, giới hạn song song,   │
│  tạm dừng, hủy, ETA                                     │
│  ┌───────────────────────────────────────────────────┐  │
│  │ IMediaPipeline: Image · Video · Audio · Gif · Pdf │  │
│  └───────────────────────────────────────────────────┘  │
│  FolderScanner · FileTransaction · UndoService           │
│  ToolLocator · ToolChain · MediaProbe · FileLogger       │
└─────────────────────────────────────────────────────────┘
```

## Vì sao chia vậy

**`UltraCompressor.Core` không biết gì về giao diện.** Không tham chiếu WinForms, không
tham chiếu WebView2. Toàn bộ test chạy trên đây. Nhờ vậy có thể kiểm thử engine bằng xUnit
thay vì phải dựng cửa sổ.

**`UltraCompressor.App` chỉ làm ba việc**: đặt WebView2, dịch `postMessage` thành lời
gọi hàm, và cung cấp các hộp thoại mà trang web không tự mở được (chọn thư mục, chọn
thư mục đích xuất kết quả).

**Cầu là danh sách lệnh đóng băng.** `AppHost.DispatchAsync` là một `switch` trên tên
lệnh. Trang web không thể gọi hàm tuỳ ý trên máy người dùng, và mọi lệnh đều đi qua một
chỗ để ghi nhật ký. Thêm lệnh mới là thêm một dòng vào `switch` cộng một hàm xử lý.

## Luồng nén một tệp

```
start(dryRun, outputFolder)
  └─ RunJobAsync(job)          ← TUẦN TỰ, theo thứ tự thêm vào
       └─ vòng lặp: mục nào chưa IsComplete
            ├─ await gate.WaitAsync(token)        ← tạm dừng
            ├─ await _limiter.WaitAsync(token)    ← giới hạn song song
            ├─ ProcessItemAsync
            │    ├─ File.Exists?                  ← không có thì bỏ qua
            │    ├─ MediaProbe.ProbeAsync         ← thời lượng, có tiếng, khung hình
            │    ├─ pipeline.Resolve(kind)        ← chọn pipeline theo đuôi tệp
            │    ├─ workspace.CreatePath()        ← tệp tạm trong data\tmp
            │    ├─ pipeline.RunAsync()           ← gọi ffmpeg/gifsicle/gs
            │    └─ quyết định dùng kết quả:
            │         NewSize >= OldSize  → giữ bản gốc  (NoSizeGain)
            │         tiết kiệm < ngưỡng → giữ bản gốc  (BelowMinSaving)
            │         dryRun           → xoá tệp tạm, chỉ ghi con số
            │         còn lại          → FileTransaction: .bak rồi thay thế
            └─ _limiter.Release()
```

## Ba quyết định đáng ghi

**Một mức nén, nhưng thông số thì tách theo loại media.** Vẫn chỉ có ba mức
(Light / Balanced / Strong) và một dropdown, nhưng `CompressionProfile` mang tham số
riêng cho từng loại: video CRF + preset, ảnh `-q:v`, audio bitrate, PDF preset, GIF
lossy + fps + tỉ lệ co. Chọn "Cân bằng" là đồng thời video CRF 23 **và** ảnh `-q:v 5`
**và** audio 192k **và** PDF `/ebook` **và** GIF lossy 40/20 fps.

Riêng **bề rộng tối đa phải tách ảnh với video** (`ImageMaxWidth` / `VideoMaxWidth`).
Trước đó cả hai dùng chung một `MaxWidth`, nên chọn "Mạnh" là cả ảnh lẫn video đều bị
bóp về 1080px. Sai về bản chất: ảnh nhìn toàn màn hình và có thể phóng to, còn video đã bị
giới hạn bởi khung hình mà mắt theo kịp.

**Job chạy tuần tự theo thứ tự thêm, song song bên trong một job.** `Task.WhenAll` khiến
mọi job cùng lúc — bản đồ hóa 20 tập phim thì 20 tiến trình ffmpeg, tốc độ tổng tụt và máy
nghẽn. Thứ tự là thứ tự chèn vào `_jobs` (danh sách chỉ có thêm vào, không sắp xếp lại).

**Mức nén được chụp lúc thêm thư mục, nên phải cho người dùng thấy.** Đổi dropdown *sau
khi* đã thêm thì job cũ vẫn nén bằng mức cũ. Đây là lý do người người dùng thấy kết quả
không như mong đợi mà không biết vì sao. Vì thế `JobDto` mang cả mức của job và cờ
`LevelDiffersFromCurrent`, giao diện tô cảnh báo và cho nút áp dụng mức hiện tại.

**Tệp tạm luôn nằm trong `data\tmp` của thư mục dự án, không nằm cạnh tệp gốc.**
Cùng một ổ đĩa thì việc ghi thêm không làm đầy ổ khác, và dọn `tmp` không đụng tới thư
mục người dùng.

**Tiến độ từng tệp đọc từ `-progress pipe:1`, không phải từ dòng log của ffmpeg.**
Dòng tiến độ (`frame=... time=...`) nằm trên **stderr** ở mức `info`, mà pipeline chạy
`-loglevel error` để không rác log — nên với cách cũ nó **không bao giờ được in**, và
`ParseTime` không bao giờ khớp, tiến độ đứng ở 0% tới khi xong. `-progress` ghi
`out_time_us` ra **stdout** dạng `key=value`, hoạt động ở mọi mức log. Tỉ lệ phần trăm
lấy từ thời lượng đã probe sẵn, không phải đoán từ log.

**Kéo-thả đã bỏ — và đừng thử thêm lần nữa.** Đã thử đủ các đường và đều thất bại trên
Windows hiện nay:

| Cách | Kết quả |
|---|---|
| `WM_DROPFILES` + `WS_EX_ACCEPTFILES` trên Form | Không tới. Explorer ngày nay luôn chạy kéo bằng OLE, chỉ bàn giao cho cửa sổ đã `RegisterDragDrop`. |
| OLE `IDropTarget` tự viết (`RegisterDragDrop`) | Đăng ký thành công nhưng không bao giờ được gọi. |
| `AllowExternalDrop = false` để WebView2 nhường quyền | Vô hiệu — WebView2 vẫn giữ vai trò drop target. |
| Sự kiện `CoreWebView2.DragDrop` | Không tồn tại trong SDK 1.0.4191.47. |

Hai phép thử tách bạch nguyên nhân, và cả hai đều bằng kéo chuột thật (UI Automation định
vị mục trong Explorer + `SendInput` di chuyển con trỏ):

- `WindowFromPoint` tại điểm thả trả về `Chrome_RenderWidgetHostHWND` thuộc tiến trình
  `msedgewebview2` — cửa sổ con đó nằm trên Form trong chuỗi cha.
- Một cửa sổ WinForms **trần**, không WebView2, bật đúng `WS_EX_ACCEPTFILES`, kéo chuột thật
  từ Explorer — cũng không nhận `WM_DROPFILES` nào.

Cùng phép kéo đó thả vào Notepad thì mở file bình thường, nên chuỗi kéo là đúng và lỗi nằm
ở phần tiếp nhận, không phải ở cách tạo kéo. Cơ chế cũ đã bị Windows bỏ hoàn toàn.

Thay vào đó: nút **Thêm thư mục** / **Thêm tệp** (`Ctrl+Shift+O` cho tệp lẻ). Giữ
`AllowExternalDrop = false` để WebView2 không tự mở tệp thả vào làm trắng màn hình.

**`MediaHost` phải trả `206 Partial Content` khi có header `Range`.** Chromium (kể cả
`<video>`) luôn gửi `Range` khi tua. Trả `200` cho mọi yêu cầu thì mỗi lần tua phải đọc
lại tệp từ byte 0 — đó là lý do màn hình so sánh giật so với VLC, vốn seek thẳng trên tệp.

**Không bao giờ ghi đè tệp gốc khi chưa có `.bak`.** `FileTransaction` tạo bản sao lưu
trước, rồi mới thay thế, và chỉ xoá `.bak` khi thay thế thành công xong. Nếu đứt giữa
chừng, tệp gốc vẫn còn nguyên.

**Tiết kiệm âm thì không dùng.** Đây là chốt chặn quan trọng nhất: tệp nén sẵn (đặc biệt
video) rất dễ bị mã hoá lại thành thứ lớn hơn. Ứng dụng so kích thước và giữ nguyên
bản gốc nếu không nhỏ hơn, thay vì giao cho người dùng một tệp tệ hơn.

## Điểm cần biết khi sửa

| Sửa ở đây | Phải kiểm tra |
|---|---|
| `DispatchAsync` | Thêm tên lệnh vào `CommandsWithoutResult` nếu hàm trả `null` |
| `PauseGate` | Không được để `WaitAsync` tiêu tốn khoá nào — xem `docs/CHANGELOG.md` |
| Thêm pipeline | Đăng ký trong `PipelineRegistry`, thêm mức trong `CompressionProfile` |
| Thêm định dạng | `MediaClassifier.Supported` + test phân loại |
| Đổi tham số nén | Cập nhật `docs/PHASE0-REFERENCE.md` để đối chiếu với bản gốc |

## DPI

Khung chủ khai báo `PerMonitorV2` và `app.manifest` gắn `dpiAwareness`. Cửa sổ con
WebView2 đo bằng đơn vị vật lý còn bề mặt vẽ bằng điểm ảnh thật, nên lệch đúng hệ số
1,25 trên màn hình 125%. `LogGeometryAsync` đo lại trong tiến trình và ghi vào nhật ký;
nếu thấy `vung client` khác `ClientSize` thì đừng tin con số ngoài tiến trình.

## Kiểm thử giao diện không cần chuột

Ba biến môi trường cho phép chụp và thao tác giao diện khi kiểm thử:

| Biến | Tác dụng |
|---|---|
| `UC_CAPTURE` | đường dẫn ảnh PNG; đặt rồi thì `CaptureIfRequestedAsync` chạy |
| `UC_CAPTURE_DELAY` | số giây chờ **trước** khi chạy script (mặc định 5) |
| `UC_EVAL_SETTLE` | số mili giây chờ **sau** khi chạy script (mặc định 1200) |
| `UC_EVAL_JS` | biểu thức JavaScript chạy trước lúc chụp, kết quả ghi vào nhật ký |

Hai điều cần biết về `UC_EVAL_JS`, cả hai đều đã tốn thời gian tìm ra:

1. **Script phải là biểu thức, không phải câu lệnh có `return` ở ngoài hàm.** `return` ở
   ngoài hàm là `SyntaxError` và im lặng không làm gì cả — dễ tưởng là hỏng ứng dụng.
   Dùng dạng `(() => { ...; return x; })()`.
2. **`ExecuteScriptAsync` không đợi promise.** Script `async` vẫn đang chạy thì ảnh đã được
   chụp. Muốn chụp đúng lúc job đang nén dở, hãy đặt `UC_EVAL_SETTLE` đủ lâu, hoặc tốt hơn
   là dùng cầu `log` để ghi kết quả ra nhật ký rồi đọc tệp log sau.

Chụp dùng `CapturePreviewAsync` của WebView2 chứ không chụp màn hình từ PowerShell, vì
cách sau bị Windows ảo hoá theo DPI và chỉ lấy được một phần cửa sổ.
