using System.Text.Json;
using Island.Core;

namespace Island.ClaudeCode;

/// <summary>One-line description of what a tool call wants to do, for permission cards.</summary>
public static class ToolSummary
{
    public static string Describe(string? toolName, string? inputJson, int max = 200)
    {
        if (string.IsNullOrEmpty(inputJson)) return "";

        using var doc = JsonDocument.Parse(inputJson);
        var root = doc.RootElement;
        var primary = toolName switch
        {
            "Bash" or "PowerShell" => JsonUtil.Str(root, "command"),
            "Edit" or "MultiEdit" or "Write" or "Read" or "NotebookEdit" =>
                JsonUtil.Str(root, "file_path") ?? JsonUtil.Str(root, "notebook_path"),
            "WebFetch" => JsonUtil.Str(root, "url"),
            "WebSearch" => JsonUtil.Str(root, "query"),
            _ => null,
        };
        return TextUtil.Truncate(primary ?? inputJson, max);
    }

    /// <summary>The plain-language description Claude attaches to shell commands, when present.</summary>
    public static string? Description(string? toolName, string? inputJson, int max = 160)
    {
        if (toolName is not ("Bash" or "PowerShell") || string.IsNullOrEmpty(inputJson)) return null;

        using var doc = JsonDocument.Parse(inputJson);
        var text = JsonUtil.Str(doc.RootElement, "description");
        return string.IsNullOrWhiteSpace(text) ? null : TextUtil.Truncate(text, max);
    }

    /// <summary>"Claude wants to run Bash." for shells, "Claude wants to use Edit." for everything else.</summary>
    public static string Headline(string toolName) =>
        IsShell(toolName) ? $"Claude wants to run {toolName}." : $"Claude wants to use {toolName}.";

    /// <summary>The prompt shown before the command: "$" for Bash, "PS>" for PowerShell, nothing otherwise.</summary>
    public static string CommandPrefix(string toolName) => toolName switch
    {
        "Bash" => "$",
        "PowerShell" => "PS>",
        _ => "",
    };

    /// <summary>
    /// Identifies a tool call across its PermissionRequest / PreToolUse and its PostToolUse, which share no id. Built
    /// from the tool and its main argument (command, file, URL, query), which Claude Code repeats unchanged; other
    /// input fields may differ (defaults filled in, answers added). AskUserQuestion is keyed by its first question.
    /// Kept in memory only.
    /// </summary>
    public static string CallKey(string? toolName, string? inputJson)
    {
        var tool = toolName ?? "";
        if (string.IsNullOrEmpty(inputJson)) return tool;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            var main = tool == "AskUserQuestion"
                ? FirstQuestion(root)
                : JsonUtil.Str(root, "command") ?? JsonUtil.Str(root, "file_path") ?? JsonUtil.Str(root, "notebook_path")
                    ?? JsonUtil.Str(root, "url") ?? JsonUtil.Str(root, "query") ?? JsonUtil.Str(root, "pattern");
            return tool + "\n" + (main ?? root.GetRawText());
        }
        catch (JsonException)
        {
            return tool + "\n" + inputJson;
        }
    }

    private static string? FirstQuestion(JsonElement root) =>
        JsonUtil.Prop(root, "questions") is { ValueKind: JsonValueKind.Array } questions
            ? questions.EnumerateArray().Select(q => JsonUtil.Str(q, "question")).FirstOrDefault(q => q is not null)
            : null;

    private static bool IsShell(string toolName) => toolName is "Bash" or "PowerShell";
}
