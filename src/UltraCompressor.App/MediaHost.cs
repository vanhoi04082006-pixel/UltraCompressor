using System.Globalization;
using System.IO;
using Microsoft.Web.WebView2.Core;
using UltraCompressor.Core.Media;

namespace UltraCompressor.App;

/// <summary>
/// Phục vụ tệp media cho trang web qua một virtual host riêng.
///
/// Trang chạy trên <c>https://app.local</c> nên không tự tải được <c>file:///C:/...</c>.
/// Thay vì mở toàn bộ thư mục (lộ mọi tệp trên máy), mỗi tệp được đăng ký một mã ngẫu
/// nhiên và URL là <c>https://media.local/&lt;mã&gt;</c>. Trang chỉ biết mã, không biết
/// đường dẫn, và muốn xem tệp nào ngoài danh sách đăng ký thì không có cách nào.
///
/// <para><b>Hỗ trợ <c>Range</c> là bắt buộc, không phải tuỳ chọn.</b> Chromium (và cả
/// <c>&lt;video&gt;</c> lẫn <c>&lt;audio&gt;</c>) luôn gửi <c>Range</c> khi tua. Nếu ta trả
/// <c>200</c> cho mọi yêu cầu thì mỗi lần tua nó phải đọc lại tệp từ byte 0 — đó là lý do
/// màn hình so sánh giật và tua nghẹn so với VLC, vốn seek thẳng trên tệp. Trả
/// <c>206 Partial Content</c> với <c>Content-Range</c> đúng thì Chromium chỉ đọc đúng
/// đoạn nó cần.</para>
///
/// Chỉ dùng cho màn hình so sánh: nhờ đó hai bản gốc và bản nén phát cùng lúc trong chính
/// ứng dụng, thay vì bắt người dùng mở hai cửa sổ trình phát bên ngoài.
/// </summary>
public sealed class MediaHost
{
    public const string Origin = "https://media.local";

    private const int BufferSize = 256 * 1024;

    private readonly Dictionary<string, string> _byToken = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Đăng ký một tệp và trả về URL để nhúng. Tệp không tồn tại thì trả null.</summary>
    public string? Register(string path)
    {
        if (!File.Exists(path)) return null;

        lock (_gate)
        {
            // Cùng một tệp đăng ký nhiều lần vẫn trả cùng một URL, để so sánh lại không
            // sinh ra mã mới mỗi lần bấm.
            foreach (var pair in _byToken)
            {
                if (string.Equals(pair.Value, path, StringComparison.OrdinalIgnoreCase)) return $"{Origin}/{pair.Key}";
            }

            var token = Guid.NewGuid().ToString("N");
            _byToken[token] = path;
            return $"{Origin}/{token}";
        }
    }

    public string? Resolve(string requestUri)
    {
        var token = TokenOf(requestUri);

        lock (_gate)
        {
            return _byToken.TryGetValue(token, out var resolved) ? resolved : null;
        }
    }

    /// <summary>Lấy cả đường dẫn và độ dài của một mã. Trả false nếu mã không có trong sổ.</summary>
    private bool TryResolve(string requestUri, out string path, out long length)
    {
        var token = TokenOf(requestUri);

        lock (_gate)
        {
            if (_byToken.TryGetValue(token, out path!))
            {
                return TryMeasure(path, out length);
            }
        }

        path = string.Empty;
        length = 0;
        return false;
    }

