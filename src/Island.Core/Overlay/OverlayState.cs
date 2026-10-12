namespace Island.Core;

/// <summary>Declaration order is display priority: a permission request always outranks everything else.</summary>
public enum CardKind { Permission, NeedsInput, ProjectApproval, UsageMilestone, Finished }

public abstract record OverlayCard
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When set, the card disappears by itself at this time. Permission cards never expire.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public abstract CardKind Kind { get; }
}

public sealed record PermissionCard : OverlayCard
{
    public required string SessionId { get; init; }
    public required string SessionName { get; init; }
    public required PendingPermission Permission { get; init; }
    public string? LastPrompt { get; init; }
    public string? ClientApp { get; init; }
    public string? Agent { get; init; }
    public override CardKind Kind => CardKind.Permission;
}

public sealed record NoticeCard : OverlayCard
{
    public required string SessionId { get; init; }
    public required string SessionName { get; init; }
    public required SessionPhase Phase { get; init; }
    public required string Text { get; init; }
    public string? LastPrompt { get; init; }
    public string? ClientApp { get; init; }
    public string? Agent { get; init; }
    public override CardKind Kind => Phase == SessionPhase.Finished ? CardKind.Finished : CardKind.NeedsInput;
}

/// <summary>"Show this project on the island?" Stays until the user answers.</summary>
public sealed record ProjectCard : OverlayCard
{
    public required ProjectRef Project { get; init; }
    public string? Agent { get; init; }
    public override CardKind Kind => CardKind.ProjectApproval;
}

public sealed record UsageCard : OverlayCard
{
    public required MilestoneEvent Milestone { get; init; }

    /// <summary>High usage stays until the user dismisses it.</summary>
    public bool Sticky { get; init; }

    public override CardKind Kind => CardKind.UsageMilestone;
}

public sealed record OverlayOptions
{
    public TimeSpan FinishedHideAfter { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan MilestoneHideAfter { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Milestones at or above this percentage stay until dismissed.</summary>
    public int StickyFromPercent { get; init; } = 90;

    public bool ShowFinished { get; init; } = true;
}

/// <param name="Cards">Visible cards in display order (most urgent first).</param>
/// <param name="Index">Position of <paramref name="Current"/> in <paramref name="Cards"/>, or -1.</param>
public sealed record OverlaySnapshot(IReadOnlyList<OverlayCard> Cards, OverlayCard? Current, int Index);

/// <summary>
/// The overlay's queue of cards: turns store effects and usage milestones into cards, orders them by priority,
/// expires the transient ones, and lets the user step through them. Pure logic; the window just renders it.
/// </summary>
public sealed class OverlayState
{
    private readonly object _gate = new();
    private readonly OverlayOptions _options;
    private readonly List<OverlayCard> _cards = new();
    private string? _selectedId;
    private bool _suppressNonCritical;

    public OverlayState(OverlayOptions? options = null)
    {
        _options = options ?? new OverlayOptions();
    }

    /// <summary>
    /// While true (a full-screen app or presentation is running) only permission and needs-input cards are shown.
    /// The others stay queued until it clears, unless they expire first.
    /// </summary>
    public bool SuppressNonCritical
    {
        get { lock (_gate) return _suppressNonCritical; }
        set { lock (_gate) _suppressNonCritical = value; }
    }

    public void Apply(IReadOnlyList<StoreEffect> effects, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var effect in effects)
            {
                switch (effect)
                {
                    case PermissionShown shown:
                        Remove(c => c.Id == shown.Permission.RequestId);
                        _cards.Add(new PermissionCard
                        {
                            Id = shown.Permission.RequestId,
                            CreatedAt = now,
                            SessionId = shown.SessionId,
                            SessionName = shown.DisplayName,
                            Permission = shown.Permission,
                            LastPrompt = shown.LastPrompt,
                            ClientApp = shown.ClientApp,
                            Agent = shown.Agent,
                        });
                        _selectedId = null; // an urgent card always takes the front
                        break;

                    case PermissionCleared cleared:
                        Remove(c => c.Id == cleared.RequestId);
                        break;

                    case NoticeShown notice:
                        Remove(c => c is NoticeCard n && n.SessionId == notice.SessionId);
                        if (notice.Phase == SessionPhase.Finished && !_options.ShowFinished) break;
                        _cards.Add(new NoticeCard
                        {
                            Id = "notice:" + notice.SessionId,
                            CreatedAt = now,
                            ExpiresAt = notice.Phase == SessionPhase.Finished ? now + _options.FinishedHideAfter : null,
                            SessionId = notice.SessionId,
                            SessionName = notice.DisplayName,
                            Phase = notice.Phase,
                            Text = notice.Text,
                            LastPrompt = notice.LastPrompt,
                            ClientApp = notice.ClientApp,
                            Agent = notice.Agent,
                        });
                        break;

                    case NoticeCleared cleared:
                        Remove(c => c is NoticeCard n && n.SessionId == cleared.SessionId);
                        break;

                    case ProjectApprovalRequested asked:
                        Remove(c => c is ProjectCard p && p.Project.Key == asked.Project.Key);
                        _cards.Add(new ProjectCard
                        {
                            Id = "project:" + asked.Project.Key,
                            CreatedAt = now,
                            Project = asked.Project,
                            Agent = asked.Agent,
                        });
                        break;

                    case ProjectDecided decided:
                        Remove(c => c is ProjectCard p && p.Project.Key == decided.Project.Key);
                        break;

                    case SessionRemoved removed:
                        Remove(c => c switch
                        {
                            PermissionCard p => p.SessionId == removed.SessionId,
                            NoticeCard n => n.SessionId == removed.SessionId,
                            _ => false,
                        });
                        break;
                }
            }
        }
    }

