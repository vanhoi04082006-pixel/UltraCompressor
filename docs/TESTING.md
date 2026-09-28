# Kiểm thử

## Chạy test

```powershell
dotnet test tests\UltraCompressor.Core.Tests -c Release
```

123 test, chạy khoảng 100 ms — không cần công cụ ngoài, không chạm tệp thật.

Test chia theo tệp:

| Tệp | Phủ |
|---|---|
| `CoreTests.cs` | phân loại media, dò đường dẫn công cụ, kiểm tra chạy được, hoàn tác, JSON |
| `SchedulingTests.cs` | cổng tạm dừng, ước lượng ETA, tính số luồng nén |
| `StorageTests.cs` | giao dịch tệp, quét thư mục, bộ lọc mẫu tên |
| `CompressionProfileTests.cs` | tham số của từng mức nén |
| `FFmpegOutputParserTests.cs` | đọc dòng log của ffmpeg: thời lượng, thời điểm, bitrate |
| `FormatTests.cs` | hiển thị kích thước và phần trăm |

## Ba lớp kiểm thử

**Lớp 1 — test đơn vị.** 123 test trên. Bắt được logic thuần: phân loại đuôi tệp, tính
ngưỡng, đọc output của ffmpeg, giao dịch tệp.

**Lớp 2 — kiểm thử tay trên tệp thật.** Không tự động hoá vì tốn 5 phút mỗi tập video 20
phút. Dùng thư mục thật, xem nhật ký, đo kích thước trước/sau.

**Lớp 3 — ảnh chụp giao diện.** Dùng `UC_CAPTURE` + `UC_EVAL_JS` (xem
[`ARCHITECTURE.md`](ARCHITECTURE.md)). Chụp được màn hình đang chạy ở đúng tỉ lệ DPI thật,
không bị ảo hoá.

## Quy trình đo thật

Dùng một thư mục media thật, **chép ra thư mục riêng** rồi thử trên bản chép — không
đụng tới tệp gốc của người dùng.

```powershell
# 1. Chép dữ liệu ra chỗ làm việc
$work = 'E:\...\artifacts\applytest\media'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Copy-Item 'D:\media\thu-muc-goc\*' $work

# 2. Ghi lại hash trước khi nén — đây là chứng cứ để đối chiếu sau
Get-ChildItem $work | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" }

# 3. Chạy Thử trước (dry-run), xem nhật ký
# 4. Chạy Nén thật, kiểm tra .bak đã tạo và khớp hash bản gốc
# 5. Bấm Hoàn tác, đối chiếu hash tệp gốc đã về đúng
```

Ba điều phải kiểm mỗi lần:

1. **Tệp sau nén phải mở được.** `ffmpeg -i <tệp>` phải in ra `Duration` và `Stream #`.
   Exit code 0 của ffmpeg không bảo đảm tệp đọc được.
2. **`.bak` phải trùng khớp byte-for-byte** với bản gốc, tính bằng SHA-256.
3. **Hoàn tác phải trả về đúng hash ban đầu** và xoá sạch `.bak`.

## Đo tiết kiệm thật

Cắt 2 phút đầu của một tập để đo nhanh, rồi so sánh các tham số:

```powershell
$ff = "E:\Projects\UltraCompressor\tools\ffmpeg.exe"
& $ff -y -i $src -t 120 -c copy $out\base.mp4      # mẫu gốc
foreach ($crf in 20, 23, 28) {
  & $ff -y -i $out\base.mp4 -c:v libx264 -crf $crf -preset medium -c:a aac -b:a 128k $out\c$crf.mp4
}
```

Kết quả tham khảo trên một tập anime 1080p:

| CRF | preset | Kích thước | So với gốc | Thời gian |
|---|---|---|---|---|
| 20 | medium | 38,93 MB | −29,2% | 87s |
| 23 | medium | 29,60 MB | −1,8% | 30s |
| 23 | slow | 28,33 MB | −6,0% | 41s |
| 28 | medium | 19,10 MB | −36,6% | 57s |

