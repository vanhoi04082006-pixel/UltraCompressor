# Phase 0 — Bảng tham số trích từ bản gốc (v12)

Nguồn: `ilspycmd -p` trên `MassCompressorTool.exe` (namespace `UltraCompressor_Pro_v12`).
File tham chiếu: [`reference/original-csharp/`](../reference/original-csharp).

## 1. Bảng tham số nén (lấy từ bản gốc, đã sửa ba dòng)
Nguồn: `FFmpegHelper.ProcessFileAsync` — các nhánh `if (AppSettings.Level != CompressionLevel.Light) ... else ...`.

| Tham số | Light | Balanced | Strong |
|---|---|---|---|
| **Ngưỡng chấp nhận** (`threshold`) | 0.90 | 0.95 | 0.98 |
| **MP3** `-b:a` | 320k | 192k | 128k |
| **Ảnh** `-q:v` | 3 | 5 | 10 |
| **Ảnh** `-vf scale` | `min(3840,iw)` | `min(1920,iw)` | `min(1080,iw)` |
| **Video** `-crf` | 20 | 23 | 28 |
| **Video** `-preset` | slow | medium | veryfast |
| **Video** `-vf scale` | `min(3840,iw)` | `min(1920,iw)` | `min(1080,iw)` |
| **GIF** `gifsicle --lossy` | 20 | 40 | 80 |
| **PDF** `-dPDFSETTINGS` | /prepress | /ebook | /screen |

