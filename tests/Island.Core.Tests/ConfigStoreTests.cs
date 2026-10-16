using Island.Core;

namespace Island.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-config-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Usage_tracking_is_off_by_default()
    {
        var config = new ConfigStore(ConfigPath).Load();

        Assert.False(config.UsageEnabled);
        Assert.Null(config.UsageConsentAt);
        Assert.Equal(UsageBarMode.ThinBar, config.UsageBar);
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        var consent = new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.FromHours(2));
        new ConfigStore(ConfigPath).Save(new IslandConfig { UsageEnabled = true, UsageConsentAt = consent, UsageBar = UsageBarMode.OnChange });

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.True(loaded.UsageEnabled);
        Assert.Equal(consent, loaded.UsageConsentAt);
        Assert.Equal(UsageBarMode.OnChange, loaded.UsageBar);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"v\":99,\"usageEnabled\":true}")] // a format this build does not know: do not guess, stay off
    [InlineData("[]")]
    public void A_damaged_or_unknown_file_means_defaults(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, content);

        var config = new ConfigStore(ConfigPath).Load();

        Assert.False(config.UsageEnabled);
    }

    [Fact]
    public void Unknown_and_wrong_typed_fields_are_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{\"v\":1,\"usageEnabled\":true,\"usageConsentAt\":5,\"usageBar\":\"sideways\",\"future\":{}}");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.True(config.UsageEnabled);
        Assert.Null(config.UsageConsentAt);
        Assert.Equal(UsageBarMode.ThinBar, config.UsageBar);
    }

    [Fact]
    public void The_file_holds_settings_only()
    {
        new ConfigStore(ConfigPath).Save(new IslandConfig { UsageEnabled = true });

        var text = File.ReadAllText(ConfigPath);

        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }
}
