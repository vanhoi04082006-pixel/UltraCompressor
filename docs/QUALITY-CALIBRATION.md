# Hiệu chỉnh cổng chất lượng

> **Đây là tập dữ liệu hiệu chỉnh KHỞI ĐẦU, không phải hiệu chỉnh phổ quát.**
>
> 36 điểm đo trên **2 tệp cùng một thể loại** (anime 1080p). Chưa có số liệu cho: talking
> head, gameplay, thể thao/chuyển động mạnh, quay/chụp màn hình và chữ, video tối, nhiễu hạt,
> thiên nhiên nhiều chi tiết, nguồn bitrate thấp, nguồn bitrate cao, nguồn H.264/HEVC/AV1, và
> 720p/1080p/1440p/4K.
>
> Hệ thống được thiết kế để bổ sung loại nội dung mới **tune policy mà không sửa thuật toán
> lõi**: ngưỡng nằm trong bảng tra của `QualityPolicy`, cách đo nằm sau `QualityProbe`.
> **Không được đổi ngưỡng ở đây nếu chưa có số đo chứng minh.**

## Encoder phần cứng: đã kiểm chứng được chạy, nhưng không dùng

Đo trên chính máy này, bản ffmpeg 8.0.1-essentials đóng gói, clip thử 1280×720 2 giây:

| Encoder | Kết quả |
|---|---|
| `hevc_nvenc` | chạy được — ra HEVC Main hợp lệ, 2,00s, yuv420p |
| `hevc_qsv` | chạy được — ra HEVC Main hợp lệ, 2,00s, yuv420p |
| `hevc_amf` | **hỏng** — `amfrt64.dll failed to open` (không có phần cứng AMD/driver) |

Cả hai encoder chạy được đều nhận `-cq`, nên **công tắc không sai**.

Vẫn **không dùng**, vì lý do không phải "không chạy được":

1. **CQ là thang riêng của từng họ**, không cùng nghĩa với CRF của x264/x265. Bảng ở trên rút
   ngưỡng từ CRF — dùng nó cho CQ là so hai thứ không cùng đơn vị.
2. **`SizeEstimator` chỉ hiệu chỉnh trên encoder phần mềm.** Thêm ứng viên phần cứng vào tìm
   kiếm là bộ ước lượng đoán sai kích thước, sai kiểu mà vẫn ra một tệp trông hợp lý.
3. **Chưa có đường cong CQ↔chất lượng** để hiệu chỉnh lại bộ ước lượng đó.

Muốn đưa vào thì phải đo trước: một đường cong CQ→VMAF cho từng họ, trên cùng bộ tệp đã
dùng ở đây. Đó là việc đo thật, chưa nằm trong phạm vi hiện tại.

Ngưỡng VMAF trong `QualityPolicy` **không** lấy từ tài liệu sản phẩm. Chúng rút từ lần
đo thật trên thư viện của người dùng, và bài viết này ghi lại lần đo đó để con số có
nguồn, và để lần sau đổi máy đổi bản dựng thì kiểm tra lại được.

## Vì sao không dùng số của đặc tả

Đặc tả viết ngưỡng cho **VMAF v1** (thế hệ Netflix phát hành 6/2026). Bản ffmpeg
8.0.1 đóng kèm dự án này không chạy được v1, nên đang dùng **v0.6.1neg**. Cùng một mức
chất lượng, v0.6.1neg cho điểm thấp hơn. Dùng lại nguyên xi số của v1 sẽ siết nén quá
tay: cùng một clip sẽ rớt ngưỡng ở CRF cao hơn người dùng chịu được.

## Bảng đo

Nguồn: hai tệp thật trong thư viện, cắt 3 cửa sổ 6 giây rải đều mỗi tệp, cửa sổ đo ở
1080p. `bpp` là bit trên mỗi điểm ảnh mỗi khung của ứng viên.

### Clip 1 — `Aku no Onna Kanbu - 01.mp4`, 1920×1080, 23,98 fps, **bpppf nguồn = 0,0105**

