# UltraCompressor

Nén hàng loạt ảnh, video, âm thanh, GIF và PDF theo thư mục. Là bản viết lại của
“UltraCompressor Pro v12”, giữ nguyên tham số nén cũ nhưng sửa hết những chỗ bản gốc
làm mất dữ liệu hoặc báo lỗi sai.

- **.NET 10**, khung chủ WinForms, giao diện là web (HTML/CSS/JS thuần) chạy trong WebView2
- Không cần bước build giao diện, không phụ thuộc npm
- Dữ liệu người dùng đặt ở `%LOCALAPPDATA%\UltraCompressor`, nâng cấp không mất dữ liệu

---

## Cài đặt

```powershell
# Công cụ ngoài lấy từ thư mục tools\ (đặt ffmpeg.exe và gifsicle.exe vào đó)
.\setup.ps1

# Hoặc chỉ định tường minh
.\setup.ps1 -FFmpeg 'D:\tools\ffmpeg.exe' -Gifsicle 'D:\tools\gifsicle.exe'

# Cài kèm runtime .NET để chạy được trên máy chưa cài .NET (tệp lớn hơn nhiều)
.\setup.ps1 -SelfContained
```

Ứng dụng được cài vào `%LOCALAPPDATA%\UltraCompressor` và tạo lối tắt trên desktop.

### Công cụ ngoài

