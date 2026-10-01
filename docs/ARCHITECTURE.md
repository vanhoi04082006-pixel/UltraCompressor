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

## Giai đoạn 5A — lưới an toàn sau khi nén

Số đo ở giai đoạn trước cho thấy một vấn đề: bản nén tốt nhất còn lại chất lượng của clip
người dùng đều **lớn hơn bản gốc**. Không có lưới chặn nào, công cụ sẽ làm phình tệp.

Trước 5A, engine đã có sẵn hai phép so kích thước thô (`NoSizeGain`, `BelowMinSaving`) và
`MinSavingPercent` đã được dùng. Phần **thật sự còn thiếu** là đo chất lượng. Nay gộp cả
ba vào một chỗ quyết định duy nhất: `QualityGate`.

### Thứ tự kiểm tra là cố ý: kích thước trước, chất lượng sau

So kích thước tốn không một mili giây; đo VMAF thì tốn một tiến trình ffmpeg. Với tệp mà
bản nén đã lớn hơn thì đo chất lượng cũng vô nghĩa — dù đạt VMAF 99 thì người dùng vẫn
mất dung lượng, nên bỏ luôn.

### Không đo được thì KHÔNG loại

Đo hỏng (thiếu `libvmaf`, quá giờ, ffmpeg lỗi) trả về null, và null **không phải** dữ liệu
để loại tệp. Nếu coi là thất bại thì mọi tệp video sẽ bị bỏ khi công cụ hỏng, và người
dùng không nén được gì mà không hiểu vì sao. Cùng lý do: không đo được kích thước hiển thị
thì bỏ qua nhánh đo, và loại media không phải video thì không dựng chuỗi filter giả.

Chỉ video mới đo được. Ảnh/GIF/âm thanh/PDF chỉ còn lưới kích thước — không giả vờ có
metric cảm nhận.

### Mã lý do tách khỏi thông báo

`JobItem.DecisionReason` mang mã ổn định (`OUTPUT_LARGER_THAN_SOURCE`,
`INSUFFICIENT_SIZE_SAVING`, `QUALITY_FLOOR_NOT_MET`, `SOURCE_ALREADY_EFFICIENT`,
`ACCEPTED`), còn `Message` là tiếng Việt cho người dùng. Thông báo sẽ đổi theo thời
gian; mã thì phải giữ nguyên để gom số liệu — ví dụ "sau khi nâng ngưỡng, bao nhiêu tệp
rơi vào `QUALITY_FLOOR_NOT_MET`".

`SOURCE_ALREADY_EFFICIENT` đã khai báo nhưng **chưa dùng tới**: ở 5A kết luận đó chỉ
suy ra được sau khi đã nén. Nó là kết luận của 5B, khi `ORIGINAL` thành ứng viên ngang
hàng.

Cả ba trường mới (`QualityP5`, `DecisionReason`, và `QualityScore` đã có sẵn) đều được
lưu vào `SessionStore.Project` — trước đó `QualityP5` và `DecisionReason` rơi mất khi
đóng ứng dụng.

### Ngưỡng không ngồi rải trong mã

`MinSavingPercent` đã nằm trong cấu hình và **đã được dùng** trước đây; 5A không thêm
hằng số ma. `QualityCheckEnabled` và `QualityCheckWindowSeconds` cũng nằm trong
`AppConfig`. `QualityCheckWindowSeconds` mặc định 3 giây là **chỗ dừng tạm**, ghi rõ là
vậy trong mã: chưa có số liệu đo để biết đo bao nhiêu là đủ.

Không đặt ngưỡng tiết kiệm phụ thuộc mode ở 5A, vì chưa có số đo chứng minh con số nào
hợp lý. Việc đó thuộc 5B.

### Kiểm chứng trên tệp thật

```
1. Clip anime đã nén sẵn (copy nguyên 1,44 MB)
   -> giữ nguyên bản gốc, VMAF 68,17, QUALITY_FLOOR_NOT_MET, 0 tệp tạm sót

2. Clip bị ép cứng 10,18 MB, chế độ Cân bằng
   -> giữ nguyên bản gốc, VMAF 85,97 / P5 83,28, QUALITY_FLOOR_NOT_MET

3. Clip nén sạch 5,26 MB, chế độ Mạnh
   -> giữ nguyên bản gốc, VMAF 78,95 / P5 74,89, QUALITY_FLOOR_NOT_MET
```

Invariant `NewSize <= OldSize` được kiểm bằng test duyệt hết tổ hợp 6×6 cặp kích thước,
và kiểm lại trên cả ba tệp thật.

### Kết quả này phải nói thẳng: hiện tại KHÔNG nén được gì

Cả ba tệp đều bị từ chối. Ở chế độ Cân bằng, ứng viên của bộ lập kế hoạch hiện tại đo ra
VMAF 85,97 — dưới ngưỡng 89. Nói cách khác, **bộ lập kế hoạch đang nén mạnh hơn mức ngưỡng
chất lượng cho phép**.

Đây là hệ quả đúng như mong đợi của 5A: nó chặn hại trước, rồi 3 và 4 sẽ sửa chỗ nén quá
tay bằng cách **tìm** tham số thoả ngưỡng thay vì đoán một lần. Không sửa bằng cách nới
ngưỡng — làm vậy là bịa số đo.

Cho tới khi 3 và 4 xong, hành vi đúng của ứng dụng là: **giữ nguyên tệp và nói rõ vì sao**.

## Giai đoạn 2 — RepresentativeWindowSelector: đoạn đại diện chọn theo nội dung

Trước đây đo chất lượng lấy một đoạn **ở giữa tệp**. Với tệp mà đầu là cảnh tĩnh và
giữa là cảnh cháy thì đo giữa thì hỏng, đo đầu thì qua — cả hai đều không đại diện cho
tệp. Nay đoạn đo do nội dung quyết định.

### Số đo quyết định thiết kế: seek thưa, không quét cả timeline

Đo trên tệp 30 phút của người dùng:

| Cách | Thời gian |
|---|---:|
| Giải mã toàn bộ, **không filter** | 93,2 s |
| Giải mã toàn bộ + mọi filter | 111,8 s |
| **Một cửa sổ 2 s qua seek** | **0,29–0,48 s** |

Giải mã chiếm gần hết thời gian, nên quét cả timeline đắt hơn seek **khoảng 200 lần**, dù
chỉ mở một tiến trình. Vì vậy `TimelineScanner` chạy N lần seek thưa, mỗi lần một cửa
sổ ngắn:

```
-ffmpeg -ss <vị trí> -t 2 -i <tệp>
  -vf "fps=4,scale=320:180:flags=bilinear,format=gray,
       scdet=threshold=10,signalstats,entropy,blurdetect,
       metadata=print:file=scan-<id>.txt"
  -f null -
```

