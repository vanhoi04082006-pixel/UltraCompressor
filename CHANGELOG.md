# Nhật ký thay đổi

Các mốc theo ngày. Mục **Đã sửa** liệt kê lỗi của bản gốc v12; mục **Lỗi tìm khi kiểm
thử** liệt kê lỗi do chính bản viết lại này tạo ra.

## Chưa đánh số — gom dữ liệu về thư mục dự án, sửa tiến độ và kéo thả

### Đã sửa

- **Tiến độ từng tệp chạy thật.** Cả bốn pipeline truyền `-loglevel error`, mà dòng
  `frame=… time=…` của ffmpeg nằm ở mức `info` trên **stderr** — nên nó không bao giờ được
  in, `ParseTime` không bao giờ khớp, và thanh tiến độ đứng yên ở 0% suốt tới khi tệp xong
  rồi nhảy thẳng lên 100%. Nay dùng `-progress pipe:1` (`out_time_us` trên **stdout**),
  hoạt động ở mọi mức log.
- **Âm thanh trong video không còn ghim cứng 128k.** `VideoPipeline` từng viết thẳng
  `-b:a 128k` thay vì lấy `profile.AudioBitrateKbps`, nên chọn "Nhẹ" (320k) hay "Mạnh"
  (128k) cũng ra 128k — trong khi bảng hướng dẫn hiện 320k/192k/128k. Nay không nâng
  bitrate của tệp nguồn vốn đã nhỏ hơn mức đích.
- **`MediaHost` hỗ trợ `Range`.** Trước đây bỏ qua hẳn header này và luôn trả `200` với
  `Content-Length` bằng cả tệp, dù đã quảng bá `Accept-Ranges`. Chromium lúc tua phải
  đọc lại từ byte 0, nên màn hình so sánh giật so với VLC. Nay trả `206 Partial Content`
  với `Content-Range` đúng, và `416` khi range vô lệ.
- **Cửa sổ không còn tràn ra ngoài màn hình.** `FitToWorkArea` lấy nguyên vùng làm việc
  làm `ClientSize` rồi nới ra mà không canh lại vị trí: cửa sổ được canh giữa khi còn
  1280px (mép trái = 320) nên nới lên 1918px thì mép phải = 2238, tràn ~318px ra ngoài
  màn hình 1920. Hậu quả thấy được: cột thao tác, khối "Tốc độ / Còn lại / Luồng nén" và
  status bar đều bị cắt. Nay đặt `Size` (kích thước ngoài) cho vừa vùng làm việc rồi canh
  lại vị trị.
- **Bảng không còn tràn ngang.** `max-width` trên `<td>` không có tác dụng với
  `table-layout: auto`, nên một tên tệp dài đẩy cột thao tác ra ngoài khung. Nay dùng
  `table-layout: fixed` với tỉ lệ cột cố định.
- **Bộ lọc trên bảng chi tiết không bị ghi đè.** Mỗi lần đẩy trạng thái (250 ms) gửi toàn
  bộ tệp của job, nên mỗi phím tìm kiếm chỉ sống được tới lần đẩy kế tiếp.
- **Thứ tự tệp ổn định.** Bảng chi tiết sắp xếp theo `SavedBytes` giảm dần, tức đảo lại mỗi
  khi một tệp xong — không theo dõi được tệp nào đang chạy bao nhiêu phần trăm. Nay giữ thứ
  tự lúc quét.
- **Bớt I/O ở tầng giao diện.** `File.Exists` cho `.bak` từng chạy 4 lần/giây cho mọi
  tệp đã nén (hàng nghìn lệnh I/O mỗi giây với job lớn); nay có bộ nhớ đệm 3 giây và bị
  xoá khi Duyệt / Hoàn tác. Bảng chi tiết cũng không còn dựng lại toàn bộ DOM 4 lần/giây.
- **`setup.ps1` chạy được trên Windows PowerShell 5.1.** `[string]::IsNullOrWhiteSpace`
  chỉ có từ .NET Core 2.0, nên script văng lỗi ngay dòng đầu khi chạy bằng
  `powershell.exe`.
- Sửa 2 comment hỏng UTF-8 trong `CompressionProfile.cs` (byte `0xE9` lỗi, hiện thành
  mojibake vĩnh viễn).