| Cửa sổ | Codec | CRF | VMAF | P5 | min | SSIM | bpp |
|---|---|---:|---:|---:|---:|---:|---:|
| 270 s | x264 | 18 | 94,89 | 93,72 | 93,52 | 0,9995 | 0,093 |
| 270 s | x264 | 22 | 92,37 | 90,80 | 90,59 | 0,9989 | 0,062 |
| 270 s | x264 | 26 | 88,30 | 86,08 | 85,76 | 0,9978 | 0,043 |
| 270 s | x264 | 30 | 81,88 | 78,83 | 78,12 | 0,9958 | 0,030 |
| 270 s | x264 | 34 | 72,35 | 68,12 | 66,79 | 0,9920 | 0,022 |
| 270 s | x264 | 38 | 58,67 | 53,30 | 51,78 | 0,9844 | 0,016 |
| 270 s | x265 | 18 | 95,89 | 94,65 | 94,27 | 0,9994 | 0,056 |
| 270 s | x265 | 22 | 94,32 | 92,75 | 92,42 | 0,9990 | 0,035 |
| 270 s | x265 | 26 | 92,02 | 89,94 | 89,35 | 0,9982 | 0,023 |
| 270 s | x265 | 30 | 88,36 | 85,78 | 83,85 | 0,9969 | 0,017 |
| 270 s | x265 | 34 | 82,24 | 77,95 | 75,78 | 0,9943 | 0,013 |
| 270 s | x265 | 38 | 72,08 | 65,47 | 62,87 | 0,9887 | 0,011 |
| 900 s | x265 | 18 | 96,68 | 96,11 | 95,71 | 0,9997 | 0,042 |
| 900 s | x265 | 22 | 95,53 | 94,78 | 94,24 | 0,9994 | 0,026 |
| 900 s | x265 | 26 | 93,73 | 92,67 | 91,15 | 0,9989 | 0,017 |
| 900 s | x265 | 30 | 90,91 | 89,73 | 86,52 | 0,9981 | 0,012 |
| 900 s | x265 | 34 | 85,81 | 83,47 | 79,23 | 0,9962 | 0,010 |
| 900 s | x265 | 38 | 77,25 | 73,41 | 68,08 | 0,9920 | 0,008 |
| 1440 s | x265 | 18 | 97,14 | 95,50 | 95,24 | 0,9995 | 0,065 |
| 1440 s | x265 | 22 | 95,79 | 94,16 | 93,77 | 0,9990 | 0,040 |
| 1440 s | x265 | 26 | 93,72 | 91,66 | 90,73 | 0,9983 | 0,026 |
| 1440 s | x265 | 30 | 90,38 | 87,97 | 85,59 | 0,9970 | 0,019 |
| 1440 s | x265 | 34 | 84,74 | 81,74 | 76,92 | 0,9944 | 0,015 |
| 1440 s | x265 | 38 | 75,37 | 70,16 | 64,66 | 0,9888 | 0,011 |

### Clip 2 — `Boku no Risou no Isekai Seikatsu - 01.mp4`, 1920×1080, 47,95 fps, **bpppf nguồn = 0,0087**

| Cửa sổ | Codec | CRF | VMAF | P5 | min | SSIM | bpp |
|---|---|---:|---:|---:|---:|---:|---:|
| 149 s | x265 | 18 | 96,35 | 95,33 | 94,97 | 0,9995 | 0,034 |
| 149 s | x265 | 22 | 95,40 | 93,97 | 93,40 | 0,9990 | 0,023 |
| 149 s | x265 | 26 | 93,74 | 91,64 | 90,94 | 0,9981 | 0,016 |
| 149 s | x265 | 30 | 90,18 | 87,51 | 86,59 | 0,9967 | 0,011 |
| 149 s | x265 | 34 | 83,41 | 80,65 | 80,09 | 0,9935 | 0,008 |
| 149 s | x265 | 38 | 73,69 | 71,27 | 69,14 | 0,9874 | 0,006 |
| 496 s | x265 | 22 | 94,52 | 93,46 | 90,22 | 0,9995 | 0,009 |
| 496 s | x265 | 26 | 93,22 | 91,12 | 86,47 | 0,9991 | 0,007 |
| 496 s | x265 | 30 | 90,77 | 87,14 | 80,49 | 0,9982 | 0,005 |
| 496 s | x265 | 34 | 85,74 | 81,03 | 72,08 | 0,9962 | 0,004 |
| 496 s | x265 | 38 | 77,61 | 69,41 | 58,72 | 0,9920 | 0,003 |

## Ba kết luận rút ra từ số đo

### 1. SSIM không dùng làm cổng được

Ứng viên VMAF **72** (đã rõ là hỏng) vẫn cho SSIM **0,9887**. Ngưỡng SSIM kiểu đặc tả
(0,985) cho qua tới mức ứng viên tệ. Đây không phải suy luận: nó nằm ngay trong bảng
trên, ở hàng `270 s / x265 / CRF 38`.

SSIM vẫn được đo và ghi lại, vì hữu ích khi chẩn đoán "sao tệp này trông lạ", nhưng
`QualityPolicy.Accepts` **chỉ nhìn VMAF**.

### 2. P5 phải có, không tùy chọn

