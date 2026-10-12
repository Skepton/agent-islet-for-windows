using System.Collections.ObjectModel;
using System.Windows.Input;
using Island.Core;

namespace Island.App;

public enum PanelTab { Sessions, Projects, Debug }

/// <summary>Colour of a session's status dot.</summary>
public enum StatusKind { Idle, Working, Attention, Done }

/// <summary>A pending permission request inside a session card, with the same buttons as the island.</summary>
public sealed record PendingRow(
    string Headline,
    string CommandPrefix,
    string Command,
    IReadOnlyList<ActionButton> Buttons,
    ICommand AnswerInTerminal)
{
    public bool HasPrefix => CommandPrefix.Length > 0;
}

public sealed record SessionCard(
    string Name,
    IReadOnlyList<string> Chips,
    string Status,
    StatusKind Kind,
    string LastSeen,
    string? Notice,
    IReadOnlyList<PendingRow> Pendings)
{
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
}

/// <summary>
/// Backs the control panel: live sessions (answerable), project choices, and the debug log with usage and hook status.
/// UI thread only.
/// </summary>
public sealed class PanelViewModel : ObservableObject
{
    private const int MaxEvents = 200;

    private readonly IslandHub _hub;
    private readonly Func<string> _hookStatus;
    private PanelTab _tab = PanelTab.Sessions;
    private string _usageText = "Usage: off (start the island with --poll-usage, or install with -PollUsage, to turn it on)";
    private string _hooksText = "";

