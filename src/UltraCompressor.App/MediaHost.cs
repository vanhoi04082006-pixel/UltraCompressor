using System.Globalization;
using System.IO;
using Microsoft.Web.WebView2.Core;
using UltraCompressor.Core.Media;

namespace UltraCompressor.App;

/// <summary>
/// Phá»¥c vá»¥ tá»‡p media cho trang web qua má»™t virtual host riĂªng.
///
/// Trang cháº¡y trĂªn <c>https://app.local</c> nĂªn khĂ´ng tá»± táº£i Ä‘Æ°á»£c <c>file:///C:/...</c>.
/// Thay vĂ¬ má»Ÿ toĂ n bá»™ thÆ° má»¥c (lá»™ má»i tá»‡p trĂªn mĂ¡y), má»—i tá»‡p Ä‘Æ°á»£c Ä‘Äƒng kĂ½ má»™t mĂ£ ngáº«u
/// nhiĂªn vĂ  URL lĂ  <c>https://media.local/&lt;mĂ£&gt;</c>. Trang chá»‰ biáº¿t mĂ£, khĂ´ng biáº¿t
/// Ä‘Æ°á»ng dáº«n, vĂ  muá»‘n xem tá»‡p nĂ o ngoĂ i danh sĂ¡ch Ä‘Äƒng kĂ½ thĂ¬ khĂ´ng cĂ³ cĂ¡ch nĂ o.
///
/// <para><b>Há»— trá»£ <c>Range</c> lĂ  báº¯t buá»™c, khĂ´ng pháº£i tuá»³ chá»n.</b> Chromium (vĂ  cáº£
/// <c>&lt;video&gt;</c> láº«n <c>&lt;audio&gt;</c>) luĂ´n gá»­i <c>Range</c> khi tua. Náº¿u ta tráº£
/// <c>200</c> cho má»i yĂªu cáº§u thĂ¬ má»—i láº§n tua nĂ³ pháº£i Ä‘á»c láº¡i tá»‡p tá»« byte 0 â€” Ä‘Ă³ lĂ  lĂ½ do
/// mĂ n hĂ¬nh so sĂ¡nh giáº­t vĂ  tua ngháº¹n so vá»›i VLC, vá»‘n seek tháº³ng trĂªn tá»‡p. Tráº£
/// <c>206 Partial Content</c> vá»›i <c>Content-Range</c> Ä‘Ăºng thĂ¬ Chromium chá»‰ Ä‘á»c Ä‘Ăºng
/// Ä‘oáº¡n nĂ³ cáº§n.</para>
///
/// Chá»‰ dĂ¹ng cho mĂ n hĂ¬nh so sĂ¡nh: nhá» Ä‘Ă³ hai báº£n gá»‘c vĂ  báº£n nĂ©n phĂ¡t cĂ¹ng lĂºc trong chĂ­nh
/// á»©ng dá»¥ng, thay vĂ¬ báº¯t ngÆ°á»i dĂ¹ng má»Ÿ hai cá»­a sá»• trĂ¬nh phĂ¡t bĂªn ngoĂ i.
/// </summary>
public sealed class MediaHost
{
    public const string Origin = "https://media.local";

    private const int BufferSize = 256 * 1024;

