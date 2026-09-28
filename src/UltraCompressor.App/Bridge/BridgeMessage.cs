using System.Text.Json;
using System.Text.Json.Nodes;

namespace UltraCompressor.App.Bridge;

/// <summary>
/// Cầu nối hai chiều giữa giao diện web và lõi .NET.
///
/// Dùng <c>postMessage</c> + JSON thay vì COM: không cần <c>[ComVisible]</c>, không mở rồi
/// đối tượng .NET ra ngoài, và bề mặt API chỉ gồm những lệnh ta định nghĩa sẵn — trang web
/// không thể tự gọi hàm tuỳ ý trên máy người dùng.
/// </summary>
public sealed class BridgeMessage
{
    public int? Id { get; init; }

    /// <summary>Tên lệnh khi web -> .NET.</summary>
    public string? Cmd { get; init; }

    /// <summary>Tên sự kiện khi .NET -> web.</summary>
    public string? Event { get; init; }

    public JsonNode? Args { get; init; }

    public JsonNode? Data { get; init; }

    public string? Error { get; init; }

    public bool Ok => Error is null;
}

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(BridgeMessage message) => JsonSerializer.Serialize(message, Options);

    public static BridgeMessage? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeMessage>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? GetString(BridgeMessage message, string name)
        => message.Args?[name]?.GetValue<string>();

    public static int? GetInt(BridgeMessage message, string name)
    {
        var node = message.Args?[name];
        if (node is null) return null;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
            System.Text.Json.JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var v) => v,
            _ => null,
        };
    }

    public static double? GetDouble(BridgeMessage message, string name)
    {
        var node = message.Args?[name];
        if (node is null) return null;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<double>(),
            System.Text.Json.JsonValueKind.String when double.TryParse(
                node.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
            _ => null,
        };
    }

    public static bool GetBool(BridgeMessage message, string name, bool fallback = false)
    {
        var node = message.Args?[name];
        if (node is null) return fallback;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String => bool.TryParse(node.GetValue<string>(), out var v) && v,
            _ => fallback,
        };
    }

    public static T? GetObject<T>(BridgeMessage message, string name)
    {
        var node = message.Args?[name];
        if (node is null) return default;
        try
        {
            return node.Deserialize<T>(Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
