# Nhật ký thay đổi

Các mốc theo ngày. Mục **Đã sửa** liệt kê lỗi của bản gốc v12; mục **Lỗi tìm khi kiểm
thử** liệt kê lỗi do chính bản viết lại này tạo ra.

## Chưa đánh số — tìm kiếm thích ứng theo nội dung

### Thêm

Nén video bằng một bộ tham số cố định cho mọi tệp là sai ở một chỗ: **mỗi tệp có độ khó
khác nhau**, nên tham số tốt cho tệp này lại tệ cho tệp kia. Nay có đường thích ứng: sinh
ứng viên theo đặc tính nội dung, **đo thật** trên các đoạn đại diện, rồi mới encode toàn
tệp ứng viên được chọn.

- **Lưới an toàn sau khi nén** — `QualityGate` đo VMAF từng đoạn đại diện sau khi nén và giữ
  bản gốc nếu ứng viên rớt ngưỡng. Đây là bước quyết định, không phải bước trang trí.
- **`RepresentativeWindowSelector`** chọn đoạn đại diện theo nội dung, không theo vị trí.
- **`CandidatePlanner`** sinh nhiều ứng viên có cấu trúc thay vì một CRF.
- **`PilotSearch`** đo thật: coarse → khoanh biên → loại sớm.
- **Đường chạy nhị phân trên thang điểm từng nhanh**, dừng sớm có điều kiện.
- **Chọn codec theo nội dung** — thang CRF của HEVC lệch 5 chứ không phải 2 so với H.264.
- **Công cụ ngoài mang ngữ nghĩa**: `EncoderOptions` là nơi duy nhất được biết tên công tắc
  ffmpeg, nên `-crf` của x26x không bao giờ lẫn với `-qp` của SVT-AV1 hay `-cpu-used` của
  libaom.
- **Nhánh giữ bản gốc** — `ORIGINAL` trở thành một ứng viên ngang hàng, được chọn **bằng
  bằng chứng** chứ không phải vì không tìm được gì. Với thư viện đã nén sẵn tốt, kết quả
  đúng là **không nén gì cả**.
- **`SizeEstimator`** hiệu chỉnh trên encode toàn tệp thật, đọc overhead từ box MP4.
- **`OutputContainer`** là nguồn sự thật duy nhất cho container đầu ra.
- **`TemporalProbe`** đọc trục thời gian thật: mốc bắt đầu, timebase, tần số khung, PTS
  khung đầu, tổng số khung.

### Sửa

- **Tệp mang tên sai nội dung.** `clip.ts` chứa byte MP4 (`ftyp isom`): muxer do phần mở rộng
  tệp đích quyết định, mà đường dẫn đích lại dựng theo tệp nguồn. Nay `OutputContainer.ApplyContract`
  là nơi duy nhất được quyết định tên đích, và **không bao giờ ghi đè tệp đã có**.
- **`-movflags +faststart` bị nuốt lặng lẽ** với Matroska và MPEG-TS; nguồn `.webm` có tiếng
  **hỏng hẳn** vì lệnh dùng `-c:a aac` mà WebM không nhận.
- **`log_path` của libvmaf không nhận đường dẫn tuyệt đối kiểu Windows** — dấu `:` trong `C:`
  phá cú pháp filtergraph, và bốn kiểu escape đều bị ffmpeg bỏ qua lặng lẽ: exit 0, không
  tệp log. Cần `log_fmt=json` vì phải tự tính P5 theo khung.
- **Đo phải cắt tham chiếu thành clip, không seek thẳng vào nguồn.** Đo trên nguồn nguyên vẹn
  cho VMAF 41,3 thay vì 93,5 trên nội dung chuyển động — sai một cách rất tinh vi, vì hai bên
  lệch nửa khung hình.
- **`settb`/`setpts` không sửa được lệch timestamp.** Ghi lại vì rất dễ hiểu nhầm là chúng sửa
  được: chúng giữ tầng phòng ngừa, còn nguyên nhân nằm ở nơi nội dung khung hình lệch sau khi
  giải mã.
- **`SizeEstimator` đọc nhầm bitrate âm thanh**, làm cho kế hoạch nén không bao giờ thích ứng.
- **Lưu cấu hình tắt mất cờ đang bật.** `EnableAdaptiveSearch` không có trong danh sách trường
  được chép, nên mỗi lần bấm "Lưu cấu hình" đã tắt cờ — và vì thao tác lưu ghi lại chính đối
  tượng đó, giá trị tắt còn nằm trong tệp.

### Người dùng

- **Cờ "Tìm kiếm thích ứng theo nội dung"** trong Cài đặt, **mặc định tắt**, kèm giải thích
  chi phí encode. Trước đó tính năng này tồn tại trong kho nhưng không ai bật được — chỉ sửa
  tay trong `config.json`.
