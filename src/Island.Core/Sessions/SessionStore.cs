namespace Island.Core;

/// <summary>
/// Per-session state machine fed by agent events. Pure logic: no I/O, no UI, nothing agent-specific.
/// Every method returns the effects the presentation layer should apply.
/// </summary>
public sealed class SessionStore
{
    private static readonly TimeSpan DefaultDedupeWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(12);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _dedupeWindow;

    public SessionStore(TimeSpan? dedupeWindow = null)
    {
        _dedupeWindow = dedupeWindow ?? DefaultDedupeWindow;
    }

    public IReadOnlyList<SessionInfo> Snapshot()
    {
        lock (_gate)
        {
            return _sessions.Values.OrderByDescending(s => s.LastSeen).ToList();
        }
    }

    public IReadOnlyList<StoreEffect> Apply(AgentEvent evt, DateTimeOffset now)
    {
        var effects = new List<StoreEffect>();
        var sid = evt.SessionId;
        if (string.IsNullOrEmpty(sid)) return effects;

        lock (_gate)
        {
            if (evt is SessionEnded)
            {
                if (_sessions.TryGetValue(sid, out var ending))
                {
                    ReleasePending(ending, now, PermissionOutcome.Abandoned, effects);
                    _sessions.Remove(sid);
                    effects.Add(new SessionRemoved(sid));
                }
                return effects;
            }

            var s = Touch(sid, evt, now);
            switch (evt)
            {
                case SessionStarted:
                    s = ClearNotice(s, effects) with { Phase = SessionPhase.Idle };
                    break;

                case PromptSubmitted submitted:
                    s = Working(ReleasePending(s, now, PermissionOutcome.ResolvedElsewhere, effects), effects);
                    if (!string.IsNullOrWhiteSpace(submitted.Prompt)) s = s with { LastPrompt = TextUtil.Truncate(submitted.Prompt, 160) };
                    break;

                case PermissionRequested requested:
                {
                    // Parallel tool calls (subagents) can wait at the same time, so requests queue up instead of replacing each other.
                    var pending = requested.Permission;
                    if (s.Pendings.Any(p => p.RequestId == pending.RequestId)) break;
                    if (s.NoticeCallKey is null) s = ClearNotice(s, effects); // a question tied to another call keeps waiting
                    s = s with { Phase = SessionPhase.NeedsPermission, Pendings = s.Pendings.Append(pending).ToList() };
                    effects.Add(new PermissionShown(sid, s.DisplayName, pending, s.LastPrompt, s.ClientApp, s.Agent));
                    break;
                }

                case ToolProceeded proceeded:
                {
                    // Only the call that ended is resolved: a sibling tool finishing must not dismiss a card still waiting.
                    s = ReleasePending(s, now, PermissionOutcome.ResolvedElsewhere, effects,
                        p => proceeded.CallKey is null || p.CallKey == proceeded.CallKey);
                    if (s.NoticeCallKey is null || s.NoticeCallKey == proceeded.CallKey) s = ClearNotice(s, effects);
                    s = s with
                    {
                        Phase = s.Pendings.Count > 0 ? SessionPhase.NeedsPermission
                            : s.Notice is not null ? s.Phase
                            : SessionPhase.Working,
                    };
                    break;
                }

                case InputRequested input:
                {
                    // An echo of a permission request is a duplicate of its card while it shows or was just answered.
                    var duplicateOfCard = input.EchoesPermission && (s.Pendings.Count > 0 || RecentlyResolved(s, now));
                    if (!duplicateOfCard)
                    {
                        var text = TextUtil.TruncateBlock(input.Text, 400);
                        s = s with { Phase = SessionPhase.NeedsInput, Notice = text, NoticeCallKey = input.CallKey };
                        effects.Add(new NoticeShown(sid, s.DisplayName, SessionPhase.NeedsInput, text, s.LastPrompt, s.ClientApp, s.Agent));
                    }
                    break;
                }

                case TurnFinished finished:
                {
                    s = ReleasePending(s, now, PermissionOutcome.ResolvedElsewhere, effects);
                    if (!finished.Continuing)
                    {
                        var text = TextUtil.TruncateBlock(finished.Message ?? "Finished", 400);
                        s = s with { Phase = SessionPhase.Finished, Notice = text };
                        effects.Add(new NoticeShown(sid, s.DisplayName, SessionPhase.Finished, text, s.LastPrompt, s.ClientApp, s.Agent));
                    }
                    break;
                }
            }

            _sessions[sid] = s;
            return effects;
        }
    }