**Đọc bảng này đúng cách.** CRF 23 cho −1,8% nghĩa là tệp nguồn đã được nén ở chất lượng
tương đương CRF 23 rồi. Không phải ứng dụng nén yếu. Tệp nén kém hơn sẽ không bao giờ
được ghi đè — ứng dụng giữ nguyên bản gốc.

## Những lỗi do kiểm thử phát hiện

Ghi lại ở đây để lần sau không phải tìm lại từ đầu.

| Lỗi | Biểu hiện | Nguyên nhân |
|---|---|---|
| Job nhiều tệp treo sau tệp đầu | Chỉ 1/4 tệp xong, không có tiến độ, không có ffmpeg | `PauseGate` lấy permit của semaphore ở `WaitAsync` nhưng cuối vòng lặp gọi `Resume()` — mà `Resume()` chỉ trả permit khi cổng đang tạm dừng. Tệp thứ hai chờ mãi |
| Bấm Tạm dừng treo giao diện | UI không phản hồi tới hết tệp đang chạy | `PauseGate.Pause()` gọi `_gate.Wait()` nên chặn luồng gọi tới khi tệp hiện tại xong |
| "Xoá list" báo lỗi | `Lệnh không hợp lệ: 'clearAll'` | `DispatchAsync` coi `null` là lệnh không tồn tại, nên mọi lệnh trả `null` đều bị từ chối *sau khi đã chạy xong* |
| Nhật ký ghi tiết kiệm âm | `6.55 MB -> 5.85 MB (720.2 KB, -10.7%)` | dấu `-` thừa trong chuỗi định dạng |
| Đường dẫn tràn sang ô bên cạnh | Chữ đè lên nhãn trạng thái khi mở bảng chi tiết | `max-width` trên `<td>` bị bỏ qua với bảng `table-layout: auto`; phải giới hạn ở phần tử con bằng đơn vị `ch` |
| Lối tắt báo không tìm thấy đích | Windows: Missing Shortcut | tệp thực thi là `UltraCompressor.App.exe` (lấy tên project) nhưng `setup.ps1` ghi cứng `UltraCompressor.exe` |
| Ghostscript "không tìm thấy" dù đã cài | Ứng dụng báo thiếu | `config.json` trỏ `...\MassCompressorTool\gswin64c.exe`, thiếu `\bin` |
| Bảng chi tiết đứng yên khi job chạy | Tệp đang nén vẫn hiện "Chờ" | `AppHost.OpenJobId` có setter nhưng chưa từng được gán, nên `PushAsync` không gửi sự kiện `items` |
| Báo "Gifsicle chưa có" khi `dotnet run` | Cảnh báo sai dù đã đặt gifsicle vào `tools\` | tệp thực thi nằm ở `bin\Debug\...` nên không có công cụ cạnh bên; nay dò thêm thư mục `tools\` của dự án |
| Cột "Kết quả" đảo chữ | `-12.2%` thành `2.2%1-` | `.grid .sub` áp `direction: rtl` cho mọi phần tử con thay vì chỉ đường dẫn |
| Con trỏ kéo tệp vào hiện dấu cấm | Không thả được | WebView2 đã đăng ký làm OLE drop target trước, nên cửa sổ cha nhận dữ liệu rỗng rồi từ chối |
| Cửa sổ xem trước biến mất sau vài giây | Bấm xem bản gốc xong không thấy gì | `preview` chờ ffplay với thời hạn 3 giây, hết thời hạn `ProcessRunner` giết cả cây tiến trình |
| `kind` rơi về cấp cha nên không nhúng được video | Màn hình so sánh chỉ hiện ảnh xem trước | `CompareResult.Kind` nhưng `renderCompareSide` đọc `side.kind` |

Hai lỗi đầu là nghiêm trọng: job nhiều tệp — tức là trường hợp dùng chính — treo sau tệp
đầu tiên. Chúng lọt qua vì test chỉ có một tệp mỗi job, và vì không test bằng tệp thật
nào nặng hơn 0,1 giây.

Lỗi `OpenJobId` và lỗi `direction: rtl` đều chỉ lộ ra khi **nhìn ảnh chụp giao diện** lúc
ứng dụng đang chạy, không phải lúc test. Chạy job rồi chụp lại là bắt buộc, không phải
tuỳ chọn.

## Kéo-thả: đã bỏ, và đừng thử lại

Tính năng này **không còn**. Ghi lại vì nó rất dễ bị thử lại, và mỗi lần thử lại đều tốn
cả buổi.

| Cách | Kết quả |
|---|---|
| `WM_DROPFILES` + `WS_EX_ACCEPTFILES` trên Form | Không tới |
| OLE `IDropTarget` tự viết + `RegisterDragDrop` | Đăng ký OK nhưng không bao giờ được gọi |
| `AllowExternalDrop = false` để WebView2 nhường quyền | Vô hiệu |
| `file.path` trong JavaScript | Rỗng — ngữ cảnh `https` không lộ đường dẫn |
| `webkitGetAsEntry().fullPath` | Không dựng được đường dẫn dạng ổ đĩa |
| `CoreWebView2.DragDrop` | API không tồn tại ở SDK 1.0.4191.47 |

Cách kiểm chứng (đáng học hơn cả kết luận): **kéo chuột thật**, tách từng tầng.

```powershell
# 1. Chứng minh chuỗi kéo là đúng: cùng phép kéo đó, thả vào Notepad.
#    Notepad mở file => Explorer + con trỏ + SendInput đều ổn, lỗi nằm ở phần nhận.

