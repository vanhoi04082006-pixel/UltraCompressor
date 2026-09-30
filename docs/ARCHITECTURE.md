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

## Duyệt là xoá bản gốc — và "Hoàn tác" dùng trước khi duyệt

Trước đây bấm "Duyệt" **không bao giờ xoá gì**. Nguyên nhân nằm ở `UndoService.DiscardBackups(job, keepDays)`:
mặc định cấu hình giữ `.bak` là 30 ngày, nên nhánh

```csharp
if (keepDays > 0)
{
    if (age.TotalDays < keepDays) continue;   // <-- luôn đúng
}
```

**bỏ qua mọi tệp**. Người dùng bấm Duyệt, ứng dụng báo thành công, còn bản gốc nằm đó
chiếm chỗ. Với một tệp 526 MB thì phải đợi 30 ngày, hoặc bấm tay "Dọn bản sao lưu quá hạn",
mới giải phóng được. Đó không phải ý nghĩa của nút Duyệt.

### Ngữ nghĩa mới

| Thời điểm | Chế độ chạy thử | Chế độ nén thật |
|---|---|---|
| Sau khi nén | gốc nguyên trên đĩa, bản nén nằm trong `data\tmp` | bản nén đè lên gốc, gốc nằm ở `.bak` |
| Xem so sánh | hai bản cùng tồn tại | hai bản cùng tồn tại |
| **Hoàn tác** | bỏ bản nén tạm, gốc không đổi | khôi phục gốc từ `.bak`, xoá bản nén |
| **Duyệt** | thế bản nén vào chỗ, **xoá bản gốc** | **xoá `.bak`** |

Duyệt là chốt. Sau đó không còn lối quay lui, và đó là điều người dùng chọn khi bấm Duyệt —
họ đã xem kết quả ở hộp so sánh rồi. Muốn giữ lại thì bấm Hoàn tác **trước**, lúc đó cả
hai bản còn đầy đủ.

### Không còn `File.Copy` 526 MB

Ở chế độ chạy thử, bản trước tạo `.bak` bằng `File.Copy` — tức là tốn thêm một bản 526 MB
chỉ để giữ trong khoảng thời gian giữa lúc duyệt và lúc xoá. Nay `FileTransaction.CommitAndRelease`:

1. `File.Move(goc, goc + ".bak")` — trên cùng ổ đĩa đây là **đổi tên**: tức thì, 0 byte.
   Khác ổ đĩa thì mới rơi về `File.Copy`.
2. `File.Move(ketQua, goc, overwrite: true)`.
3. `File.Delete(goc + ".bak")` — giải phóng chỗ.

Bản gốc chỉ tồn tại giữa bước 1 và bước 3. Nếu bước 2 hỏng thì bản gốc được đưa về đúng
chỗ cũ, nên không mất gì cả.

`CommitAndRelease` cố tình **từ chối** khi đã có `.bak`: đó là lối quay lui duy nhất còn
lại, ghi đè nghĩa là mất khả năng hoàn tác về bản thật (bug B7).

### Lỗi xoá không tính là lỗi duyệt

Tệp đang được trình phát giữ thì `File.Delete` hỏng. Khi đó bản nén vẫn nằm đúng chỗ, nên
`CommitResult` tách `Applied` / `Released` / `Failed`: báo "đã duyệt nhưng chưa xoá được bản
gốc", chứ không phải "duyệt thất bại". Job vẫn sang `Committed`, và `.bak` còn đó nên vẫn
hoàn tác được — chỉ là chưa giải phóng được chỗ.

Sau khi xoá, `item.BackupPath` được đặt về `null`. Nếu không, nó trỏ vào một đường dẫn
không còn tồn tại và nút "Hoàn tác" báo nhầm là còn quay lui được. `HasBackup` và
`PendingBackups` đều tính trực tiếp từ đĩa nên nút tự ẩn theo.