Năm tín hiệu lấy được trong **một lượt**: `normalized_entropy.normal.Y` (chi tiết, ffmpeg
đã trả trên [0,1] nên không phải tự chế công thức), `signalstats.YDIF` (chuyển động),
`scdet.score` (đổi cảnh), `blur` (độ sắc — đảo chiều vì nhỏ nghĩa là nhiều cạnh),
`signalstats.YAVG` (độ sáng). Số mẫu tăng theo **log thời lượng**, không tuyến tính.

Đo thật: 18 mẫu / 8,1 s cho tệp 30 phút, tức **0,27 giây mỗi phút nội dung** — so với
vài phút cho một lần nén thật.

### Tách scanner khỏi selector là điều kiện để kiểm thử được

`TimelineScanner` là I/O với ffmpeg. `RepresentativeWindowSelector` là thuần toán, không
mở tệp, **không biết ứng viên nào sẽ được encode** — đúng như yêu cầu cho giai đoạn sau
tái sử dụng. Nhờ vậy test được dựng bằng đặc trưng tổng hợp, không cần ffmpeg, và chạy
trong 100 ms.

### Vai trò, không phải top-N

Sắp theo một điểm tổng rồi lấy 3 cái trên cùng gần như chắc chắn rơi vào cùng một
cảnh. Chọn theo vai trò buộc các đoạn phải khác nhau về bản chất: `Typical`,
`HighMotion`, `HighSpatial`, `LowComplexity`. **Vai trò không có thật thì không tạo ra** —
tệt gần như tĩnh không có `HighMotion`.

### Hai lỗi thuật toán mà test bắt được

**Chuẩn hoá min-max khuếch đại nhiễu thành biến thiên thật.** Tệp gần tĩnh có YDIF dao
động 0,18–0,22, chuẩn hoá ra `[0,50 … 1,00]` và sinh ra một đoạn "chuyển động cao" hoàn
toàn vô nghĩa. Đo lại trên tệp thật: `YDIF` chạy từ **0,00** (cảnh tĩnh) tới **23,54**
(cảnh bận), và sàn đo của chính ffmpeg với cảnh tĩnh là đúng 0.

Sửa bằng cách tách hai loại câu hỏi: **thứ tự** xếp hạng bằng chuẩn hoá tương đối, còn
**vai trò có tồn tại không** phải có mốc tuyệt đối (`MotionAbsoluteFloor`, mặc định 1,0
trên thang chênh luma 0–255 ≈ 4% dải đo được). Con số này đã ghi trong mã là **tạm** và
cần mở rộng tập mẫu.

**`Typical` chiếm mẫu rồi chặn mất mẫu khó nhất.** Ở một fixture, `Typical` lấy mẫu ở
giây 10; mẫu chuyển động cao nằm ở giây 30, cách 20 s — dưới ngưỡng 30 s — nên
`HighMotion` bị loại và rơi về một mẫu tầm thường. Tức là đo đoạn dễ thay vì đo đoạn khó.

Sửa bằng thứ tự chọn **cực đại trước, trung bình sau**: một vai trò cực đại có đúng một
ứng viên đáng chọn, còn vai trò "điển hình" có cả một mảng ứng viên gần như ngang nhau.

### Khoảng cách tối thiểu phải co theo thời lượng

Một tệp 60 giây không thể chia ra ba đoạn cách nhau 30 giây. Mốc cố định khiến bộ chọn
âm thầm trả về **ít hơn số đoạn yêu cầu mà không kèm lý do**. Nay mốc thực tế là
`min(cấu hình, thời lượng / (số đoạn + 1))`.

### Chuẩn hoá tương đối, và cái giá của nó

Mọi đặc trưng chuẩn hoá theo min/max của chính các mẫu trong tệp đó. Không cần hằng số
tuyệt đối nào và không suy ra ngưỡng từ tập hiệu chỉnh hiện có.

Đánh đổi, ghi rõ trong mã: một tệp đều khó sẽ trông "đa dạng" như mọi tệp khác. Điều đó
đúng vì câu hỏi ở đây là *"đoạn nào khó **trong tệp này**"*. Câu hỏi *"tệp này khó đến
đâu"* thuộc ứng viên ở giai đoạn sau, và cần corpus rộng hơn.

### Kết quả trên tệp thật

```
Aku no Onna Kanbu - 01.mp4   (30,0 phút)  18 mẫu  8,1s  0,27 s/phút
  HighSpatial @ 566,6s   không gian 1,00 · chuyển động 0,27 · đổi cảnh 0,14
  HighMotion  @ 947,9s   không gian 0,77 · chuyển động 1,00 · đổi cảnh 0,65
  Typical     @ 1138,6s  không gian 0,69 · chuyển động 0,38 · đổi cảnh 0,26

Boku no Risou no Isekai Seikatsu - 01.mp4  (16,5 phút)  17 mẫu  6,2s  0,38 s/phút
  HighMotion  @ 49,6s    không gian 0,81 · chuyển động 1,00 · đổi cảnh 1,00
  Typical     @ 272,9s   không gian 0,89 · chuyển động 0,00 · đổi cảnh 0,00
  HighSpatial @ 831,0s   không gian 1,00 · chuyển động 0,29
```

Khớp với số đo thô: mẫu chuyển động cao nhất của tệp anime nằm ở **948 s** (`YDIF` 43,90),
mẫu nhiều chi tiết nhất ở **567 s** (entropy 0,942) — và selector chọn đúng hai mẫu đó.

**Vị trí chia đều cũ (25%/50%/75%) là 450/900/1350 s. Selector chọn 567/948/1139 s —
không đoạn nào trùng.**

Tệp thứ hai cho thấy bộ chọn bám nội dung: `Typical` rơi vào đoạn có chuyển động 0,00
(cảnh tĩnh giữa tệp) chứ không phải đoạn ở giữa thời lượng.

### Dự phòng và dọn dẹp

Tệp hỏng, quét hỏng, không đọc được mẫu nào → rơi về vị trí chia đều, vẫn trả đủ số đoạn
để job chạy tiếp. Đo trên tệp hỏng: **0,7 s**, không ném lỗi. Tệp quét tạm bị xoá trong
`finally` ngay sau mỗi mẫu — kiểm chứng 0 tệp sót.

321 test. Không nới ngưỡng VMAF, không sửa CRF, không đụng ứng viên.

## Giai đoạn 3 — CandidatePlanner: sinh nhiều ứng viên thay vì một

### Vấn đề: một tệp, một bộ tham số

`CompressionPlanner` cũ sinh đúng một ứng viên: `BaseCrf(goal)` rồi áp cho mọi codec,
`WidthCap(goal, kind)` cho độ phân giải, `PresetFor(goal)` cho preset. Ba hàm đó là
hình mẫu đúng cái **không** được làm, nên chúng ở lại trong mã và ghi rõ là legacy —
không phải để dùng, mà để so sánh.

