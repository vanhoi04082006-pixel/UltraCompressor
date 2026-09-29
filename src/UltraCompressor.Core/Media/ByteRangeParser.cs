using System.Globalization;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Một đoạn byte của tệp, theo header <c>Range</c> của HTTP.
/// </summary>
public readonly record struct ByteRange(long Start, long Count)
{
    /// <summary>Byte cuối (đóng, hai đầu). -1 khi <see cref="Count"/> bằng 0.</summary>
    public long End => Count <= 0 ? -1 : Start + Count - 1;

    /// <summary>
    /// True khi header sai không thể phục vụ — khi đó phải trả 416 thay vì 206.
    /// </summary>
    public bool Invalid { get; init; }

    public override string ToString() => Invalid ? "416" : $"{Start}-{End}";
}

/// <summary>
/// Đọc header <c>Range</c> và quyết định nên gửi đoạn nào.
///
/// <para>Tách riêng khỏi lớp phục vụ media vì đây là phần dễ sai nhất, và sai thì biểu
/// hiện là "video không phát" — triệu chứng mà người dùng không phân biệt được với tệp
/// hỏng. Trước đây nó nằm trong <c>MediaHost</c> của tầng giao diện nên không test được
/// chút nào; đây là hàm thuần, không đọc tệp, không chạm WebView2.</para>
/// </summary>
public static class ByteRangeParser
{
    /// <summary>
    /// Số byte tối đa cho một lần gửi khi client yêu cầu range mở (<c>bytes=a-</c>).
    ///
    /// <para><b>Đây là chỗ sửa một lỗi làm treo cửa sổ.</b> Client hỏi <c>Range: bytes=0-</c>
    /// nghĩa là "cho tôi từ đầu", nhưng bản trước hiểu là "tới hết tệp" và gửi cả tệp.
    /// Với tệp 552 MB, ứng dụng đẩy 552 MB qua <c>WebResourceRequested</c> — mà sự kiện
    /// đó chạy trên UI thread — nên cửa sổ đứng hình, nhất là khi đang nén song song.</para>
    ///
    /// <para>Cắt khối vẫn đúng chuẩn: RFC 9110 cho phép 206 trả với <c>Content-Range</c>
    /// hẹp hơn khoảng client yêu cầu, và client media sẽ tự xin phần tiếp theo. Không có
    /// giới hạn này thì một tệp lớn không bao giờ bắt đầu phát nổi.</para>
    /// </summary>
    public const long MaxChunkBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Phân tích header <c>Range</c>.
    ///
    /// <param name="header">Giá trị header thô, null nếu client không gửi.</param>
    /// <param name="total">Độ dài tệp.</param>
    /// <param name="maxChunk">
    /// Trần số byte cho một lần gửi. 0 = không giới hạn (giữ hành vi cũ, chỉ dùng khi
    /// cần đối chiếu).
    /// </param>
    /// <returns>
    /// null = không có range hợp lệ để áp dụng, trả 200 cả tệp (đúng như server tĩnh).
    /// <see cref="ByteRange.Invalid"/> = header sai, phải trả 416.
    /// Ngược lại là đoạn cần gửi.
    /// </returns>
    public static ByteRange? Parse(string? header, long total, long maxChunk = MaxChunkBytes)
    {
        if (total <= 0) return null;

        if (string.IsNullOrWhiteSpace(header)) return null;

        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;

        var spec = value["bytes=".Length..].Trim();

        // Nhiều đoạn: client media không dùng, và ghép tay dễ sai hơn là bỏ qua.
        if (spec.Contains(',')) return null;

        var dash = spec.IndexOf('-');
        if (dash < 0) return null;

        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();

        if (left.Length == 0)
        {
            // "-500" = 500 byte cuối. Đây là range đóng về phía đuôi nên không cắt khối.
            if (!TryParseLength(right, out var suffix) || suffix <= 0)
            {
                return Invalid();
            }

            if (suffix > total) suffix = total;
            return new ByteRange(total - suffix, suffix);
        }

        if (!TryParseLength(left, out var start) || start < 0)
        {
            return Invalid();
        }

        long end;
        var openEnded = right.Length == 0;
        if (openEnded)
        {
            end = total - 1;
        }
        else if (!TryParseLength(right, out end))
        {
            return Invalid();
        }

        if (start >= total) return Invalid();
        if (end >= total) end = total - 1;
        if (end < start) return Invalid();

        var count = end - start + 1;

        // Chỉ cắt khối khi client để mở. Range đóng là client đã tính kỹ cần bao nhiêu,
        // cắt thêm chỉ làm phát giật mà không đổi được lượng dữ liệu tải.
        if (openEnded && maxChunk > 0 && count > maxChunk)
        {
            count = maxChunk;
        }

        return new ByteRange(start, count);
    }

    private static ByteRange Invalid() => new(0, 0) { Invalid = true };

    private static bool TryParseLength(string text, out long value)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
}
