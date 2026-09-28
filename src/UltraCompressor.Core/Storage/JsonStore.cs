using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraCompressor.Core.Storage;

/// <summary>
/// Lưu/đọc cấu hình và phiên làm việc bằng JSON, ghi nguyên tử.
///
/// Bản gốc dùng <c>XmlSerializer</c> với <c>catch { }</c> nuốt mọi lỗi và ghi thẳng lên
/// tệp đích (bug B19): treo máy giữa lúc ghi là mất sạch danh sách job. Ở đây luôn ghi ra
/// tệp tạm rồi thay thế, và lỗi đọc được báo ra thay vì nuốt im lặng.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task SaveAsync<T>(string path, T value, CancellationToken token = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, token);
            await stream.FlushAsync(token);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Đọc tệp JSON. Trả về null nếu không có hoặc hỏng — kèm lý do nếu hỏng.</summary>
    public static async Task<(T? Value, string? Error)> TryLoadAsync<T>(string path, CancellationToken token = default)
        where T : class
    {
        if (!File.Exists(path)) return (null, null);

        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, token);
            return (value, value is null ? "Tệp rỗng." : null);
        }
        catch (JsonException ex)
        {
            return (null, $"Tệp hỏng ({ex.Message}). Đã giữ nguyên tệp cũ, sẽ tạo tệp mới.");
        }
        catch (Exception ex)
        {
            return (null, $"Không đọc được: {ex.Message}");
        }
    }
}
