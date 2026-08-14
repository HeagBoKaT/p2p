using System.Text.Json;
using System.Text.Json.Serialization;

namespace p2p.Services;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