Nói thẳng điều khó chịu nhất về cách làm cũ: `BaseCrf(goal)` trả cùng một con số cho
x264, x265 và AV1. Ba thứ đó **không cùng đơn vị**. "Mạnh = 28" nghĩa là CRF 28 của
x264, 28 của x265 và 28 của libaom — ba mức chất lượng khác nhau. Bất kỳ ai đọc
`BaseCrf` cũng dễ tin rằng đó là một thang chung, và đó là lý do mã này nguy hiểm hơn
trông thấy.

### Ranh giới: một miền tìm kiếm cho mỗi codec

`IEncoderSearchDomain` là điểm mấu chốt. Mỗi codec có miền riêng, và miền **không nhận
miền số của codec khác**:

| Miền | Encoder | Tham số chất lượng | Công tắc preset | Trạng thái |
|---|---|---|---|---|
| `X264SearchDomain` | `libx264` | 0–51 | `-preset` | luôn thử |
| `X265SearchDomain` | `libx265` | 0–51 | `-preset` | luôn thử |
| `LibaomAv1SearchDomain` | `libaom-av1` | 0–63 | `-cpu-used` | tắt mặc định |

Miền AV1 **không** khai báo miền bằng `new MaxQuality => 63` mà bằng `override`. Nếu
khai báo `new`, giao diện vẫn trả miền của lớp cha (`0–51`) và mọi ứng viên AV1 hợp lệ
sẽ bị chặn oan mà không có dấu hiệu gì. Có test riêng gọi qua giao diện để chặn đúng lỗi
này.

Tên lớp ghi rõ `Libaom` chứ không phải `SvtAv1`. SVT-AV1 là encoder khác hẳn, dùng thang
QP chứ không phải CRF; một lớp tên "SvtAv1" gắn với `libaom-av1` sẽ khiến người đọc
tưởng công cụ đã hỗ trợ SVT-AV1 trong khi chưa. Thêm SVT-AV1 sau cần một lớp riêng.

### Ba giá trị phải đo, không được tin tài liệu

Bảng trên không lấy từ tài liệu. Đo trên `ffmpeg 8.0.1-essentials` đi kèm, và **ba
giả định ban đầu đều sai**:

**1. `-cpu-used` của libaom chạy 0–8, không phải 0–9.** `-cpu-used 9` bị từ chối:
`Value 9.000000 for parameter 'cpu-used' out of range [0 - 8]`. Con số 9 là thói quen từ
tài liệu SVT-AV1. Nếu giữ, ứng viên ngân sách Nhanh chỉ lộ lỗi khi encode thật.

**2. x265 từ chối CRF ≥ 52, còn x264 thì không.** Trần 51 cho cả hai là **chọn có chủ
đích**, không phải đặc tính đo được của cả hai. Việc lớp bọc ffmpeg của x264 không chặn
không phải bằng chứng rằng 63 là một CRF có nghĩa. Ngoài 51 thì tệp ra thường đã lớn hơn
nguồn, tức ứng viên vô dụng — nên kẹp. Sàn 0 vì cả hai chấp nhận `-1`, nhưng -1 là chế
độ lượng tử hằng, không phải chất lượng hằng.

**3. Tham số chất lượng là số nguyên.** Cả ba encoder đều khai báo `<int>`, nên mọi điểm
được làm tròn ở **một chỗ duy nhất** ngay khi rời miền. Trước đó log in ra
`16.64029999423075`, ID mang theo, và lệnh ffmpeg nhận đúng con số đó.

### Lỗi tệ nhất: `-preset` bị bỏ qua âm thầm

`Preset()` trước đây trả về `"cpu-used=9"` — một mảnh cú pháp dòng lệnh chứ không phải
tên preset. Nếu giai đoạn dựng lệnh ghép thành `-preset cpu-used=8`, ffmpeg **không báo
lỗi**:

| Cách truyền | Thời gian | Kích thước |
|---|---|---|
| `-cpu-used 8` | **2,05 s** | 172,9 KB |
| `-preset "cpu-used=8"` | 151,8 s | 124,8 KB |
| không truyền gì (mặc định = 1) | 154,3 s | 124,8 KB |

Tệp ra **giống hệt** mặc định, và chậm hơn **74 lần**. Với tệp 40 phút, một ứng viên
"Nhanh" sẽ mất hơn 20 giờ thay vì 16 phút, và người dùng chỉ thấy nó "thành công". Đây là
dạng lỗi tệ nhất: thất bại mà trông như thành công.

Vì vậy miền tách **công tắc** khỏi **giá trị**: `PresetSwitch` (`-preset` / `-cpu-used`)
và `Preset()` trả về thẻ trần. Test chặn giá trị chứa dấu `=` hoặc khoảng trắng, và
chặn việc hai miền dùng chung công tắc.

### Mode dịch vị trí vùng tìm, không gán thang chung

Mode chỉ dịch **tâm vùng tìm** trong một băng hẹp ±6 quanh mặc định của chính encoder đó
(x264 23, x265 28, libaom 32), rồi kẹp vào miền. Băng hẹp vì mode là ý định chất lượng,
còn miền là giới hạn kỹ thuật.

Còn preset do **ngân sách tính toán** quyết định, hoàn toàn tách mode. Đó là ranh giới
giữa "chất lượng mong muốn" và "tiền bạc và thời gian được phép tiêu".

### Hạ độ phân giải phải mã hoá kỹ hơn, không phải giữ nguyên tham số

Có một lỗi thiết kế dễ bỏ qua: nếu mọi nhánh độ phân giải dùng chung một tập tham số chất
lượng, thì nhánh nhỏ hơn sẽ hỏng ngưỡng chất lượng và tốn công encode vô ích. Hạ 4K xuống
1440p mà giữ nguyên tham số là mất rất nhiều chi tiết cảm nhận.

Nên vùng tìm dịch theo số lần giảm một nửa số pixel, dùng `log2`:

```
bước chất lượng = số lần giảm một nửa số pixel × 4
```

**Chiều** là chắc chắn và kiểm chứng được không cần đo: nhánh nhỏ thì mỗi pixel phải
được mã hoá kỹ hơn. **Độ lớn** 4 là chưa có số đo nào trong kho, nên được đánh dấu
`Uncalibrated` ngay trong tên hằng số. Đây không phải ngưỡng chất lượng và không được
dùng để kết luận ứng viên nào đạt — việc đó thuộc `QualityProbe` ở giai đoạn sau.

### Câu hỏi mở: mật độ bit dịch vùng tìm theo chiều nào

`ContentBias` trước đây dịch vùng tìm theo mật độ bit, kèm comment giải thích **ngược
chiều với đoạn code ngay bên dưới**: comment nói nguồn hết dự trữ thì "nén nhẹ hơn",
còn code lại hạ tham số chất lượng — tức nén *nặng* hơn.

Hai hướng đều nghe hợp lý:

- **hướng "nén mạnh"**: nguồn đã không còn chi tiết, giữ chất lượng cao cũng không thu
  được byte nào, chỉ tốn dung lượng;
- **hướng "nén nhẹ"**: nguồn đã bị nén đến mức hạt nhiễu lộ lên, nén thêm sẽ hỏng hình.

Không có số đo nào trong kho để chọn giữa hai hướng, nên thay vì tung đồng xu, **mật độ
bit bị loại khỏi vị trí này**. Nó vẫn được giữ trong hồ sơ nguồn để giai đoạn tìm kiếm và
giai đoạn hiệu chỉnh dùng, và câu hỏi được ghi lại trong mã nguồn.

Còn tín hiệu **độ khó nội dung** thì giữ, vì chiều của nó kiểm chứng được mà không cần đo
chất lượng: nhiều chuyển động nghĩa là nhiều khối phải dựng lại mỗi khung hình, và đó là
nơi hạt và nhấp nháy lộ ra đầu tiên khi siết tham số.

### Ứng viên phải dựng được thành lệnh ffmpeg

`Reason` trước đây in ra **chỉ số điểm** ("chất lượng = 2/3") thay vì tham số thật, và ở
nhánh nguồn không in kích thước cụ thể. Cả hai khiến log không dùng để dựng lại lệnh
được. Nay mỗi ứng viên tự mô tả trọn:

```
libx264 · giữ nguyên độ phân giải nguồn 1920x1080 · điểm dò thô · -preset medium -crf 22
```

### Tập ứng viên có cấu trúc, không phải danh sách phẳng

Ứng viên nhóm theo **nhánh** (codec × kích thước). Điểm đầu mỗi nhánh là
`CoarseProbe` — điểm dò thô; các điểm sau là `QualityAnchor` để giai đoạn tìm kiếm
khoanh biên. Đây là cấu trúc giai đoạn tìm kiếm cần, không phải chi tiết trang trí.

`CandidateOrigin` chỉ mô tả **vai trò tìm kiếm**. Việc một ứng viên có thuộc nhánh giữ
nguyên độ phân giải nguồn hay không nằm ở `BranchId` và kích thước. Trộn hai ý nghĩa đó
vào một kiểu đã sinh ra `Origin = nhánh[0] ? SourceResolution : SourceResolution` — một
mệnh đề đúng vô nghĩa, khiến `Origin` không bao giờ là `CoarseProbe`, tức giai đoạn tìm
kiếm không biết đâu là điểm dò thô.

### Kết quả trên ba hồ sơ nguồn

Cùng BALANCED, ngân sách Normal, x264:

| Hồ sơ nguồn | Nhánh hình | Điểm chất lượng x264 |
|---|---|---|
| 4K 60fps, nội dung bận | 3840x2160 | 21 / 17 / 13 |
| | 2560x1440 | 17 / 13 / 9 |
| 1080p 24fps | 1920x1080 | 23 / 19 / 15 |
| | 1280x720 | 18 / 15 / 11 |
| 720p 25fps | 1280x720 | 22 / 18 / 14 |
| | 852x480 | 18 / 14 / 10 |

Cùng một mode và cùng một ngưỡng, ba nguồn ra ba tập ứng viên khác nhau. Đó là thứ phân
biệt một bộ lập kế hoạch thật với một bảng preset — và là thứ mà `BaseCrf(goal)` không
bao giờ làm được.

Kiểm trên 348 ứng viên (3 hồ sơ × 3 mode × 3 ngân sách): 0 ứng viên vượt kích thước
nguồn, 0 sai fps, 0 ngoài miền codec, 0 tham số có phần thập, 0 trùng trong cùng plan.

Thiếu codec thì báo rõ thay vì im lặng: bỏ `libx265` khỏi ffmpeg → 6 ứng viên, ghi rõ
bỏ `libx265` và `libaom-av1`; không còn codec nào → 0 ứng viên kèm ghi chú.

### 380 test. Chưa động vào đường ống nén

`CandidatePlanner` **chưa** được nối vào engine. `CompressionPlanner` cũ vẫn là thứ
được gọi, và được ghi rõ là legacy. Việc thay thế thuộc giai đoạn 4 — cùng lúc đó mới
cần tới giá trị `PresetSwitch`/`Preset`/`QualitySwitch` ở đây để dựng lệnh.

Không nới ngưỡng VMAF, không đổi `QualityPolicy`, không sửa CRF, không nối engine.

## Giai đoạn 4A — Linh kiện lõi của Pilot Search (CHƯA nối runtime)

Năm thành phần độc lập, deterministic, kiểm thử được. **Production runtime vẫn chạy
`CompressionPlanner` legacy.** `candidate_planner_runtime_active = false`.

### Nguyên tắc bất di bất dịch

> Planner đề xuất. Measurement quyết định.

`CandidatePlanner` sinh ra các ứng viên mà **không biết** ứng viên nào đạt. Không thành
viên nào được phép tự tuyên bố mình có chất lượng tốt — đó là việc của `QualityProbe`
đo được.

### Thứ tự trách nhiệm

```
EncodeTarget.TryFromRequest   chặn phóng to, méo tỉ lệ, kích thước lẻ
        ↓
EncodeTransform               một nơi duy nhất quyết định hình được tạo ra thế nào
        ↓
PilotEncoder                  encode CHỈ các đoạn đại diện
        ↓
ReferenceWindowExtractor      cắt tham chiếu thành clip, dùng chung mọi ứng viên
        ↓
QualityAggregator             gộp nhiều đoạn, theo hướng bảo thủ
        ↓
SizeEstimator                 ước lượng, chỉ để xếp hạng
        ↓
ParetoSelector                loại ứng viên bị áp đảo
```

`PilotSearch` (giai đoạn 4B) sẽ điều phối, **không** nhét orchestration vào bất kỳ tầng
nào ở trên.

### Sai lầm lớn nhất tìm được: VMAF gần như không nhạy lệch nửa khung, nhưng sụp khi lệch một khung

Đo trên bốn nguồn tổng hợp, 1280×720 @ 25 fps, tự so với chính nó:

| Lệch | VMAF |
|---|---|
| 0,00 s | 99,2 – 100,0 |
| 0,02 s (nửa khung) | 98,8 – 99,2 |
| **0,04 s (một khung)** | **21,2 – 68,7** |

Trên tệp anime thật của dự án, cùng **một** ứng viên cho mean **41,3** theo cách seek thẳng
vào nguồn, và **94,0** khi cắt cả hai thành clip. Chênh 2,3 lần, không phải vì chất lượng
mà vì đọc lệch khung hình.

Hai nguyên nhân, cả hai đều là lỗi của ta:

