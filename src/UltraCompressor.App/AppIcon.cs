using System.Reflection;

namespace UltraCompressor.App;

/// <summary>
/// Nạp icon ứng dụng dùng chung cho cửa sổ, tray và shortcut.
///
/// <para>Vì sao cần đọc từ resource thay vì nạp file .ico trên đĩa: file .ico chỉ tồn tại
/// trong thư mục publish. Khi chạy bằng <c>dotnet run</c> — tức đúng trường hợp dùng để
/// phát triển — thư mục làm việc có <c>app.ico</c> nhưng đường dẫn khác, và nếu code đi tìm
/// file thì sẽ im lặng rơi về icon .NET mặc định. Đọc từ resource của chính assembly thì
/// luôn đúng, dù chạy kiểu nào.</para>
///
/// <para>Nạp một lần rồi dùng lại: tạo <see cref="Icon"/> là đọc file từ đĩa, làm mỗi
/// lần gọi sẽ giữ một handle mở và làm rò handle cho tới khi form đóng.</para>
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// AppUserModelID của ứng dụng.
    ///
    /// <para>Đây là chuỗi định danh, không phải chuỗi hiển thị. Windows lưu nó cùng các
    /// ghim trên thanh tác vụ, nên <b>đổi chuỗi này làm mất liên kết với các ghim cũ</b> —
    /// người dùng sẽ phải ghim lại từ đầu. Giữ nguyên kể từ lần đầu phát hành.</para>
    ///
    /// <para>Nó cũng là thứ khiến cửa sổ gom lại đúng một nhóm khi ứng dụng được chạy từ
    /// nhiều đường dẫn (bản cài trong Program Files và bản chạy thử trong thư mục dự
    /// án). Không có nó, Windows tự sinh ID theo đường dẫn tệp và mỗi bản thành một ứng
    /// dụng riêng trên thanh tác vụ.</para>
    /// </summary>
    public const string AppUserModelId = "vn.ultracompressor.app";

    private const string ResourceName = "UltraCompressor.App.app.ico";

    private static Icon? _cached;

    /// <summary>Icon ứng dụng. Trả về null nếu không lấy được — không ném lỗi.</summary>
    public static Icon? Load() => _cached ??= LoadCore();

    private static Icon? LoadCore()
    {
        // Đường dẫn 1: resource nhúng, luôn có khi publish.
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null) return new Icon(stream);
        }
        catch
        {
            // Rơi xuống cách dự phòng bên dưới.
        }

        // Đường dẫn 2: file cạnh tệp thực thi, phục vụ lúc phát triển.
        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "app.ico");
            return File.Exists(beside) ? new Icon(beside) : null;
        }
        catch
        {
            return null;
        }
    }
}