    private static bool TryMeasure(string path, out long length)
    {
        try
        {
            // Đo lại mỗi lần thay vì nhớ từ lúc đăng ký. Tệp có thể vừa bị nén tiếp
            // trong lúc người dùng đang xem, hoặc bị thay sau khi duyệt — độ dài cũ thì
            // Content-Range sai, mà Content-Range sai thì Chromium từ chối phát. Hai
            // lệnh thống kê tệp rẻ hơn nhiều so với chuyện phải đoán sai.
            length = new FileInfo(path).Length;
            return true;
        }
        catch (Exception)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Tách mã tệp ra khỏi URL.</summary>
    private static string TokenOf(string requestUri)
    {
        // Kiểu của Request.Uri khác nhau giữa các bản SDK: bản này là string, bản mới
        // hơn là Uri. Nhận string và tự tách phần đường dẫn cho cả hai.
        var slash = requestUri.IndexOf('/', requestUri.IndexOf("//", StringComparison.Ordinal) + 2);
        var token = slash >= 0 ? requestUri[(slash + 1)..] : requestUri;

        var query = token.IndexOf('?');
        if (query >= 0) token = token[..query];

        return token.Trim('/');
    }

    /// <summary>Trả kết quả cho một yêu cầu media, hoặc null nếu không phục vụ được.</summary>
    public CoreWebView2WebResourceResponse? Respond(
        CoreWebView2Environment environment,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var requested = args.Request.Uri ?? string.Empty;
        var known = TryResolve(requested, out var path, out var total);

        // Ghi lại MỌI yêu cầu media, kể cả yêu cầu không phục vụ được.
        //
        // Lý do: khi thẻ <video> không phát, Chromium không đưa lý do lên giao diện và cũng
        // không ghi ra đâu. Một khung đen 0:00 không phân biệt được "tệp hỏng", "sai
        // Content-Type", "tệp bị khoá" hay "lỗi trong code" — bốn nguyên nhân, một biểu
        // hiện. Có dòng log thì lần sau mở nhật ký là biết ngay.
        var header = RangeHeaderOf(args.Request.Headers);
        var token = requested.Length > 0 ? requested[(requested.LastIndexOf('/') + 1)..] : requested;
        Diagnostic.Log($"MediaHost: yêu cầu {token} | tệp {(known ? Path.GetFileName(path) : "KHÔNG CÓ")} | Range={header ?? "(không)"}");

        if (!known)
        {
            Diagnostic.Log($"MediaHost: trả null (không phục vụ được) cho {token}.");
            return null;
        }

        try
        {
            if (total == 0)
            {
                Diagnostic.Log($"MediaHost: tệp rỗng, trả null cho {token}.");
                return null;
            }

            // Phân tích Range do ByteRangeParser lo. Nó cắt khối cho range mở: client hỏi
            // "bytes=0-" nghĩa là "cho tôi từ đầu", bản trước hiểu thành "tới hết tệp" và
            // đẩy trọn 552 MB qua sự kiện chạy trên UI thread — cửa sổ đứng hình.
            var range = ByteRangeParser.Parse(header, total);

            // Range không hợp lệ (vượt quá độ dài tệp) -> trả 416, đúng chuẩn HTTP.
            if (range is { Invalid: true })
            {
                Diagnostic.Log($"MediaHost: Range không hợp lệ, trả 416 cho {token}.");
                return environment.CreateWebResourceResponse(
                    Stream.Null,
                    416,
                    "Range Not Satisfiable",
                    $"Content-Range: bytes */{total.ToString(CultureInfo.InvariantCulture)}\r\n");
            }

            var (offset, count) = range is { } ok
                ? (ok.Start, ok.Count)
                : (0L, total);

            // Không dùng FileOptions.SequentialScan ở đây: yêu cầu Range nhảy tới giữa tệp
            // nên không phải tuần tự, và cờ đó khiến bộ đệm I/O đoán sai hướng đọc.
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                BufferSize,
                FileOptions.Asynchronous);

            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);

            var end = offset + count - 1;
            var headers = new System.Text.StringBuilder()
                .Append("Content-Type: ").Append(MimeTypeOf(path)).Append("\r\n")
                .Append("Accept-Ranges: bytes\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append("Content-Length: ")
                .Append(count.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");

            if (range is not null)
            {
                headers.Append("Content-Range: bytes ")
                    .Append(offset.ToString(CultureInfo.InvariantCulture))
                    .Append('-')
                    .Append(end.ToString(CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(total.ToString(CultureInfo.InvariantCulture))
                    .Append("\r\n");
            }

            Diagnostic.Log(
                $"MediaHost: trả {(range is null ? 200 : 206)} cho {token} | " +
                $"{MimeTypeOf(path)} | {count}/{total} byte");

            return environment.CreateWebResourceResponse(
                stream,
                range is null ? 200 : 206,
                range is null ? "OK" : "Partial Content",
                headers.ToString());
        }
        catch (IOException ex)
        {
            // Tệp đang được ghi, hoặc đã bị khoá. Bỏ qua thay vì làm sập trang — nhưng
            // phải ghi lại, nếu không thì lại là một khung đen không giải thích.
            Diagnostic.Log($"MediaHost: lỗi I/O khi phục vụ {token}: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostic.Log($"MediaHost: không có quyền đọc {token}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Lấy nguyên văn header <c>Range</c>, dùng để ghi nhật ký.</summary>
    private static string? RangeHeaderOf(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Đoán kiểu MIME từ phần mở rộng.
    ///
    /// <para>Phải bỏ dấu <c>.bak</c> ở cuối trước khi đoán. Tệp backup của ứng dụng có
    /// tên như <c>phim.mp4.bak</c>: nếu đoán trực tiếp thì ra
    /// <c>application/octet-stream</c>, và thẻ <c>&lt;video&gt;</c> của Chromium sẽ từ
    /// chối tệp — hộp so sánh hiện một khung đen, 0:00, không báo lỗi nào. Người dùng
    /// thấy đúng triệu chứng "bản gốc hỏng" trong khi bản gốc hoàn toàn ổn.</para>
    /// </summary>
    private static string MimeTypeOf(string path)
    {
        var extension = Path.GetExtension(path);

        // Bỏ một lớp .bak. Dùng vòng lặp thay vì kiểm tra một lần: sao lưu của sao lưu
        // cũng phải phát được, và tệp tên kết thúc bằng .bak.bak là do chính ứng dụng tạo ra.
        while (extension.Equals(".bak", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^extension.Length];
            extension = Path.GetExtension(path);
        }

        return extension.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" or ".avi" or ".mov" => "video/x-matroska",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }
}
