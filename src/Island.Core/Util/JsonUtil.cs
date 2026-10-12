using System.Text.Json;

namespace Island.Core;

/// <summary>
/// Tolerant JSON accessors. A missing, null or wrong-typed field yields null / false / the fallback,
/// never an exception, so payloads from other agent versions keep working.
/// </summary>
public static class JsonUtil
{
    public static JsonElement? Prop(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value;
    }

    public static string? Str(JsonElement obj, string name) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static bool Bool(JsonElement obj, string name) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.True };

    public static long Int64(JsonElement obj, string name, long fallback = 0) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : fallback;

    public static int Int32(JsonElement obj, string name, int fallback = 0) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : fallback;

    public static double Double(JsonElement obj, string name, double fallback = 0) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) ? n : fallback;

    /// <summary>The member as compact JSON text, or null when absent or null.</summary>
    public static string? Raw(JsonElement obj, string name) =>
        Prop(obj, name) is { } v ? v.GetRawText() : null;

    /// <summary>Each element of an array member as compact JSON text; empty when absent.</summary>
    public static IReadOnlyList<string> RawArray(JsonElement obj, string name) =>
        Prop(obj, name) is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(e => e.GetRawText()).ToList()
            : Array.Empty<string>();
}
