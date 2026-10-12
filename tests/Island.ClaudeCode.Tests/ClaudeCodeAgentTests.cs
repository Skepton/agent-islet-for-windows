using System.Text.Json;
using Island.ClaudeCode;
using Island.Core;

namespace Island.ClaudeCode.Tests;

/// <summary>Claude Code hook payloads in, neutral events and PermissionRequest hook output out.</summary>
public class ClaudeCodeAgentTests
{
    private const string AddRules =
        """{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"node -e ' *"}],"behavior":"allow","destination":"localSettings"}""";

    private const string AddDirectories =
        """{"type":"addDirectories","directories":["C:\\proj"],"destination":"session"}""";

    private readonly ClaudeCodeAgent _agent = new();

    public static IEnumerable<object[]> HookFixtures() =>
        Directory.GetFiles(Payloads.Fixture("hooks")).Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(HookFixtures))]
    public void Every_captured_hook_fixture_translates(string fileName)
    {
        var evt = _agent.Translate(Payloads.Text(fileName), "req");

        Assert.NotNull(evt);
        Assert.False(string.IsNullOrEmpty(evt.SessionId));
    }

    [Theory]
    [InlineData("SessionStart", typeof(SessionStarted))]
    [InlineData("UserPromptSubmit", typeof(PromptSubmitted))]
    [InlineData("PostToolUse", typeof(ToolProceeded))]
    [InlineData("PermissionDenied", typeof(ToolProceeded))]
    [InlineData("Stop", typeof(TurnFinished))]
    [InlineData("SessionEnd", typeof(SessionEnded))]
    [InlineData("PreToolUse", typeof(SessionActivity))]
    [InlineData("SomethingNew", typeof(SessionActivity))]
    public void Events_map_to_neutral_events(string hookEvent, Type expected)
    {
        var evt = _agent.Translate(Payloads.Hook(hookEvent), "req");

        Assert.IsType(expected, evt);
        Assert.Equal("C:\\work\\my-app", evt!.Cwd);
    }

    [Fact]
    public void Payload_without_a_session_is_ignored()
    {
        Assert.Null(_agent.Translate("""{"hook_event_name":"Stop"}""", "req"));
    }

    [Fact]
    public void Permission_request_fixture_becomes_a_card_with_claudes_wording()
    {
        var evt = Assert.IsType<PermissionRequested>(_agent.Translate(Payloads.Text("PermissionRequest.json"), "req-7"));
        var permission = evt.Permission;

        Assert.Equal("req-7", permission.RequestId);
        Assert.Equal("Bash", permission.ToolName);
        Assert.Equal("Claude wants to run Bash.", permission.Headline);
        Assert.Equal("$", permission.CommandPrefix);
        Assert.False(string.IsNullOrEmpty(permission.Summary));
    }

    [Theory]
    [InlineData("Bash", "$", "Claude wants to run Bash.")]
    [InlineData("PowerShell", "PS>", "Claude wants to run PowerShell.")]
    [InlineData("Edit", "", "Claude wants to use Edit.")]
    public void Shells_get_a_prompt_prefix_and_other_tools_say_use(string tool, string prefix, string headline)
    {
        Assert.Equal(prefix, ToolSummary.CommandPrefix(tool));
        Assert.Equal(headline, ToolSummary.Headline(tool));
    }

    [Fact]
    public void Suggestions_become_remember_options_with_the_reference_labels()
    {
        var payload = Payloads.Hook("PermissionRequest",
            Payloads.BashPermission("node -e 1") + ",\"permission_suggestions\":[" + AddRules + "," + AddDirectories + "]");

        var options = Assert.IsType<PermissionRequested>(_agent.Translate(payload, "r")).Permission.Suggestions;

        Assert.Collection(options,
            o => Assert.Equal(("Always Allow (Bash)", "Allow + remember Bash(node -e ' *) in this project (just you)", AddRules), (o.ButtonLabel, o.Detail, o.Payload)),
            o => Assert.Equal(("Allow + add folder", "Allow + add folder C:\\proj for this session", AddDirectories), (o.ButtonLabel, o.Detail, o.Payload)));
    }

    [Theory]
    [InlineData("session", "for this session")]
    [InlineData("localSettings", "in this project (just you)")]
    [InlineData("projectSettings", "in this project (shared)")]
    [InlineData("userSettings", "in all projects")]
    [InlineData("somewhereNew", "(somewhereNew)")]
    public void Suggestions_that_differ_only_in_where_they_are_saved_get_different_labels(string destination, string where)
    {
        var raw = "{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"PowerShell\",\"ruleContent\":\"Start-Sleep:*\"}],\"destination\":\"" + destination + "\"}";

        Assert.Equal("Allow + remember PowerShell(Start-Sleep:*) " + where, SuggestionChoice.FromRaw(new[] { raw })[0].Label);
    }

    [Fact]
    public void A_permission_request_and_its_PostToolUse_share_a_call_key_that_other_calls_do_not()
    {
        var request = Assert.IsType<PermissionRequested>(_agent.Translate(
            Payloads.Hook("PermissionRequest", Payloads.BashPermission("npm test")), "r"));
        var same = Assert.IsType<ToolProceeded>(_agent.Translate(Payloads.Hook("PostToolUse",
            ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\",\"timeout\":120000},\"tool_use_id\":\"t1\""), "p"));
        var other = Assert.IsType<ToolProceeded>(_agent.Translate(Payloads.Hook("PostToolUse",
            ",\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:\\\\a.cs\"}"), "p"));

        Assert.NotNull(request.Permission.CallKey);
        Assert.Equal(request.Permission.CallKey, same.CallKey); // extra input fields (timeout) do not matter
        Assert.NotEqual(request.Permission.CallKey, other.CallKey);
    }

    [Fact]
    public void A_question_and_its_answered_PostToolUse_share_a_call_key()
    {
        var asked = """{"questions":[{"question":"Which one?","options":[{"label":"A"}]}]}""";
        var answered = """{"questions":[{"question":"Which one?","options":[{"label":"A"}]}],"answers":{"Which one?":"A"}}""";

        var question = Assert.IsType<InputRequested>(_agent.Translate(
            Payloads.Hook("PreToolUse", ",\"tool_name\":\"AskUserQuestion\",\"tool_input\":" + asked), "q"));
        var done = Assert.IsType<ToolProceeded>(_agent.Translate(
            Payloads.Hook("PostToolUse", ",\"tool_name\":\"AskUserQuestion\",\"tool_input\":" + answered), "p"));

        Assert.Equal(question.CallKey, done.CallKey);
    }

    [Fact]
    public void Always_allow_names_the_rule_tool_and_falls_back_to_the_permission_tool()
    {
        var mcp = SuggestionChoice.FromRaw(new[] { """{"type":"addRules","rules":[{"toolName":"mcp__x__y"}]}""" })[0];
        var noRules = SuggestionChoice.FromRaw(new[] { """{"type":"addRules"}""" })[0];

        Assert.Equal("Always Allow (mcp__x__y)", mcp.ButtonLabel("Bash"));
        Assert.Equal("Always Allow (WebFetch)", noRules.ButtonLabel("WebFetch"));
    }

    [Fact]
    public void Unknown_suggestion_types_keep_their_generic_label()
    {
        var choice = SuggestionChoice.FromRaw(new[] { """{"type":"setMode","mode":"acceptEdits"}""" })[0];

        Assert.Equal("Allow + setMode", choice.ButtonLabel("Bash"));
    }

    [Fact]
    public void Permission_prompt_notification_only_echoes_the_request()
    {
        var echo = Assert.IsType<InputRequested>(_agent.Translate(Payloads.Text("Notification.permission_prompt.json"), "n"));
        var idle = Assert.IsType<InputRequested>(_agent.Translate(
            Payloads.Hook("Notification", ",\"notification_type\":\"idle_prompt\",\"message\":\"Waiting\""), "n"));

        Assert.True(echo.EchoesPermission);
        Assert.False(idle.EchoesPermission);
        Assert.Equal("Waiting", idle.Text);
    }

    [Fact]
    public void A_question_from_Claude_becomes_input_requested_with_its_options()
    {
        var input = """
            {"questions":[{"question":"What should I do next?","header":"Next","multiSelect":false,
              "options":[{"label":"Build it","description":"x"},{"label":"Commit first","description":"y"}]}]}
            """;

        var evt = Assert.IsType<InputRequested>(_agent.Translate(
            Payloads.Hook("PreToolUse", ",\"tool_name\":\"AskUserQuestion\",\"tool_input\":" + input), "q"));

        Assert.Equal("What should I do next?\n• Build it\n• Commit first", evt.Text);
        Assert.False(evt.EchoesPermission);
        Assert.Equal("PreToolUse(AskUserQuestion)", evt.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"questions\":[{\"header\":\"no question\"}]}")]
    [InlineData("{\"questions\":\"oops\"}")]
    public void A_question_in_an_unexpected_shape_still_shows_a_card(string? input)
    {
        var extra = ",\"tool_name\":\"AskUserQuestion\"" + (input is null ? "" : ",\"tool_input\":" + input);

        var evt = Assert.IsType<InputRequested>(_agent.Translate(Payloads.Hook("PreToolUse", extra), "q"));

        Assert.Equal("Claude has a question for you", evt.Text);
    }

    [Fact]
    public void PreToolUse_for_other_tools_shows_nothing()
    {
        Assert.IsType<SessionActivity>(_agent.Translate(Payloads.Hook("PreToolUse", ",\"tool_name\":\"Bash\""), "p"));
    }

    [Fact]
    public void Several_questions_are_separated_by_a_blank_line_and_long_text_is_cut()
    {
        var two = """{"questions":[{"question":"A?","options":[{"label":"1"}]},{"question":"B?"}]}""";
        var huge = "{\"questions\":[{\"question\":\"" + new string('x', 1000) + "\"}]}";

        Assert.Equal("A?\n• 1\n\nB?", QuestionText.From(two));
        Assert.Equal(400, QuestionText.From(huge)!.Length);
    }

    [Theory]
    [InlineData("PostToolUse", ",\"tool_name\":\"Bash\"", "PostToolUse(Bash)")]
    [InlineData("Notification", ",\"notification_type\":\"idle_prompt\",\"message\":\"secret\"", "Notification(idle_prompt)")]
    [InlineData("Stop", "", "Stop")]
    public void Source_names_the_event_but_never_its_content(string hookEvent, string extra, string expected)
    {
        Assert.Equal(expected, _agent.Translate(Payloads.Hook(hookEvent, extra), "s")!.Source);
    }

    [Fact]
    public void Unrelated_notification_types_only_keep_the_session_alive()
    {
        var evt = _agent.Translate(Payloads.Hook("Notification", ",\"notification_type\":\"auth_success\",\"message\":\"ok\""), "n");

        Assert.IsType<SessionActivity>(evt);
    }

    [Fact]
    public void Stop_hook_active_means_the_turn_continues()
    {
        var evt = Assert.IsType<TurnFinished>(_agent.Translate(
            Payloads.Hook("Stop", ",\"stop_hook_active\":true,\"last_assistant_message\":\"done\""), "s"));

        Assert.True(evt.Continuing);
        Assert.Equal("done", evt.Message);
    }

    [Fact]
    public void Allow_prints_the_hook_specific_output()
    {
        using var doc = JsonDocument.Parse(_agent.EncodeVerdict(PermissionVerdict.Allow)!);
        var hookSpecific = doc.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("PermissionRequest", hookSpecific.GetProperty("hookEventName").GetString());
        Assert.Equal("""{"behavior":"allow"}""", hookSpecific.GetProperty("decision").GetRawText());
    }

    [Fact]
    public void Deny_without_a_message_tells_claude_where_it_came_from()
    {
        using var doc = JsonDocument.Parse(_agent.EncodeVerdict(PermissionVerdict.Deny())!);
        var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");

        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
        Assert.Equal(ClaudeCodeAgent.DefaultDenyMessage, decision.GetProperty("message").GetString());
    }

    [Fact]
    public void Remember_echoes_the_raw_suggestion_back_unchanged()
    {
        var option = SuggestionChoice.FromRaw(new[] { AddDirectories })[0].ToOption("Bash");

        using var doc = JsonDocument.Parse(_agent.EncodeVerdict(PermissionVerdict.Remember(option))!);
        var updated = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("updatedPermissions");

        Assert.Equal(AddDirectories, updated[0].GetRawText());
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
    public void Allow_without_remembered_rules_is_behavior_only()
    {
        Assert.Equal("{\"behavior\":\"allow\"}", PermissionDecision.Allow());
    }

    [Fact]
    public void Deny_escapes_the_message()
    {
        var message = "say \"no\"\nnow";

        using var doc = JsonDocument.Parse(PermissionDecision.HookOutput(PermissionDecision.Deny(message)));

        Assert.Equal(message, doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("message").GetString());
    }
}
