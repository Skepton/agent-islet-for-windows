using Island.ClaudeCode;
using Island.Core;

namespace Island.ClaudeCode.Tests;

/// <summary>Claude Code's http hooks: the request becomes the same frame the forwarder used to build.</summary>
public class ClaudeCodeHttpFrameTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 21, 0, 0, TimeSpan.Zero);

    private readonly ClaudeCodeAgent _agent = new();

    private static HookHttpRequest Request(string body, params (string Name, string Value)[] headers) =>
        new(body, headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void A_permission_request_waits_for_an_answer()
    {
        var frame = _agent.FrameFromHttp("req-1", Request(Payloads.Text("PermissionRequest.json")), Now);

        Assert.NotNull(frame);
        Assert.True(frame.Wait);
        Assert.Equal("req-1", frame.Id);
        Assert.Equal(ClaudeCodeAgent.AgentId, frame.Agent);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), frame.ReceivedAtUnixMs);
    }

    [Theory]
    [InlineData("UserPromptSubmit.json")]
    [InlineData("PostToolUse.json")]
    [InlineData("PreToolUse.json")]
    [InlineData("Notification.permission_prompt.json")]
    [InlineData("Stop.json")]
    [InlineData("SessionEnd.json")]
    public void Every_other_event_is_fire_and_forget(string fixture)
    {
        var frame = _agent.FrameFromHttp("r", Request(Payloads.Text(fixture)), Now);

        Assert.NotNull(frame);
        Assert.False(frame.Wait);
    }

    [Fact]
    public void The_payload_goes_through_unchanged_so_translate_reads_it_as_before()
    {
        var body = Payloads.Text("PermissionRequest.with-suggestions.json");

        var frame = _agent.FrameFromHttp("r", Request(body), Now)!;
        var evt = _agent.Translate(frame.PayloadJson, frame.Id);

        Assert.Equal(body, frame.PayloadJson);
        var permission = Assert.IsType<PermissionRequested>(evt);
        Assert.NotEmpty(permission.Permission.Suggestions);
    }

    [Fact]
    public void The_editor_and_the_pid_come_from_the_headers()
    {
        var frame = _agent.FrameFromHttp(
            "r", Request(Payloads.Text("Stop.json"), ("x-entrypoint", "claude-vscode"), ("X-Claude-Pid", "4242")), Now)!;

        Assert.Equal("VS Code", frame.ClientApp);
        Assert.Equal(4242, frame.ClientPid);
    }

    [Fact]
    public void Missing_or_odd_headers_are_tolerated()
    {
        var frame = _agent.FrameFromHttp("r", Request(Payloads.Text("Stop.json"), ("X-Entrypoint", ""), ("X-Claude-Pid", "not a number")), Now)!;

        Assert.Null(frame.ClientApp);
        Assert.Equal(0, frame.ClientPid);
    }

    [Fact]
    public void A_body_that_is_not_a_hook_is_ignored()
    {
        Assert.Null(_agent.FrameFromHttp("r", Request("""{"something":"else"}"""), Now));
    }

    [Theory]
    [InlineData(@"C:\work\my-app\.claude\worktrees\agent-a5f3", @"C:\work\my-app")]
    [InlineData("C:/work/my-app/.claude/worktrees/agent-a5f3/src", @"C:\work\my-app")]
    [InlineData(@"c:\WORK\my-app\.CLAUDE\Worktrees\x", @"c:\WORK\my-app")]
    [InlineData(@"C:\work\my-app", null)]
    [InlineData(@"C:\work\my-app\.claude", null)]
    [InlineData(@"\.claude\worktrees\x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_subagents_worktree_maps_back_to_its_project(string? cwd, string? expected)
    {
        Assert.Equal(expected, ProjectFolder.OfWorktree(cwd));
    }

    [Fact]
    public void A_subagent_frame_carries_the_project_so_it_is_not_asked_about_as_a_new_project()
    {
        var body = """{"hook_event_name":"Stop","session_id":"s1","cwd":"C:\\work\\my-app\\.claude\\worktrees\\agent-1"}""";

        var frame = _agent.FrameFromHttp("r", Request(body), Now)!;

        Assert.Equal(@"C:\work\my-app", frame.Project);
    }
}
