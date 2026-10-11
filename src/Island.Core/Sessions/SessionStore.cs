namespace Island.Core;

/// <summary>
/// Per-session state machine fed by hook payloads. Pure logic: no I/O, no UI.
/// Every method returns the effects the presentation layer should apply.
/// </summary>
public sealed class SessionStore
{
    private static readonly TimeSpan DefaultDedupeWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(12);

    // Notification types that mean "Claude is waiting for a human". permission_prompt is only a
    // duplicate of a PermissionRequest card, so it is filtered separately in Apply.
    private static readonly string[] NeedsYouTypes =
    [
        "permission_prompt", "idle_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input",
    ];

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

    /// <param name="requestId">Frame id. Becomes the permission request id for PermissionRequest.</param>
    public IReadOnlyList<StoreEffect> Apply(HookPayload hook, string requestId, DateTimeOffset now)
    {
        var effects = new List<StoreEffect>();
        var sid = hook.SessionId;
        if (string.IsNullOrEmpty(sid)) return effects;

        lock (_gate)
        {
            if (hook.EventName == "SessionEnd")
            {
                if (_sessions.TryGetValue(sid, out var ending))
                {
                    ReleasePending(ending, now, PermissionOutcome.Abandoned, effects);
                    _sessions.Remove(sid);
                    effects.Add(new SessionRemoved(sid));
                }
                return effects;
            }

            var s = Touch(sid, hook, now);
            switch (hook.EventName)
            {
                case "SessionStart":
                    s = ClearNotice(s, effects) with { Phase = SessionPhase.Idle };
                    break;

                case "UserPromptSubmit":
                    s = Working(ReleasePending(s, now, PermissionOutcome.ResolvedElsewhere, effects), effects);
                    if (!string.IsNullOrWhiteSpace(hook.Prompt)) s = s with { LastPrompt = TextUtil.Truncate(hook.Prompt, 160) };
                    break;

                case "PermissionRequest":
                {
                    s = ReleasePending(s, now, PermissionOutcome.Superseded, effects);
                    var pending = new PendingPermission(
                        requestId,
                        hook.ToolName ?? "tool",
                        ToolSummary.Describe(hook.ToolName, hook.ToolInputJson),
                        hook.PermissionMode ?? "",
                        SuggestionChoice.FromRaw(hook.PermissionSuggestionsJson),
                        ToolSummary.Description(hook.ToolName, hook.ToolInputJson));
                    s = ClearNotice(s, effects) with { Phase = SessionPhase.NeedsPermission, Pending = pending };
                    effects.Add(new PermissionShown(sid, s.DisplayName, pending, s.LastPrompt, s.ClientApp));
                    break;
                }

                case "PostToolUse":
                case "PermissionDenied":
                    s = Working(ReleasePending(s, now, PermissionOutcome.ResolvedElsewhere, effects), effects);
                    break;

                case "Notification":
                {
                    var type = hook.NotificationType;
                    // Claude Code sends permission_prompt ~6 s after the PermissionRequest; it is a duplicate
                    // of the card when one is showing or was just answered.
                    var duplicateOfCard = type == "permission_prompt" && (s.Pending is not null || RecentlyResolved(s, now));
                    if (!duplicateOfCard && type is not null && NeedsYouTypes.Contains(type))
                    {
                        var text = TextUtil.Truncate(hook.Message ?? type, 160);
                        s = s with { Phase = SessionPhase.NeedsInput, Notice = text };
                        effects.Add(new NoticeShown(sid, s.DisplayName, SessionPhase.NeedsInput, text, s.LastPrompt, s.ClientApp));
                    }
                    break;
                }

                case "Stop":
                {
                    s = ReleasePending(s, now, PermissionOutcome.ResolvedElsewhere, effects);
                    if (!hook.StopHookActive)
                    {
                        var text = TextUtil.Truncate(hook.LastAssistantMessage ?? "Finished", 400);
                        s = s with { Phase = SessionPhase.Finished, Notice = text };
                        effects.Add(new NoticeShown(sid, s.DisplayName, SessionPhase.Finished, text, s.LastPrompt, s.ClientApp));
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
            var owner = _sessions.Values.FirstOrDefault(x => x.Pending?.RequestId == requestId);
            if (owner is null) return effects;
            _sessions[owner.SessionId] = ReleasePending(owner, now, outcome, effects);
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

    private SessionInfo Touch(string sid, HookPayload hook, DateTimeOffset now)
    {
        _sessions.TryGetValue(sid, out var existing);
        var cwd = hook.Cwd ?? existing?.Cwd;
        var session = existing ?? new SessionInfo(sid, "", null, SessionPhase.Idle, null, null, now, null);
        return session with
        {
            Cwd = cwd,
            DisplayName = DisplayNameFor(cwd, hook.SessionTitle, sid),
            LastSeen = now,
            ClientApp = hook.ClientApp ?? session.ClientApp,
        };
    }

    private static SessionInfo ReleasePending(SessionInfo s, DateTimeOffset now, PermissionOutcome outcome, List<StoreEffect> effects)
    {
        if (s.Pending is not { } pending) return s;
        effects.Add(new PermissionCleared(s.SessionId, pending.RequestId, outcome));
        return s with
        {
            Pending = null,
            LastPermissionResolvedAt = now,
            Phase = s.Phase == SessionPhase.NeedsPermission ? SessionPhase.Working : s.Phase,
        };
    }

    private static SessionInfo Working(SessionInfo s, List<StoreEffect> effects) =>
        ClearNotice(s, effects) with { Phase = SessionPhase.Working };

    private static SessionInfo ClearNotice(SessionInfo s, List<StoreEffect> effects)
    {
        if (s.Notice is null) return s;
        effects.Add(new NoticeCleared(s.SessionId));
        return s with { Notice = null };
    }

    private bool RecentlyResolved(SessionInfo s, DateTimeOffset now) =>
        s.LastPermissionResolvedAt is { } resolved && now - resolved <= _dedupeWindow;

    internal static string DisplayNameFor(string? cwd, string? title, string sid)
    {
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
