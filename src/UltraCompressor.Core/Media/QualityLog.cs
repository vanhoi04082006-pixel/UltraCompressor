using System.Text.Json;

namespace UltraCompressor.Core.Media;

/// <summary>
/// Đọc tệp log JSON do <c>libvmaf</c> ghi ra và gom thành một mẫu chất lượng.
///
/// <para>Tách ra khỏi <see cref="QualityProbe"/> vì đây là phần thuần toán, không đụng
/// tới ffmpeg: test được trực tiếp mà không cần dựng tệp video thật. Nếu để chung,
/// kiểm thử bắt buộc phải chạy ffmpeg — hoặc phải mở một hàm chỉ dành riêng cho test,
/// tức là một loại mùi code khác.</para>
/// </summary>
public static class QualityLog
{
    /// <summary>
    /// Phân vị dùng cho cổng chất lượng, cùng số 5% mà tài liệu VMAF khuyên dùng.
    ///
    /// <para>Ý nghĩa: "95% khung hình phải đạt ngưỡng này". Chỉ nhìn trung bình thì một
    /// tệp vài cảnh hỏng vẫn đạt, vì đa số khung còn lại đẹp.</para>
    /// </summary>
    public const double Percentile = 0.05;

    /// <summary>
    /// Gom log thành mẫu. Trả false nếu log không đọc được, không phải log JSON, hoặc
    /// không có khung nào mang điểm VMAF — tức là "không đo được", khác hẳn "đo ra 0".
    /// </summary>
    public static bool TryParse(string json, out QualitySample sample)
    {
        sample = new QualitySample(0, 0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames) ||
                frames.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var vmaf = new List<double>();
            var ssim = new List<double>();

            foreach (var frame in frames.EnumerateArray())
            {
                if (!frame.TryGetProperty("metrics", out var metrics)) continue;

                if (metrics.TryGetProperty("vmaf", out var v) && v.TryGetDouble(out var vmafValue))
                {
                    vmaf.Add(vmafValue);
                }

                if (metrics.TryGetProperty("float_ssim", out var s) && s.TryGetDouble(out var ssimValue))
                {
                    ssim.Add(ssimValue);
                }
            }

            if (vmaf.Count == 0) return false;

            vmaf.Sort();
            var index = (int)Math.Floor(Percentile * (vmaf.Count - 1));

            sample = new QualitySample(
                Mean: vmaf.Average(),
                P5: vmaf[index],
                Min: vmaf[0],
                SsimMean: ssim.Count > 0 ? ssim.Average() : 0,
                Frames: vmaf.Count);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
