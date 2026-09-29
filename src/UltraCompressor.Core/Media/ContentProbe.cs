using UltraCompressor.Core.Processes;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Đặc trưng nội dung của tệp, lấy từ vài khung hình mẫu.
///
/// <para><b>Vì sao cần.</b> Đo thật trên hai loại nội dung của cùng một máy cho kết quả
/// ngược hẳn nhau về việc dùng HEVC có đáng không:</para>
///
/// <list type="bullet">
/// <item>Quay màn hình: x264 CRF 28 → 0,650 MB; x265 CRF 30 → 0,652 MB.
/// Cùng dung lượng, mà SSIM còn THẤP hơn. HEVC không giúp được gì.</item>
/// <item>Anime: x264 CRF 28 → 6,962 MB; x265 CRF 32 → 1,706 MB. Nhỏ hơn 4,1 lần,
/// SSIM chỉ giảm 0,003. HEVC rất đáng.</item>
/// </list>
///
/// <para>Hai tệp đó cùng codec nguồn (H.264), cùng độ phân giải, cùng bộ mã hoá — khác
/// nhau ở chỗ hình ảnh có <b>mảng màu phẳng lớn</b> hay không. Không có tín hiệu này thì
/// "tự chọn codec" chỉ là đoán mò, và đoán sai thì tốn thời gian gấp 5 lần mà không
/// thu được byte nào.</para>
///
/// <para>Cách tính: lấy vài khung hình ở giữa tệp, thu nhỏ về 160×90 xám, rồi đo
/// <b>độ phẳng</b> — tỉ lệ pixel nằm trong cụm màu gần như không đổi. Anime có nền trải
/// phẳng nên tỉ lệ cao; màn hình có chữ ở khắp nơi nên tỉ lệ thấp.</para>
/// </summary>
public sealed record ContentInfo
{
    /// <summary>Tỉ lệ phần trăm pixel nằm trong cụm phẳng, 0–100.</summary>
    public double Flatness { get; init; }

    /// <summary>Khác biệt trung bình giữa các khung liên tiếp, 0–255. Đo mức chuyển động.</summary>
    public double Motion { get; init; }

    /// <summary>Số khung hình đã lấy được. 0 = probe thất bại.</summary>
    public int Frames { get; init; }

    /// <summary>Probe có dùng được không.</summary>
    public bool Valid => Frames > 0 && Flatness > 0;
}

public sealed class ContentProbe(string ffmpegPath)
{
    // 160×90 = 14 400 pixel/khung. Đủ để đo độ phẳng, nhỏ để không tốn thời gian.
    private const int Width = 160;
    private const int Height = 90;
    private const int FrameSize = Width * Height;

    /// <summary>Cạnh của một ô dùng để đo độ phẳng, tính bằng pixel.</summary>
    private const int Block = 4;

    /// <summary>Lấy vài khung hình liên tiếp ở khoảng 30% tệp.</summary>
    public async Task<ContentInfo> ProbeAsync(
        string path,
        double? durationSeconds,
        int wanted = 3,
        CancellationToken token = default)
    {
        if (durationSeconds is not { } d || d <= 0) return new ContentInfo();

        // Một lần seek, rồi lấy vài khung liên tiếp.
        //
        // Bản đầu tiên mở 3 input, mỗi input một vị trí khác nhau rồi ghép lại. Cách đó
        // cho tín hiệu trải đều hơn, nhưng tốn 143 GIÂY cho một tệp 19 phút — nhiều hơn cả
        // thời gian nén. Một lần seek ở 30% và lấy 3 khung liên tiếp mất 0,2 giây, nhanh
        // hơn 700 lần, và chỉ dùng để quyết định có hạ số khung hình hay không — thứ mà
        // vài khung liên tiếp ở giữa tệp nói đủ.
        var offset = (d * 0.3).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", offset,
            "-i", path,
            "-frames:v", wanted.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-vf", $"scale={Width}:{Height},format=gray",
            "-f", "rawvideo",
            "-",
        };

        BinaryProcessResult result;
        try
        {
            // Timeout ngắn: đây chỉ là lấy vài khung hình. Nếu tệp hỏng khiến ffmpeg kẹt,
            // mất vài giây rồi bỏ — tốt hơn là làm treo cả job.
            result = await ProcessRunner.RunBinaryAsync(
                ffmpegPath,
                [.. args],
                TimeSpan.FromSeconds(30),
                token);
        }
        catch
        {
            // Probe nội dung hỏng thì chỉ mất tối ưu hoá, không được làm hỏng job.
            return new ContentInfo();
        }

        var raw = result.StandardOutput;
        if (raw.Length < FrameSize) return new ContentInfo();

        var frames = raw.Length / FrameSize;
        var flat = 0;
        var compared = 0;
        var motion = 0.0;

        for (var f = 0; f < frames; f++)
        {
            var start = f * FrameSize;
            var frame = raw.AsSpan(start, FrameSize);

            // Độ phẳng: chia khung thành ô 4×4, ô nào toàn bộ pixel nằm trong biên độ 6
            // thì tính là phẳng. Chữ trên màn hình tạo hàng loạt ô có biên độ lớn.
            //
            // Điều kiện vòng lặp là `y + 4 <= Height` chứ không phải `y < Height`: chiều cao
            // 90 không chia hết cho 4, nên `y < Height` vẫn lấy ô bắt đầu ở hàng 88 và đọc
            // tới hàng 91 — vượt mảng. Sai số này chỉ lộ ra khi chạy trên tệp thật.
            for (var y = 0; y + Block <= Height; y += Block)
            {
                for (var x = 0; x + Block <= Width; x += Block)
                {
                    var min = 255;
                    var max = 0;
                    for (var dy = 0; dy < Block; dy++)
                    {
                        var row = (y + dy) * Width + x;
                        for (var dx = 0; dx < Block; dx++)
                        {
                            var v = frame[row + dx];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                    }

                    if (max - min <= 6) flat++;
                }
            }

            // Chuyển động: so khung này với khung trước.
            if (f > 0)
            {
                var previous = raw.AsSpan(start - FrameSize, FrameSize);
                long sum = 0;
                for (var i = 0; i < FrameSize; i++)
                {
                    sum += Math.Abs(frame[i] - previous[i]);
                }

                motion += (double)sum / FrameSize;
                compared++;
            }
        }

        var blocks = frames * ((Width / Block) * (Height / Block));
        if (blocks == 0) return new ContentInfo();

        return new ContentInfo
        {
            Flatness = 100.0 * flat / blocks,
            Motion = compared == 0 ? 0 : motion / compared,
            Frames = frames,
        };
    }
}
