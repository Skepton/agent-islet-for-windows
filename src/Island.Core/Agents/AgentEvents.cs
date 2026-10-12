namespace Island.Core;

/// <summary>
/// Something an agent session did, in agent-neutral terms. Integrations build these from their own hook payloads;
/// <see cref="SessionStore"/> turns them into session state and card effects.
/// </summary>
public abstract record AgentEvent
{
    public required string SessionId { get; init; }
    public string? Cwd { get; init; }

    /// <summary>Used as the display name when there is no working folder.</summary>
    public string? SessionTitle { get; init; }

    /// <summary>"VS Code", "Terminal", ... when the forwarder could tell.</summary>
    public string? ClientApp { get; init; }

    /// <summary>Display name of the agent ("Claude Code"); filled in by the hub from the integration.</summary>
    public string? Agent { get; init; }

    /// <summary><see cref="ProjectRef.Key"/> of the session's project folder; filled in by the hub.</summary>
    public string? Project { get; init; }
}

/// <summary>A new or resumed session.</summary>
public sealed record SessionStarted : AgentEvent;

/// <summary>The user sent a prompt: the agent is working again.</summary>
public sealed record PromptSubmitted : AgentEvent
{
    /// <summary>Shown on cards; kept in memory only, never persisted.</summary>
    public string? Prompt { get; init; }
}

/// <summary>The agent is blocked until someone allows or denies a tool call.</summary>
public sealed record PermissionRequested : AgentEvent
{
    public required PendingPermission Permission { get; init; }
}

/// <summary>The agent moved past a tool call (it ran, or was denied elsewhere), so any pending request was answered outside the island.</summary>
public sealed record ToolProceeded : AgentEvent;

/// <summary>The agent is waiting for the user (a question, an idle prompt, ...).</summary>
public sealed record InputRequested : AgentEvent
{
    public required string Text { get; init; }

    /// <summary>
    /// True when this only repeats a permission request that has its own card (Claude Code sends a
    /// "needs your permission" notification a few seconds after the request). It is dropped while that card is
    /// showing or was just answered.
    /// </summary>
    public bool EchoesPermission { get; init; }
}

/// <summary>The agent finished its turn.</summary>
public sealed record TurnFinished : AgentEvent
{
    /// <summary>The agent's last message, if any.</summary>
    public string? Message { get; init; }

    /// <summary>True when the agent is about to carry on by itself (e.g. a stop hook told it to); no "finished" card is shown.</summary>
    public bool Continuing { get; init; }
}

/// <summary>The session closed.</summary>
public sealed record SessionEnded : AgentEvent;

/// <summary>Anything else from a known session: keeps it alive and up to date, shows nothing.</summary>
public sealed record SessionActivity : AgentEvent;