### Đã bỏ tuỳ chọn "Giữ tệp .bak sau khi duyệt"

Không còn gì để giữ: Duyệt luôn xoá. Ngưỡng 30 ngày chuyển thành hằng số riêng cho nút
"Dọn tệp `.bak` rơi vãi" (`AppHost.StrayBackupDays`), và **cố ý không cho cấu hình** — nếu
để người dùng đặt ngưỡng nhỏ, nút dọn sẽ xoá luôn bản gốc của những tệp **đang chờ duyệt**,
tức là mất dữ liệu thật chứ không phải dọn rác.

Kiểm chứng trên clip cắt từ tệp thật:

```
1. CHAY THU + DUYET   goc 2,0 MB -> 0,9 MB, .bak khong con, thu muc chi con video.mp4
2. NEN THAT + HOAN TAC  .bak = goc 2,0 MB, hoan tac khoi phuc khop tuyet doi
3. NEN THAT + DUYET    .bak chiem 2,0 MB -> xoa sach, giai phong 2,0 MB
```

## Nén thật tự duyệt, không còn bước bấm "Duyệt"

Sau khi sửa nghĩa của nút Duyệt, còn một bước vô nghĩa nữa: ở chế độ nén thật, bản nén đã
thay bản gốc ngay lúc nén xong, bản gốc nằm trong `.bak` — nhưng người dùng phải bấm thêm
một cái nữa thì `.bak` mới bị xoá. Bấm cũng chẳng xem được gì, vì bản gốc đã bị dùng làm
`.bak` từ trước; mất đường lui rồi mới báo.

Nay `AutoApproveRealJobAsync` chạy khi job đi tới cuối bình thường:

| Chế độ | Kết thúc job | `.bak` |
|---|---|---|
| Nén thật, ghi tại chỗ | `Committed` | **xoá luôn, không cần bấm** |
| Chạy thử | `PendingReview` | không sinh `.bak`; giữ bản nén tạm để duyệt tay |
| Xuất thư mục khác | `Committted` | không sinh `.bak`; bản gốc nguyên vẹn |

Chạy thử giữ nguyên bước duyệt tay, vì đó mới là chỗ xem bằng mắt trước khi mất bản gốc.
Chế độ xuất trước đây cũng dừng ở "Chờ duyệt" với **0 tệp chờ** — bấm Duyệt không làm gì cả;
nay đánh dấu xong luôn.

### Cố ý xoá ở cuối job, không xoá trong vòng lặp từng tệp

Xoá ngay sau mỗi tệp thì sớm hơn, nhưng bấm Huỷ giữa chừng sẽ mất sạch đường lui: những tệp
đã nén xong rồi cũng không còn gốc để quay về. Xoá ở cuối job thì đường lui giữ nguyên suốt
lúc chạy, kể cả sau khi Huỷ. Đổi lại trong lúc chạy vẫn phải chịu dung lượng tạm — **đúng bằng
tình huống có nút Duyệt tay trước đây**, chỉ khác là không còn phải bấm nữa. Nếu việc giải
phóng chỗ sớm quan trọng hơn, đổi sang xoá trong vòng lặp là một dòng.

Điều kiện chặn: chỉ tự duyệt khi **thật sự có tệp nào đã thay thế** (`Items.Any(i => i.IsApplied)`).
Job nén thật mà hỏng hết thì không được báo "Đã ghi" — nó sẽ giống hệt lúc bấm Duyệt trên
một job không có gì để duyệt.

`CommitResult` tách `Applied` / `Released` / `Failed` nên nhật ký ghi rõ:

```
Tự động duyệt: xoá bản gốc của 37 tệp, không hoàn tác được nữa.
Muốn xem trước khi mất bản gốc thì bật chế độ Chạy thử.
```

Kiểm chứng trên clip cắt từ tệp thật, chạy cả engine lẫn ffmpeg thật:

