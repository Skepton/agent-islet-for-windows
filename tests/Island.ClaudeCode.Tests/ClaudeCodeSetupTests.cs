using System.Text.Json.Nodes;
using Island.ClaudeCode;

namespace Island.ClaudeCode.Tests;

/// <summary>Everything here runs against temp files: the real ~/.claude/settings.json is never touched.</summary>
public sealed class ClaudeCodeSetupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 21, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-setup-" + Guid.NewGuid().ToString("N"));

    private string Settings => Path.Combine(_dir, "claude", "settings.json");
    private string Source => Path.Combine(_dir, "app", HookSettings.ExeName);
    private string Target => Path.Combine(_dir, "data", "bin", HookSettings.ExeName);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ClaudeCodeSetup Setup() => new(Settings, Source, Target, () => Now);

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WithForwarder(string content = "forwarder v1") => Write(Source, content);

    [Fact]
    public void Before_connecting_the_status_says_so()
    {
        var status = Setup().Status();

        Assert.Equal(SetupState.NotConnected, status.State);
        Assert.Equal(0, status.Hooks);
    }

    [Fact]
    public void Connecting_copies_the_forwarder_and_registers_the_hooks_pointing_at_the_copy()
    {
        WithForwarder();
        Write(Settings, """{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo mine"}]}]}}""");

        var result = Setup().Connect();

        Assert.True(result.Ok, result.Message);
        Assert.Equal("forwarder v1", File.ReadAllText(Target));
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Equal("opus", (string?)root["model"]);
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count); // the user's own hook stays, ours is added
        var commands = stop.Select(g => (string?)g!["hooks"]![0]!["command"]).ToList();
        Assert.Contains(HookSettings.CommandFor(Target), commands);
        Assert.Contains("echo mine", commands);
        Assert.Equal(SetupState.Connected, Setup().Status().State);
    }

    [Fact]
    public void The_old_settings_file_is_backed_up_before_anything_is_written()
    {
        WithForwarder();
        const string original = """{"model":"opus"}""";
        Write(Settings, original);

        var result = Setup().Connect();

        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Settings)!, "settings.json.island-backup-*"));
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Contains("Backup", result.Message);
    }

    [Fact]
    public void Connecting_twice_changes_nothing_the_second_time()
    {
        WithForwarder();
        Assert.True(Setup().Connect().Ok);
        var after = File.ReadAllText(Settings);

        var again = Setup().Connect();

        Assert.True(again.Ok);
        Assert.Contains("Already connected", again.Message);
        Assert.Equal(after, File.ReadAllText(Settings));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Settings)!, "settings.json.island-backup-*")); // nothing was written, so no backup
    }

    [Fact]
    public void A_new_forwarder_replaces_the_old_copy_when_connecting_again()
    {
        WithForwarder("forwarder v1");
        Assert.True(Setup().Connect().Ok);
        WithForwarder("forwarder v2 is longer");

        Assert.True(Setup().Connect().Ok);

        Assert.Equal("forwarder v2 is longer", File.ReadAllText(Target));
    }

    [Fact]
    public void Settings_that_are_not_valid_json_are_left_exactly_as_they_are()
    {
        WithForwarder();
        Write(Settings, "{ this is not json");

        var result = Setup().Connect();

        Assert.False(result.Ok);
        Assert.Contains("Nothing was changed", result.Message);
        Assert.Equal("{ this is not json", File.ReadAllText(Settings));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Settings)!, "*.island-backup-*"));
        Assert.Equal(SetupState.Unreadable, Setup().Status().State);
    }

    [Fact]
    public void Without_a_forwarder_anywhere_connecting_explains_and_changes_nothing()
    {
        Write(Settings, "{}");

        var result = Setup().Connect();

        Assert.False(result.Ok);
        Assert.Contains(HookSettings.ExeName, result.Message);
        Assert.Equal("{}", File.ReadAllText(Settings));
    }

    [Fact]
    public void A_missing_settings_file_is_created()
    {
        WithForwarder();

        Assert.True(Setup().Connect().Ok);

        Assert.True(File.Exists(Settings));
        Assert.Equal(SetupState.Connected, Setup().Status().State);
    }

    [Fact]
    public void Disconnecting_removes_only_the_islands_hooks()
    {
        WithForwarder();
        Write(Settings, """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo mine"}]}]}}""");
        Assert.True(Setup().Connect().Ok);

        var result = Setup().Disconnect();

        Assert.True(result.Ok, result.Message);
        var text = File.ReadAllText(Settings);
        Assert.Contains("echo mine", text);
        Assert.DoesNotContain(HookSettings.ExeName, text);
        Assert.Equal(SetupState.NotConnected, Setup().Status().State);
        Assert.True(File.Exists(Target)); // the files stay; only the settings entries go
    }

    [Fact]
    public void Disconnecting_when_nothing_is_connected_is_harmless()
    {
        Write(Settings, """{"model":"opus"}""");

        var result = Setup().Disconnect();

        Assert.True(result.Ok);
        Assert.Equal("""{"model":"opus"}""", File.ReadAllText(Settings));
    }

    [Fact]
    public void The_confirmation_names_the_files_it_will_touch_and_the_backup()
    {
        var summary = Setup().ConnectSummary();

        Assert.Contains(Settings, summary);
        Assert.Contains(Path.GetDirectoryName(Target)!, summary);
        Assert.Contains("backup", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PermissionRequest", summary);
    }
}
