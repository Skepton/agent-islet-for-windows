using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Island.Core;

/// <summary>Where the island keeps its own files. ISLAND_DATA_DIR overrides it (tests, smoke scripts).</summary>
public static class IslandPaths
{
    public const string DataDirEnvVar = "ISLAND_DATA_DIR";

    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCodeIsland")
                : overridden;
        }
    }

    public static string StateFile => Path.Combine(DataDir, "state.json");

    /// <summary>Which project folders the user chose to show or ignore.</summary>
    public static string ProjectsFile => Path.Combine(DataDir, "projects.json");
}

/// <summary>
/// Persists milestone progress so a restart never re-announces thresholds. Contains only percentages and reset
/// times. Loading is forgiving (a missing or damaged file means "nothing announced yet"); saving is atomic.
/// </summary>
public sealed class MilestoneStateStore
{
    private const int FormatVersion = 1;

    private readonly string _path;

    public MilestoneStateStore(string? path = null)
    {
        _path = path ?? IslandPaths.StateFile;
    }

    public MilestoneState Load()
    {
        var state = new MilestoneState();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            var root = doc.RootElement;
            if (JsonUtil.Int32(root, "v", -1) != FormatVersion) return state;
            if (JsonUtil.Prop(root, "windows") is not { ValueKind: JsonValueKind.Object } windows) return state;

            foreach (var entry in windows.EnumerateObject())
            {
                var notified = JsonUtil.Int32(entry.Value, "notified", -1);
                var lastPercent = JsonUtil.Double(entry.Value, "lastPercent", double.NaN);
                var resetsText = JsonUtil.Str(entry.Value, "resetsAt");
                if (notified < 0 || double.IsNaN(lastPercent) || resetsText is null) continue;
                if (!DateTimeOffset.TryParse(resetsText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var resetsAt)) continue;

                state.Windows[entry.Name] = new WindowProgress(notified, lastPercent, resetsAt);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MilestoneState();
        }
        return state;
    }

    public void Save(MilestoneState state)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", FormatVersion);
            w.WriteStartObject("windows");
            foreach (var (key, progress) in state.Windows.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(key);
                w.WriteNumber("notified", progress.NotifiedThreshold);
                w.WriteNumber("lastPercent", progress.LastPercent);
                w.WriteString("resetsAt", progress.ResetsAt.ToString("O", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(_path, buffer.ToArray());
    }
}
