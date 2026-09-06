using System.Text.Json;
using System.Text.Json.Serialization;

namespace CabinetNC.Cloud.Contracts;

/// <summary>
/// The one serializer configuration both ends of the wire use. camelCase names in declaration order,
/// nulls written explicitly, enums as their spec strings (never integers). The API hashes request
/// bodies produced with these options, so changing them changes every <c>inputSha256</c>.
/// </summary>
public static class CloudJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, Options);
        if (value is null)
            throw new JsonException("JSON body was null.");
        return value;
    }

    static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
