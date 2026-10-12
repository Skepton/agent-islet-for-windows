namespace Island.Core;

public enum SessionPhase { Idle, Working, NeedsPermission, NeedsInput, Finished }

public enum PermissionOutcome
{
    /// The user clicked Allow or Deny on the island.
    Answered,

    /// The user chose "Answer in terminal"; the hook returns no decision.
    DeferredToTerminal,

    /// The agent moved on (ran or skipped the tool, got a new prompt, finished its turn), so it was answered outside the island.
    ResolvedElsewhere,

    /// The hook connection closed (answered in the terminal, or the hook hit its deadline).
    Abandoned,
}

/// <summary>A permission request as the card shows it. The agent integration fills in every display string.</summary>
/// <param name="ToolName">"Bash", "Edit", ...</param>
/// <param name="Summary">One line: the command, file or URL.</param>
/// <param name="Suggestions">Ways to "allow and remember" the agent offered; may be empty.</param>
/// <param name="Description">The agent's plain-language description of the call, when it gave one.</param>
/// <param name="Headline">"Claude wants to run Bash."; the card falls back to a generic line when empty.</param>
/// <param name="CommandPrefix">"$", "PS>", or empty for tools that are not a shell.</param>
/// <param name="CallKey">
/// Identifies the tool call, so the event that ends it (<see cref="ToolProceeded.CallKey"/>) closes this request and
/// no other. Null when the agent cannot tell calls apart.
/// </param>
public sealed record PendingPermission(
    string RequestId,
    string ToolName,
    string Summary,
    string PermissionMode,
    IReadOnlyList<RememberOption> Suggestions,
    string? Description = null,
    string Headline = "",
    string CommandPrefix = "",
    string? CallKey = null);

/// <param name="Pendings">Permission requests waiting for an answer, oldest first. Parallel tool calls (subagents) can have several.</param>
/// <param name="LastPrompt">Truncated copy of the user's last prompt. In memory only; never written to disk.</param>
/// <param name="ClientApp">"VS Code", "Terminal", ... when the forwarder could tell.</param>
/// <param name="Agent">Display name of the agent running the session ("Claude Code").</param>
/// <param name="Project"><see cref="ProjectRef.Key"/> of the project folder, when known.</param>
/// <param name="NoticeCallKey">The tool call a needs-input notice belongs to (a question); only that call's end clears it.</param>
public sealed record SessionInfo(
    string SessionId,
    string DisplayName,
    string? Cwd,
    SessionPhase Phase,
    IReadOnlyList<PendingPermission> Pendings,
    string? Notice,
    DateTimeOffset LastSeen,
    DateTimeOffset? LastPermissionResolvedAt,
    string? LastPrompt = null,
    string? ClientApp = null,
    string? Agent = null,
    string? Project = null,
    string? NoticeCallKey = null)
{
    /// <summary>The newest waiting request, or null.</summary>
    public PendingPermission? Pending => Pendings.Count == 0 ? null : Pendings[^1];
}

/// <summary>What the presentation layer should show or clear after a store update.</summary>
public abstract record StoreEffect;

public sealed record PermissionShown(
    string SessionId,
    string DisplayName,
    PendingPermission Permission,
    string? LastPrompt = null,
    string? ClientApp = null,
    string? Agent = null) : StoreEffect;

public sealed record PermissionCleared(string SessionId, string RequestId, PermissionOutcome Outcome) : StoreEffect;

public sealed record NoticeShown(
    string SessionId,
    string DisplayName,
    SessionPhase Phase,
    string Text,
    string? LastPrompt = null,
    string? ClientApp = null,
    string? Agent = null) : StoreEffect;

public sealed record NoticeCleared(string SessionId) : StoreEffect;

public sealed record SessionRemoved(string SessionId) : StoreEffect;

/// <summary>Any event arrived (for the debug log). <paramref name="Shown"/> is false when its project is not shown.</summary>
public sealed record EventReceived(string SessionId, string Source, bool Shown) : StoreEffect;

/// <summary>A session from a folder the user has not decided about yet reached the island: ask whether to show it.</summary>
public sealed record ProjectApprovalRequested(ProjectRef Project, string? Agent = null) : StoreEffect;

/// <summary>The user decided about a project (or forgot it: <see cref="ProjectDecision.Unknown"/>).</summary>
public sealed record ProjectDecided(ProjectRef Project, ProjectDecision Decision) : StoreEffect;