    /// <param name="hookStatus">Describes whether the agent hooks are installed; re-read whenever the panel refreshes.</param>
    public PanelViewModel(IslandHub hub, ProjectsViewModel projects, Func<string> hookStatus, string version, Action openDataFolder)
    {
        _hub = hub;
        _hookStatus = hookStatus;
        Projects = projects;
        Version = version;
        ShowSessions = new RelayCommand(() => Tab = PanelTab.Sessions);
        ShowProjects = new RelayCommand(() => Tab = PanelTab.Projects);
        ShowDebug = new RelayCommand(() => Tab = PanelTab.Debug);
        OpenDataFolder = new RelayCommand(openDataFolder);
        CopyLog = new RelayCommand(() => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Events.Reverse())));
        ClearLog = new RelayCommand(() => Events.Clear());
    }

    public ProjectsViewModel Projects { get; }
    public string Version { get; }

    public ICommand ShowSessions { get; }
    public ICommand ShowProjects { get; }
    public ICommand ShowDebug { get; }
    public ICommand OpenDataFolder { get; }
    public ICommand CopyLog { get; }
    public ICommand ClearLog { get; }

    public PanelTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            Raise(nameof(IsSessions));
            Raise(nameof(IsProjects));
            Raise(nameof(IsDebug));
            Raise(nameof(PageTitle));
            if (value == PanelTab.Projects) Projects.Refresh();
        }
    }

    public bool IsSessions
    {
        get => Tab == PanelTab.Sessions;
        set { if (value) Tab = PanelTab.Sessions; }
    }

    public bool IsProjects
    {
        get => Tab == PanelTab.Projects;
        set { if (value) Tab = PanelTab.Projects; }
    }

    public bool IsDebug
    {
        get => Tab == PanelTab.Debug;
        set { if (value) Tab = PanelTab.Debug; }
    }

    public string PageTitle => Tab switch
    {
        PanelTab.Projects => "Projects",
        PanelTab.Debug => "Debug",
        _ => "Sessions",
    };

    public ObservableCollection<SessionCard> Sessions { get; } = new();
    public ObservableCollection<string> Events { get; } = new();

    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasSessions => Sessions.Count > 0;
    public string SessionCount => Sessions.Count == 0 ? "" : Sessions.Count.ToString();

    public string UsageText
    {
        get => _usageText;
        private set => Set(ref _usageText, value);
    }

    public string HooksText
    {
        get => _hooksText;
        private set => Set(ref _hooksText, value);
    }

    public void Apply(IReadOnlyList<StoreEffect> effects)
    {
        foreach (var effect in effects) LogLine(Describe(effect));
        if (effects.Any(e => e is not EventReceived)) Refresh();
        if (effects.Any(e => e is ProjectDecided)) Projects.Refresh();
    }

    public void ApplyUsage(UsageUpdate update)
    {
        foreach (var milestone in update.Milestones)
        {
            LogLine($"usage milestone    {UsageFormat.WindowLabel(milestone.Window)} reached {milestone.Threshold}% (now {milestone.Percent:0.#}%)");
        }
        UsageText = DescribeUsage(update);
    }

    /// <summary>Rebuilds the session cards (and their relative times) and re-reads the hook status.</summary>
    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        Sessions.Clear();
        foreach (var session in _hub.Sessions) Sessions.Add(ToCard(session, now));
        Raise(nameof(HasNoSessions));
        Raise(nameof(HasSessions));
        Raise(nameof(SessionCount));
        HooksText = _hookStatus();
    }

    public void LogLine(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }

    private SessionCard ToCard(SessionInfo session, DateTimeOffset now)
    {
        var chips = new[] { session.Agent, session.ClientApp }.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList();
        var (status, kind) = session.Phase switch
        {
            SessionPhase.NeedsPermission => ("Needs your permission", StatusKind.Attention),
            SessionPhase.NeedsInput => ("Waiting for you", StatusKind.Attention),
            SessionPhase.Working => ("Working", StatusKind.Working),
            SessionPhase.Finished => ("Finished", StatusKind.Done),
            _ => ("Idle", StatusKind.Idle),
        };
        var age = now - session.LastSeen;
        return new SessionCard(
            session.DisplayName,
            chips,
            status,
            kind,
            UsageFormat.Age(age < TimeSpan.Zero ? TimeSpan.Zero : age) + " ago",
            session.Notice,
            session.Pendings.Select(p => ToPending(session, p, now)).ToList());
    }

    private PendingRow ToPending(SessionInfo session, PendingPermission pending, DateTimeOffset now)
    {
        // The same card the island shows, so labels and answers are identical in both places.
        var card = new PermissionCard
        {
            Id = pending.RequestId,
            CreatedAt = now,
            SessionId = session.SessionId,
            SessionName = session.DisplayName,
            Permission = pending,
            LastPrompt = session.LastPrompt,
            ClientApp = session.ClientApp,
            Agent = session.Agent,
        };
        var text = CardPresenter.Present(card, now);
        return new PendingRow(
            text.AttentionLine,
            text.CommandPrefix,
            text.Command,
            text.Actions
                .Select(a => new ActionButton(
                    // Every remember option, by its full label: several may otherwise all read "Always Allow (Bash)".
                    a.Kind == PermissionActionKind.Remember ? pending.Suggestions[a.SuggestionIndex].Detail : a.Label,
                    a.Kind,
                    new RelayCommand(() => Answer(card, CardPresenter.Answer(card, a)))))
                .ToList(),
            new RelayCommand(() => Answer(card, CardPresenter.AnswerInTerminal(card))));
    }

    /// <summary>Sends the verdict to the waiting forwarder and records what was sent, so a click is never silent.</summary>
    private void Answer(PermissionCard card, PermissionAnswer answer)
    {
        LogLine($"answered           {card.SessionName}  {answer.Log}");
        _hub.Answer(card.Id, answer.Verdict, answer.Outcome);
    }

    private static string DescribeUsage(UsageUpdate update)
    {
        var parts = update.Latest?.Windows.Select(w =>
            $"{UsageFormat.WindowLabel(w.Id)} {w.Percent:0.#}% (resets {w.ResetsAt.LocalDateTime:ddd HH:mm})").ToList();
        var latest = parts is { Count: > 0 } ? "Usage: " + string.Join("  ·  ", parts) : null;

        return update.Result.Status switch
        {
            UsageFetchStatus.Ok => latest ?? "Usage: no active limit window reported",
            UsageFetchStatus.Unavailable => "Usage: unavailable (not signed in with a subscription account)",
            UsageFetchStatus.TokenExpired => (latest ?? "Usage: waiting") + "  -  the login has expired; will retry once the agent refreshes it",
            var other => (latest ?? "Usage: no data yet") + $"  -  last poll: {other}",
        };
    }

    private static string Describe(StoreEffect effect) => effect switch
    {
        EventReceived r => $"event              {Short(r.SessionId)}  {r.Source}{(r.Shown ? "" : "  (project not shown)")}",
        PermissionShown p => $"permission asked   {p.DisplayName}  {p.Permission.ToolName}: {p.Permission.Summary}" +
            (p.Permission.Suggestions.Count == 0 ? "" : $"  [{p.Permission.Suggestions.Count} remember options: {string.Join(" | ", p.Permission.Suggestions.Select(o => o.Detail))}]"),
        PermissionCleared c => $"permission closed  {Short(c.SessionId)}  ({c.Outcome})",
        NoticeShown n => $"{n.Phase,-18} {n.DisplayName}  {TextUtil.Truncate(n.Text, 120)}",
        NoticeCleared c => $"notice cleared     {Short(c.SessionId)}",
        SessionRemoved r => $"session removed    {Short(r.SessionId)}",
        ProjectApprovalRequested p => $"new project        {p.Project.Path}  (waiting for Show / Ignore)",
        ProjectDecided d => $"project decided    {d.Project.Path}  ({d.Decision})",
        _ => effect.GetType().Name,
    };

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
