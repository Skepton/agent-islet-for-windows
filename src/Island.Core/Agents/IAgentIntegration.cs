namespace Island.Core;

/// <summary>
/// One supported coding agent (Claude Code today). Everything agent-specific lives behind this interface:
/// turning the agent's hook payloads into neutral <see cref="AgentEvent"/>s, and turning the user's answer on a
/// permission card back into the exact text the agent's hook must print. The rest of the island never sees either format.
/// </summary>
public interface IAgentIntegration
{
    /// <summary>Stable id carried in every frame ("claude-code"). Never shown to the user.</summary>
    string Id { get; }

    /// <summary>Name shown on cards ("Claude Code").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Reads one hook payload. Returns null when it carries nothing the island uses (e.g. no session id).
    /// May throw on malformed input; the caller then drops the payload and the hook stays silent.
    /// </summary>
    /// <param name="requestId">The id to give a permission request started by this payload.</param>
    AgentEvent? Translate(string payloadJson, string requestId);

    /// <summary>
    /// Turns a hook the agent delivered over HTTP (see <see cref="HookHttpServer"/>) into the frame the hub handles, the
    /// way the agent's own forwarder would have: which calls wait for an answer, the client app, the project folder.
    /// Returns null when the call carries nothing the island uses. May throw on malformed input; the call is then dropped.
    /// </summary>
    HookFrame? FrameFromHttp(string requestId, HookHttpRequest request, DateTimeOffset now);

    /// <summary>What the forwarder prints to the agent's hook stdout for <paramref name="verdict"/>; null prints nothing.</summary>
    string? EncodeVerdict(PermissionVerdict verdict);
}
