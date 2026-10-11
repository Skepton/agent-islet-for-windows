using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

internal static class Payloads
{
    /// <summary>Path to a file or folder under tests/fixtures (copied next to the test binaries).</summary>
    public static string Fixture(params string[] parts) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", Path.Combine(parts));

    public static HookPayload Load(string hookFixtureName) =>
        HookPayload.Parse(File.ReadAllText(Fixture("hooks", hookFixtureName)));

    public static HookPayload Parse(Dictionary<string, string?> fields) =>
        HookPayload.Parse(JsonSerializer.Serialize(fields));

    /// <summary>Builds a payload from a base object plus raw JSON members (each starting with a comma).</summary>
    public static HookPayload Hook(string eventName, string extraMembers = "", string sessionId = "s1") =>
        HookPayload.Parse(
            "{\"hook_event_name\":\"" + eventName + "\",\"session_id\":\"" + sessionId +
            "\",\"cwd\":\"C:\\\\work\\\\my-app\"" + extraMembers + "}");

    public static string BashPermission(string command) =>
        ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"" + command + "\"},\"permission_mode\":\"default\"";
}
