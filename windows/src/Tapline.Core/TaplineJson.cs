using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tapline.Core;

/// <summary>Serializer options for Tapline's model types: camelCase properties, enums as the
/// Swift raw values ("peak", "lowShelf", "both"...), nulls omitted as Swift's encoder does.
/// Read-only; copy with <c>new JsonSerializerOptions(TaplineJson.Options)</c> to customise.</summary>
public static class TaplineJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Writes an enum as its camelCase name, also when no options-level converter is set.</summary>
public sealed class CamelCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum;
