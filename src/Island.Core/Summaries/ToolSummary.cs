using System.Text.Json;

namespace Island.Core;

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
}