    /// <summary>
    /// Ends a pending permission for a reason that did not come through a hook event:
    /// the user answered in the island (Answered / DeferredToTerminal) or the hook connection closed (Abandoned).
    /// </summary>
    public IReadOnlyList<StoreEffect> ResolvePermission(string requestId, DateTimeOffset now, PermissionOutcome outcome)
    {
        var effects = new List<StoreEffect>();
        lock (_gate)
        {
            var owner = _sessions.Values.FirstOrDefault(x => x.Pendings.Any(p => p.RequestId == requestId));
            if (owner is null) return effects;
            _sessions[owner.SessionId] = ReleasePending(owner, now, outcome, effects, p => p.RequestId == requestId);
        }
        return effects;
    }

    /// <summary>Removes every session of a project (the user chose to ignore it), releasing its pending request.</summary>
    public IReadOnlyList<StoreEffect> RemoveProject(string projectKey, DateTimeOffset now)
    {
        var effects = new List<StoreEffect>();
        lock (_gate)
        {
            foreach (var session in _sessions.Values.Where(s => s.Project == projectKey).ToList())
            {
                ReleasePending(session, now, PermissionOutcome.Abandoned, effects);
                _sessions.Remove(session.SessionId);
                effects.Add(new SessionRemoved(session.SessionId));
            }
        }
        return effects;
    }

    /// <summary>Removes sessions that have been silent for longer than the retention period.</summary>
    public IReadOnlyList<StoreEffect> Prune(DateTimeOffset now)
    {
        var effects = new List<StoreEffect>();
        lock (_gate)
        {
            foreach (var stale in _sessions.Values.Where(s => now - s.LastSeen > Retention).ToList())
            {
                ReleasePending(stale, now, PermissionOutcome.Abandoned, effects);
                _sessions.Remove(stale.SessionId);
                effects.Add(new SessionRemoved(stale.SessionId));
            }
        }
        return effects;
    }

    private SessionInfo Touch(string sid, AgentEvent evt, DateTimeOffset now)
    {
        _sessions.TryGetValue(sid, out var existing);
        var cwd = evt.Cwd ?? existing?.Cwd;
        var session = existing ?? new SessionInfo(sid, "", null, SessionPhase.Idle, Array.Empty<PendingPermission>(), null, now, null);
        return session with
        {
            Cwd = cwd,
            DisplayName = DisplayNameFor(cwd, evt.SessionTitle, sid, evt.ProjectName),
            LastSeen = now,
            ClientApp = evt.ClientApp ?? session.ClientApp,
            Agent = evt.Agent ?? session.Agent,
            Project = evt.Project ?? session.Project,
        };
    }

    /// <summary>Releases the pending requests that match <paramref name="which"/> (all of them by default).</summary>
    private static SessionInfo ReleasePending(
        SessionInfo s, DateTimeOffset now, PermissionOutcome outcome, List<StoreEffect> effects, Func<PendingPermission, bool>? which = null)
    {
        var released = s.Pendings.Where(which ?? (_ => true)).ToList();
        if (released.Count == 0) return s;

        foreach (var pending in released) effects.Add(new PermissionCleared(s.SessionId, pending.RequestId, outcome));
        var remaining = s.Pendings.Except(released).ToList();
        return s with
        {
            Pendings = remaining,
            LastPermissionResolvedAt = now,
            Phase = remaining.Count == 0 && s.Phase == SessionPhase.NeedsPermission ? SessionPhase.Working : s.Phase,
        };
    }

    private static SessionInfo Working(SessionInfo s, List<StoreEffect> effects) =>
        ClearNotice(s, effects) with { Phase = SessionPhase.Working };

    private static SessionInfo ClearNotice(SessionInfo s, List<StoreEffect> effects)
    {
        if (s.Notice is null) return s;
        effects.Add(new NoticeCleared(s.SessionId));
        return s with { Notice = null, NoticeCallKey = null };
    }

    private bool RecentlyResolved(SessionInfo s, DateTimeOffset now) =>
        s.LastPermissionResolvedAt is { } resolved && now - resolved <= _dedupeWindow;

    /// <summary>The project folder wins over the working folder: a subagent's cwd is its worktree ("agent-…").</summary>
    internal static string DisplayNameFor(string? cwd, string? title, string sid, string? projectName = null)
    {
        if (!string.IsNullOrWhiteSpace(projectName)) return projectName;
        var folder = FolderName(cwd);
        if (!string.IsNullOrEmpty(folder)) return folder;
        if (!string.IsNullOrWhiteSpace(title)) return TextUtil.Truncate(title, 40);
        return sid.Length > 8 ? sid[..8] : sid;
    }

    private static string? FolderName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var parts = cwd.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[^1];
    }
}