```
1. NEN THAT          1,45 MB -> 0,43 MB  Committed  khong .bak  khong con tieu muc tam
2. CHAY THU          PendingReview         goc nguyen ven, khong .bak, con ban nen tam
3. XUAT THU MUC KHAC Committed             goc nguyen ven, khong .bak, co tep o thu muc dich
```

## Đóng cửa sổ khi đang xử lý — ba lựa chọn, và một lỗi tự duyệt lừa

Trước đây `MainForm` **không hề có `FormClosing`**. Bấm X là thoát thẳng, giữa lúc
ffmpeg đang chạy, không hỏi gì.

Nay bấm X khi đang xử lý sẽ hỏi:

| Lựa chọn | Hành vi |
|---|---|
| **Ở lại** | không thoát |
| **Hoàn tác rồi thoát** | khôi phục bản gốc mọi tệp đã nén xong, rồi thoát |
| **Lưu lại để chạy tiếp** | giữ kết quả, ghi phiên; lần sau có nút **Tiếp tục** |

Rảnh thì thoát luôn, không hỏi. Dùng form riêng (`ExitConfirmForm`) thay vì
`MessageBox` vì MessageBox chỉ có ba nút Yes/No/Cancel — đúng số lựa chọn nhưng nhãn
không nói được gì, và bấm "Yes" mà không biết mình vừa chọn xoá bản gốc.

### Lỗi nghiêm trọng: tự duyệt chạy đè lên hộp thoại

Hai chức năng này đụng nhau, và cái đặt sau phá cái đặt trước. Khi thoát, ta tạm dừng job
rồi chờ tệp đang chạy nốt — rồi job đó **kết thúc**, và `AutoApproveRealJobAsync` xoá sạch
`.bak`. Việc này xảy ra **trước khi người dùng kịp bấm chọn**. Hộp thoại lúc ấy bày ra
một lựa chọn đã không còn gì để chọn: bấm "Hoàn tác" thì báo 0 tệp.

Kiểm chứng bắt được đúng lỗi này — lần chạy đầu cho `khoi phuc 0 tep, 2 loi`, và kiểm
"còn .bak để hoàn tác" đỏ.

Cách sửa: `CompressionEngine.DeferAutoApprove`. Cửa sổ bật cờ **trước khi** tạm dừng;
job vẫn chạy nốt và kết thúc nhưng không tự duyệt, giữ `.bak` cho tới khi người dùng quyết.
Nếu họ chọn "Ở lại" thì `AbandonExitPreparation()` tắt cờ lại — nếu quên, cả phiên đó sẽ
không còn tự duyệt nữa.

Hệ quả phụ phải xử: job nén thật giữ `.bak` thì kết thúc ở "Chờ duyệt", mà engine chỉ
nhận "Chờ"/"Tạm dừng" khi bấm chạy. `MakeResumable()` đưa chúng về "Chờ", nên lần sau bấm
**Tiếp tục** là chạy được.

### Nút "Tiếp tục" dùng lệnh riêng, không dùng lệnh `start`

`start` ghi đè `DryRun` và `OutputFolder` của mọi job theo ô chế độ trên thanh công cụ.
Dùng nó để "Tiếp tục" thì một job đang chạy thử bị đổi sang ghi đè, và ngược lại. Nên có
`resumeJobs`: giữ nguyên chế độ đã lưu của từng job.

Phiên lưu sau **mỗi tệp**, nên ngay cả tắt máy cưỡng ép cũ còn dữ liệu để chạy tiếp.
`SessionStore.LoadAsync` đã tự hạ "Đang chạy"/"Tạm dừng" về "Chờ" và xoá cờ đang-xử-lý;
thiếu duy nhất là `AppHost.RestoreSessionAsync` gọi `LoadSessionAsync` — trước đó hàm này
**có sẵn mà chưa ai gọi**, tức là app ghi phiên ra đĩa rồi không bao giờ đọc lại.

