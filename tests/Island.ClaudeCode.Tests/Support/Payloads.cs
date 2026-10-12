using System.Text.Json;
using Island.ClaudeCode;
using Island.Core;

namespace Island.ClaudeCode.Tests;

internal static class Payloads
{
    private static readonly ClaudeCodeAgent Agent = new();

    /// <summary>Path to a file or folder under tests/fixtures (copied next to the test binaries).</summary>
    public static string Fixture(params string[] parts) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", Path.Combine(parts));

    public static string Text(string hookFixtureName) => File.ReadAllText(Fixture("hooks", hookFixtureName));

    public static HookPayload Load(string hookFixtureName) => HookPayload.Parse(Text(hookFixtureName));

    public static string Json(Dictionary<string, string?> fields) => JsonSerializer.Serialize(fields);

    /// <summary>A hook payload from a base object plus raw JSON members (each starting with a comma).</summary>
    public static string Hook(string eventName, string extraMembers = "", string sessionId = "s1") =>
        "{\"hook_event_name\":\"" + eventName + "\",\"session_id\":\"" + sessionId +
        "\",\"cwd\":\"C:\\\\work\\\\my-app\"" + extraMembers + "}";

    public static string BashPermission(string command) =>
        ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"" + command + "\"},\"permission_mode\":\"default\"";

    /// <summary>What the hub does with a payload: translate it and stamp the agent's name on it.</summary>
    public static AgentEvent Event(string payloadJson, string requestId = "req") =>
        (Agent.Translate(payloadJson, requestId) ?? throw new InvalidOperationException("Payload has no session.")) with
        {
            Agent = Agent.DisplayName,
        };
}
