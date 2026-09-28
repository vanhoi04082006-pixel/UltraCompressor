using System.Globalization;
using System.IO;
using Microsoft.Web.WebView2.Core;

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
        // Kiểu của Request.Uri khác nhau giữa các bản SDK: bản này là string, bản mới
        // hơn là Uri. Nhận string và tự tách phần đường dẫn cho cả hai.
        var path = requestUri;
        var slash = path.IndexOf('/', path.IndexOf("//", StringComparison.Ordinal) + 2);
        var token = slash >= 0 ? path[(slash + 1)..] : path;

        var query = token.IndexOf('?');
        if (query >= 0) token = token[..query];

        token = token.Trim('/');
        if (token.Length == 0) return null;

        lock (_gate)
        {
            return _byToken.TryGetValue(token, out var resolved) ? resolved : null;
        }
    }

    /// <summary>Trả kết quả cho một yêu cầu media, hoặc null nếu không phục vụ được.</summary>
    public CoreWebView2WebResourceResponse? Respond(
        CoreWebView2Environment environment,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var path = Resolve(args.Request.Uri);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var info = new FileInfo(path);
            var total = info.Length;
            if (total == 0) return null;

            var range = ParseRange(args.Request.Headers, total);

            // Range không hợp lệ (vượt quá độ dài tệp) -> trả 416, đúng chuẩn HTTP.
            if (range is { Invalid: true })
            {
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

            return environment.CreateWebResourceResponse(
                stream,
                range is null ? 200 : 206,
                range is null ? "OK" : "Partial Content",
                headers.ToString());
        }
        catch (IOException)
        {
            // Tệp đang được ghi, hoặc đã bị khoá. Bỏ qua thay vì làm sập trang.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Kết quả đọc header <c>Range</c>.</summary>
    private readonly record struct ByteRange(long Start, long Count)
    {
        public bool Invalid { get; init; }
    }

    /// <summary>
    /// Đọc <c>Range: bytes=a-b</c>. Trả null khi không có header (nguyên tệp) hoặc header
    /// không phải dạng byte — lúc đó trả 200 cả tệp, đúng như cách server tĩnh làm.
    /// </summary>
    private static ByteRange? ParseRange(System.Collections.Generic.IEnumerable<KeyValuePair<string, string>> headers, long total)
    {
        string? raw = null;
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase))
            {
                raw = header.Value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;

        var spec = value["bytes=".Length..].Trim();

        // Nhiều đoạn: Chromium không dùng cho media, và ghép tay dễ sai hơn là bỏ qua.
        if (spec.Contains(',')) return null;

        var dash = spec.IndexOf('-');
        if (dash < 0) return null;

        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();

        long start;
        long end;

        if (left.Length == 0)
        {
            // "-500" = 500 byte cuối.
            if (!long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return new ByteRange(0, 0) { Invalid = true };
            }

            if (suffix > total) suffix = total;
            return new ByteRange(total - suffix, suffix);
        }

        if (!long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
        {
            return new ByteRange(0, 0) { Invalid = true };
        }

        if (right.Length == 0)
        {
            end = total - 1;
        }
        else if (!long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
        {
            return new ByteRange(0, 0) { Invalid = true };
        }

        if (start >= total) return new ByteRange(0, 0) { Invalid = true };
        if (end >= total) end = total - 1;
        if (end < start) return new ByteRange(0, 0) { Invalid = true };

        return new ByteRange(start, end - start + 1);
    }

    private static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
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
