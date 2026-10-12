using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Island.Core;

namespace Island.App;

public sealed record SessionRow(string Name, string Phase, string Notice, string LastSeen);

public sealed record RememberAction(string Label, ICommand Command);

public sealed class PermissionRow
{
    public required string Session { get; init; }
    public required string Tool { get; init; }
    public required string Summary { get; init; }
    public required string Mode { get; init; }
    public required ICommand Allow { get; init; }
    public required ICommand Deny { get; init; }
    public required ICommand Terminal { get; init; }
    public required IReadOnlyList<RememberAction> Remember { get; init; }
}

/// <summary>
/// Backs the debug window: usage, sessions, pending permission requests and a short event log.
/// Must be used from the UI thread. The real overlay (WP4) will bind to the same hub.
/// </summary>
public sealed class DebugViewModel : INotifyPropertyChanged
{
    private const int MaxEvents = 200;

    private readonly IslandHub _hub;
    private string _usageText = "Usage: off (start the app with --poll-usage to turn it on)";

    public DebugViewModel(IslandHub hub)
    {
        _hub = hub;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string UsageText
    {
        get => _usageText;
        private set
        {
            if (_usageText == value) return;
            _usageText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageText)));
        }
    }

    public ObservableCollection<SessionRow> Sessions { get; } = new();
    public ObservableCollection<PermissionRow> Permissions { get; } = new();
    public ObservableCollection<string> Events { get; } = new();

    public void Apply(IReadOnlyList<StoreEffect> effects)
    {
        foreach (var effect in effects) LogLine(Describe(effect));
        Refresh();
    }

    public void ApplyUsage(UsageUpdate update)
    {
        foreach (var milestone in update.Milestones)
        {
            LogLine($"usage milestone    {WindowLabel(milestone.Window)} reached {milestone.Threshold}% (now {milestone.Percent:0.#}%)");
        }
        UsageText = DescribeUsage(update);
    }

    public void Refresh()
    {
        var snapshot = _hub.Sessions;

        Sessions.Clear();
        Permissions.Clear();
        foreach (var session in snapshot)
        {
            Sessions.Add(new SessionRow(
                session.DisplayName,
                session.Phase.ToString(),
                session.Notice ?? "",
                session.LastSeen.LocalDateTime.ToString("HH:mm:ss")));

            if (session.Pending is { } pending)
                Permissions.Add(ToRow(session, pending));
        }
    }

    public void LogLine(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }

    private PermissionRow ToRow(SessionInfo session, PendingPermission pending)
    {
        return new PermissionRow
        {
            Session = session.DisplayName,
            Tool = pending.ToolName,
            Summary = pending.Summary,
            Mode = pending.PermissionMode,
            Allow = new RelayCommand(() => Answer(session, pending, "allow", PermissionDecision.Allow(), PermissionOutcome.Answered)),
            Deny = new RelayCommand(() => Answer(session, pending, "deny", PermissionDecision.Deny("Denied from Claude Code Island"), PermissionOutcome.Answered)),
            Terminal = new RelayCommand(() => Answer(session, pending, "no decision, answer in terminal", null, PermissionOutcome.DeferredToTerminal)),
            Remember = pending.Suggestions
                .Select(choice => new RememberAction(
                    choice.Label,
                    new RelayCommand(() => Answer(
                        session, pending, $"allow, sending update to Claude Code: {choice.RawJson}",
                        PermissionDecision.Allow(new[] { choice.RawJson }), PermissionOutcome.Answered))))
                .ToList(),
        };
    }

    /// <summary>Sends the decision to the waiting forwarder and records what was sent, so a click is never silent.</summary>
    private void Answer(SessionInfo session, PendingPermission pending, string what, string? decisionJson, PermissionOutcome outcome)
    {
        LogLine($"answered           {session.DisplayName}  {pending.ToolName}: {what}");
        _hub.Answer(pending.RequestId, decisionJson, outcome);
    }

    private static string WindowLabel(UsageWindowId id) => id == UsageWindowId.FiveHour ? "5h" : "7d";

    private static string DescribeUsage(UsageUpdate update)
    {
        var parts = update.Latest?.Windows.Select(w =>
            $"{WindowLabel(w.Id)} {w.Percent:0.#}% (resets {w.ResetsAt.LocalDateTime:ddd HH:mm})").ToList();
        var latest = parts is { Count: > 0 } ? "Usage: " + string.Join("  ·  ", parts) : null;

        return update.Result.Status switch
        {
            UsageFetchStatus.Ok => latest ?? "Usage: no active limit window reported",
            UsageFetchStatus.Unavailable => "Usage: unavailable (not signed in to Claude Code with a claude.ai account)",
            UsageFetchStatus.TokenExpired => (latest ?? "Usage: waiting") + "  -  Claude Code's login has expired; will retry once it refreshes",
            var other => (latest ?? "Usage: no data yet") + $"  -  last poll: {other}",
        };
    }

    private static string Describe(StoreEffect effect) => effect switch
    {
        PermissionShown p => $"permission asked   {p.DisplayName}  {p.Permission.ToolName}: {p.Permission.Summary}",
        PermissionCleared c => $"permission closed  {Short(c.SessionId)}  ({c.Outcome})",
        NoticeShown n => $"{n.Phase,-18} {n.DisplayName}  {n.Text}",
        NoticeCleared c => $"notice cleared     {Short(c.SessionId)}",
        SessionRemoved r => $"session removed    {Short(r.SessionId)}",
        _ => effect.GetType().Name,
    };

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
