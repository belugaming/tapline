using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tapline.Core;

/// <summary>Reads a stored settings collection (a JSON object or array) entry by entry, like
/// <c>TolerantJSON</c> on macOS.
///
/// An entry that fails to decode (damaged, or saved by a newer Tapline) no longer takes the whole
/// collection with it, and it is not erased either: the <c>Encode</c> methods write it back
/// unchanged, so going back to an older version and forward again loses nothing.</summary>
public sealed class TolerantJson
{
    /// <summary>Entries of the decoded data that could not be read, as parsed JSON.</summary>
    private Dictionary<string, JsonNode?> unreadableEntries = [];
    private List<JsonNode?> unreadableElements = [];

    public int UnreadableCount => unreadableEntries.Count + unreadableElements.Count;

    /// <summary>Null when <paramref name="node"/> is not a JSON object at all.</summary>
    public Dictionary<string, T>? DecodeObject<T>(JsonNode? node, JsonSerializerOptions options)
    {
        unreadableEntries = [];
        if (node is not JsonObject obj) return null;
        var values = new Dictionary<string, T>();
        foreach (var (key, value) in obj)
        {
            if (TryDecode(value, options, out T? decoded)) values[key] = decoded;
            else unreadableEntries[key] = value?.DeepClone();
        }
        return values;
    }

    /// <summary>Null when <paramref name="node"/> is not a JSON array at all.</summary>
    public List<T>? DecodeArray<T>(JsonNode? node, JsonSerializerOptions options)
    {
        unreadableElements = [];
        if (node is not JsonArray array) return null;
        var values = new List<T>();
        foreach (var element in array)
        {
            if (TryDecode(element, options, out T? decoded)) values.Add(decoded);
            else unreadableElements.Add(element?.DeepClone());
        }
        return values;
    }

    /// <summary><paramref name="values"/> plus the unreadable entries whose keys are not taken by a value.</summary>
    public JsonObject EncodeObject<T>(IReadOnlyDictionary<string, T> values, JsonSerializerOptions options)
    {
        var obj = JsonSerializer.SerializeToNode(values, options) as JsonObject ?? new JsonObject();
        foreach (var (key, raw) in unreadableEntries)
            if (!values.ContainsKey(key)) obj[key] = raw?.DeepClone();
        return obj;
    }

    /// <summary><paramref name="values"/> followed by the unreadable elements.</summary>
    public JsonArray EncodeArray<T>(IEnumerable<T> values, JsonSerializerOptions options)
    {
        var array = JsonSerializer.SerializeToNode(values, options) as JsonArray ?? new JsonArray();
        foreach (var raw in unreadableElements) array.Add(raw?.DeepClone());
        return array;
    }

    /// <summary>Drops the unreadable entries, so the next encode writes only the values.</summary>
    public void Forget()
    {
        unreadableEntries = [];
        unreadableElements = [];
    }

    private static bool TryDecode<T>(JsonNode? node, JsonSerializerOptions options, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        value = default;
        if (node is null) return false;
        try
        {
            value = node.Deserialize<T>(options);
        }
        catch (Exception)
        {
            // Whatever the model types throw on data they cannot take: the entry is unreadable.
            return false;
        }
        return value is not null;
    }
}