    private readonly Dictionary<string, string> _byToken = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>ÄÄƒng kĂ½ má»™t tá»‡p vĂ  tráº£ vá» URL Ä‘á»ƒ nhĂºng. Tá»‡p khĂ´ng tá»“n táº¡i thĂ¬ tráº£ null.</summary>
    public string? Register(string path)
    {
        if (!File.Exists(path)) return null;

        lock (_gate)
        {
            // CĂ¹ng má»™t tá»‡p Ä‘Äƒng kĂ½ nhiá»u láº§n váº«n tráº£ cĂ¹ng má»™t URL, Ä‘á»ƒ so sĂ¡nh láº¡i khĂ´ng
            // sinh ra mĂ£ má»›i má»—i láº§n báº¥m.
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

    /// <summary>Láº¥y cáº£ Ä‘Æ°á»ng dáº«n vĂ  Ä‘á»™ dĂ i cá»§a má»™t mĂ£. Tráº£ false náº¿u mĂ£ khĂ´ng cĂ³ trong sá»•.</summary>
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
            // Äo láº¡i má»—i láº§n thay vĂ¬ nhá»› tá»« lĂºc Ä‘Äƒng kĂ½. Tá»‡p cĂ³ thá»ƒ vá»«a bá»‹ nĂ©n tiáº¿p
            // trong lĂºc ngÆ°á»i dĂ¹ng Ä‘ang xem, hoáº·c bá»‹ thay sau khi duyá»‡t â€” Ä‘á»™ dĂ i cÅ© thĂ¬
            // Content-Range sai, mĂ  Content-Range sai thĂ¬ Chromium tá»« chá»‘i phĂ¡t. Hai
            // lá»‡nh thá»‘ng kĂª tá»‡p ráº» hÆ¡n nhiá»u so vá»›i chuyá»‡n pháº£i Ä‘oĂ¡n sai.
            length = new FileInfo(path).Length;
            return true;
        }
        catch (Exception)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>TĂ¡ch mĂ£ tá»‡p ra khá»i URL.</summary>
    private static string TokenOf(string requestUri)
    {
        // Kiá»ƒu cá»§a Request.Uri khĂ¡c nhau giá»¯a cĂ¡c báº£n SDK: báº£n nĂ y lĂ  string, báº£n má»›i
        // hÆ¡n lĂ  Uri. Nháº­n string vĂ  tá»± tĂ¡ch pháº§n Ä‘Æ°á»ng dáº«n cho cáº£ hai.
        var slash = requestUri.IndexOf('/', requestUri.IndexOf("//", StringComparison.Ordinal) + 2);
        var token = slash >= 0 ? requestUri[(slash + 1)..] : requestUri;

        var query = token.IndexOf('?');
        if (query >= 0) token = token[..query];

        return token.Trim('/');
    }

    /// <summary>Tráº£ káº¿t quáº£ cho má»™t yĂªu cáº§u media, hoáº·c null náº¿u khĂ´ng phá»¥c vá»¥ Ä‘Æ°á»£c.</summary>
    public CoreWebView2WebResourceResponse? Respond(
        CoreWebView2Environment environment,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var requested = args.Request.Uri ?? string.Empty;
        var known = TryResolve(requested, out var path, out var total);

        // Ghi láº¡i Má»ŒI yĂªu cáº§u media, ká»ƒ cáº£ yĂªu cáº§u khĂ´ng phá»¥c vá»¥ Ä‘Æ°á»£c.
        //
        // LĂ½ do: khi tháº» <video> khĂ´ng phĂ¡t, Chromium khĂ´ng Ä‘Æ°a lĂ½ do lĂªn giao diá»‡n vĂ  cÅ©ng
        // khĂ´ng ghi ra Ä‘Ă¢u. Má»™t khung Ä‘en 0:00 khĂ´ng phĂ¢n biá»‡t Ä‘Æ°á»£c "tá»‡p há»ng", "sai
        // Content-Type", "tá»‡p bá»‹ khoĂ¡" hay "lá»—i trong code" â€” bá»‘n nguyĂªn nhĂ¢n, má»™t biá»ƒu
        // hiá»‡n. CĂ³ dĂ²ng log thĂ¬ láº§n sau má»Ÿ nháº­t kĂ½ lĂ  biáº¿t ngay.
        var header = RangeHeaderOf(args.Request.Headers);
        var token = requested.Length > 0 ? requested[(requested.LastIndexOf('/') + 1)..] : requested;
        Diagnostic.Log($"MediaHost: yĂªu cáº§u {token} | tá»‡p {(known ? Path.GetFileName(path) : "KHĂ”NG CĂ“")} | Range={header ?? "(khĂ´ng)"}");

        if (!known)
        {
            Diagnostic.Log($"MediaHost: tráº£ null (khĂ´ng phá»¥c vá»¥ Ä‘Æ°á»£c) cho {token}.");
            return null;
        }

        try
        {
            if (total == 0)
            {
                Diagnostic.Log($"MediaHost: tá»‡p rá»—ng, tráº£ null cho {token}.");
                return null;
            }

            // PhĂ¢n tĂ­ch Range do ByteRangeParser lo. NĂ³ cáº¯t khá»‘i cho range má»Ÿ: client há»i
            // "bytes=0-" nghÄ©a lĂ  "cho tĂ´i tá»« Ä‘áº§u", báº£n trÆ°á»›c hiá»ƒu thĂ nh "tá»›i háº¿t tá»‡p" vĂ 
            // Ä‘áº©y trá»n 552 MB qua sá»± kiá»‡n cháº¡y trĂªn UI thread â€” cá»­a sá»• Ä‘á»©ng hĂ¬nh.
            var range = ByteRangeParser.Parse(header, total);

            // Range khĂ´ng há»£p lá»‡ (vÆ°á»£t quĂ¡ Ä‘á»™ dĂ i tá»‡p) -> tráº£ 416, Ä‘Ăºng chuáº©n HTTP.
            if (range is { Invalid: true })
            {
                Diagnostic.Log($"MediaHost: Range khĂ´ng há»£p lá»‡, tráº£ 416 cho {token}.");
                return environment.CreateWebResourceResponse(
                    Stream.Null,
                    416,
                    "Range Not Satisfiable",
                    $"Content-Range: bytes */{total.ToString(CultureInfo.InvariantCulture)}\r\n");
            }

            var (offset, count) = range is { } ok
                ? (ok.Start, ok.Count)
                : (0L, total);

            // KhĂ´ng dĂ¹ng FileOptions.SequentialScan á»Ÿ Ä‘Ă¢y: yĂªu cáº§u Range nháº£y tá»›i giá»¯a tá»‡p
            // nĂªn khĂ´ng pháº£i tuáº§n tá»±, vĂ  cá» Ä‘Ă³ khiáº¿n bá»™ Ä‘á»‡m I/O Ä‘oĂ¡n sai hÆ°á»›ng Ä‘á»c.
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
                $"MediaHost: tráº£ {(range is null ? 200 : 206)} cho {token} | " +
                $"{MimeTypeOf(path)} | {count}/{total} byte");

            return environment.CreateWebResourceResponse(
                stream,
                range is null ? 200 : 206,
                range is null ? "OK" : "Partial Content",
                headers.ToString());
        }
        catch (IOException ex)
        {
            // Tá»‡p Ä‘ang Ä‘Æ°á»£c ghi, hoáº·c Ä‘Ă£ bá»‹ khoĂ¡. Bá» qua thay vĂ¬ lĂ m sáº­p trang â€” nhÆ°ng
            // pháº£i ghi láº¡i, náº¿u khĂ´ng thĂ¬ láº¡i lĂ  má»™t khung Ä‘en khĂ´ng giáº£i thĂ­ch.
            Diagnostic.Log($"MediaHost: lá»—i I/O khi phá»¥c vá»¥ {token}: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diagnostic.Log($"MediaHost: khĂ´ng cĂ³ quyá»n Ä‘á»c {token}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Láº¥y nguyĂªn vÄƒn header <c>Range</c>, dĂ¹ng Ä‘á»ƒ ghi nháº­t kĂ½.</summary>
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
    /// ÄoĂ¡n kiá»ƒu MIME tá»« pháº§n má»Ÿ rá»™ng.
    ///
    /// <para>Pháº£i bá» dáº¥u <c>.bak</c> á»Ÿ cuá»‘i trÆ°á»›c khi Ä‘oĂ¡n. Tá»‡p backup cá»§a á»©ng dá»¥ng cĂ³
    /// tĂªn nhÆ° <c>phim.mp4.bak</c>: náº¿u Ä‘oĂ¡n trá»±c tiáº¿p thĂ¬ ra
    /// <c>application/octet-stream</c>, vĂ  tháº» <c>&lt;video&gt;</c> cá»§a Chromium sáº½ tá»«
    /// chá»‘i tá»‡p â€” há»™p so sĂ¡nh hiá»‡n má»™t khung Ä‘en, 0:00, khĂ´ng bĂ¡o lá»—i nĂ o. NgÆ°á»i dĂ¹ng
    /// tháº¥y Ä‘Ăºng triá»‡u chá»©ng "báº£n gá»‘c há»ng" trong khi báº£n gá»‘c hoĂ n toĂ n á»•n.</para>
    /// </summary>
    private static string MimeTypeOf(string path)
    {
        var extension = Path.GetExtension(path);

        // Bá» má»™t lá»›p .bak. DĂ¹ng vĂ²ng láº·p thay vĂ¬ kiá»ƒm tra má»™t láº§n: sao lÆ°u cá»§a sao lÆ°u
        // cÅ©ng pháº£i phĂ¡t Ä‘Æ°á»£c, vĂ  tá»‡p tĂªn káº¿t thĂºc báº±ng .bak.bak lĂ  do chĂ­nh á»©ng dá»¥ng táº¡o ra.
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