# 2. Chứng minh Form không phải nơi nhận: dòng HWND tại điểm thả, đi ngược lên cha.
#    Kết quả: Chrome_RenderWidgetHostHWND (pid = msedgewebview2) nằm TRÊN Form.

# 3. Chứng minh không phải do WebView2: một cửa sổ WinForms trần, không WebView2,
#    bật đúng WS_EX_ACCEPTFILES — cũng không nhận WM_DROPFILES nào.
```

Điểm 3 mới là kết luận: **cơ chế `WM_DROPFILES` đã bị Windows bỏ hoàn toàn**, nên sửa thêm
phía .NET cũng vô ích. Explorer ngày nay luôn chạy kéo bằng OLE và chỉ bàn giao cho cửa sổ
đã `RegisterDragDrop`; mà cửa sổ con của Chromium đã đăng ký trước.

**Bài học chung: trong ứng dụng khung chủ WebView2, đừng trông chờ JavaScript lấy được
đường dẫn tệp, và đừng tin tài liệu của phiên bản SDK trước.**

```powershell
$dll = "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2\<phiên bản>\lib\net462\Microsoft.Web.WebView2.Core.dll"
[System.Reflection.Assembly]::LoadFrom($dll).GetExportedTypes() |
  ForEach-Object { $t = $_; $_.GetMembers() | Where-Object Name -match 'Drop' |
    ForEach-Object { "$($t.Name).$($_.Name)" } } | Sort-Object -Unique
