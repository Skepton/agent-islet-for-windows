using Island.ClaudeCode;

namespace Island.ClaudeCode.Tests;

public class HttpHookSettingsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:47821/hook/claude-code/abc", true)]
    [InlineData("http://localhost:1/hook/claude-code/abc", true)]
    [InlineData("http://127.0.0.1:47821/hook/other-agent/abc", false)]
    [InlineData("https://127.0.0.1:47821/hook/claude-code/abc", false)]
    [InlineData("http://example.com/hook/claude-code/abc", false)]
    [InlineData("http://127.0.0.1:8080/hooks/pre-tool-use", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_the_islands_own_local_address_counts_as_ours(string? url, bool expected)
    {
        Assert.Equal(expected, HookSettings.IsIslandUrl(url));
    }

    [Fact]
    public void Someone_elses_http_hook_on_localhost_is_never_touched()
    {
        const string settings = """{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"http","url":"http://localhost:8080/hooks/pre-tool-use"}]}]}}""";

        var installed = HookSettings.InstallHttp(settings, "http://127.0.0.1:47821/hook/claude-code/t");
        var removed = HookSettings.Uninstall(installed);

        Assert.Contains("localhost:8080/hooks/pre-tool-use", installed);
        Assert.True(HookSettings.SameJson(settings, removed));
    }

    [Fact]
    public void Install_then_uninstall_leaves_the_file_as_it_was()
    {
        const string settings = """{"model":"opus","permissions":{"allow":["Bash(ls)"]}}""";

        var roundTrip = HookSettings.Uninstall(HookSettings.InstallHttp(settings, "http://127.0.0.1:1/hook/claude-code/t"));

        Assert.True(HookSettings.SameJson(settings, roundTrip));
    }

    [Fact]
    public void The_count_covers_both_kinds_and_the_helper_count_only_the_old_one()
    {
        const string settings = """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"\"C:/x/island-hook.exe\" hook"},
                                        {"type":"http","url":"http://127.0.0.1:1/hook/claude-code/t"},
                                        {"type":"command","command":"echo mine"}]}]}}
            """;

        Assert.Equal(2, HookSettings.CountOurs(settings));
        Assert.Equal(1, HookSettings.CountHelperEntries(settings));
        Assert.Equal("http://127.0.0.1:1/hook/claude-code/t", HookSettings.HttpUrlOf(settings));
    }

    [Fact]
    public void A_hooks_section_of_the_wrong_shape_aborts_with_nothing_written()
    {
        Assert.Throws<FormatException>(() => HookSettings.InstallHttp("""{"hooks":[]}""", "http://127.0.0.1:1/hook/claude-code/t"));
        Assert.Throws<FormatException>(() => HookSettings.InstallHttp("""{"hooks":{"Stop":{}}}""", "http://127.0.0.1:1/hook/claude-code/t"));
    }
}