### Thêm

- **Hỗ trợ tệp lẻ.** Trước đây chỉ nhận thư mục và `AddFolder` báo lỗi nếu gặp tệp. Nay
  có `ScanFile` / `AddFile` / `AddPaths` (tự phân biệt thư mục với tệp), nút **Thêm tệp**
  mở hộp chọn đa chọn với bộ lọc dựng từ chính `MediaClassifier`, và phím `Ctrl+Shift+O`.
- **Kéo thả thư mục / tệp vào cửa sổ**, có lớp phủ "Thả vào đây" báo hiệu khi đang kéo.
- **Cột `%` cố định trong bảng chi tiết**, luôn hiện cho mọi dòng: đang chạy thì hiện
  phần trăm tăng dần, xong rồi thì giữ lại 100% thay vì mất thanh ngay như trước.
- **Badge loại media** ở cột đầu bảng hàng đợi, và nhãn **TỆP** cho job một tệp lẻ (trước
  cả ba tệp cùng thư mục đều hiện tên thư mục cha nên trông như một).
- **Nút thao tác hiện sẵn** thay vì chỉ hiện khi rê chuột — trước đó bảng trông như không
  có nút nào.
- **Nút chọn thư mục đích** khi xuất kết quả, thay vì phải gõ tay đường dẫn.
- **Có "Thư mục dự án"** ở thanh trạng thái và trong Cài đặt, cùng đường dẫn dữ liệu.
- Ảnh xem trước trong màn hình so sánh được **nhớ trên đĩa** và cache có giới hạn 64 mục
  (trước cache vô hạn, mở nhiều tệp sẽ nuôi bộ nhớ và hiện dữ liệu cũ).
- GIF trong màn hình so sánh hiện bằng **ảnh tĩnh** thay vì nhúng `<img>` động — một GIF
  động vẽ lại liên tục trong đúng tiến trình đang render cả giao diện.

### Đổi

