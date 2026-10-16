using System.Globalization;
using System.Text.Json;

namespace Island.Core;

/// <summary>How the usage numbers show up when no card needs the user.</summary>
public enum UsageBarMode
{
    /// <summary>A thin bar sits at the top of the screen; hover it to read the numbers. They also appear when they change.</summary>
    ThinBar,

    /// <summary>Nothing is drawn until the numbers change; then they show for a few seconds.</summary>
    OnChange,
}

/// <summary>
/// The user's settings. Usage tracking is off until the user turns it on; turning it on records when they agreed, because
/// it makes the island read the agent's sign-in token and call the provider's usage endpoint.
/// </summary>
public sealed record IslandConfig
{
    public bool UsageEnabled { get; init; }
    public DateTimeOffset? UsageConsentAt { get; init; }
    public UsageBarMode UsageBar { get; init; } = UsageBarMode.ThinBar;
}

/// <summary>Reads and writes <c>config.json</c>. Loading is forgiving (missing or damaged file means defaults); saving is atomic.</summary>
public sealed class ConfigStore
{
    private const int FormatVersion = 1;

    private readonly string _path;

    public ConfigStore(string? path = null)
    {
        _path = path ?? IslandPaths.ConfigFile;
    }

    public IslandConfig Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            var root = doc.RootElement;
            if (JsonUtil.Int32(root, "v", -1) != FormatVersion) return new IslandConfig();

            DateTimeOffset? consentAt = DateTimeOffset.TryParse(
                JsonUtil.Str(root, "usageConsentAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
            return new IslandConfig
            {
                UsageEnabled = JsonUtil.Bool(root, "usageEnabled"),
                UsageConsentAt = consentAt,
                UsageBar = JsonUtil.Str(root, "usageBar") == "onChange" ? UsageBarMode.OnChange : UsageBarMode.ThinBar,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new IslandConfig();
        }
    }

    public void Save(IslandConfig config)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", FormatVersion);
            w.WriteBoolean("usageEnabled", config.UsageEnabled);
            if (config.UsageConsentAt is { } at) w.WriteString("usageConsentAt", at.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("usageBar", config.UsageBar == UsageBarMode.OnChange ? "onChange" : "thinBar");
            w.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(_path, buffer.ToArray());
    }
}