- Cờ đó được chép như mọi trường giao diện khác. Trước đây nó nằm ở nhánh "giữ nguyên", nên ô
  bật mới sẽ là **nút mù**: bật, bấm Lưu, và cờ tắt lại.

### Đo thật

| Mức | Tập video 1080p | Ảnh JPEG |
|---|---|---|
| Cân bằng | −5,5% | giữ nguyên |
| Mạnh | −36,6% | giữ nguyên |

**Chỉ là kết quả của đường cũ.** Thang VMAF hiệu chỉnh trên 2 tệp anime 1080p; xem
[`docs/QUALITY-CALIBRATION.md`](docs/QUALITY-CALIBRATION.md) — bộ đó là **provisional**, chưa
phổ quát. `ORIGINAL` chỉ được chọn khi bộ ước lượng được chứng nhận, và hiện nó **không**.

### Chưa làm (ghi rõ thay vì giấu)

- **Tương ứng khung hình VMAF: CHƯA chứng minh** cho đường *clip thử nghiệm đối chiếu bản mã
  hoá toàn tệp*. Đo được hai trường hợp và cả hai đều khớp ở offset 0: hai clip cùng GOP
  (72 khung, PTS 0, VMAF **94,64**) và hai clip **khác** GOP (VMAF **94,59**). Giả thuyết "lệch
  lưới keyframe" đã bị loại trừ bằng chính phép đo đó. Biên tìm offset **không được mở rộng**,
  và có lý do: bỏ một khung làm điểm rơi từ 94,6 xuống 32,0 — đó là vác, nên khi lưới cuối chọn
  "lệch 1 khung" thì nó đang bám cách giải thích sai. Xem `docs/TESTING.md`.
- **Encoder phần cứng không dùng.** Đã kiểm chứng trên máy này: `hevc_nvenc` và `hevc_qsv`
  chạy được và ra HEVC hợp lệ, `hevc_amf` hỏng vì thiếu `amfrt64.dll`. Không dùng vì CQ của
  chúng là thang riêng chưa đo, còn `SizeEstimator` chỉ hiệu chỉnh trên encoder phần mềm.
- **Ảnh, âm thanh, GIF, PDF vẫn chỉ quyết định bằng kích thước** — không có metric cảm nhận.
- **Chưa chạy encode thật nào trong kho hiệu chỉnh CQ phần cứng**, nên chưa có đường cong
  CQ↔chất lượng cho chúng.

## Chưa đánh số — tham số nén theo loại, xử lý tuần tự, hiện mức của job

### Đã sửa

- **Bề rộng tối đa tách riêng cho ảnh và video.** Cả hai dùng chung một trường
  `MaxWidth`, nên chọn "Mạnh" thì **cả ảnh lẫn video** đều bị bóp về 1080px, còn
  "Nhẹ" thì ảnh được phép to tới 3840px — thừa rõ cho một bức ảnh chụp. Ảnh và
  video bị ràng buộc bởi hai thứ khác nhau: ảnh nhìn toàn màn hình và có thể
  phóng to, video đã bị giới hạn bởi khung hình mà mắt theo kịp.

  | | Bề rộng video | Bề rộng ảnh | `-q:v` | CRF |
  |---|---|---|---|---|
  | Nhẹ | 3840 | 2560 | 3 | 20 |
  | Cân bằng | 1920 | 1920 | 5 | 23 |
  | Mạnh | 1920 | 1600 | 10 | 28 |

  Video ở "Cân bằng" và "Mạnh" đều giữ 1920px: CRF 28 đã đủ để nhỏ tệp, hạ thêm
  bề rộng nữa là cắt hai lần vào cùng một tệp.
- **PDF ở mức "Nhẹ" đổi từ `/prepress` sang `/default`.** `/prepress` là thiết
  lập cho quy trình in offset — giữ ảnh ở 300dpi và sinh tệp rất lớn. Người dùng
  chọn "Nhẹ" là muốn giữ chất lượng, không phải muốn chuẩn bị in.
- **Kéo thả: hai lỗi chồng nhau, trước đó chưa bao giờ hoạt động.**
  1. `DragAcceptFiles` nằm ở **shell32.dll**, không phải user32.dll. Khai báo sai
     DLL → `EntryPointNotFoundException` ngay lúc P/Invoke, mà lời gọi nằm trong
     try/catch nên ứng dụng vẫn chạy bình thường — chỉ có cờ `WS_EX_ACCEPTFILES`
     không bao giờ được bật, và kéo thả im lặng không hoạt động.
  2. Hằng `WS_EX_ACCEPTFILES` là **0x00000010**, không phải 0x00080000 (cái sau là
     `WS_EX_LAYERED`). Sai hằng khiến chính phần chẩn đoán báo "không nhận thả"
     trong khi cờ đã bật đúng, rồi đi tìm một lỗi không tồn tại.