- **Mọi thứ nằm trong thư mục dự án.** Bản cài ở `app\`, dữ liệu ở `data\` (cấu hình,
  phiên, nhật ký, tệp nén tạm, profile WebView2). Trước đó `setup.ps1` cài vào
  `%LOCALAPPDATA%\UltraCompressor` — **trùng đúng** `AppPaths.DataDirectory`, nên một lần
  cài là ổ C: phình lên **130 MB** (ffmpeg.exe 95 MB + profile WebView2 32 MB) mà không ai
  biết. Ghi đè nơi lưu bằng `UC_DATA_DIR` / `UC_ROOT`.
- `setup.ps1` mặc định cài vào `<dự án>\app` và có `-RemoveLegacy` để dọn thư mục cũ ở
  ổ C:.

### Ghi chú kỹ thuật

- Kéo thả **không** dùng được API `CoreWebView2.DragOver` / `DragDrop`: SDK
  `Microsoft.Web.WebView2` 1.0.4191.47 đã bỏ hẳn (đã kiểm tra bằng reflection — 497 kiểu xuất
  ra, không có `CoreWebView2DragDropEventArgs`, không thành viên nào chứa "Drop" ngoài
  `AllowExternalDrop`). Ba cách đã thử trước đây và đều hỏng lý do khác nhau, xem
  `docs/TESTING.md`.

## 2.0.0 — bản viết lại

### Thêm

- **Màn hình so sánh trước / sau** phát trực tiếp trong ứng dụng: mỗi bên một trình phát
  `<video>`/`<audio>` nhúng, có đủ nút bấm, phát cả hai cùng lúc được. Không còn phải mở
  hai cửa sổ trình phát bên ngoài.
- **Thanh kéo giữa hai cột** trong màn hình so sánh để chia tỉ lệ; dùng bàn phím được
  (← →, mỗi lần 2%).
- `MediaHost`: phục vụ tệp media cho trang qua virtual host `media.local`, mỗi tệp một mã
  ngẫu nhiên. Trang không biết đường dẫn thật và không xem được tệp nào chưa đăng ký.
- **Tiến độ từng tệp.** Dòng thư mục hiện tên tệp đang nén kèm phần trăm; bảng Chi tiết
  có thanh tiến độ riêng cho tệp đang chạy. Trước đó chỉ có thanh tổng đếm số tệp, nên một
  tập video 20 phút kẹt trông y hệt một tệp ảnh nhỏ.
- Kéo thả thư mục hoặc tệp từ Explorer vào cửa sổ — **đã bị gỡ**, xem mục Lỗi bên dưới.
- Lệnh cầu `log` để giao diện ghi vào nhật ký — thao tác không làm được bằng script thì
  nhật ký là manh mối duy nhất khi nó hỏng.
- Biến môi trường `UC_EVAL_SETTLE` để chụp giao diện đúng lúc muốn xem.
- Log mức debug ghi rõ tệp đang xử lý và tệp vừa xong. Nén video có thể chạy hàng chục
  phút mà không ghi gì, nên không có hai dòng này thì không phân biệt được "đang chạy" với
  "đã kẹt".
- Test hồi quy cho `PauseGate`: chờ nhiều lần liên tiếp, tạm dừng sau khi đã chờ, và
  tạm dừng không chặn luồng gọi.

### Đã sửa (lỗi của bản gốc v12)

- **Nhánh GIF bị đảo ngược** — có gifsicle mới gọi gifsicle, thiếu mới gọi ffmpeg; filter
  `fps` là code chết. Nay luôn chạy ffmpeg trước rồi mới gifsicle.
- **Ngưỡng chấp nhận tăng theo mức nén**, khiến mức Mạnh khó đạt nhất.
- **Hủy job lúc đang tạm dừng treo vô hạn** — vòng chờ không nhìn thấy `CancellationToken`.
- **Ghostscript hỏng nhưng báo lỗi im lặng** — nay kiểm tra mã thoát và báo rõ.
- **Nút "Duyệt" chỉ xoá `.bak`** thay vì duyệt kết quả; hộp thoại xác nhận ghi sai nội dung.
- **ETA luôn trễ** vì "đã xử lý" chỉ cộng dồn sau khi tệp xong.
- **Mất EXIF/orientation** khi nén ảnh — thiếu `-map_metadata 0`.
- **EPNG có thể treo** vì không có `-nostdin`.
- **Đường dẫn tương đối cho công cụ ngoài** — phụ thuộc thư mục làm việc hiện tại.
- **Ghi đè `.bak`** khi tạo bản sao lưu.
- Bảng đầy đủ 23 mục ở [`docs/PHASE0-REFERENCE.md`](docs/PHASE0-REFERENCE.md).

### Lỗi tìm được khi kiểm thử

Các lỗi dưới đây do bản viết lại này gây ra, phát hiện khi chạy trên tệp thật.

- **Job nhiều tệp treo sau tệp đầu tiên.** `PauseGate.WaitAsync` lấy một permit của
  `SemaphoreSlim`, nhưng cuối mỗi vòng lặp lại gọi `Resume()` để "trả permit" — mà
  `Resume()` chỉ trả khi cổng đang tạm dừng. Permit bị nuốt, tệp thứ hai chờ vô hạn. Lỗi
  này làm hỏng đúng trường hợp dùng chính của ứng dụng, nhưng test không bắt được vì mọi
  job trong test chỉ có một tệp. Sửa bằng cách tách bạch hai ý nghĩa: cổng chỉ trả lời
  "được chạy chưa" bằng `TaskCompletionSource`, còn giới hạn song song do semaphore riêng
  của engine đảm nhiệm.
- **Bấm Tạm dừng treo giao diện.** `PauseGate.Pause()` gọi `_gate.Wait()` nên chặn
  luồng gọi — tức là UI đứng hình tới khi tệp đang chạy xong. `Pause()` nay chỉ đặt cờ.
- **"Xoá list", "xoá 1 thư mục", "hoàn tác 1 tệp" báo `Lệnh không hợp lệ`.**
  `DispatchAsync` coi kết quả `null` là lệnh không tồn tại, nên mọi lệnh cố tình không
  trả dữ liệu đều bị từ chối *sau khi đã chạy xong* — lỗi hiện ra dù tác vụ đã đúng.
  Nay có danh sách `CommandsWithoutResult` khai báo tường minh.
- **Lối tắt desktop trỏ tới tệp không tồn tại.** Tệp thực thi là `UltraCompressor.App.exe`
  (lấy tên project) nhưng `setup.ps1` ghi cứng `UltraCompressor.exe`. Nay đặt
  `<AssemblyName>` và cho `setup.ps1` tự dò tệp thực thi thật.
- **Nhật ký ghi tiết kiệm âm** cho tệp nén nhỏ hơn: `6.55 MB -> 5.85 MB (..., -10.7%)`.
  Dấu `-` thừa trong chuỗi định dạng.
- **Đường dẫn thư mục tràn chồng nhãn trạng thái** khi mở bảng chi tiết. `max-width` trên
  `<td>` bị bỏ qua với bảng `table-layout: auto`; phải bọc nội dung trong span và giới hạn
  bằng đơn vị `ch`.
- **Nhận ký im lặng khi job kẹt** — dòng debug về từng tệp bị thiếu nên không phân biệt
  được trạng thái.
- **Bảng chi tiết không cập nhật khi job đang chạy.** `AppHost.OpenJobId` có setter nhưng
  **chưa từng được gán**, nên `PushAsync` không bao giờ gửi sự kiện `items` và bảng chỉ hiện
  ảnh chụp tại lúc mở. Nay `getItems` ghi nhớ job đang mở.
- **Cảnh báo sai "Gifsicle chưa có" khi chạy thử bằng `dotnet run`.** Tệp thực thi nằm ở
  `bin\Debug\net10.0-windows\` nên không có công cụ nào cạnh bên. Nay dò thêm thư mục
  `tools\` của dự án khi chạy từ mã nguồn.
- **Cột "Kết quả" hiện chữ bị đảo** (`-12.2%` thành `2.2%1-`) vì `.grid .sub` áp
  `direction: rtl` — mẹo cắt chữ đường dẫn từ bên trái — cho mọi phần tử con chứ không
  riêng đường dẫn.
- **Kéo thả thư mục — đã thử ba cách, đều hỏng, đã gỡ khỏi 2.0.0.**
  1. `file.path` trong JavaScript: rỗng, vì trang chạy trên `https://app.local` là ngữ
     cảnh an toàn nên Chromium không đưa đường dẫn tệp ra cho trang.
  2. `entry.fullPath` của `webkitGetAsEntry`: không dựng được đường dẫn dạng ổ đĩa.
  3. `AllowDrop` của WinForms rồi tới `WM_DROPFILES`: WebView2 đã đăng ký làm OLE drop
     target trước, nên con trỏ hiện dấu cấm và không thả được.

  Làm cho nó chạy được có lẽ phải can thiệp cửa sổ con của WebView2 — nhiều công sức hơn
  giá trị của nó. Dùng nút **Thêm thư mục** hoặc `Ctrl+O`. Chi tiết ở
  [`docs/TESTING.md`](docs/TESTING.md).
