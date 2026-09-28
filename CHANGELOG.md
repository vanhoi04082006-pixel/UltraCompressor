# Nhật ký thay đổi

Các mốc theo ngày. Mục **Đã sửa** liệt kê lỗi của bản gốc v12; mục **Lỗi tìm khi kiểm
thử** liệt kê lỗi do chính bản viết lại này tạo ra.

## 2.0.0 — bản viết lại

### Thêm

- Kéo thả thư mục hoặc tệp từ Explorer vào cửa sổ. WebView2 không có sự kiện "đã thả"
  ở phía .NET nên sự kiện HTML5 được xử lý trong `wwwroot/app.js`; đường dẫn lấy từ
  `file.path` của WebView2, dự phòng bằng `entry.fullPath`.
- Lớp phủ "Thả vào đây" hiện lên khi kéo vào cửa sổ.
- Lệnh cầu `log` để giao diện ghi vào nhật ký — thao tác kéo chuột không làm được bằng
  script, nên nhật ký là manh mối duy nhất khi nó hỏng.
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
- **Nhận khi im lặng khi job kẹt** — dòng debug về từng tệp bị thiếu nên không phân biệt
  được trạng thái.

### Đã đổi

- Tên tệp thực thi `UltraCompressor.App.exe` → `UltraCompressor.exe`.
- `docs/PHASE0-REFERENCE.md` và mã nguồn bản gốc đã decompile nằm trong
  `reference/original-csharp/` thay vì thư mục `_ref` ngoài dự án.
