using System.Text.Json;

namespace Island.Core;

/// <summary>
/// One entry of permission_suggestions with a human label. The shape varies by <see cref="Type"/>
/// (observed: "addRules", "addDirectories"), so RawJson is kept verbatim and echoed back on "allow and remember".
/// </summary>
public sealed record SuggestionChoice(string Type, string Label, string RawJson)
{
    public static IReadOnlyList<SuggestionChoice> FromRaw(IEnumerable<string> rawItems) =>
        rawItems.Select(Parse).ToList();

    private static SuggestionChoice Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var type = JsonUtil.Str(root, "type") ?? "unknown";
        var label = type switch
        {
            "addRules" => "Allow + remember " + RuleText(root),
            "addDirectories" => "Allow + add folder " + FirstString(root, "directories"),
            _ => "Allow + " + type,
        };
        return new SuggestionChoice(type, label.TrimEnd(), raw);
    }

    private static string RuleText(JsonElement root)
    {
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return "";
        var first = rules.EnumerateArray().FirstOrDefault();
        var tool = JsonUtil.Str(first, "toolName") ?? "";
        var content = JsonUtil.Str(first, "ruleContent");
        return content is null ? tool : $"{tool}({content})";
    }

    private static string FirstString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return "";
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) return item.GetString() ?? "";
        }
        return "";
    }
}
