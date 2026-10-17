using System.Text.Json.Nodes;
using Island.ClaudeCode;

namespace Island.ClaudeCode.Tests;

/// <summary>Everything here runs against temp files: the real ~/.claude/settings.json is never touched.</summary>
public sealed class ClaudeCodeSetupTests : IDisposable
{
    private const string Url = "http://127.0.0.1:47821/hook/claude-code/tok123";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 21, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-setup-" + Guid.NewGuid().ToString("N"));

    private string Settings => Path.Combine(_dir, "claude", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ClaudeCodeSetup Setup(string? url = Url) => new(Settings, () => url, () => Now);

    private void Write(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, content);
    }

    private JsonNode Read() => JsonNode.Parse(File.ReadAllText(Settings))!;

    private string[] Backups() => Directory.Exists(Path.GetDirectoryName(Settings)!)
        ? Directory.GetFiles(Path.GetDirectoryName(Settings)!, "settings.json.island-backup-*")
        : Array.Empty<string>();

    private const string UserHook = """{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo mine"}]}]}}""";

    [Fact]
    public void Before_connecting_the_status_says_so()
    {
        var status = Setup().Status();

        Assert.Equal(SetupState.NotConnected, status.State);
        Assert.Equal(0, status.Hooks);
    }

    [Fact]
    public void Connecting_registers_http_hooks_that_call_the_apps_endpoint_and_keeps_the_users_own()
    {
        Write(UserHook);

        var result = Setup().Connect();

        Assert.True(result.Ok, result.Message);
        var root = Read();
        Assert.Equal("opus", (string?)root["model"]);
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Equal("echo mine", (string?)stop[0]!["hooks"]![0]!["command"]);
        var ours = stop[1]!["hooks"]![0]!;
        Assert.Equal("http", (string?)ours["type"]);
        Assert.Equal(Url, (string?)ours["url"]);
        Assert.Equal(SetupState.Connected, Setup().Status().State);
    }

