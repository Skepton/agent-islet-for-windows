using System.Globalization;
using System.Text.Json;

namespace Island.Core;

/// <summary>Whether sessions from a project folder may reach the island.</summary>
public enum ProjectDecision { Unknown, Allowed, Ignored }

/// <summary>A project folder: <see cref="Key"/> compares paths case-insensitively; <see cref="Path"/> is for display.</summary>
public sealed record ProjectRef(string Key, string Path, string Name)
{
    /// <summary>Null for an empty path. Accepts either slash and a trailing separator.</summary>
    public static ProjectRef? From(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim().Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length == 0) return null;
        if (normalized.Length == 2 && normalized[1] == ':') normalized += "\\"; // "C:" means the drive root

        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length == 0 ? normalized : parts[^1];
        return new ProjectRef(normalized.ToLowerInvariant(), normalized, name);
    }
}

public sealed record ProjectEntry(ProjectRef Project, ProjectDecision Decision, DateTimeOffset DecidedAt);

/// <summary>
/// The user's per-project choices ("show this folder's sessions on the island?"). Thread-safe. Every change is saved
/// straight away when a store is given. Only folder paths and choices are kept: nothing from the sessions themselves.
/// </summary>
public sealed class ProjectPolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectEntry> _entries = new(StringComparer.Ordinal);
    private readonly ProjectPolicyStore? _store;

    public ProjectPolicy(ProjectPolicyStore? store = null)
    {
        _store = store;
        if (store is null) return;
        foreach (var entry in store.Load()) _entries[entry.Project.Key] = entry;
    }

    public ProjectDecision Check(ProjectRef project)
    {
        lock (_gate) return _entries.TryGetValue(project.Key, out var entry) ? entry.Decision : ProjectDecision.Unknown;
    }

    /// <summary>Every decided project, by name.</summary>
    public IReadOnlyList<ProjectEntry> List()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(e => e.Project.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Project.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Records a choice. <see cref="ProjectDecision.Unknown"/> forgets the project, so it is asked about again.</summary>
    public void Set(ProjectRef project, ProjectDecision decision, DateTimeOffset now)
    {
        List<ProjectEntry> snapshot;
        lock (_gate)
        {
            if (decision == ProjectDecision.Unknown) _entries.Remove(project.Key);
            else _entries[project.Key] = new ProjectEntry(project, decision, now);
            snapshot = _entries.Values.ToList();
        }

        try
        {
            _store?.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still applies until the app restarts; it will just be asked again then.
        }
    }
}

/// <summary>Persists project choices to projects.json next to the island's other state. Loading is forgiving; saving is atomic.</summary>
public sealed class ProjectPolicyStore
{
    private const int FormatVersion = 1;

    private readonly string _path;

    public ProjectPolicyStore(string? path = null)
    {
        _path = path ?? IslandPaths.ProjectsFile;
    }

    public IReadOnlyList<ProjectEntry> Load()
    {
        var entries = new List<ProjectEntry>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            var root = doc.RootElement;
            if (JsonUtil.Int32(root, "v", -1) != FormatVersion) return entries;
            if (JsonUtil.Prop(root, "projects") is not { ValueKind: JsonValueKind.Array } projects) return entries;

            foreach (var item in projects.EnumerateArray())
            {
                if (ProjectRef.From(JsonUtil.Str(item, "path")) is not { } project) continue;
                var decision = JsonUtil.Str(item, "decision") switch
                {
                    "allowed" => ProjectDecision.Allowed,
                    "ignored" => ProjectDecision.Ignored,
                    _ => ProjectDecision.Unknown,
                };
                if (decision == ProjectDecision.Unknown) continue;
                DateTimeOffset.TryParse(JsonUtil.Str(item, "decidedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var decidedAt);
                entries.Add(new ProjectEntry(project, decision, decidedAt));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<ProjectEntry>();
        }
        return entries;
    }

    public void Save(IEnumerable<ProjectEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", FormatVersion);
            w.WriteStartArray("projects");
            foreach (var entry in entries.OrderBy(e => e.Project.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("path", entry.Project.Path);
                w.WriteString("decision", entry.Decision == ProjectDecision.Allowed ? "allowed" : "ignored");
                w.WriteString("decidedAt", entry.DecidedAt.ToString("O", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(_path, buffer.ToArray());
    }
}