**1. Tham chiếu và ứng viên lấy bằng hai đường seek khác nhau.** Nên `ReferenceWindowExtractor`
cắt tham chiếu thành clip bằng **đúng cấu trúc lệnh** của `PilotEncoder`, rồi đo clip đối
clip tại `(0, d)`. Điểm khung hình đầu tiên trùng nhau *theo cách xây dựng*, không phải nhờ
một con số thật phân nào khớp — con số đó phụ thuộc bản ffmpeg và cấu trúc khung hình tệp.
Cắt **một lần, dùng chung cho mọi ứng viên**: các ứng viên chỉ khác ở phần mã hoá, mà đoạn
tham chiếu thì giống nhau. Tham chiếu mã hoá **không tổn thất** (CRF 0) để nó mang đúng
pixel gốc; mã hoá tổn thất ở tham chiếu sẽ cộng sai số giống nhau vào mọi ứng viên, và sai
số đó khác nhau theo độ dễ của nội dung — tức làm méo chính phép so sánh.

**2. `EncodeTransform` cứ dựng `fps=23.98` cho một nguồn 23,976 fps.** Comment nói là không
dựng, code thì có. Chênh 0,004 fps buộc ffmpeg lặp hoặc bỏ khung hình, và ứng viên lệch
trục thời gian với tham chiếu. Nay `fps=` chỉ được dựng khi chênh lệch vượt `FpsEpsilon`.

Cả hai lỗi đều **âm thầm**: ffmpeg trả mã 0, log trông bình thường, và mọi ứng viên đều bị
loại oan. Không có gì báo động.

### Gộp chất lượng: bảo thủ tuyệt đối

Ứng viên khả thi **chỉ khi mọi** đoạn đo được đều đạt cả ngưỡng mean lẫn ngưỡng P5.

Ví dụ ở BALANCED (89/85): đoạn A 94/91 đạt, đoạn B 88/84 rớt, đoạn C 95/92 đạt. Trung bình
là 92,3 — đẹp. Nhưng B là cảnh khó, và người dùng sẽ thấy đúng cảnh đó bị hỏng. Trung
bình là cách nhanh nhất để che một sự cố.

**Không đo được thì không phải đạt.** Đây là khác biệt lớn nhất so với lưới chất lượng cuối,
vốn cố tình fail-open để người dùng vẫn nén được khi công cụ hỏng. Ở đây ứng viên chưa đo
thì **không biết** nó có an toàn không, nên không được đi tiếp. Mã
`PILOT_MEASUREMENT_UNAVAILABLE`.

**Không phát minh ngưỡng mới.** `Min` và đoạn tệ nhất được **lưu để chẩn đoán**, không
tham gia quyết định. Chưa có số đo nào đủ rộng để biết ngưỡng cho chúng.

**Không lấy trung bình P5.** P5 luôn thuộc về một đoạn cụ thể. Trung bình P5 của vài đoạn
là con số không thuộc về đoạn nào và không có nghĩa gì. Chấm điểm xếp hạng cũng theo **đoạn
tệ nhất**, khớp với điều quyết định dùng để loại.

**SSIM chỉ là telemetry.** Đã đo được VMAF 80 với SSIM 0,9999: SSIM cao không chứng minh
điều gì. Test khẳng định SSIM 0,90 và 0,9999 cho cùng một kết luận.

### Ước lượng dung lượng: tách ba thành phần, ghi rõ giả định

Không dùng `pilotBytes / pilotDuration * fullDuration` — sai theo ba lý do cùng lúc: clip
thử nghiệm **không có âm thanh**, vỏ container không tỉ lệ thuần với thời lượng, và một đoạn
tĩnh kéo tỉ lệ xuống.

Ước lượng tách `VideoBytes` + `AudioBytes` + `ContainerBytes`, lấy tỉ lệ từ đoạn **đắc
nhất** (ước thận trọng hơn là ước nhỏ rồi chọn nhầm ứng viên tệ), và trả kèm `IsReliable`
cùng danh sách giả định. Thiếu bitrate âm thanh thì `IsReliable = false` và ghi rõ phần
này bị ước bằng 0 — tức tổng ước lượng chắc chắn thấp hơn thực tế.

Ước lượng **không bao giờ** là nguồn sự thật. Nguồn sự thật là `FileInfo(fullOutput).Length`
sau khi encode, và lưới cuối giữ `NewSize <= OldSize`.

### Pareto: chỉ chất lượng đã đo, không bao giờ tham số encoder

Ứng viên A áp đảo B khi A không kém ở chiều nào (chất lượng, dung lượng) và tốt hơn đủ ở
ít nhất một chiều. Chênh lệch dưới `QualityEpsilon`/`SizeRatioEpsilon` coi như bằng, vì VMAF
lệch vài phần nghìn là nhiễu đo, xếp hạng theo nhiễu là vô nghĩa.

**CRF 30 của x264, CRF 30 của libaom và QP 30 của SVT-AV1 là ba mức chất lượng không liên
quan.** Đưa thang số thô vào phép so sẽ loại nhầm một ứng viên HEVC chỉ vì con số của nó
trông lớn hơn. Chỉ VMAF đã đo mới vào phép so, vì mọi ứng viên đều đo bằng cùng một mô hình
trên cùng một điều kiện hiển thị.

Chỉ ứng viên khả thi mới vào frontier. Thứ tự kết quả tất định (chất lượng giảm dần, rồi
dung lượng, rồi chi phí, rồi ID) vì ứng viên chạy song song không có thứ tự nào cố định.

### Mã lý do tách khỏi câu chữ

Câu chữ sẽ được viết lại khi cần; mã thì được đếm và lọc. Gộp làm một thì mọi lần sửa câu
chữ đều phá vỡ thống kê. Mã của giai đoạn tìm **không dùng chung** với mã của lưới cuối, và
không tái dụng `SOURCE_ALREADY_EFFICIENT` — mã đó nói về chính tệp nguồn và thuộc giai đoạn
5B.

### Vòng đời tệp thử nghiệm

`PilotEncoder` **không** xoá clip khi thành công: bước đo sắp tới cần đọc chúng. Xoá ở đó
nghĩa là `OutputPath` trỏ tới tệp không tồn tại và cả chuỗi đo rơi vào hư không. Clip hỏng
bị xoá ngay vì không còn gì để đo. Vòng đời thuộc người gọi, qua `Release`.

### Kiểm chứng thật trên một nguồn

1920×1080 h264 23,98 fps, 35,2 MB, 3 đoạn đại diện (HighMotion 6,0s · HighSpatial 65,0s ·
Typical 104,3s), mỗi đoạn 3,0 s.

| ứng viên | HighMotion | HighSpatial | Typical | khả thi | ước lượng |
|---|---|---|---|---|---|
| 1920×1080 crf16 | 97,91 | 96,66 | 99,07 | có | 125,9 MB |
| 1920×1080 crf24 | 95,83 | 93,77 | 95,40 | có | 73,8 MB |
| 1920×1080 crf32 | 88,45 | 86,90 | 86,02 | **không** | 53,5 MB |
| 1280×720 crf20 | 94,18 | 89,70 | 90,70 | có | 66,2 MB |