- **Cửa sổ không còn tràn ra ngoài màn hình.** `FitToWorkArea` lấy nguyên vùng
  làm việc làm `ClientSize` (đó là kích thước *ngoài*, cộng thêm thanh tiêu đề và
  viền thì vượt) rồi nới ra mà không canh lại vị trí — cửa sổ được canh giữa khi
  còn 1280px nên nới lên 1918px thì mép phải = 2238, tràn ~318px ra ngoài màn hình
  1920. Hậu quả thấy được: cột thao tác, khối "Tốc độ / Còn lại / Luồng nén" và
  status bar đều bị cắt.
- **Bảng không còn tràn ngang.** `max-width` trên `<td>` không có tác dụng với
  `table-layout: auto`, nên một tên tệp dài đẩy cột thao tác ra ngoài khung.
- **Bộ lọc trên bảng chi tiết không bị ghi đè.** Mỗi lần đẩy trạng thái (250 ms)
  gửi toàn bộ tệp của job, nên mỗi phím tìm kiếm chỉ sống được tới lần đẩy kế tiếp.
- **Thứ tự tệp ổn định.** Bảng chi tiết sắp theo `SavedBytes` giảm dần, tức đảo
  lại mỗi khi một tệp xong — không theo dõi được tệp nào đang chạy bao nhiêu %.
- **Bớt I/O ở tầng giao diện.** `File.Exists` cho `.bak` chạy 4 lần/giây cho mọi
  tệp đã nén; nay có bộ nhớ đệm 3 giây, bị xoá khi Duyệt / Hoàn tác.
- **`setup.ps1` chạy được trên Windows PowerShell 5.1.** `[string]::IsNullOrWhiteSpace`
  chỉ có từ .NET Core 2.0, nên script văng lỗi ngay dòng đầu khi chạy bằng
  `powershell.exe`.

### Đổi

- **Job được xử lý tuần tự theo đúng thứ tự thêm vào.** Trước đây `Task.WhenAll` —
  tất cả job cùng chạy. Với danh sách nhiều thư mục thì bản đồ hóa 20 tập phim
  cùng lúc, mỗi job một tiến trình ffmpeg, tốc độ tổng tụt và máy nghẽn. Bên
  trong một job vẫn chạy song song tới giới hạn luồng — đó là chỗ tốn thời gian
  thật sự.
- `CompressionProfile.ScaleFilter` → `VideoFilter` và `ImageFilter`.
- `setup.ps1` mặc định cài vào `<dự án>\app`, có `-RemoveLegacy` để dọn ổ C:.

### Thêm

- **Mức nén hiện trên từng dòng job.** Mức được chụp lúc thêm thư mục, nên đổi
  dropdown *sau khi* đã thêm thì job cũ vẫn nén bằng mức cũ — và bằng thị giác
  không có cách nào biết. Nay lệch mức thì badge đổi sang màu cảnh báo và dòng đó
  có nút **⟳** để áp dụng mức đang chọn ngay. Lệnh `applyLevel` chỉ đổi mức, không
  nén lại tệp đã xử lý, và từ chối khi job đang chạy.
- **Hỗ trợ tệp lẻ.** `ScanFile` / `AddFile` / `AddPaths` (tự phân biệt thư mục với
  tệp), nút **Thêm tệp** (`Ctrl+Shift+O`), hộp chọn đa chọn với bộ lọc dựng từ
  chính `MediaClassifier`.
- **Kéo thả thư mục / tệp vào cửa sổ**, có lớp phủ báo hiệu khi đang kéo.
- **Cột `%` luôn hiện cho mọi tệp** trong bảng chi tiết, tăng dần khi đang nén và
  giữ lại 100% khi xong.
- **Nút chọn thư mục đích** khi xuất kết quả, thay vì phải gõ tay.
- **Có "Thư mục dự án"** ở thanh trạng thái và trong Cài đặt.

### Ghi chú kỹ thuật

- Kéo thả **không** dùng được API `CoreWebView2.DragOver` / `DragDrop`: SDK
  `Microsoft.Web.WebView2` 1.0.4191.47 đã bỏ hẳn (đã kiểm tra bằng reflection — 497
  kiểu xuất ra, không có `CoreWebView2DragDropEventArgs`, không thành viên nào
  chứa "Drop" ngoài `AllowExternalDrop`). Dùng `WM_DROPFILES` trên HWND của Form.


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