    public void ApplyUsage(UsageUpdate update, DateTimeOffset now)
    {
        if (update.Milestones.Count == 0) return;

        lock (_gate)
        {
            foreach (var milestone in update.Milestones)
            {
                Remove(c => c is UsageCard u && u.Milestone.Window == milestone.Window); // only the latest per window
                var sticky = milestone.Threshold >= _options.StickyFromPercent;
                _cards.Add(new UsageCard
                {
                    Id = $"usage:{milestone.Window}:{milestone.Threshold}",
                    CreatedAt = now,
                    ExpiresAt = sticky ? null : now + _options.MilestoneHideAfter,
                    Milestone = milestone,
                    Sticky = sticky,
                });
            }
        }
    }

    /// <summary>Removes expired cards. Returns true when anything changed.</summary>
    public bool Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            return Remove(c => c.ExpiresAt is { } expires && expires <= now) > 0;
        }
    }

    public OverlaySnapshot Snapshot()
    {
        lock (_gate)
        {
            var visible = _cards
                .Where(c => !_suppressNonCritical || c.Kind is CardKind.Permission or CardKind.NeedsInput)
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.CreatedAt)
                .ToList();

            var index = _selectedId is null ? 0 : visible.FindIndex(c => c.Id == _selectedId);
            if (index < 0) index = 0;
            return visible.Count == 0
                ? new OverlaySnapshot(visible, null, -1)
                : new OverlaySnapshot(visible, visible[index], index);
        }
    }

    public void Next() => Step(+1);

    public void Previous() => Step(-1);

    public void Dismiss(string id)
    {
        lock (_gate) Remove(c => c.Id == id);
    }

    private void Step(int direction)
    {
        var snapshot = Snapshot();
        if (snapshot.Cards.Count < 2) return;
        var next = (snapshot.Index + direction + snapshot.Cards.Count) % snapshot.Cards.Count;
        lock (_gate) _selectedId = snapshot.Cards[next].Id;
    }

    private int Remove(Predicate<OverlayCard> match)
    {
        var removed = _cards.RemoveAll(match);
        if (_selectedId is not null && _cards.All(c => c.Id != _selectedId)) _selectedId = null;
        return removed;
    }
}
