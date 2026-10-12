using System.Text.Json;
using Island.Core;

namespace Island.ClaudeCode;

/// <summary>
/// One entry of permission_suggestions with a human label. The shape varies by <see cref="Type"/>
/// (observed: "addRules", "addDirectories"), so RawJson is kept verbatim and echoed back on "allow and remember".
/// </summary>
/// <param name="RuleTool">The first tool an addRules suggestion names, when it names one.</param>
public sealed record SuggestionChoice(string Type, string Label, string RawJson, string? RuleTool = null)
{
    public static IReadOnlyList<SuggestionChoice> FromRaw(IEnumerable<string> rawItems) =>
        rawItems.Select(Parse).ToList();

    /// <summary>The label for the card's button. <paramref name="fallbackTool"/> names the tool when the rule itself doesn't.</summary>
    public string ButtonLabel(string fallbackTool) => Type switch
    {
        "addRules" => $"Always Allow ({RuleTool ?? fallbackTool})",
        "addDirectories" => "Allow + add folder",
        _ => Label,
    };

    /// <summary>The neutral form the island shows; the raw suggestion rides along as the payload.</summary>
    public RememberOption ToOption(string fallbackTool) => new(ButtonLabel(fallbackTool), Label, RawJson);

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
        return new SuggestionChoice(type, label.TrimEnd(), raw, FirstRuleTool(root));
    }

    private static string RuleText(JsonElement root)
    {
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return "";
        var first = rules.EnumerateArray().FirstOrDefault();
        var tool = JsonUtil.Str(first, "toolName") ?? "";
        var content = JsonUtil.Str(first, "ruleContent");
        return content is null ? tool : $"{tool}({content})";
    }

    private static string? FirstRuleTool(JsonElement root)
    {
        if (JsonUtil.Prop(root, "rules") is not { ValueKind: JsonValueKind.Array } rules) return null;
        foreach (var rule in rules.EnumerateArray())
        {
            var tool = JsonUtil.Str(rule, "toolName");
            if (!string.IsNullOrWhiteSpace(tool)) return tool;
        }
        return null;
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
