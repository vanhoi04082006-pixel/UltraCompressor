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

## Bốn quyết định đáng ghi

**Codec đầu ra phải theo nội dung, không theo mặc định.** Đây là chỗ một bảng thông số
cố định hỏng nặng nhất. Đo thật trên cùng một tệp quay màn hình (1918×1078, 30 fps):

| Tham số | Dung lượng | SSIM |
|---|---|---|
| H.264 CRF 28 | 563.609 byte | 0.99459 |
| HEVC CRF 33 | 574.949 byte | 0.98998 |

HEVC **to hơn 2% và kém hơn**. Đúng là HEVC gốc không sinh ra cho nội dung màn hình —
HEVC có phần mở rộng riêng cho loại đó (HEVC Screen Content Coding), vì khối 64×64 và
biến đổi dài của nó làm hỏng cạnh chữ sắc. Cùng phép đo trên anime thì ngược lại hẳn:
HEVC nhỏ hơn **52–62%**.

Vậy nên `CompressionPlanner` có trục thứ hai bên cạnh mức mục tiêu: **nhóm nội dung**,
lấy từ SI/TI của ITU-T P.910 (`ContentComplexityProbe`). Ba điểm lấy mẫu, mỗi điểm hai
khung liên tiếp ở 320×180 thang xám — tốn ~0.5 giây với tệp 24 phút, không cần giải mã
toàn bộ. Đo được: quay màn hình SI 122.8 / TI 0.02; anime SI 89–92 / TI 0–6.5. Ngưỡng
SI 110 nằm giữa, mỗi bên lệch hơn 10 điểm. Cần **cả hai** điều kiện mới gọi là màn hình,
vì anime hạn chế chuyển động cũng ra TI ~ 0.

**Lệch thang CRF giữa H.264 và HEVC là 5, và con số này từng sai.** Thang mặc định của
x265 là 28, của x264 là 23 — hai thang được thiết kế để cho cùng chất lượng. Bản đầu
đặt là `+2` do đoán, và nó làm HEVC **nén quá tay**: cùng dung lượng nhưng SSIM thấp hơn
H.264 ở mọi tệp đo. Đo lại: offset `+5` cho HEVC nhỏ hơn 52–62% với SSIM chỉ lệch ~0.003.

Kết quả ba mức sau khi sửa, chạy đúng lệnh mà planner sinh ra:

| Tệp | Nhóm | Codec | Nhẹ | Cân bằng | Mạnh |
|---|---|---|---|---|---|
| Quay màn hình | ScreenContent | `libx264` | 7.8% | 6.0% | 3.9% |
| Anime | FlatMotionless | `libx265` | 46.0% | 28.1% | 20.6% |
| Anime | FlatMotionless | `libx265` | 46.7% | 29.8% | 20.6% |

So với bản có offset sai: mức "Nhẹ" 9% → **46%**, mức "Mạnh" 63.5% → **20.6%**.

`ContentComplexity` có một cái bẫy đáng ghi lại: SI là **độ lệch chuẩn** của độ lớn
Sobel, nên một trường cạnh đều đặn (sọc 2px) cho SI = 0, y hệt ảnh phẳng. Ảnh thật không
bao giờ đều tăm tắp như thế, nhưng đó là lý do không được dùng SI một mình.

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

## Cửa sổ so sánh đứng hình — bốn nguyên nhân, đều đã sửa

Bấm "Phát cả hai" làm treo cửa sổ. Bằng chứng trong nhật ký:

```
12:09:44  MediaHost: trả 206 | 552110592/552110592 byte
12:09:44  MediaHost: trả 206 | 174081325/174081325 byte
```

Bốn nguyên nhân, tìm ra từ log rồi mới sửa:

1. **Range mở trả cả tệp.** Chromium hỏi `Range: bytes=0-` nghĩa là "cho tôi từ đầu",
   bản cũ hiểu thành "tới hết tệp". Nay `ByteRangeParser` cắt khối 2 MB, hợp lệ theo
   RFC 9110 (206 được phép trả hẹp hơn client yêu cầu, client media sẽ tự xin tiếp).
   Đo trên đúng cặp tệp đó: **726 MB → 4,2 MB, giảm 99,42%** byte đi qua UI thread.
   Range đóng không đổi một byte nào, nên không mất chất lượng phát.