| Công cụ | Cần cho | Ghi chú |
|---|---|---|
| `ffmpeg.exe` | ảnh, video, âm thanh, GIF | Bắt buộc. Lấy từ [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) |
| `gifsicle.exe` | tối ưu GIF thêm | Không có vẫn nén GIF được, chỉ kém hiệu quả hơn |
| `gswin64c.exe` | nén PDF | Cài [Ghostscript](https://ghostscript.com/releases/) bản **đầy đủ** |
| `ffplay.exe` | xem trước trước/sau | Tuỳ chọn |

> **Lưu ý về Ghostscript.** Bản `gswin64c.exe` đi kèm bản v12 cũ chỉ là tệp stub 93 KB
> thiếu DLL, chạy lên báo `Can't load Ghostscript DLL`. Bản này nhận ra tình trạng đó
> ngay khi mở ứng dụng và hướng dẫn cài bản đầy đủ, thay vì âm thầm đánh dấu mọi tệp
> PDF là “giữ nguyên”.

Ứng dụng **chạy thử** từng công cụ chứ không chỉ đọc `--version`: nó tạo một ảnh GIF
nhỏ rồi nén thật, và chạy một lệnh PostScript tối thiểu cho Ghostscript. Lý do: trên
chính máy này, `gifsicle.exe` in cảnh báo “Can't load DLL” rồi vẫn chạy tốt, còn
`gswin64c.exe` báo lỗi tương tự và hỏng thật — chuỗi cảnh báo không đáng tin.

---

## Cách dùng

1. **Thêm thư mục** — bấm nút, bấm `Ctrl+O`, hoặc **kéo thả từ Explorer vào cửa sổ**.
   Thả được cả thư mục lẫn tệp lẻ. Thư mục con được quét tự động. Tệp lẻ được gom về
   thư mục chứa nó, vì ứng dụng luôn làm việc theo thư mục.
2. Chọn **mức nén** và **cách ghi**.
3. **Bắt đầu**. Xem mức tiết kiệm ở cột tương ứng.
4. Mở **Chi tiết** để xem từng tệp, tệp nào bị giữ nguyên và vì sao.
5. **Duyệt** để áp dụng, hoặc **Hoàn tác** để trả bản gốc về.

### Ba cách ghi kết quả

| Cách | Tệp gốc | Bản sao lưu | Khi nào dùng |
|---|---|---|---|
| **Thử trước** (mặc định) | không đụng | không có | Xem trước sẽ tiết kiệm bao nhiêu |
| **Nén thật** | thay thế | `.bak` cùng thư mục | Đã hài lòng với kết quả |
| **Xuất thư mục khác** | không đụng | không có | Giữ nguyên thư mục gốc, lấy kết quả đi nơi khác |

Ở chế độ thử, ứng dụng **nén thật từng tệp để đo** rồi xoá kết quả, nên bạn thấy đúng
những gì sẽ xảy ra chứ không phải con số ước lượng. Bấm **Duyệt** để nén thật và thay thế.

### Phím tắt

| Phím | Tác dụng |
|---|---|
| `Ctrl+O` | Thêm thư mục |
| `Ctrl+Enter` | Bắt đầu |
| `Space` | Tạm dừng / Tiếp tục |
| `?` | Hướng dẫn |
| `L` | Nhật ký |
| `T` | Đổi giao diện sáng/tối |
| `Esc` | Đóng bảng chi tiết |

---

## Tham số nén

Giữ nguyên như bản gốc. Xem `docs/PHASE0-REFERENCE.md` để đối chiếu từng dòng với
code đã decompile.

| | Nhẹ | Cân bằng | Mạnh |
|---|---|---|---|
| Video CRF / preset | 20 / slow | 23 / medium | 28 / veryfast |
| Video chiều rộng tối đa | 3840px | 1920px | 1080px |
| Ảnh `-q:v` | 3 | 5 | 10 |
| Âm thanh | 320k | 192k | 128k |
| GIF `--lossy` | 20 | 40 | 80 |
| PDF `-dPDFSETTINGS` | /prepress | /ebook | /screen |

**Ngưỡng tiết kiệm tối thiểu** là con số độc lập trong Cài đặt, không gắn với mức nén.
Bản gốc gộp nhầm hai thứ này: ngưỡng tăng dần theo mức, nên mức “Mạnh” lại khó đạt
nhất (file phải nhỏ hơn 98% mới được nhận) — ngược với ý đồ.

---

## An toàn dữ liệu

- **Ghi đè là nguyên tử.** Sao chép bản gốc sang `.bak` *trước*, rồi mới thay thế tệp.
  Bản gốc dùng `Move` hai lần; nếu lần hai hỏng thì bản gốc nằm lại trong `.bak` và tệp
  chính biến mất.
- **Không bao giờ ghi đè một `.bak` đã có.** Bản `.bak` đầu tiên là bản gốc nguyên vẹn;
  ghi đè nó nghĩa là mất khả năng quay lại bản thật.
- **Phiên ghi nguyên tử** (tệp tạm rồi thay thế) và báo lỗi đọc thay vì nuốt im lặng.
  Treo máy giữa lúc lưu không làm mất danh sách job.
- **Bộ lọc loại trừ mặc định** bỏ qua `*.bak`. Bản gốc không lọc gì, nên chạy lần hai
  sẽ nén tiếp chính tệp `.bak` mà nó vừa tạo.
- **Bỏ qua tệp kết quả lớn hơn bản gốc**, kèm lý do hiển thị rõ.
- Kiểm tra dung lượng ổ đĩa trước khi nén hàng loạt.

---

## Nhật ký

`%LOCALAPPDATA%\UltraCompressor\logs\ultra-YYYYMMDD.log`, xoay vòng ở 4 MB.

Ở mức `Debug` (đặt trong Cài đặt) nhật ký ghi **nguyên văn từng lệnh ffmpeg** kèm mã
thoát và 20 dòng stderr cuối. Đây là thứ cần để chẩn đoán khi kết quả nén kỳ lạ — bản
gốc không lưu lại gì nên không tra được.

Xem ngay trong ứng dụng: nút **Nhật ký**.

---

## Phát triển

```powershell
dotnet build                                   # build cả solution
dotnet test tests\UltraCompressor.Core.Tests   # 120 test
dotnet run --project src\UltraCompressor.App   # chạy thử, không cần publish
```

### Bố cục

```
src/UltraCompressor.Core/     Lõi, không phụ thuộc giao diện
  Models/                     Job, JobItem, AppConfig, các enum
  Pipelines/                  Một pipeline cho mỗi loại media
  Processes/                  Chạy tiến trình ngoài, đọc output theo dòng
  Scheduling/                 Engine, bộ quét thư mục, cổng tạm dừng, ước lượng ETA
  Storage/                    Giao dịch tệp, hoàn tác, phiên, kiểm tra dung lượng
  Toolchain/                  Tìm và kiểm tra khả năng chạy của công cụ ngoài
  Media/                      Phân tích output của ffmpeg
  Diagnostics/                Nhật ký theo ngày

src/UltraCompressor.App/      Khung chủ + giao diện web
  Bridge/                     Cầu postMessage hai chiều, DTO, AppHost
  wwwroot/                    index.html, styles.css, app.js

tests/UltraCompressor.Core.Tests/   120 test cho lõi

docs/PHASE0-REFERENCE.md     Bảng tham số trích từ bản gốc + 23 lỗi đã tìm ra
reference/original-csharp/   Mã nguồn bản gốc đã decompile bằng ilspycmd, chỉ để đối chiếu
artifacts/                   Ảnh chụp màn hình, media mẫu, script kiểm thử (không commit)
tools/                       ffmpeg.exe, gifsicle.exe — setup.ps1 chép sang thư mục cài
publish/                     Thư mục staging mà setup.ps1 xuất bản vào (không commit)
```

Mọi thứ liên quan tới dự án đều nằm trong thư mục này. Riêng dữ liệu lúc chạy
(`config.json`, phiên làm việc, nhật ký) để ở `%LOCALAPPDATA%\UltraCompressor` — đó là
quy ước của Windows, để nâng cấp app không đụng mất cấu hình của người dùng.

### Vài quyết định thiết kế đáng ghi

**Vì sao WinForms chứ không WPF.** Giao diện là 100% web nên khung chủ chỉ là nơi đặt
WebView2. Bản đầu tiên dùng WPF và gặp lỗi khó chịu: cửa sổ con WebView2 bị cấp kích
thước theo đơn vị logic còn bề mặt vẽ theo điểm ảnh thật, lệch đúng hệ số 1,25 trên
màn hình 125% — mép phải giao diện bị cắt mất. WinForms + `ApplicationHighDpiMode`
xử lý việc này đúng, và đây cũng là tổ hợp được kiểm thử kỹ nhất.

Nếu chuyển khung chủ sang công nghệ khác, khai báo DPI trong manifest.

**Vì sao đo từ bên trong tiến trình.** Chụp màn hình từ PowerShell bị Windows ảo hóa theo
DPI, chỉ lấy được ~80% cửa sổ, rất dễ khiến tưởng giao diện bị cắt trong khi thực tế
không. `LogGeometryAsync` đo trong tiến trình đã khai báo DPI-aware nên con số đáng tin.
`UC_CAPTURE` + `UC_EVAL_JS` + `UC_CAPTURE_DELAY` (biến môi trường) cho phép chụp và thao
tác giao diện khi kiểm thử.

**Cầu web↔lõi.** `postMessage` + JSON, không dùng COM. Trang web không thể tự gọi hàm tuỳ
ý trên máy người dùng; bề mặt chỉ gồm những lệnh khai báo sẵn.

---

## Những lỗi tìm được ở bản gốc

Bảng đầy đủ 23 mục ở `docs/PHASE0-REFERENCE.md`. Vài lỗi đáng chú ý:

- **Nhánh GIF bị đảo ngược.** `if (File.Exists(gifsicle))` nghĩa là *có* gifsicle thì gọi
  gifsicle (không giảm fps, không resize, không palettegen), *thiếu* gifsicle mới gọi ffmpeg.
  Kết quả: filter `fps=15`/`fps=20` là code chết. Bản này luôn chạy ffmpeg trước rồi mới
  gifsicle — mức Mạnh giảm được 44% trên ảnh kiểm thử.
- **Ngưỡng chấp nhận tăng theo mức nén**, khiến mức Mạnh khó đạt nhất.
- **Hủy job lúc đang tạm dừng sẽ treo vô hạn** — vòng chờ không nhìn thấy
  `CancellationToken`.
- **Ghostscript hỏng nhưng báo lỗi im lặng** — không kiểm tra mã thoát, mọi PDF bị đánh
  dấu “giữ nguyên” mà không có lý do.
- **Nút “Duyệt” chỉ xoá `.bak`**, không phải duyệt kết quả; hộp thoại xác nhận còn ghi
  sai nội dung.
- **ETA luôn trễ** vì “đã xử lý” chỉ cộng dồn sau khi tệp xong.
- **Mất EXIF/orientation** khi nén ảnh (thiếu `-map_metadata 0`).
- **EPNG có thể treo** vì không có `-nostdin`.
- Bản gốc dùng đường dẫn tương đối cho công cụ ngoài, nên phụ thuộc thư mục làm việc
  hiện tại.

---

## Kết quả đo thật

Các con số dưới đây đo trên một thư mục thật: 2 tập video 1080p (19–20 phút, h.264
~1500 kb/s) và 2 ảnh JPEG, tổng 442 MB.

| Mức | Video | Ảnh JPEG | Ghi chú |
|---|---|---|---|
| Cân bằng (CRF 23) | −5,5% | giữ nguyên | chỉ được âm thanh 249k→128k |
| Mạnh (CRF 28) | −36,6% | giữ nguyên | đo trên 2 phút đầu |

**Vì sao mức Cân bằng gần như không được gì.** Tập gốc đã nén sẵn ở bitrate thấp
(~1374 kb/s video), nên mã hoá lại ở CRF 23 cho ra thành phẩm lớn hơn bản gốc. Ứng dụng
nhận ra điều đó và **giữ nguyên bản gốc** thay vì ghi đè bằng thứ tệ hơn. Đây là hành vi
đúng, không phải lỗi. Muốn tiết kiệm thật thì dùng mức Mạnh, hoặc nâng ngưỡng chấp
nhận trong Cài đặt.

Chi tiết về quy trình kiểm thử ở [`docs/TESTING.md`](docs/TESTING.md).
Kiến trúc ở [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