- **Cửa sổ xem trước biến mất sau 3 giây.** Lệnh `preview` chờ ffplay với thời hạn 3 giây,
  mà hết thời hạn thì `ProcessRunner` giết cả cây tiến trình. Nay khởi chạy tách rời,
  không chờ, không giết.
- **Bấm phát mở trình phát toàn màn hình**, không đóng hay thu nhỏ được, buộc phải nhấn
  Esc. Bỏ cờ `-fs`, thêm tiêu đề và kích thước cửa sổ. Nay màn hình so sánh phát trực
  tiếp trong ứng dụng nên cửa sổ ngoài chỉ còn dùng khi thật cần.
- **Tên tệp trong màn hình so sánh bị đảo** (`02-clip.mp4` thành `clip.mp4-02`) vì dùng
  `direction: rtl` cho cả tên tệp, không riêng đường dẫn.
- **`kind` đặt nhầm chỗ** nên giao diện không biết đâu là video để nhúng trình phát, rơi
  xuống nhánh ảnh xem trước. Đưa xuống từng bên so sánh.

### Đã đổi

- Tên tệp thực thi `UltraCompressor.App.exe` → `UltraCompressor.exe`.
- `docs/PHASE0-REFERENCE.md` và mã nguồn bản gốc đã decompile nằm trong
  `reference/original-csharp/` thay vì thư mục `_ref` ngoài dự án.
