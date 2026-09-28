# Đóng góp

## Trước khi gửi

```powershell
.\check.ps1        # chạy đúng các bước mà GitHub Actions chạy
.\check.ps1 -Fix   # cho phép tự sửa định dạng
```

Một lệnh, đủ bốn bước. Đừng chạy tay từng lệnh — bước kiểm tra định dạng rất dễ sót,
và nó **không làm build hỏng** nên luôn phát hiện muộn: build xong, test xong, push xong,
rồi CI mới đỏ.

Nếu sửa giao diện, chụp lại màn hình và **nhìn tấm ảnh**. Một lỗi tràn chữ trong
bảng đã lọt qua build và test, chỉ thấy được khi nhìn ảnh chụp.

### Về kết thúc dòng

`.gitattributes` ép CRLF cho mọi tệp văn bản và `.editorconfig` khai báo
`end_of_line = crlf`. Hai tệp đó phải khớp nhau. Nếu chỉ có `.gitattributes` thì
`dotnet format` phải đoán mặc định, và kết quả kiểm tra sẽ **khác nhau giữa máy
(CRLF) và CI**: cùng một commit vừa báo lỗi định dạng ở đây lại xanh ở kia, và không
ai biết tin nào đúng.

Sửa `end_of_line` trong `.editorconfig` thì phải sửa cả `.gitattributes` cho khớp.

## Test bắt buộc cho từng thay đổi

| Sửa gì | Test gì |
|---|---|
| Logic lõi, lớp `Core` | test xUnit trong `tests/`, phải có test mới |
| Pipeline nén | chạy thật trên tệp thật, xem [`docs/TESTING.md`](docs/TESTING.md) |
| Giao diện | `UC_CAPTURE` + `UC_EVAL_JS`, nhìn ảnh |
| Đường dẫn công cụ ngoài | sửa `ToolLocator`, kiểm tra cả trường hợp tìm trong `PATH` |

Một test phải **thất bại trước khi sửa**. Viết test sau khi đã sửa xong thì test đó gần
như chắc chắn chỉ kiểm tra lại điều đã đúng.

## Quy ước

- Tiếng Việt cho thông báo lỗi, chú thích và tên test (viết không dấu: `Ten_mo_ta`).
- Comment giải thích **vì sao**, không giải thích **cái gì** — `cái gì` thì đọc code là
  biết. Đặc biệt khi sửa lỗi, ghi lại triệu chứng và nguyên nhân gốc.
- Không thêm thư viện cho việc mà 30 dòng mã viết được.
- Không nuốt lỗi bằng `catch { }` trơn. Nếu phải bỏ qua, để lại lý do.

## Sửa lỗi treo

Treo là lỗi nặng nhất vì người dùng không có đường nào thoát ra. Trước khi sửa, hãy
trả lời:

1. Job đang chờ ở đâu? Thêm log ở điểm đó thay vì đoán.
2. Tín hiệu nào mở lại chỗ đó?
3. Hai bước 1 và 2 có khớp nhau không?

`PauseGate` từng hỏng vì `WaitAsync` tiêu tốn permit còn `Resume` trả permit theo điều
kiện khác — không khớp. Khi thêm cơ chế chặn mới, hãy viết test cho **nhiều lần liên
tiếp**, không chỉ một lần.

## Không commit

- `tools/*.exe` — công cụ nhị phân, lấy riêng theo hướng dẫn cài đặt.
- `reference/original-csharp/` — mã nguồn của bản gốc đã decompile, thuộc bản quyền của
  chủ sở hữu bản gốc; chỉ dùng để đối chiếu tại máy.
- `artifacts/` — ảnh chụp, media mẫu, script kiểm thử tạm.
- `publish/` — thư mục staging của `setup.ps1`.
- `app/` — bản cài do `setup.ps1` tạo ra.
- `data/` — dữ liệu lúc chạy: cấu hình, phiên, nhật ký, tệp nén tạm, profile trình duyệt.
