using Xunit;

namespace UltraCompressor.Core.Tests;

/// <summary>
/// Đánh dấu một test cần ffmpeg thật. Thiếu ffmpeg thì test bị <b>skip</b>, không phải xanh.
///
/// <para>Skip chứ không pass là chủ ý: một test báo xanh mà chưa từng chạy tạo cảm giác an
/// toàn giả, tệ hơn hẳn một test đỏ. Ở đây CI không cài ffmpeg, nên nếu đánh dấu bằng cách
/// khác (ví dụ <c>return</c> sớm) thì CI sẽ báo xanh cho những test chưa từng chạy.</para>
///
/// <para>Đây không phải ngoại lệ cho bản dựng có mojibake. Nó là một dạng "không đủ điều kiện
/// chạy" — giống hệt việc bỏ qua test cần GPU trên máy không có GPU, chỉ khác là ở đây còn
/// phải báo ra chứ không để người đọc tưởng đã kiểm.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresFFmpegAttribute : FactAttribute
{
    public RequiresFFmpegAttribute()
    {
        if (!HasBundledFFmpeg())
        {
            Skip = "Cần ffmpeg.exe đi kèm để chứng minh bằng công cụ thật. Chạy lại sau setup.ps1.";
        }
    }

    private static bool HasBundledFFmpeg()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UltraCompressor.slnx")))
            {
                break;
            }

            dir = dir.Parent;
        }

        var root = dir?.FullName ?? AppContext.BaseDirectory;

        foreach (var relative in new[] { "app/ffmpeg.exe", "tools/ffmpeg.exe", "../app/ffmpeg.exe" })
        {
            try
            {
                if (File.Exists(Path.GetFullPath(Path.Combine(root, relative))))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        return false;
    }
}