CRF 32 bị loại đúng vì đoạn tệ nhất 86,02 < 89. Ba ứng viên còn lại tạo thành frontier với
đánh đổi chất lượng/dung lượng thật. Tham chiếu cắt một lần, 6,6–15,9 MB/đoạn, 0,6–1,0 s.
Temp cleanup: 0 file còn lại.

Cả bốn ước lượng đều **lớn hơn tệp nguồn** — đúng thật, vì các CRF này quá tiết chễ cho
tệp đó. Giai đoạn 4B sẽ phải dò tham số rộng hơn; nếu vẫn không ứng viên nào đủ nhỏ thì
giữ bản gốc là kết quả đúng.

472 test, `check.ps1` sạch, Debug `-warnaserror` sạch, 0 suppression mới.

Không nối `VideoPipeline`, không nối `CompressionEngine`, không thêm feature flag, không
đo nguồn low-bpppf, không làm giai đoạn 5B.

## Giai đoạn 4B — Nối tìm kiếm thích ứng vào runtime, sau cờ tắt

`AdaptiveVideoPipeline` bọc `VideoPipeline`. Cờ `AppConfig.EnableAdaptiveSearch` tắt là mặc
định; tắt thì chuyển thẳng cho đường cũ, từng bít không đổi. Bật thì:
`CandidatePlanner` → `PilotSearch` → encode toàn tệp bằng `BuildFullArguments` (dùng CHUNG
phép biến đổi với phần thử) → lưới Phase 5A của engine giữ bất biến như mọi đường khác.

Ba trạng thái, ba hành vi — bảng này được kiểm trọn vẹn bằng `DecideFor`, không nằm ẩn
trong `switch`:

- `SelectedCandidate` → encode toàn tệp.
- `NoFeasibleCandidate` → giữ bản gốc, KHÔNG rơi về đường cũ.
- `SearchInfrastructureFailure` → rơi về đường cũ, kèm mã lý do GỐC (không chỉ
  `LEGACY_FALLBACK_USED`, vì chỉ thấy tên mã thì không sửa được gì).

### Hai phát hiện về căn thời gian, cả hai đều đo bằng ffmpeg thật

**1. Seek thẳng hai lần có thể lệch một khung hình.** Cùng một cửa sổ, clip tham chiếu cho
VMAF 90,32 còn seek thẳng vào nguồn chỉ 85,07 (thậm chí 73 khung so với 72). Vì vậy lưới
Phase 5A cũng phải đo clip-vs-clip từ cùng mốc 0 như giai đoạn thử — `QualityGate` nhận
thêm `IReferenceWindowSource` (đặt ở tầng media để cả hai tầng dùng chung), và khi cắt clip
hỏng thì fail-open như cũ.

**2. Clip-vs-clip vẫn chưa đủ: timebase khác nhau đẩy lệch nửa khung.** Tệp nguồn (timebase
90k, mốc 0,021s) và tệp đầu ra (timebase 24k, mốc 0,041s) có cùng 2879 khung hình, nhưng
cùng một mốc giây lại trỏ vào hai khung khác nhau — đã đo: cùng mốc cho VMAF 7,0, còn bỏ
một khung ứng viên thì SSIM lên 0,99. Vì vậy sau khi cắt clip còn thử ba cách căn (0,0),
(bỏ 1 khung ứng viên), (bỏ 1 khung tham chiếu) và lấy điểm cao nhất. Chỉ ±1 khung: lệch hơn
thế là lỗi khác (rớt khung, sai FPS) và không được hấp thụ lặng lẽ. Cách căn được ghi vào
thông báo để tái lập được phép đo.

### Hiệu chỉnh ước lượng dung lượng trên encode toàn tệp thật (đợt 2)

Đợt 1 chỉ ghi sai số, chưa sửa. Đợt 2 đo tiếp trên 2 nguồn (60,1 s) + 1 điểm 120 s,
x264 preset medium, pilot 3 đoạn:

| mẫu | video thật / thô | audio thật / ước | overhead |
|---|---|---|---|
| nguồn A crf24 | 0,631 | 0,975 | 41.734 B |
| nguồn A crf32 | 0,607 | 0,975 | 41.734 B |
| nguồn B crf24 | 0,661 | 0,989 | 42.179 B |
| nguồn B crf32 | 0,663 | 0,989 | 42.179 B |
| nguồn A 120 s crf24 | — | — | 82.634 B (≈2× điểm 60 s) |

Cộng 3 tỉ số video đợt 1: trung bình 7 mẫu = **0,72** (dao động 0,61…0,89 — hệ số sửa
độ lệch trung bình, không sửa được phương sai). Audio trung bình = **0,98**. Overhead
fit tuyến tính theo thời lượng: base 1.208 B + 678 B/s. Byte track đọc từ box `stsz`
bằng `Mp4TrackSizes` (không ffprobe, không parse stderr); cross-check trên remux khớp
từng byte. Pipeline truyền bitrate MỤC TIÊU (min với nguồn) vào estimator thay vì
bitrate nguồn thô.

### Tìm kiếm nhị phân trên thang điểm của nhánh

Thay coarse (luôn 2 điểm) + vét cạn nhánh đạt bằng `BranchSearch`: đo điểm đầu, điểm
cuối, rồi chia đôi tới cặp biên kề nhau.

**Nhánh là dấu vân tay phép biến đổi, không phải tên.** `TransformFingerprint` gom codec,
tên encoder, kích thước, FPS, định dạng pixel, preset, tune, họ điều khiển tốc độ và chuỗi
bộ lọc (dựng thật qua `EncodeTransform.BuildFilter`). Trước đây nhóm theo `BranchId`
("h264/1280x720") — một thoả thuận miệng, không có gì chặn ai đó thêm ứng viên khác FPS vào
đúng nhánh đó. Nay khác nhau ở bất kỳ thành phần nào là chắc chắn khác nhánh.

**Mọi lần cắt cần hai chứng cứ.** Bản trước điểm đầu rớt là cắt cả nhánh — dựa vào *một*
phép đo, mà điểm đầu lại là chỗ dễ rớt nhất. Nay: điểm đầu rớt chỉ đặt biên; phải dò tới
điểm cuối; hai đầu cùng rớt thì còn phải dò một điểm ở giữa nữa (`HasUnattemptedInterior`).
Nhánh chỉ hai điểm thì không có chỗ lấy điểm thứ ba nên cắt ngay. Chi phí: một lần encode
thừa cho mỗi nhánh toàn bộ rớt.

Giới hạn thật của cách này: **ba điểm trải khắp nhánh vẫn không phải chứng minh**. Một
"đảo ngọc" hẹp giữa nhánh vẫn lọt. Chấp nhận được vì hệ quả tệ nhất là bỏ sót một cơ hội,
không bao giờ là giao tệp tệ hơn nguồn — `QualityGate` 5A vẫn kiểm tệp đầu ra thật.

