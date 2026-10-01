using System.Globalization;
using UltraCompressor.Core.Planning;

namespace UltraCompressor.Core.Search;

/// <summary>
/// Dấu vân tay của <b>phép biến đổi</b> mà một ứng viên dùng: tập những thứ phải
/// <b>cố định</b> suốt một nhánh tìm kiếm, và là thứ duy nhất được phép thay đổi là
/// tham số chất lượng.
/// </summary>
/// <remarks>
/// <para><b>Vì sao cần dấu vân tay thay vì nhóm theo tên.</b> Tìm kiếm nhị phân chỉ hợp
/// lệ khi chất lượng <i>đơn điệu</i> theo thang điểm, và chất lượng chỉ đơn điệu được khi
/// mọi thứ khác về phép biến đổi đều giữ nguyên. Nhóm theo một chuỗi tên do người viết
/// ("<c>h264/1280x720</c>") chỉ là <i>thỏa thuận miệng</i>: không có gì chặn ai đó thêm một
/// ứng viên khác FPS hay khác định dạng pixel vào đúng nhánh đó, và khi đó mọi lần cắt
/// dựa trên giả định đơn điệu sẽ cắt nhầm — mà không có lỗi biên dịch nào bắt được.</para>
///
/// <para>Vì vậy nhánh được định nghĩa bằng <b>giá trị</b> của dấu vân tay, không phải bằng
/// một nhãn tự do. Hai ứng viên khác nhau ở bất kỳ thành phần nào của dấu vân tay thì
/// <b>chắc chắn</b> rơi vào hai nhánh khác nhau, kể cả khi chúng có cùng tên.</para>
///
/// <para>Danh sách thành phần là điểm chốt an toàn, không phải bản liệt kê cho đủ:
/// thêm một chiều biến đổi mới vào đây thì chuỗi đổi theo, tức nhánh cũ tự tách ra thay vì
/// trộn lẫn âm thầm.</para>
///
/// <para><b>Không dùng loại này để quyết định ứng viên nào thắng.</b> Nó chỉ trả lời
/// "các ứng viên này có đo trên cùng một phép biến đổi không", tức là câu hỏi về
/// <i>tính hợp lệ của phép đo</i>. Câu hỏi "tốt hơn bao nhiêu" thuộc về
/// <see cref="ParetoSelector"/>.</para>
/// </remarks>
/// <param name="Value">Chuỗi khóa, ổn định theo thứ tự thành phần.</param>
public readonly record struct TransformFingerprint(string Value)
{
    /// <summary>Nhãn rút gọn để người đọc nhật ký hiểu nhánh đó là gì.</summary>
    public string Label => Value.Split('|')[0];

    /// <summary>
    /// Dấu vân tay của một ứng viên, tính trong <b>ngữ cảnh nguồn</b>.
    /// </summary>
    /// <param name="candidate">Ứng viên encode.</param>
    /// <param name="sourceWidth">Bề rộng nguồn — cần để dựng đúng chuỗi bộ lọc.</param>
    /// <param name="sourceHeight">Chiều cao nguồn.</param>
    /// <remarks>
    /// Chuỗi bộ lọc được dựng thật (qua <see cref="EncodeTransform.BuildFilter"/>) chứ không
    /// chép từ kích thước, vì bộ lọc là thứ encoder thực sự chạy: hai kích thước khác nhau có
    /// thể cho ra cùng một chuỗi lọc (ví dụ cùng chiều cao, bề rộng tự suy ra), và ngược
    /// lại một chuỗi lọc giống nhau vẫn có thể khác nhau ở chỗ khác mà dấu vân tay đã ghi.
    /// </remarks>
    public static TransformFingerprint Of(VideoEncodeCandidate candidate, int sourceWidth, int sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var filter = FilterFor(candidate, sourceWidth, sourceHeight);

        // Thứ tự cố định và không bỏ sót thành phần nào. Dùng `|` vì không xuất hiện trong
        // tên encoder hay tên tham số ffmpeg.
        var parts = new[]
        {
            $"{candidate.Codec}|{candidate.EncoderName}",
            $"{candidate.Width}x{candidate.Height}",
            candidate.Fps.ToString("0.####", CultureInfo.InvariantCulture),
            candidate.PixelFormat,
            $"{candidate.Speed.Switch} {candidate.Speed.Text}".Trim(),
            candidate.Tune ?? string.Empty,
            // Họ điều khiển tốc độ. CRF của x264 và QP của SVT-AV1 là hai thang khác nhau
            // dù có cùng con số, nên chỉ nhét con số vào mà không ghi họ là so sai thang.
            candidate.Quality.Switch,
            filter,
        };

        return new TransformFingerprint(string.Join('|', parts));
    }

    private static string FilterFor(VideoEncodeCandidate candidate, int sourceWidth, int sourceHeight)
    {
        if (!EncodeTarget.TryFromRequest(
                sourceWidth, sourceHeight, candidate.Width, candidate.Height, out var target, out var failure))
        {
            // Không dựng được phép biến đổi thì KHÔNG gộp vào nhánh nào cả. Dùng một nhãn
            // riêng biệt cho mọi ứng viên lỗi để chúng không bao giờ bị nhị phân cắt chung.
            // Ứng viên như vậy sẽ rớt ở bước dựng lệnh encode toàn tệp, nơi có thông báo rõ.
            return $"<khong-dung-duoc:{failure}>";
        }

        return EncodeTransform.BuildFilter(target, sourceWidth, sourceHeight);
    }

    public override string ToString() => Value;
}
