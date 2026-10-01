namespace UltraCompressor.Core.Tests;

/// <summary>
/// Tìm ffmpeg đi kèm, dùng chung cho mọi test cần chạy công cụ thật.
///
/// <para>Trước đây mỗi file test có một bản riêng. Nhiều bản của cùng một logic sẽ trôi
/// lệch: một bản thêm đường dẫn, một bản quên, và test chỉ hỏng ở máy của người viết bản
/// đó. Đây là một chỗ, và nó chỉ tồn tại vì lý do đó.</para>
///
/// <para>Chỉ tìm trong cây thư mục dự án — không đọc PATH. Đọc PATH sẽ khiến test chạy với
/// một bản ffmpeg khác với bản ứng dụng dùng, và mọi kết luận rút ra từ test đó không còn
/// nói về sản phẩm nữa.</para>
///
/// <para>Tên thành viên cố ý KHÔNG là <c>Path</c>: bên trong class đó, <c>Path</c> sẽ che
/// mất <see cref="System.IO.Path"/> và mọi lệnh gọi <c>Path.Combine</c> trở thành lỗi
/// biên dịch khó hiểu.</para>
/// </summary>
internal static class TestFFmpeg
{
    private static readonly string[] RelativeCandidates =
        ["app/ffmpeg.exe", "tools/ffmpeg.exe", "../app/ffmpeg.exe"];

    /// <summary>Đường dẫn tới ffmpeg, hoặc null nếu chưa chạy <c>setup.ps1</c>.</summary>
    public static string? ExePath => Find();

    /// <summary>Đường dẫn tới ffmpeg, ném lỗi nếu không có.</summary>
    /// <remarks>
    /// Chỉ dùng trong test đã gắn <see cref="RequiresFFmpegAttribute"/>, vì attribute đã bỏ
    /// qua test khi thiếu ffmpeg. Gọi ở test không gắn attribute thì lỗi này đúng — thà
    /// ném lỗi rõ ràng còn hơn âm thầm trả null rồi "hỏng" ở một chỗ xa hơn.
    /// </remarks>
    public static string Require() =>
        Find() ?? throw new InvalidOperationException(
            "Không tìm thấy ffmpeg.exe đi kèm. Chạy setup.ps1 trước khi chạy test này.");

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "UltraCompressor.slnx")))
            {
                foreach (var relative in RelativeCandidates)
                {
                    try
                    {
                        var candidate = System.IO.Path.GetFullPath(
                            System.IO.Path.Combine(dir.FullName, relative));

                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Đường dẫn không hợp lệ trên nền tảng này: thử phương án kế tiếp.
                    }
                    catch (NotSupportedException)
                    {
                    }
                }

                return null;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