**Phi đơn điệu được đánh dấu, không bị nuốt.** Số đo trá chiều thật sự (điểm chỉ số cao đạt
trong khi điểm chỉ số thấp đã rớt) báo `NON_MONOTONIC_BRANCH_OBSERVED`, nhánh đó chuyển sang
dò tuyến tính và **không cắt gì nữa**. Biên lùi sai hướng thì không đánh dấu — đó vẫn là giả
định đúng, chỉ là ta không dòng được nữa; gộp hai thứ làm mã chẩn đoán mất ý nghĩa.
`SearchStatistics.BranchesNonMonotonic` để đếm được.

**Dừng sớm đã bị gỡ, có chủ đích.** Bản trước đóng cả nhánh khi điểm đầu vượt ngưỡng ≥ 3
VMAF. Điểm đầu là điểm **chất lượng cao nhất, tệp lớn nhất** của nhánh; mọi điểm chưa đo đều
nhỏ hơn, và bộ chọn ưu tiên tệp nhỏ — nên "tiết kiệm encode" ở đó đồng nghĩa với "bỏ qua ứng
viên có thể thắng", tức **có thể chọn ra tệp LỚN hơn**. Không có cách nào thu hẹp điều kiện
bật/tắt để sửa, vì ứng viên bị bỏ qua luôn là ứng viên nhỏ hơn. Mối lệch: heuristic giả định
"ưu tiên chất lượng", còn bộ chọn thực thi "ưu tiên dung lượng". `EarlyStopMargin` và
`PassesWithMargin` được giữ lại (chưa hiệu chỉnh, không có quyền quyết định nào) để làm mốc
đối chiếu.

### Phase 5B — ORIGINAL là một ứng viên ngang hàng

`OriginalCandidate` được `CandidatePlanner` sinh ra như mọi ứng viên khác, định danh ổn
định `ORIGINAL`, đứng đầu `CandidatePlan.Candidates`. Nó **không có điểm chất lượng** — không
so VMAF với chính nó, vì 100 so với 100 là so với bản thân nó. Nó khai đúng những gì mình có:
byte thật, kích thước, FPS, codec, tính chất âm thanh, tổn thất thế hệ = 0, chi phí encode = 0,
không rủi ro tương thích mới. Nó không đi qua Pareto.

`OriginalComparison` là ngữ nghĩa so sánh riêng. **Không có điểm số tổng hợp** (không có
`VMAF × a + tiết kiệm × b`) vì trọng số chưa có số đo nào chứng minh. Thứ tự là ràng buộc:
khả thi chất lượng → lợi ích dung lượng theo đúng `AppConfig.MinSavingPercent` → Pareto →
chi phí tính toán.

**Điều kiện để giữ bản gốc là "không chứng minh được", không phải "ước lượng không đẹp".**
Ứng viên chỉ bị bác khi ngay cả ở **biên dưới** của khoảng quan sát nó vẫn không nhỏ hơn nguồn
đủ xa — tức để bác phải có lý do mạnh nhất. Hệ quả là thiên lệch một chiều: nghiêng về giữ bản
gốc khi không chắc, nghiêng về encode khi có dấu hiệu lợi ích. Encode rồi hóa ra không đáng thì
vẫn an toàn tuyệt đối vì 5A giữ bản gốc; bỏ mất một khoản tiết kiệm thật thì không hoàn tác
được.

Bốn kết cục, bốn mã, bốn hành vi — và **hai kết cục giữ bản gốc phải phân biệt**:

| mã | nghĩa | hành vi |
|---|---|---|
| `PILOT_SELECTED` | ứng viên chứng minh được lợi ích | encode toàn tệp → **5A vẫn chạy** |
| `ORIGINAL_SELECTED` | có ứng viên đạt, không ứng viên nào chứng minh lợi ích | giữ bản gốc, bỏ encode |
| `NO_FEASIBLE_CANDIDATE` | đã thử, không ứng viên nào đạt chất lượng | giữ bản gốc, **không** rơi về đường cũ |
| `LEGACY_FALLBACK_USED` | hỏng hạ tầng | rơi về đường cũ, kèm nguyên nhân gốc |

Câu báo cáo luôn mở đầu bằng mã kết cục, và khi giữ bản gốc thì ghi thẳng **số byte toàn tệp
đã tiết kiệm** — con số duy nhất chứng minh được 5B có tác dụng hay không.

**Metadata không bao giờ được tự kết luận.** Planner chỉ sinh nhánh ORIGINAL khi biết byte
nguồn; không có `bpppf < x`, không có `bitrate < y`, không có `codec == AV1` → giữ nguyên.

### Ước lượng: vẫn chỉ để xếp hạng, nay kèm khoảng và nguồn gốc

`SizeEstimator.Calibration` gom số mẫu, độ lan tỉa, trạng thái và tài liệu — **cùng chỗ** với
chính các hằng số 0,72 / 0,98 / 1208 / 678, vì tách ra chỉ để "cho gọn" là làm mất đúng thứ cần
giữ. Trạng thái là `provisional-calibrated-on-limited-corpus`, không phải `calibrated`.

`HeuristicEstimateBounds` là **biên heuristics**, không phải khoảng tin cậy 95%: trên 7 tệp
đã đo, tỉ số nằm trong 0,61…0,89, nên biên là 0,61…0,89. Không có phân phối xác suất thì không
được gọi là khoảng tin cậy — và gọi vậy là nói dối, hậu quả là ai đó dựa vào độ chắc chắn
không có thật để ra quyết định không hoàn tác được. `null` nghĩa là "không có ý kiến", không
phải "hẹp"; thiếu khoảng thì không dùng để bác ứng viên nào.

### Căn khung hình: bó hẹp, và đo tần suất thay vì nới phạm vi

`AlignmentSearchWidth = 3`, ghim bằng test. Thêm ±2, ±3 không phải tăng độ chính xác đăng ký
mà là biến phép đo thành bộ tìm offset để nâng điểm: ứng viên lệch thật sẽ tìm được cách căn
"đẹp" và đi qua. `AlignmentTelemetry` đếm tần suất phải lệch khung; trên 50% số đoạn thì lưới
cảnh báo trong thông báo — vì đó là tín hiệu đúng về đường cắt clip hoặc dấu thời gian, và
phải điều tra ở tầng đó chứ không nới phạm vi ở đây.

### Nợ kỹ thuật còn lại

- **MPEG-TS.** Đã đo: remux **stream-copy, không đổi một pixel** sang MPEG-TS làm VMAF rơi
  4,1 điểm, nguyên nhân chưa truy ra được. Ta **không có đường nào remux sang MPEG-TS** (đã
  grep toàn kho: chỉ có ghi chú, không có mã). Cô lập rủi ro: `MediaClassifier.IsMpegTs` hạ mức
  tin cậy phép đo, và `OriginalComparison` **không bao giờ** kết luận giữ bản gốc từ số đo đáng
  ngờ — thay vào đó encode và để 5A quyết định bằng kích thước và số đo thật.
