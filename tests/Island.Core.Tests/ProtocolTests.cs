using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Hook_frame_is_one_compact_line_and_round_trips()
    {
        var pretty = """
            {
              "hook_event_name": "Stop",
              "session_id": "s1"
            }
            """;
        using var doc = JsonDocument.Parse(pretty);

        var line = Frames.EncodeHook("abc", 1700000000000, 42, false, doc.RootElement);
        var frame = Frames.DecodeHook(line);

        Assert.DoesNotContain('\n', line);
        Assert.Equal("abc", frame.Id);
        Assert.Equal(1700000000000, frame.ReceivedAtUnixMs);
        Assert.Equal(42, frame.ClientPid);
        Assert.False(frame.Wait);
        Assert.Equal("{\"hook_event_name\":\"Stop\",\"session_id\":\"s1\"}", frame.PayloadJson);
    }

    [Fact]
    public void Client_app_travels_in_the_frame_and_is_optional()
    {
        using var doc = JsonDocument.Parse("""{"hook_event_name":"Stop"}""");

        var with = Frames.DecodeHook(Frames.EncodeHook("a", 1, 2, false, doc.RootElement, "VS Code"));
        var without = Frames.DecodeHook(Frames.EncodeHook("a", 1, 2, false, doc.RootElement));

        Assert.Equal("VS Code", with.ClientApp);
        Assert.Null(without.ClientApp);
    }

    [Theory]
    [InlineData("claude-vscode", "VS Code")]
    [InlineData("cli", "Terminal")]
    [InlineData("sdk-ts", null)]
    [InlineData(null, null)]
    public void Entrypoints_map_to_friendly_names(string? entrypoint, string? expected)
    {
        Assert.Equal(expected, ClientAppNames.FromEntrypoint(entrypoint));
    }

    [Fact]
    public void Description_is_only_read_for_shell_tools()
    {
        var input = "{\"command\":\"ls\",\"description\":\"List files\"}";

        Assert.Equal("List files", ToolSummary.Description("Bash", input));
        Assert.Equal("List files", ToolSummary.Description("PowerShell", input));
        Assert.Null(ToolSummary.Description("Edit", input));
        Assert.Null(ToolSummary.Description("Bash", "{\"command\":\"ls\"}"));
        Assert.Null(ToolSummary.Description("Bash", null));
    }

    [Fact]
    public void Frame_with_another_protocol_version_is_rejected()
    {
        var line = "{\"v\":2,\"id\":\"x\",\"kind\":\"hook\",\"payload\":{}}";

        Assert.Throws<InvalidDataException>(() => Frames.DecodeHook(line));
    }

    [Fact]
    public void Null_decision_round_trips_as_null()
    {
        var line = Frames.EncodeResponse("id-1", null);

        var (id, decision) = Frames.DecodeResponse(line);

        Assert.Equal("id-1", id);
        Assert.Null(decision);
    }

    [Fact]
    public void Decision_round_trips_as_raw_json()
    {
        var line = Frames.EncodeResponse("id-2", PermissionDecision.Deny("nope"));

        var (id, decision) = Frames.DecodeResponse(line);

        Assert.Equal("id-2", id);
        Assert.Equal("{\"behavior\":\"deny\",\"message\":\"nope\"}", decision);
    }

    [Fact]
    public void Allow_without_remembered_rules_is_behavior_only()
    {
        Assert.Equal("{\"behavior\":\"allow\"}", PermissionDecision.Allow());
    }

    [Fact]
    public void Allow_and_remember_echoes_the_suggestion_unchanged()
    {
        var suggestion = Payloads.Load("PermissionRequest.with-suggestions.json").PermissionSuggestionsJson;

        using var doc = JsonDocument.Parse(PermissionDecision.Allow(suggestion));
        var updated = doc.RootElement.GetProperty("updatedPermissions");

        Assert.Equal("allow", doc.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("addRules", updated[0].GetProperty("type").GetString());
        Assert.Equal("localSettings", updated[0].GetProperty("destination").GetString());
    }

    [Fact]
    public void Deny_escapes_the_message_and_hook_output_wraps_it()
    {
        var message = "say \"no\"\nnow";
        var decision = PermissionDecision.Deny(message);

        using var doc = JsonDocument.Parse(PermissionDecision.HookOutput(decision));
        var hookSpecific = doc.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("PermissionRequest", hookSpecific.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", hookSpecific.GetProperty("decision").GetProperty("behavior").GetString());
        Assert.Equal(message, hookSpecific.GetProperty("decision").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Bash", "{\"command\":\"npm test\",\"description\":\"x\"}", "npm test")]
    [InlineData("PowerShell", "{\"command\":\"Get-Date\"}", "Get-Date")]
    [InlineData("Edit", "{\"file_path\":\"C:\\\\a\\\\b.cs\"}", "C:\\a\\b.cs")]
    [InlineData("WebFetch", "{\"url\":\"https://example.com\"}", "https://example.com")]
    [InlineData("Bash", "{\"command\":\"line1\\nline2\"}", "line1 line2")]
    public void Summarises_known_tools(string tool, string input, string expected)
    {
        Assert.Equal(expected, ToolSummary.Describe(tool, input));
    }

    [Fact]
    public void Unknown_tool_falls_back_to_compact_input()
    {
        Assert.Equal("{\"x\":1}", ToolSummary.Describe("Mystery", "{\"x\":1}"));
    }

    [Fact]
    public void Long_text_is_truncated_with_an_ellipsis()
    {
        var result = TextUtil.Truncate(new string('a', 300), 10);

        Assert.Equal(10, result.Length);
        Assert.EndsWith("…", result);
    }
}
