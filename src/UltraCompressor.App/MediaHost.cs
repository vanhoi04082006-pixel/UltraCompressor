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
/// Chỉ dùng cho màn hình so sánh: nhờ đó hai bản gốc và bản nén phát cùng lúc trong chính
/// ứng dụng, thay vì bắt người dùng mở hai cửa sổ trình phát bên ngoài.
/// </summary>
public sealed class MediaHost
{
    public const string Origin = "https://media.local";

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

    /// <summary>Trả về kết quả cho một yêu cầu media, hoặc null nếu không phục vụ được.</summary>
    public CoreWebView2WebResourceResponse? Respond(
        CoreWebView2Environment environment,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var path = Resolve(args.Request.Uri);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Accept-Ranges để <video> tua được; Content-Length để hiện thanh thời lượng.
            return environment.CreateWebResourceResponse(
                stream,
                200,
                "OK",
                $"Content-Type: {MimeTypeOf(path)}\r\nContent-Length: {stream.Length}\r\nAccept-Ranges: bytes\r\n");
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