Khoảng cách `mean − P5` dao động 1,2…7 điểm tuỳ cảnh, và `min` thấp hơn nữa — hàng
`270 s / x265 / CRF 30` có mean 88,36 nhưng min 83,85. Chỉ nhìn mean thì một tệp có vài
cảnh hỏng vẫn đạt. Vì vậy ngưỡng đặt thành cặp, và `P5` thấp hơn `mean` khoảng 4 điểm.

### 3. Ngưỡng đề xuất, từ cột trên

| Mode | mean | P5 | CRF x265 tương ứng (cửa sổ dễ / cửa sổ khó) |
|---|---:|---:|---|
| Nhẹ | 93 | 89 | 18–26 / 18–22 |
| Cân bằng | 89 | 85 | 30 / 26–30 |
| Mạnh | 84 | 80 | 34 / 34 |

## Phát hiện quan trọng nhất: thư viện này không nên nén lại

Cột `bpppf nguồn` là **0,0105** và **0,0087**. Ở 1080p24, 0,0105 tương đương khoảng
**520 kbit/s** cho toàn bộ tệp — mỗi tệp 30 phút nằm gọn trong 113 MB.

Nhưng ứng viên HEVC chất lượng tốt nhất trong bảng:

- CRF 18, cửa sổ 270 s → bpp 0,056, tức khoảng **2,8 Mbit/s**
- CRF 26 → bpp 0,023, tức khoảng **1,15 Mbit/s**
- CRF 38 (VMAF 72, đã hỏng) → bpp 0,011, mới ngang bằng nguồn

Nghĩa là **mọi mức CRF còn lại chất lượng đều cho tệp lớn hơn bản gốc**. Tệp nguồn đã
nén sẵn tốt hơn bất cứ lần nén lại nào ở cùng độ phân giải.

HEVC vẫn thắng H.264 khoảng 50% ở cùng CRF — đó là kết quả đo được trước đây và là lý
do phải có bước đo. Nhưng so với *chính tệp nguồn* thì thua.

Hệ quả với sản phẩm: với thư viện này, kết quả đúng là **không nén gì cả**, chứ không
phải nén. Đây chính là quy tắc `RETURN_ORIGINAL` trong đặc tả, và nó là một yêu cầu
chứ không phải tuỳ chọn: nếu không có nó, công cụ sẽ **làm phình** tệp người dùng.

## Hai lỗi kỹ thuật lộ ra khi hiệu chỉnh

Cả hai đều đã sửa trong mã, ghi lại vì chúng đều im lặng — không báo lỗi, chỉ cho kết
quả sai.

**`log_path` của libvmaf không nhận đường dẫn tuyệt đối kiểu Windows.** Dấu `:` trong
`C:` phá vỡ cú pháp filtergraph, và bốn kiểu escape khác nhau (`C\:/...`, trong dấu nháy
đơn, dấu gạch chéo ngược, không escape) đều bị ffmpeg bỏ qua **lặng lẽ**: exit 0, không
dòng lỗi nào, không có tệp log. Cách đúng là cho ffmpeg chạy với thư mục làm việc là
thư mục tạm và truyền tên tệp trần — `ProcessRunner.RunAsync` nay có tham số
`workingDirectory` cho việc này.

**`log_path` phải kèm `log_fmt=json`.** Không có nó, tệp tên đuôi `.json` vẫn nhận nội
dung XML, và `JsonDocument.Parse` ném lỗi. Cần JSON vì phải tự tính P5 từ điểm theo
khung — libvmaf chỉ in điểm gộp, mà điểm gộp không có phân vị thấp.

**Ngoài ra:** một lần đo hỏng vì hết giờ ở cảnh chuyển động mạnh 1080p với x265 CRF 18.
Mốc 120 giây nâng lên 240 giây. Cửa sổ đo ở giai đoạn sau lấy ở độ phân giải thấp hơn
nên sẽ nhanh hơn nhiều.

## Cách chạy lại

Bộ hiệu chỉnh là chương trình tạm, không nằm trong kho mã. Nguyên tắc khi hiệu chỉnh lại:

1. Chọn ít nhất 2 tệp thật, nhiều loại nội dung (anime, quay/chụp màn hình, chuyển động).
2. Cắt 3 cửa sổ 6 giây rải đều mỗi tệp.
3. Với mỗi cửa sổ, encode ở các mức CRF rồi đo bằng `QualityProbe`.
4. Vẽ đường biên Pareto (bpp ngang, VMAF dọc) để xem mode có chọn gần biên không.
5. Chỉnh bảng trong `QualityPolicy`, ghi kết quả vào đây.