- **Tệp tạm giữ nguyên phần mở rộng nguồn** (`TempWorkspace.CreatePath`), nên nguồn `.ts` sẽ
  nhận `-movflags +faststart` với đường dẫn đầu ra `.ts` — thuộc muxer mov/mp4. Chưa có tệp
  `.ts` nào trong kho kiểm thử để xác nhận hành vi thật. **Chưa sửa**: nằm ngoài phạm vi 5B và
  đụng mọi pipeline.
- **`item.DecisionReason` không được gán cho hai nhánh giữ bản gốc**, vì engine thoát sớm khi
  `!result.Success`. Thông báo vẫn mang đủ thông tin; chỉ mất khả năng gom theo mã trên UI.
  Sửa sẽ đụng tầng lịch trình của mọi pipeline.
- **`AppHost.CopyConfig`** không chép `EnableAdaptiveSearch`, nên lưu cấu hình từ giao diện sẽ
  tắt cờ đang bật. Đã sửa (giữ nguyên giá trị thay vì chép từ DTO). **Chưa có test hồi quy**:
  dự án kiểm thử không tham chiếu được dự án WinForms mà không phải đổi TFM của toàn bộ dự án
  kiểm thử.

### Kiểm chứng E2E trên media thật

Bản sao 120 s có tiếng (39,4 MB): search đo 6/6 ứng viên, 12 phép VMAF, chọn
H264/1280×720/crf18 (VMAF đoạn tệ nhất 90,1); đầu ra 32,4 MB (tiết kiệm 17,8%); lưới cuối
ACCEPTED với VMAF 90,3 (P5 88,7). Âm thanh giữ nguyên, không nâng bitrate. Temp cleanup: 0
file, 0 thư mục search còn lại.

### Ma trận kho kiểm thử và khoảng trống còn lại

Chưa được phép tuyên bố "đã hiệu chỉnh tổng quát". Những gì **đã** đo:

| nhóm | tình trạng |
|---|---|
| tổng hợp `testsrc2` 720p, crf 14 / 30 / 40, 20 s | có — E2E, đủ đường thích ứng |
| tổng hợp `testsrc2` 320×240 crf 16 | có — PILOT_SELECTED, tiết kiệm 43% |
| anime 1080p h264 120 s có tiếng (nguồn nghiệm thu) | có — PILOT_SELECTED, tiết kiệm 17,8% |
| nhiễu ngẫu nhiên 720p crf 32 (84 MB) | đo một lần, **phát hiện hỏng** — xem dưới |

Những gì **chưa** có, nên mọi tuyên bố về chúng là suông:

- talking head · gameplay · thể thao/chuyển động mạnh · hoạt hình/anime (ngoài một tệp) ·
  quay màn hình / chữ / UI · tối hoặc nhiễu · thiên nhiên nhiều chi tiết
- nguồn **HEVC** và **AV1**; nguồn **bitrate thấp đã nén kỹ**; nguồn **bitrate cao**
- 720p · 1080p (ngoài một tệp) · 1440p · **4K**
- nguồn có container khác MP4 ở đường chạy thật (xem nợ MPEG-TS)

**Khoảng trống đã tốn công và đo được, không phải suông:** trên nguồn nhiễu ngẫu nhiên 720p
crf 32 (84,13 MB), estimator ra **87,8 MB** còn tệp thật là **122,3 MB** — lệch **−28%**, ngoài
toàn bộ khoảng 0,61…0,89 đã hiệu chỉnh. Lỗi nằm ở giả định "đoạn đắc nhất đại diện cho cả
tệp": nội dung nhiễu không có đoạn nào đắc hơn đoạn nào. Ứng viên vẫn **đạt** chất lượng
(VMAF 99,7 — tái mã hoá nhiễu thì không mất gì), nên đường thích ứng chọn nó và 5A phải loại vì
tệp lớn hơn nguồn. Đây đúng là hành vi an toàn, nhưng nó là khoảng trống thật của bộ hiệu
chỉnh và cần một mẫu nội dung entropy cao trước khi tuyên bố bất kỳ điều gì về nhóm đó.

### Hiệu năng: 5B so với baseline đã nghiệm thu

Cùng một nguồn nghiệm thu (1080p h264, 120,1 s, 37,58 MB, có tiếng 247 kb/s), Balanced:

| | baseline 5A | 5B |
|---|---|---|
| ứng viên đã đo | 3/6 | 4/6 |
| phép đo VMAF | 6 | 8 |
| thời gian tìm kiếm | 130,9 s | **186,5 s** |
| ứng viên chọn | 720p crf18 | 720p crf18 (không đổi) |
| ước lượng | 33,5 MB | 33,5 MB (không đổi) |
| tệp thật | 32,38 MB | 32,38 MB (không đổi) |
| tiết kiệm | 17,8% | 17,8% |
| 5A | ACCEPTED, VMAF 90,3 / P5 88,7 | ACCEPTED, VMAF 90,3 / P5 88,7 |

Tổng thời gian một lần nén: 285,4 s (tìm 186,5 s + encode toàn tệp ~98,9 s); lưới 5A 135,2 s.
**Tìm kiếm đắt hơn 42%** và đổi lại y hệt kết quả — đúng cái giá của việc gỡ dừng sớm và thêm
điểm xác nhận thứ ba. Đây là cái giá đã được chọn có chủ đích; nếu sau này tìm ra cách lấy lại
tốc độ thì phải là cách **không** đổi lựa chọn, không phải bật lại dừng sớm.

**Số encode toàn tệp bị bỏ qua:** đo được trên nguồn `testsrc2` crf 40 (1,35 MB) — 4 ứng
viên đạt chất lượng, ngay cả biên dưới cũng không nhỏ hơn nguồn đủ xa → `ORIGINAL_SELECTED`,
tiết kiệm **1,42 MB** (toàn bộ kích thước tệp) và **toàn bộ thời gian encode**; search vẫn
chạy 105,6 s nên tổng thời gian giảm so với việc encode toàn tệp.

**Lệch khung hình:** 5A ghi nhận **1/2 đoạn phải lệch khung** trên nguồn nghiệm thu → vượt
ngưỡng cảnh báo 50%. Kết quả chọn vẫn đúng và 5A vẫn ACCEPTED, nhưng đây đúng là tín hiệu đúng
mà bước 10 yêu cầu phải điều tra (đường cắt clip, dấu thời gian của nguồn 1080p → 720p).
Không nới phạm vi căn để làm nó im.

612 test, 0 bị bỏ qua, `check.ps1` sạch, Debug `-warnaserror` sạch, 0 suppression mới.
Không đổi ngưỡng VMAF, không làm giai đoạn 5B tiếp.