> **Hai dòng trên đã cố ý sửa lại — xem [Sự lệch so với bản gốc](#sự-lệch-so-với-bản-gốc).**
> Bảng này ghi tham số **của bản gốc v12**, để đối chiếu. Bảng **đang dùng** nằm ở
> `CompressionProfile.cs` và trong mục *Tham số nén* của hướng dẫn trong ứng dụng.

### Lệnh đầy đủ

**Video** (mọi thứ không thuộc `.mp3/.gif/.pdf/.jpg/.jpeg/.png`):
```
-i "<src>" -c:v libx264 -crf <CRF> -preset <PRESET> -vf "<SCALE>:-2"
-c:a aac -b:a 128k -movflags +faststart "<tmp>" -y
```

**Ảnh** (`.jpg .jpeg .png`):
```
-i "<src>" -q:v <Q> -vf "<SCALE>:-2" "<tmp>" -y
```

**MP3**:
```
-i "<src>" -b:a <320k|192k|128k> -map_metadata 0 "<tmp>" -y
```

**GIF — nhánh ffmpeg** (fallback):
```
-i "<src>" -vf "<FILTER>" -loop 0 "<tmp>" -y
```
với
- Strong: `fps=15,scale=iw*0.8:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse=dither=bayer`
- Light/Balanced: `fps=20,scale=iw:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse`

**GIF — nhánh gifsicle**:
```
--lossy=<20|40|80> -O3 --colors 256 "<src>" -o "<tmp>"
```

**PDF**:
```
-dNOPAUSE -dBATCH -dSAFER -sDEVICE=pdfwrite -dCompatibilityLevel=1.4
-dPDFSETTINGS=</prepress|/ebook|/screen> -sOutputFile="<tmp>" "<src>"
```

**Preview** (ffplay, mở 2 cửa sổ):
```
-window_title "GỐC"  -autoexit "<original>
-window_title "NÉN"  -autoexit "<compressed>
```

## 1a. Sự lệch so với bản gốc

Ba chỗ cố ý khác. Lý do đã ghi ở `CompressionProfile.cs`; ở đây chỉ liệt kê để khi đối
chiếu với bản gốc không tưởng là sơ suất.

| Tham số | Bản gốc | Đang dùng | Vì sao |
|---|---|---|---|
| **Ảnh** `-vf scale` (Mạnh) | `min(1080,iw)` | `min(1600,iw)` | Ảnh nhìn toàn màn hình và có thể phóng to; 1080px thấp cả màn hình 1440px phổ biến nhất. Bản gốc cũng dùng **chung** trần này với video, nên ảnh bị bóp cùng video. |
| **Ảnh** `-vf scale` (Nhẹ) | `min(3840,iw)` | `min(2560,iw)` | 3840px là trần cho **video** 4K. Áp vào ảnh thì thừa rõ — không ai in ảnh 4K từ màn hình. |
| **PDF** (Nhẹ) | `/prepress` | `/default` | `/prepress` là thiết lập cho quy trình in offset: giữ ảnh ở 300dpi và sinh tệp rất lớn. Người dùng chọn mức "Nhẹ" là muốn giữ chất lượng, không phải muốn chuẩn bị in. |

Còn lại giữ nguyên. Riêng bề rộng video ở mức Mạnh nay là 1920 thay vì 1080: CRF 28 đã
đủ để nhỏ tệp, hạ thêm bề rộng là cắt hai lần vào cùng một tệp.

## 2. Ngưỡng chấp nhận — bản gốc có bug

Điều kiện nhận kết quả (giống nhau ở `RunFFmpegAsync` và `ApplyResultAsync`):
```csharp
if ((double)newSize < (double)item.OldSize * threshold)
```
Với `threshold` tăng dần theo mức mạnh, hệ quả là **mức Strong khó đạt nhất**: file phải nhỏ hơn
**98%** mới được nhận, tức giảm ≥2% mới đổi. Đó là ngược với ý đồ "nén mạnh thì siết chặt hơn".

Ảnh hưởng: các file giảm 1–2% ở mức Strong bị đánh dấu "Giữ nguyên" dù đã nén được.
→ **Sửa:** ngưỡng nên là *giảm tối thiểu* độc lập với mức nén, ví dụ `MinSavingRatio` cố định ~1–2%.

## 3. Danh sách bug phát hiện trong bản gốc

| # | Vị trí | Vấn đề |
|---|---|---|
| B1 | `FFmpegHelper.cs:66` | **Nhánh GIF bị đảo ngược.** `if (File.Exists(GifsiclePath))` chạy gifsicle; `else` mới chạy ffmpeg. Hệ quả khi có gifsicle (bản đóng gói luôn có): GIF chỉ được re-optimize, **không giảm fps, không resize, không palettegen**. Filter `fps=15`/`fps=20` là dead code. Ý đồ tác giả nhiều khả năng là ffmpeg → rồi gifsicle. |
| B2 | `FFmpegHelper.cs:116` | `File.Exists(GhostscriptPath)` với path tương đối → phụ thuộc current working directory, không phải thư mục exe. |
| B3 | `FFmpegHelper.cs:41-48` | Ngưỡng tăng theo mức mạnh (mục 2). |
| B4 | `FFmpegHelper.cs:136,153` | Nhánh Ghostscript **không kiểm tra exit code**, không capture stderr. GS fail → temp không tồn tại → `ApplyResultAsync` im lặng set `Skipped = true`, không lý do. Tương tự gifsicle (`:85`). |
| B5 | `gswin64c.exe` trong bản đóng gói | File 0.1 MB là **stub hỏng**: `Can't load Ghostscript DLL`. Cần full bộ cài Ghostscript. Đã xác minh trên máy. |
| B6 | `FFmpegHelper.cs:311-313` | `File.Move(src, bak)` rồi `File.Move(tmp, src)` — **không nguyên tử**. Move thứ hai lỗi ⇒ file gốc nằm lại trong `.bak`, job hỏng. |
| B7 | `FFmpegHelper.cs:308-310` | Xoá `.bak` cũ trước khi backup ⇒ chạy lần hai là mất khả năng undo về bản gốc thật. |
| B8 | `FFmpegHelper.cs` (không có `-nostdin`) | ffmpeg có thể đọc stdin và treo. |
| B9 | `FFmpegHelper.cs:167` | Ảnh không dùng `-map_metadata 0` ⇒ **mất EXIF/orientation** khi nén ảnh JPEG. |
| B10 | `FFmpegHelper.cs:52-63` | MP3 mức Light ép `-b:a 320k` kể cả khi nguồn đã 128k ⇒ up-convert vô nghĩa, chỉ tăng size. |
| B11 | `FFmpegHelper.cs:249,257` | Regex `(\d{2}):(\d{2}):(\d{2})\.(\d{2})` chỉ nhận đúng 2 chữ số → video trên 99 giờ không có progress. |
| B12 | `FFmpegHelper.cs:286-292` | Ảnh không có progress thật, hard-code 50%. |
| B13 | `MainForm.cs:363` | `IsImage = true` cho cả `.gif`, nhưng `.gif` đi nhánh riêng ⇒ dùng `IsImage` để quyết định có parse progress hay không là sai. |
| B14 | `MainForm.cs:395` | `MessageBox.Show` **từ luồng background** khi quét folder xong (`Task.Run` + `AddJobFromPath` chạy trong đó không, nhưng `SessionManager.Save` và cập nhật UI thì có). `grid.Refresh()` trên `uiTimer` chạy UI thread nhưng model bị ghi từ nhiều thread. |
| B15 | `MainForm.cs:406-410` | Vòng `while (globalPaused || job.JobPaused) { await Task.Delay(500); }` — **nuốt CancellationToken**. Hủy job khi đang pause ⇒ treo vô hạn. |
| B16 | `MainForm.cs:406` | Không `token.ThrowIfCancellationRequested()` trong vòng pause ⇒ cũng treo. |
| B17 | `MainForm.cs:419` | `item.CurrentPercent` ghi từ luồng ffmpeg, không lock. |
| B18 | `MainForm.cs:499-503` | ETA tính từ `BytesOriginal` của job đang chạy nhưng `BytesOriginal` chỉ cộng dồn **sau khi** file xong (`MainForm.cs:428`) ⇒ tốc độ bị trễ, ETA lúc đầu vô nghĩa. |
| B19 | `SessionManager.cs:8,40,52` | `catch { }` nuốt mọi lỗi — session hỏng thì âm thầm mất dữ liệu người dùng. Ghi file không atomic ⇒ crash giữa lúc ghi là hỏng session. |
| B20 | `MainForm.cs:611-616` | Hoàn tác nuốt lỗi `catch { }`, reset counter có bug chuỗi gán `num2/num4/processedCount` (vô hại vì đều = 0 nhưng rõ ràng là copy-paste loãng). |
| B21 | `MainForm.cs:559` | Nút "Duyệt" **xoá toàn bộ `.bak`** — không phải "duyệt kết quả" mà là hủy khả năng khôi phục. Confirm dialog ghi sai ("Xóa backup folder"). |
| B22 | Toàn bộ | Thiếu `-nostdin`, thiếu `ffprobe`, thiếu bộ lọc loại trừ (`.bak` của lần trước sẽ bị quét lại và nén tiếp), không kiểm tra dung lượng ổ đĩa, không log lệnh. |
| B23 | `MainForm.cs:641` | `if (!File.Exists("ffmpeg.exe"))` — tương đối với CWD, như form load bằng đường dẫn tuyệt đối ⇒ cảnh báo sai. |

## 4. Quyết định cho bản 2.0

Giữ nguyên bảng tham số mục 1 (hành vi nén là thứ người dùng đã quen và đã đo).
Sửa B1–B23, cộng các tính năng mới trong `docs/PLAN.md` §11.