Không tự chạy tiếp khi mở app: người dùng vừa mở app, bấm nhầm là chạy tiếp cả mấy chục
tệp rồi xoá bản gốc trước khi kịp nhìn. Nút "Bỏ qua" chỉ gỡ khỏi danh sách, không đụng
tệp trên đĩa.

### Trường "Đang xử lý"

Tên tệp đang nén đã có sẵn ở từng dòng bảng, nhưng khi nhiều job chạy song song thì phải
săn từng dòng. `.nowbar` gom tất cả tệp đang chạy về một thanh dưới thanh công cụ, mỗi tệp
kèm tên job để khỏi lẫn. Chấm nhấp nháy có `@media (prefers-reduced-motion)` tắt.

Kiểm chứng bằng engine + ffmpeg thật trên clip cắt từ tệp thật:

```
1. DONG GIUA CHUNG -> HOAN TAC
   1 tep xong, 1 tep dang chay -> giu duoc 2 tep .bak -> khoi phuc 2/2, 0 loi
   ca hai tep khop dung luong goc, khong con .bak
2. DONG GIUA CHUNG -> LUU LAI -> MO LAI -> CHAY TIEP
   giu ket qua, 2 tep .bak -> nap tu dia ra trang thai "Cho" -> chay tiep toi het
   het .bak sau khi tu duyet
```

## Cổng chất lượng: đo được thì mới dám nén

Trước đây cả pipeline không hề đo chất lượng: đọc metadata, đo SI/TI, lên một kế hoạch,
nén một lần, xong. "Chất lượng" chỉ là thứ giả định suy ra từ CRF.

Nay có `QualityProbe`: đo VMAF (kèm SSIM trong cùng một lượt) trên một đoạn đại diện,
và `QualityPolicy` đặt ngưỡng cứng theo mode.

### SSIM không dùng làm cổng — và đây là do đo, không phải do tài liệu

Ứng viên VMAF **72,08** (đã rõ là hỏng) vẫn cho SSIM **0,9887**. Ngưỡng SSIM kiểu đặc
tả 0,985 cho qua tới mức ứng viên tệ, tức là cổng không tồn tại. SSIM vẫn được đo và ghi
lại để chẩn đoán, nhưng `Accepts` chỉ nhìn VMAF.

Còn một ví dụ nữa, đo lúc dựng: ứng viên 1080p bị thu xuống 720p rồi so ở 1080p cho
VMAF 78,2 / SSIM 0,9972. Chỉ dùng SSIM thì coi như đạt và chấp nhận mất 56% chiều cao.

### P5 là bắt buộc, và khoảng cách mean − P5

Trên clip thật, `mean − P5` dao động 1,2…7 điểm, còn `min` thấp hơn nữa (hàng x265 CRF
30: mean 88,36, min 83,85). Chỉ nhìn mean thì một tệp vài cảnh hỏng vẫn đạt, vì đa số
khung còn lại đẹp. Ngưỡng vì thế là cặp, và `P5` thấp hơn `mean` khoảng 4 điểm —
đúng khoảng cách quan sát được.

### Model VMAF phải ghim, và bản dựng hiện tại không chạy được v1

Model v1 của Netflix (6/2026) cần 4 feature extractor. Bản ffmpeg 8.0.1 đóng kèm chỉ có
`cambi`; thiếu `speed`, `adm3`, `motion3`:

```
libvmaf ERROR could not initialize feature extractor "Cambi_feature_cambi_score"
```

Tải model v1 vào repo **không giải quyết được** — file đúng, binary thiếu feature. Nên
nay dùng `vmaf_v0.6.1neg` (bản dành cho tối ưu encoder) và `VmafModels` chỉ liệt kê
model **đã chạy thật**. Khi nào đổi bản dựng, chỉ cần bổ sung một mục và thả file vào
`tools/vmaf/`; bảng ngưỡng v1 đã viết sẵn trong `QualityPolicy` để bật lại ngay.

