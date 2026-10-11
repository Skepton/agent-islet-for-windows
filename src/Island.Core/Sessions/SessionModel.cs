namespace Island.Core;

public enum SessionPhase { Idle, Working, NeedsPermission, NeedsInput, Finished }

public enum PermissionOutcome
{
    /// The user clicked Allow or Deny on the island.
    Answered,

    /// The user chose "Answer in terminal"; the hook returns no decision.
    DeferredToTerminal,

    /// Claude Code moved on (PostToolUse / PermissionDenied / UserPromptSubmit / Stop), so it was answered outside the island.
    ResolvedElsewhere,

    /// The hook connection closed (answered in the terminal, or the hook hit its deadline).
    Abandoned,

    /// A newer permission request arrived in the same session.
    Superseded,
}

public sealed record PendingPermission(
    string RequestId,
    string ToolName,
    string Summary,
    string PermissionMode,
    IReadOnlyList<SuggestionChoice> Suggestions,
    string? Description = null);

/// <param name="LastPrompt">Truncated copy of the user's last prompt. In memory only; never written to disk.</param>
/// <param name="ClientApp">"VS Code", "Terminal", ... when the forwarder could tell.</param>
public sealed record SessionInfo(
    string SessionId,
    string DisplayName,
    string? Cwd,
    SessionPhase Phase,
    PendingPermission? Pending,
    string? Notice,
    DateTimeOffset LastSeen,
    DateTimeOffset? LastPermissionResolvedAt,
    string? LastPrompt = null,
    string? ClientApp = null);

/// <summary>What the presentation layer should show or clear after a store update.</summary>
public abstract record StoreEffect;

public sealed record PermissionShown(
    string SessionId,
    string DisplayName,
    PendingPermission Permission,
    string? LastPrompt = null,
    string? ClientApp = null) : StoreEffect;

public sealed record PermissionCleared(string SessionId, string RequestId, PermissionOutcome Outcome) : StoreEffect;

public sealed record NoticeShown(
    string SessionId,
    string DisplayName,
    SessionPhase Phase,
    string Text,
    string? LastPrompt = null,
    string? ClientApp = null) : StoreEffect;

public sealed record NoticeCleared(string SessionId) : StoreEffect;

public sealed record SessionRemoved(string SessionId) : StoreEffect;
