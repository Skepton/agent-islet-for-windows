using System.Text;
using System.Text.Json.Nodes;

namespace Island.ClaudeCode.Tests;

/// <summary>The PLAN WP6 matrix, against temp files only. Never the real ~/.claude/settings.json.</summary>
public sealed class InstallerTests : IDisposable
{
    private const string Exe = @"C:\Users\Some One\AppData\Local\ClaudeCodeIsland\bin\island-hook.exe";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private const string OnlyModel = """
        {
          "model": "opus",
          "autoUpdatesChannel": "latest"
        }
        """;

    private const string ForeignHooks = """
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "notify-send done" } ] } ],
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "audit.sh" } ] } ],
            "PostToolUse": [ { "matcher": "Edit", "hooks": [ { "type": "command", "command": "fmt" }, { "type": "command", "command": "lint" } ] } ]
          },
          "permissions": { "allow": [ "Bash(npm test:*)" ] }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-install-" + Guid.NewGuid().ToString("N"));

    public InstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private string WriteSettings(string text, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(SettingsPath, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes);
        return SettingsPath;
    }

    private static IEnumerable<JsonObject> Entries(string json, string eventName) =>
        (JsonNode.Parse(json)!["hooks"]![eventName] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .SelectMany(g => (g["hooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>());

    public static IEnumerable<object[]> Shapes() => new[]
    {
        new object[] { "missing", null! },
        new object[] { "empty", "" },
        new object[] { "only model", OnlyModel },
        new object[] { "foreign hooks", ForeignHooks },
        new object[] { "crlf", ForeignHooks.Replace("\n", "\r\n") },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Install_then_uninstall_gives_back_the_same_json(string name, string? original)
    {
        _ = name;
        var installed = HookSettings.Install(original, Exe);
        var uninstalled = HookSettings.Uninstall(installed);

        Assert.Equal(8, HookSettings.CountOurs(installed));
        Assert.True(HookSettings.SameJson(original, uninstalled), uninstalled);
    }

    [Fact]
    public void Each_event_gets_one_entry_with_the_quoted_forward_slash_command_and_its_timeout()
    {
        var installed = HookSettings.Install(OnlyModel, Exe);

        foreach (var (eventName, timeout, _) in HookSettings.Events)
        {
            var entry = Assert.Single(Entries(installed, eventName));
            Assert.Equal("command", (string?)entry["type"]);
            Assert.Equal("\"C:/Users/Some One/AppData/Local/ClaudeCodeIsland/bin/island-hook.exe\" hook", (string?)entry["command"]);
            Assert.Equal(timeout, (int?)entry["timeout"]);
        }
        Assert.Equal("opus", (string?)JsonNode.Parse(installed)!["model"]);
    }

    [Fact]
    public void PreToolUse_is_only_registered_for_AskUserQuestion_and_other_events_have_no_matcher()
    {
        var hooks = JsonNode.Parse(HookSettings.Install(OnlyModel, Exe))!["hooks"]!.AsObject();

        Assert.Equal("AskUserQuestion", (string?)hooks["PreToolUse"]![0]!["matcher"]);
        Assert.All(hooks.Where(e => e.Key != "PreToolUse"), e => Assert.Null(e.Value![0]!["matcher"]));
    }

    [Fact]
    public void Installing_twice_is_the_same_as_once()
    {
        var once = HookSettings.Install(ForeignHooks, Exe);

        var twice = HookSettings.Install(once, Exe);

        Assert.True(HookSettings.SameJson(once, twice));
        Assert.Equal(8, HookSettings.CountOurs(twice));
    }

    [Fact]
    public void Reinstalling_from_a_new_location_replaces_the_old_command()
    {
        var old = HookSettings.Install(OnlyModel, @"D:\old\island-hook.exe");

        var moved = HookSettings.Install(old, Exe);

        Assert.Equal(8, HookSettings.CountOurs(moved));
        Assert.DoesNotContain("D:/old", moved);
    }

    [Fact]
    public void Foreign_hooks_on_the_same_events_are_kept()
    {
        var installed = HookSettings.Install(ForeignHooks, Exe);

        Assert.Contains(Entries(installed, "Stop"), e => (string?)e["command"] == "notify-send done");
        Assert.Contains(Entries(installed, "PostToolUse"), e => (string?)e["command"] == "fmt");
        Assert.Contains(Entries(installed, "PostToolUse"), e => (string?)e["command"] == "lint");
        Assert.Contains(Entries(installed, "PreToolUse"), e => (string?)e["command"] == "audit.sh");
    }

    [Fact]
    public void Uninstall_removes_our_entry_from_a_shared_group_but_keeps_the_rest()
    {
        var shared = """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"mine"},{"type":"command","command":"\"C:/x/island-hook.exe\" hook"}]}]}}
            """;

        var result = HookSettings.Uninstall(shared);

        Assert.Equal("mine", (string?)Assert.Single(Entries(result, "Stop"))["command"]);
    }

    [Fact]
    public void A_hooks_section_that_was_already_empty_is_kept()
    {
        Assert.True(HookSettings.SameJson("""{"hooks":{}}""", HookSettings.Uninstall("""{"hooks":{}}""")));
        Assert.True(HookSettings.SameJson("""{"hooks":{"Stop":[]}}""", HookSettings.Uninstall("""{"hooks":{"Stop":[]}}""")));
    }

    [Theory]
    [InlineData("""{"hooks":[]}""")]
    [InlineData("""{"hooks":{"Stop":{}}}""")]
    [InlineData("[1,2]")]
    [InlineData("{\"model\": \"opus\",}")]
    [InlineData("// comment\n{}")]
    public void Unexpected_shapes_and_invalid_json_are_refused(string text)
    {
        Assert.Throws<FormatException>(() => HookSettings.Install(text, Exe));
    }

    [Theory]
    [InlineData("island-hook.exe hook", true)]
    [InlineData("\"C:/a b/ISLAND-HOOK.EXE\" hook  ", true)]
    [InlineData("\"C:/a/island-hook.exe\" simulate x", false)]
    [InlineData("other.exe hook", false)]
    [InlineData(null, false)]
    public void Our_entries_are_recognised_by_command(string? command, bool ours)
    {
        Assert.Equal(ours, HookSettings.IsIslandCommand(command));
    }

    [Fact]
    public void File_install_backs_up_first_and_writes_without_a_bom()
    {
        var path = WriteSettings(OnlyModel, bom: true);

        var change = SettingsFile.Install(path, Exe, dryRun: false, Now);

        Assert.True(change.Changed);
        Assert.NotNull(change.BackupPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(OnlyModel)), File.ReadAllBytes(change.BackupPath!));
        var written = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, written[0]);
        Assert.Equal(8, HookSettings.CountOurs(Encoding.UTF8.GetString(written)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void File_round_trip_restores_the_content_and_keeps_crlf()
    {
        var original = ForeignHooks.Replace("\n", "\r\n");
        var path = WriteSettings(original);

        SettingsFile.Install(path, Exe, dryRun: false, Now);
        SettingsFile.Uninstall(path, dryRun: false, Now.AddMinutes(1));

        var after = File.ReadAllText(path);
        Assert.True(HookSettings.SameJson(original, after));
        Assert.Contains("\r\n", after);
        Assert.DoesNotContain("\r\r", after);
    }

    [Fact]
    public void Backups_made_in_the_same_second_never_overwrite_each_other()
    {
        var path = WriteSettings(OnlyModel);

        var install = SettingsFile.Install(path, Exe, dryRun: false, Now);
        var uninstall = SettingsFile.Uninstall(path, dryRun: false, Now);

        Assert.NotEqual(install.BackupPath, uninstall.BackupPath);
        Assert.Equal(OnlyModel, File.ReadAllText(install.BackupPath!)); // the original survives
        Assert.Equal(8, HookSettings.CountOurs(File.ReadAllText(uninstall.BackupPath!)));
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        var path = WriteSettings(OnlyModel);

        var change = SettingsFile.Install(path, Exe, dryRun: true, Now);

        Assert.True(change.Changed);
        Assert.Equal(OnlyModel, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Reinstalling_an_identical_setup_does_not_touch_the_file()
    {
        var path = WriteSettings(OnlyModel);
        SettingsFile.Install(path, Exe, dryRun: false, Now);
        var stamp = File.GetLastWriteTimeUtc(path);
        var files = Directory.GetFiles(_dir).Length;

        var again = SettingsFile.Install(path, Exe, dryRun: false, Now.AddMinutes(5));

        Assert.False(again.Changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(files, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Invalid_json_on_disk_is_left_alone()
    {
        var path = WriteSettings("{ not json");

        Assert.Throws<FormatException>(() => SettingsFile.Install(path, Exe, dryRun: false, Now));

        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Missing_file_is_created_and_uninstall_with_no_file_does_nothing()
    {
        var path = Path.Combine(_dir, "new", "settings.json");

        Assert.False(SettingsFile.Uninstall(path, dryRun: false, Now).Changed);
        Assert.False(File.Exists(path));

        var change = SettingsFile.Install(path, Exe, dryRun: false, Now);

        Assert.True(change.Changed);
        Assert.Null(change.BackupPath);
        Assert.Equal(8, HookSettings.CountOurs(File.ReadAllText(path)));
    }
}