Vì vậy ngưỡng **không** chép từ đặc tả (bản đó viết cho v1, cho điểm cao hơn ở cùng mức
chất lượng). Số trong `QualityPolicy` rút từ lần quét thật, ghi đầy đủ ở
`docs/QUALITY-CALIBRATION.md`:

| Mode | mean | P5 |
|---|---:|---:|
| Nhẹ | 93 | 89 |
| Cân bằng | 89 | 85 |
| Mạnh | 84 | 80 |

### Ba điều kiện đo, sai là ra số vô nghĩa

1. **Hai luồng phải cùng kích thước.** Ứng viên bị thu nhỏ được phóng lại về đúng kích
   thước hiển thị của nguồn, để mất chiều không gian bị trừ điểm đúng như nó đáng bị trừ.
2. **Thứ tự là `[cái bị nén][bản gốc]`** và phép so không đối xứng. Cùng một cặp tệp,
   đảo thứ tự cho 78,18 và 86,63. Sai thứ tự thì mọi cổng chất lượng đảo ngược mà không
   báo lỗi. Kiểm bằng tính đơn điệu: ứng viên tốt phải điểm cao hơn, và khoảng cách
   phải rộng hơn (23,2 so với 15,0 khi đúng thứ tự).
3. **Phải tự tính P5 từ log JSON.** libvmaf chỉ in điểm gộp, mà điểm gộp không có phân
   vị thấp.

### Hai lỗi im lặng lộ ra khi hiệu chỉnh

Cả hai đều không báo lỗi, chỉ cho kết quả sai — loại lỗi tệ nhất.

**`log_path` không nhận đường dẫn tuyệt đối kiểu Windows.** Dấu `:` trong `C:` phá vỡ
cú pháp filtergraph, và bốn kiểu escape (`C\:/...`, trong dấu nháy đơn, gạch chéo ngược,
không escape) đều bị bỏ qua lặng lẽ: exit 0, không dòng lỗi, không có tệp log. Cách
đúng là cho ffmpeg chạy với thư mục làm việc là thư mục tạm và truyền tên tệp trần.
`ProcessRunner.RunAsync` nay có tham số `workingDirectory` cho việc này.

**`log_path` phải kèm `log_fmt=json`.** Không có nó, tệp tên đuôi `.json` vẫn nhận nội
dung XML và `JsonDocument.Parse` ném lỗi.

Ngoài ra một lần đo hỏng vì hết giờ ở cảnh chuyển động mạnh 1080p với x265 CRF 18; mốc
120 giây nâng lên 240.

### Phát hiện quan trọng nhất: thư viện này không nên nén lại

`bpppf` của hai tệp đo là **0,0105** và **0,0087** — ở 1080p24 tương đương khoảng
520 kbit/s cho cả tệp 30 phút. Nhưng ứng viên HEVC chất lượng tốt nhất trong bảng đo là
bpp 0,056 (CRF 18) và 0,023 (CRF 26); phải tới CRF 38 (VMAF 72, đã hỏng) mới ngang bằng
nguồn.

Nghĩa là **mọi mức CRF còn lại chất lượng đều cho tệp lớn hơn bản gốc**. Nguồn đã nén
sẵn tốt hơn bất cứ lần nén lại nào ở cùng độ phân giải. (HEVC vẫn thắng H.264 khoảng
50% ở cùng CRF — nhưng thua chính tệp nguồn.)

Kết quả đúng với thư viện này là **không nén gì cả**. Đây là quy tắc `RETURN_ORIGINAL`
trong đặc tả, và ở đây nó là yêu cầu chứ không phải tuỳ chọn: không có nó, công cụ sẽ
**làm phình** tệp của người dùng.

### Trạng thái

`QualityProbe` + `QualityPolicy` + `QualityLog` đã dùng thật và đo thật; 280 test. Chưa
nối vào `VideoPipeline` — hiện tầng đo chỉ chờ để vòng tìm nghiệm ở giai đoạn sau dùng.
