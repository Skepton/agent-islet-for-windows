using Island.Core;

namespace Island.Core.Tests;

public class FixtureTests
{
    public static IEnumerable<object[]> HookFixtures() =>
        Directory.GetFiles(Payloads.Fixture("hooks")).Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(HookFixtures))]
    public void Every_captured_hook_fixture_parses(string fileName)
    {
        var payload = Payloads.Load(fileName);

        Assert.False(string.IsNullOrEmpty(payload.EventName));
        Assert.False(string.IsNullOrEmpty(payload.SessionId));
    }

    [Fact]
    public void Permission_request_fixture_exposes_tool_and_input()
    {
        var payload = Payloads.Load("PermissionRequest.json");

        Assert.Equal("PermissionRequest", payload.EventName);
        Assert.Equal("Bash", payload.ToolName);
        Assert.Contains("command", payload.ToolInputJson);
    }

    [Fact]
    public void Permission_prompt_notification_fixture_carries_its_type()
    {
        var payload = Payloads.Load("Notification.permission_prompt.json");

        Assert.Equal("permission_prompt", payload.NotificationType);
        Assert.Contains("permission", payload.Message ?? "");
    }

    [Fact]
    public void Rule_suggestion_fixture_is_labelled_as_a_remembered_rule()
    {
        var payload = Payloads.Load("PermissionRequest.with-suggestions.json");

        var choice = Assert.Single(SuggestionChoice.FromRaw(payload.PermissionSuggestionsJson));
        Assert.Equal("addRules", choice.Type);
        Assert.Equal("Allow + remember Bash(node -e ' *)", choice.Label);
    }

    [Fact]
    public void Directory_suggestion_fixture_is_labelled_as_add_folder()
    {
        var payload = Payloads.Load("PermissionRequest.with-adddirectories.json");

        var choice = Assert.Single(SuggestionChoice.FromRaw(payload.PermissionSuggestionsJson));
        Assert.Equal("addDirectories", choice.Type);
        Assert.StartsWith("Allow + add folder ", choice.Label);
        Assert.Contains("my-project", choice.Label);
    }
}