2. **`WebResourceRequested` chạy trên UI thread** (`MainForm.cs`). Mọi yêu cầu byte-range
   đều mở file ngay tại đó, nên chỉ cần đẩy đủ nhiều byte là nghẽn hẳn. Sửa (1) làm cho
   lượng đẩy trên UI thread không còn đáng kể.

3. **Vòng tua không có hạn chót.** `setInterval` 100 ms, lệch > 0,15 s là tua, không bao
   giờ dừng. Khi ffmpeg đang chiếm CPU thì hai bên không bao giờ hội tụ, nên nó tua vô hạn
   — mỗi lần tua lại là một yêu cầu media mới trên UI thread. Nay giới hạn 8 lần, giãn
   dần 100 → 1800 ms, chỉ tua khi `readyState >= 2` và không đang tua, rồi báo rõ thay
   vì cứ tua tiếp.

4. **Đóng modal không dọn dẹp.** `closeModal` chỉ ẩn hộp thoại: không `pauseBoth()`, không
   xoá timer, không bỏ `src`. Video chạy ngầm và vòng tua tiếp tục sau khi đã đóng — mỗi
   lần mở lại chồng thêm một vòng. Đây là lý do hiện tượng **tích luỹ dần theo số lần
   bấm** chứ không xuất hiện ngay lần đầu.

`ByteRangeParser` được tách sang `Core` vì trước đó logic Range nằm trong `MediaHost` ở
tầng giao diện nên **không test được chút nào** — và sai thì biểu hiện thành "video không
phát", cùng triệu chứng với tệp hỏng. Nay có 21 test cho logic này.

## So sánh cạnh nhau bên ngoài: ffmpeg ghép, ffplay hiển thị

Kế hoạch ban đầu là "một ffplay với `-f hstack`". Chạy thật cho thấy **cả ba hướng đều
không dùng được**:

| Cách | Kết quả |
|---|---|
| `ffplay -f hstack` | `Unknown input format: hstack` — hstack là bộ lọc, không phải định dạng |
| `ffplay -filter_complex ...` | `Option not found` — đó là tuỳ chọn của ffmpeg CLI |
| `ffplay -vf "[0:v][1:v]hstack" -i a -i b` | `provided as input filename, but ... was already specified` |

Nguyên nhân gốc: **ffplay chỉ nhận một tệp.** Đó là giới hạn của chính ffplay.

Cách chạy được là hai tiến trình nối bằng pipe:

```
ffmpeg -i goc -i nen -filter_complex "[0:v][1:v]hstack=inputs=2[v]" -f nut -   |   ffplay -i pipe:0
```

Vẫn đạt đủ ba điều: một cửa sổ, hai bên cạnh nhau, và **đồng bộ tuyệt đối** vì chỉ có
một đồng hồ trong một pipeline. Ngoài ra nó chạy ngoài tiến trình ứng dụng nên không
tranh CPU với ffmpeg đang nén, và không đụng `WebResourceRequested`. Kiểm chứng trên clip
12 giây cắt từ đúng tệp của người dùng, cả ba kịch bản (cùng chiều cao / lệch chiều cao
/ không rõ kích thước) đều `exit 0`.

Hai điều kiện dễ sai đã ghi thành test:

- Mọi tham số phải nằm **trước** dấu `-` cuối. Đặt `-map` sau đó thì ffmpeg coi là output
  thứ hai và báo `Unable to choose an output format for 'pipe:1'`.
- `-t` của ffplay không dừng được tiến trình khi nguồn là pipe, nên cửa sổ chỉ đóng khi
  ffmpeg hết dữ liệu. Đây là hành vi mong muốn khi xem cả tập, nhưng bài kiểm chứng phải
  dùng clip ngắn thì mới kịp kết thúc.

`ffplay.exe` không có sẵn trong `app/` (chỉ có `ffmpeg.exe`), nên `"tools": {}` trong
`data/config.json` khiến nút "Mở ra ngoài" rơi về trình phát mặc định của hệ thống và
không làm được gì. Nay `ffplay.exe` nằm cạnh `ffmpeg.exe` (cùng bản build 8.0.1 full
static, SHA256 của ffmpeg hai bên trùng nhau) nên `ToolLocator` tự tìm thấy. Người dùng
vẫn có thể ghi đè bằng bản khác ở Cài đặt → Công cụ ngoài.