```

## Ba lớp lỗi mà test tự động bắt được, nhưng người dùng vẫn thấy

Cả ba đều im lặng: job chạy, tệp ra, không test nào đỏ.

| Lỗi | Vì sao test không thấy | Test chặn lại bằng gì |
|---|---|---|
| Chiều cao bằng chiều rộng (1918×1918) | `ParseDimension` là hàm thuần, nhưng test chỉ kiểm tra chiều rộng | `FFmpegOutputParser.ParseDimensions` trả về **cả hai**, có test riêng |
| Bitrate đọc trên dòng cuối stderr | Dòng cuối là `At least one output file must be specified` — luôn không khớp | Test với nguyên văn stderr thật |
| Job kẹt "Chờ duyệt" sau khi duyệt | `keepDays == 0` mà mặc định là 30 | Test `DiscardBackups` với `keepDays: 30` |

Đặc biệt lỗi 1: mật độ bit/px/khung dùng `W × H`, nên báo vuông làm **kế hoạch nén sai**
chứ không chỉ sai hiển thị. Lỗi hiển thị âm thầm thì lỗi nghiêm trọng nhất.

## Đo thật ba mức trên tệp thật

`dotnet test` xanh **không** có nghĩa là nén đúng — lớp nén là hàm thuần trả về chuỗi lệnh
ffmpeg, không hề chạy ffmpeg. Phải chạy thật và **in ra con số**.

Đo trên `Mama x Holic Miwaku no Mama to Amaama Kankei` (2 video 19 phút, 1920×1080, 24 fps):

| Mức | Tập 01 | Tập 02 | Cả thư mục |
|---|---|---|---|
| Nhẹ | 9,0% | 2,7% | 26 MB |
| Cân bằng | 35,2% | 29,5% | 144 MB |
| Mạnh | 63,5% | 61,1% | 276 MB |

Cùng bộ đó, chạy **trước** khi sửa lỗi bitrate: 9,7% và 3,4% ở mức Cân bằng. Sai 4 lần mà
không có test nào đỏ.

Hai ảnh `.jpg` nhỏ (55 KB, 40 KB) bị bỏ qua ở mức Cân bằng và Mạnh — đúng bản chất, JPEG
không cải thiện được nữa. Ở mức Nhẹ thì vẫn thử, rồi `NoSizeGain` loại vì kết quả to hơn.

## Vòng đời tệp: kiểm chứng bằng chính mã của ứng dụng

Không mô phỏng thao tác. Gọi đúng `FileTransaction.Commit` → `UndoService.DiscardBackups`
→ `UndoService.RestoreAll` trên tệp thật, rồi kiểm tra nội dung còn nguyên:

```
TRUOC   : goc=10007  .bak tồn tại=False
Commit  : err=(không có)
          goc=7  .bak=10007
Duyệt   : failed=0 -> trạng thái job = Committed
          còn .bak? True
Hoàn tác: restored=1 failed=0
          nội dung sau hoàn tác: "BẢN GỐC - 10000 ký tự xx..."
          .bak còn lại? False
```

## Kiểm tra nhúng video

`<video>` trong WebView2 chỉ chạy nếu bản dựng có codec. Trước khi làm tính năng nhúng,
kiểm tra trước bằng:

```javascript
document.createElement('video').canPlayType('video/mp4; codecs="avc1.42E01E"')
```

Trả về `"probably"` hoặc `"maybe"` thì dùng được. Nếu rỗng thì phải rơi về trình phát
ngoài. Trên máy này WebView2 có cả H.264 lẫn AAC.

**Nhưng codec có trong bản dựng chưa đủ — `Content-Type` sai thì vẫn đen.** Tệp sao lưu có
tên `phim.mp4.bak`; nếu đoán MIME trực tiếp từ phần mở rộng thì ra
`application/octet-stream`, Chromium từ chối, và `<video>` hiện khung đen 0:00 **không báo
lỗi nào**. `MediaHost.MimeTypeOf` phải bỏ lớp `.bak` trước khi đoán. Khung đen không kèm
chữ giải thích là loại lỗi tệ nhất, nên `<video>` có bắt sự kiện `error` và in lý do.

## Trước khi gửi thay đổi

```powershell
.\check.ps1          # giống hệt CI: định dạng, build, test, build Debug coi cảnh báo là lỗi
git status --short   # không được còn tệp đã sửa mà chưa commit
```

Nếu sửa giao diện, chụp lại các màn hình và **nhìn tấm ảnh**, đừng chỉ tin build xong.
Vụ tràn chữ ở trên là do nhìn ảnh mới phát hiện, không phải do test.