    [Fact]
    public void The_hooks_pass_the_editor_and_pid_as_headers_and_name_the_variables_they_may_read()
    {
        Assert.True(Setup().Connect().Ok);

        var ours = Read()["hooks"]!["Stop"]![0]!["hooks"]![0]!;
        Assert.Equal("$CLAUDE_CODE_ENTRYPOINT", (string?)ours["headers"]!["X-Entrypoint"]);
        Assert.Equal("$CLAUDE_PID", (string?)ours["headers"]!["X-Claude-Pid"]);
        var allowed = ours["allowedEnvVars"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("CLAUDE_CODE_ENTRYPOINT", allowed);
        Assert.Contains("CLAUDE_PID", allowed);
    }

    [Fact]
    public void Session_start_is_left_out_because_it_only_supports_command_hooks_and_ask_user_question_keeps_its_matcher()
    {
        Assert.True(Setup().Connect().Ok);

        var hooks = Read()["hooks"]!.AsObject();
        Assert.False(hooks.ContainsKey("SessionStart"));
        Assert.Equal("AskUserQuestion", (string?)hooks["PreToolUse"]![0]!["matcher"]);
        Assert.Equal(600, (int)hooks["PermissionRequest"]![0]!["hooks"]![0]!["timeout"]!);
        Assert.Equal(7, hooks.Count);
    }

    [Fact]
    public void The_old_settings_file_is_backed_up_before_anything_is_written()
    {
        Write("""{"model":"opus"}""");

        var result = Setup().Connect();

        var backup = Assert.Single(Backups());
        Assert.Equal("""{"model":"opus"}""", File.ReadAllText(backup));
        Assert.Contains("Backup", result.Message);
    }

    [Fact]
    public void Connecting_twice_changes_nothing_the_second_time()
    {
        Assert.True(Setup().Connect().Ok);
        var after = File.ReadAllText(Settings);

        var again = Setup().Connect();

        Assert.True(again.Ok);
        Assert.Contains("Already connected", again.Message);
        Assert.Equal(after, File.ReadAllText(Settings));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Hooks_that_run_the_old_helper_are_recognised_and_replaced_without_touching_the_users_own()
    {
        Write("""
            {"hooks":{"Stop":[
              {"hooks":[{"type":"command","command":"echo mine"}]},
              {"hooks":[{"type":"command","command":"\"C:/Users/me/AppData/Local/ClaudeCodeIsland/bin/island-hook.exe\" hook","timeout":10}]}],
             "SessionStart":[{"hooks":[{"type":"command","command":"\"C:/x/island-hook.exe\" hook"}]}]}}
            """);
        Assert.Equal(SetupState.ViaHelper, Setup().Status().State);

        Assert.True(Setup().Connect().Ok);

        var text = File.ReadAllText(Settings);
        Assert.DoesNotContain("island-hook.exe", text);
        Assert.Contains("echo mine", text);
        Assert.False(Read()["hooks"]!.AsObject().ContainsKey("SessionStart")); // the helper's SessionStart entry is gone too
        Assert.Equal(SetupState.Connected, Setup().Status().State);
        Assert.Single(Backups());
    }

    [Fact]
    public void A_hook_aimed_at_an_older_address_is_flagged_and_connecting_fixes_it()
    {
        Assert.True(Setup("http://127.0.0.1:47821/hook/claude-code/old-token").Connect().Ok);

        Assert.Equal(SetupState.ViaHelper, Setup().Status().State);
        Assert.True(Setup().Connect().Ok);

        Assert.Equal(SetupState.Connected, Setup().Status().State);
        var stop = Read()["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stop); // replaced, not added to
        Assert.Equal(Url, (string?)stop[0]!["hooks"]![0]!["url"]);
    }

    [Fact]
    public void Settings_that_are_not_valid_json_are_left_exactly_as_they_are()
    {
        Write("{ this is not json");

        var result = Setup().Connect();

        Assert.False(result.Ok);
        Assert.Contains("Nothing was changed", result.Message);
        Assert.Equal("{ this is not json", File.ReadAllText(Settings));
        Assert.Empty(Backups());
        Assert.Equal(SetupState.Unreadable, Setup().Status().State);
    }

    [Fact]
    public void While_the_apps_endpoint_is_not_running_connecting_explains_and_changes_nothing()
    {
        Write("{}");

        var result = Setup(url: null).Connect();

        Assert.False(result.Ok);
        Assert.Contains("endpoint", result.Message);
        Assert.Equal("{}", File.ReadAllText(Settings));
    }

    [Fact]
    public void A_missing_settings_file_is_created()
    {
        Assert.True(Setup().Connect().Ok);

        Assert.True(File.Exists(Settings));
        Assert.Equal(SetupState.Connected, Setup().Status().State);
    }

    [Fact]
    public void Disconnecting_removes_only_the_islands_hooks_whichever_kind_they_are()
    {
        Write("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo mine"},{"type":"command","command":"\"C:/x/island-hook.exe\" hook"}]}]}}""");
        Assert.True(Setup().Connect().Ok);

        var result = Setup().Disconnect();

        Assert.True(result.Ok, result.Message);
        var text = File.ReadAllText(Settings);
        Assert.Contains("echo mine", text);
        Assert.DoesNotContain("island-hook.exe", text);
        Assert.DoesNotContain("/hook/claude-code/", text);
        Assert.Equal(SetupState.NotConnected, Setup().Status().State);
    }

    [Fact]
    public void Disconnecting_when_nothing_is_connected_is_harmless()
    {
        Write("""{"model":"opus"}""");

        var result = Setup().Disconnect();

        Assert.True(result.Ok);
        Assert.Equal("""{"model":"opus"}""", File.ReadAllText(Settings));
        Assert.Empty(Backups());
    }

    [Fact]
    public void The_confirmation_names_the_file_it_edits_the_events_and_the_backup_and_says_nothing_is_installed()
    {
        var summary = Setup().ConnectSummary();

        Assert.Contains(Settings, summary);
        Assert.Contains("PermissionRequest", summary);
        Assert.Contains("backup", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing is installed or copied", summary);
        Assert.DoesNotContain("tok123", summary); // the secret is not shown on screen
    }
}
