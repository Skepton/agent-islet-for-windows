using System.Globalization;

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

/// <param name="Path">The full folder path, so two projects with the same name can be told apart.</param>
public sealed record ProjectCardText(string Title, string Question, string Path, IReadOnlyList<string> Chips);

/// <param name="Level">See <see cref="UsageFormat.Level"/>.</param>
public sealed record UsageCardText(string Title, double Percent, int Level, string ResetsText);

/// <param name="Verdict">For <c>IslandHub.Answer</c>; null means "no decision, answer in the terminal".</param>
/// <param name="Log">One line for the debug log describing what is being sent.</param>
public sealed record PermissionAnswer(PermissionVerdict? Verdict, PermissionOutcome Outcome, string Log);

/// <param name="Level">See <see cref="UsageFormat.Level"/>.</param>
public sealed record UsageStripItem(string Label, string PercentText, int Level, string Countdown);

/// <summary>Turns overlay cards into the exact strings the window shows, so the WPF side only binds.</summary>
public static class CardPresenter
{
    private const int MaxPromptInTitle = 60;
    private const int MaxPromptInYouLine = 140;

    /// <param name="maxRemember">How many "allow and remember" buttons to offer, in the agent's order. The island shows
    /// one (several would all read "Always Allow (Bash)"); the panel lists them all with their full labels.</param>
    public static PermissionCardText Present(PermissionCard card, DateTimeOffset now, int maxRemember = int.MaxValue)
    {
        var permission = card.Permission;
        var tool = permission.ToolName;

        var actions = new List<PermissionAction>
        {
            new(PermissionActionKind.Deny, "No"),
            new(PermissionActionKind.Allow, "Yes"),
        };
        for (var i = 0; i < Math.Min(permission.Suggestions.Count, Math.Max(0, maxRemember)); i++)
            actions.Add(new PermissionAction(PermissionActionKind.Remember, permission.Suggestions[i].ButtonLabel, i));

        return new PermissionCardText(
            Title(card.SessionName, card.LastPrompt),
            YouLine(card.LastPrompt),
            string.IsNullOrWhiteSpace(permission.Headline) ? $"Permission needed to use {tool}." : permission.Headline,
            tool,
            permission.CommandPrefix,
            permission.Summary,
            string.IsNullOrWhiteSpace(permission.Description) ? null : permission.Description,
            Chips(card.Agent, card.ClientApp, now - card.CreatedAt),
            actions);
    }

    public static NoticeCardText Present(NoticeCard card, DateTimeOffset now) => new(
        Title(card.SessionName, card.LastPrompt),
        YouLine(card.LastPrompt),
        card.Text,
        card.Phase == SessionPhase.Finished,
        Chips(card.Agent, card.ClientApp, now - card.CreatedAt));

    public static ProjectCardText Present(ProjectCard card) => new(
        $"New project: {card.Project.Name}",
        $"Show {(string.IsNullOrWhiteSpace(card.Agent) ? "agent" : card.Agent)} sessions from this folder on the island?",
        card.Project.Path,
        string.IsNullOrWhiteSpace(card.Agent) ? Array.Empty<string>() : new[] { card.Agent });

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

    /// <summary>The 5h and 7d readings for the strip at the top of the island, in that order.</summary>
    public static IReadOnlyList<UsageStripItem> PresentStrip(UsageSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Windows
            .OrderBy(w => w.Id)
            .Select(w => new UsageStripItem(
                UsageFormat.WindowLabel(w.Id),
                $"{Math.Round(Math.Clamp(w.Percent, 0, 100)):0}%",
                UsageFormat.Level(w.Percent),
                UsageFormat.Countdown(w.ResetsAt - now)))
            .ToList();

    /// <summary>What a click on <paramref name="action"/> sends to the agent.</summary>
    public static PermissionAnswer Answer(PermissionCard card, PermissionAction action)
    {
        var tool = card.Permission.ToolName;
        var suggestions = card.Permission.Suggestions;
        switch (action.Kind)
        {
            case PermissionActionKind.Deny:
                return new PermissionAnswer(PermissionVerdict.Deny(), PermissionOutcome.Answered, $"{tool}: deny");
            case PermissionActionKind.Remember when action.SuggestionIndex >= 0 && action.SuggestionIndex < suggestions.Count:
                var option = suggestions[action.SuggestionIndex];
                return new PermissionAnswer(
                    PermissionVerdict.Remember(option), PermissionOutcome.Answered, $"{tool}: allow and remember: {option.Detail}");
            default:
                return new PermissionAnswer(PermissionVerdict.Allow, PermissionOutcome.Answered, $"{tool}: allow");
        }
    }

    public static PermissionAnswer AnswerInTerminal(PermissionCard card) =>
        new(null, PermissionOutcome.DeferredToTerminal, $"{card.Permission.ToolName}: no decision, answer in terminal");

    private static string Title(string session, string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? session : $"{session} · {TextUtil.Truncate(prompt, MaxPromptInTitle)}";

    private static string? YouLine(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? null : "You: " + TextUtil.Truncate(prompt, MaxPromptInYouLine);

    private static IReadOnlyList<string> Chips(string? agent, string? clientApp, TimeSpan age)
    {
        var chips = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent)) chips.Add(agent);
        if (!string.IsNullOrWhiteSpace(clientApp)) chips.Add(clientApp);
        chips.Add(UsageFormat.Age(age < TimeSpan.Zero ? TimeSpan.Zero : age));
        return chips;
    }
}
