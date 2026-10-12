using System.Globalization;
using System.Text.Json;

namespace Island.Core;

public enum PermissionActionKind { Deny, Allow, Remember }

/// <param name="SuggestionIndex">Index into <c>PendingPermission.Suggestions</c> for <see cref="PermissionActionKind.Remember"/>, else -1.</param>
public sealed record PermissionAction(PermissionActionKind Kind, string Label, int SuggestionIndex = -1);

/// <param name="Title">"project · last prompt", or just the project when no prompt is known.</param>
/// <param name="YouLine">"You: ..." or null.</param>
/// <param name="CommandPrefix">"$" for Bash, "PS>" for PowerShell, empty for tools that have no shell.</param>
/// <param name="Actions">Left to right: No, Yes, then one per permission suggestion.</param>
public sealed record PermissionCardText(
    string Title,
    string? YouLine,
    string AttentionLine,
    string ToolLabel,
    string CommandPrefix,
    string Command,
    string? Description,
    IReadOnlyList<string> Chips,
    IReadOnlyList<PermissionAction> Actions);

/// <param name="IsDone">True for a finished session (the UI shows a green "Done" tag).</param>
public sealed record NoticeCardText(
    string Title,
    string? YouLine,
    string Body,
    bool IsDone,
    IReadOnlyList<string> Chips);

/// <param name="Level">See <see cref="UsageFormat.Level"/>.</param>
public sealed record UsageCardText(string Title, double Percent, int Level, string ResetsText);

/// <summary>Turns overlay cards into the exact strings the window shows, so the WPF side only binds.</summary>
public static class CardPresenter
{
    private const int MaxPromptInTitle = 60;
    private const int MaxPromptInYouLine = 140;

    public static PermissionCardText Present(PermissionCard card, DateTimeOffset now)
    {
        var permission = card.Permission;
        var tool = permission.ToolName;
        var isShell = tool is "Bash" or "PowerShell";

        var actions = new List<PermissionAction>
        {
            new(PermissionActionKind.Deny, "No"),
            new(PermissionActionKind.Allow, "Yes"),
        };
        for (var i = 0; i < permission.Suggestions.Count; i++)
            actions.Add(new PermissionAction(PermissionActionKind.Remember, SuggestionLabel(permission.Suggestions[i], tool), i));

        return new PermissionCardText(
            Title(card.SessionName, card.LastPrompt),
            YouLine(card.LastPrompt),
            isShell ? $"Claude wants to run {tool}." : $"Claude wants to use {tool}.",
            tool,
            tool switch { "Bash" => "$", "PowerShell" => "PS>", _ => "" },
            permission.Summary,
            string.IsNullOrWhiteSpace(permission.Description) ? null : permission.Description,
            Chips(card.ClientApp, now - card.CreatedAt),
            actions);
    }

    public static NoticeCardText Present(NoticeCard card, DateTimeOffset now) => new(
        Title(card.SessionName, card.LastPrompt),
        YouLine(card.LastPrompt),
        card.Text,
        card.Phase == SessionPhase.Finished,
        Chips(card.ClientApp, now - card.CreatedAt));

    /// <param name="zone">Zone for the reset time; local time when null.</param>
    public static UsageCardText Present(UsageCard card, TimeZoneInfo? zone = null)
    {
        var milestone = card.Milestone;
        var label = UsageFormat.WindowLabel(milestone.Window);
        var percent = Math.Clamp(milestone.Percent, 0, 100);

        var resets = TimeZoneInfo.ConvertTime(milestone.ResetsAt, zone ?? TimeZoneInfo.Local);
        var format = milestone.Window == UsageWindowId.SevenDay ? "ddd HH:mm" : "HH:mm";
        return new UsageCardText(
            $"{label} limit  ·  {milestone.Threshold} % used",
            percent,
            UsageFormat.Level(percent),
            "resets " + resets.ToString(format, CultureInfo.CurrentCulture));
    }

    /// <summary>The label for a "remember" button. <paramref name="fallbackTool"/> names the tool when the rule itself doesn't.</summary>
    public static string SuggestionLabel(SuggestionChoice choice, string fallbackTool) => choice.Type switch
    {
        "addRules" => $"Always Allow ({RuleTool(choice.RawJson) ?? fallbackTool})",
        "addDirectories" => "Allow + add folder",
        _ => choice.Label,
    };

    private static string Title(string session, string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? session : $"{session} · {TextUtil.Truncate(prompt, MaxPromptInTitle)}";

    private static string? YouLine(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? null : "You: " + TextUtil.Truncate(prompt, MaxPromptInYouLine);

    private static IReadOnlyList<string> Chips(string? clientApp, TimeSpan age)
    {
        var chips = new List<string> { "Claude Code" };
        if (!string.IsNullOrWhiteSpace(clientApp)) chips.Add(clientApp);
        chips.Add(UsageFormat.Age(age < TimeSpan.Zero ? TimeSpan.Zero : age));
        return chips;
    }

    private static string? RuleTool(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (JsonUtil.Prop(doc.RootElement, "rules") is not { ValueKind: JsonValueKind.Array } rules) return null;
            foreach (var rule in rules.EnumerateArray())
            {
                var tool = JsonUtil.Str(rule, "toolName");
                if (!string.IsNullOrWhiteSpace(tool)) return tool;
            }
        }
        catch (JsonException)
        {
            // Unknown shape: fall back to the permission's own tool name.
        }
        return null;
    }
}
