using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Island.ClaudeCode;

/// <summary>
/// Adds and removes the island's hook entries in a Claude Code settings file, as pure text-in, text-out transforms.
/// Everything else in the file is kept. Our entries are recognised by their command (<c>"…/island-hook.exe" hook</c>),
/// so other hooks on the same events are never touched.
/// </summary>
public static class HookSettings
{
    public const string ExeName = "island-hook.exe";

    /// <summary>One hook entry: event, timeout in seconds, and a tool matcher (null = every tool).</summary>
    public sealed record HookEvent(string Event, int TimeoutSeconds, string? Matcher = null);

    /// <summary>
    /// The events the island listens to. PermissionRequest waits for an answer (the forwarder gives up at 540 s,
    /// before this). The others return in well under a second; their timeout only bounds a hung forwarder.
    /// SessionEnd has a tiny budget in Claude Code. PreToolUse runs before every tool call, so it is registered for
    /// AskUserQuestion only (Claude's questions fire no other event; see docs/findings.md).
    /// </summary>
    public static readonly IReadOnlyList<HookEvent> Events =
    [
        new("SessionStart", 10),
        new("UserPromptSubmit", 10),
        new("PermissionRequest", 600),
        new("PreToolUse", 10, "AskUserQuestion"),
        new("PostToolUse", 10),
        new("Notification", 10),
        new("Stop", 10),
        new("SessionEnd", 2),
    ];

    /// <summary>
    /// The events for the <c>http</c> hook type: the same list without SessionStart, which Claude Code only lets
    /// <c>command</c> and <c>mcp_tool</c> hooks handle. (The island makes a session on its first event anyway.)
    /// </summary>
    public static IReadOnlyList<HookEvent> HttpEvents { get; } = Events.Where(e => e.Event != "SessionStart").ToList();

    /// <summary>Environment values the island wants, passed as headers (an http hook cannot read the environment itself).</summary>
    private static readonly (string Header, string Variable)[] EnvHeaders =
    [
        ("X-Entrypoint", "CLAUDE_CODE_ENTRYPOINT"),
        ("X-Claude-Pid", "CLAUDE_PID"),
    ];

    /// <summary>Claude Code runs hooks under Git Bash on Windows: forward slashes, quoted (paths may contain spaces).</summary>
    public static string CommandFor(string exePath) => $"\"{exePath.Replace('\\', '/')}\" hook";

    public static bool IsIslandCommand(string? command) =>
        command is not null
        && command.Contains(ExeName, StringComparison.OrdinalIgnoreCase)
        && command.TrimEnd().EndsWith(" hook", StringComparison.Ordinal);

    /// <summary>A hook URL that points at the island's own local endpoint (any port or token).</summary>
    public static bool IsIslandUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.Host is "127.0.0.1" or "localhost"
        && uri.AbsolutePath.StartsWith("/hook/claude-code/", StringComparison.Ordinal);

    /// <summary>One of our hook entries, in either form: the old helper command or the http link.</summary>
    private static bool IsIslandEntry(JsonObject entry) =>
        IsIslandCommand(CommandOf(entry)) || (StringOf(entry, "type") == "http" && IsIslandUrl(StringOf(entry, "url")));

    /// <summary>Adds our <c>http</c> entries pointing at <paramref name="url"/>, replacing any earlier ones of either kind.</summary>
    /// <exception cref="FormatException">The content is not a JSON object, or its hooks section has an unexpected shape.</exception>
    public static string InstallHttp(string? settingsJson, string url)
    {
        var root = Parse(settingsJson);
        RemoveOurs(root);

        var hooks = root["hooks"] switch
        {
            null => new JsonObject(),
            JsonObject existing => existing,
            _ => throw new FormatException("\"hooks\" is not an object."),
        };
        if (root["hooks"] is null) root["hooks"] = hooks;

        foreach (var (name, timeout, matcher) in HttpEvents)
        {
            var groups = hooks[name] switch
            {
                null => new JsonArray(),
                JsonArray existing => existing,
                _ => throw new FormatException($"\"hooks.{name}\" is not an array."),
            };
            if (hooks[name] is null) hooks[name] = groups;

            var headers = new JsonObject();
            var allowed = new JsonArray();
            foreach (var (header, variable) in EnvHeaders)
            {
                headers[header] = "$" + variable;
                allowed.Add((JsonNode)JsonValue.Create(variable)!);
            }

            // JsonNode-typed construction: the generic Add<T> overloads are not trim-safe.
            var entry = new JsonObject
            {
                ["type"] = "http",
                ["url"] = url,
                ["timeout"] = timeout,
                ["headers"] = headers,
                ["allowedEnvVars"] = allowed,
            };
            var group = new JsonObject();
            if (matcher is not null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray(entry);
            groups.Add((JsonNode)group);
        }

        return Write(root, settingsJson);
    }

    /// <summary>Adds our entries (replacing any earlier ones, so it is safe to run again).</summary>
    /// <param name="settingsJson">Current file content; null or blank for a missing or empty file.</param>
    /// <exception cref="FormatException">The content is not a JSON object, or its hooks section has an unexpected shape.</exception>
    public static string Install(string? settingsJson, string exePath)
    {
        var root = Parse(settingsJson);
        RemoveOurs(root);

        var hooks = root["hooks"] switch
        {
            null => new JsonObject(),
            JsonObject existing => existing,
            _ => throw new FormatException("\"hooks\" is not an object."),
        };
        if (root["hooks"] is null) root["hooks"] = hooks;

        var command = CommandFor(exePath);
        foreach (var (name, timeout, matcher) in Events)
        {
            var groups = hooks[name] switch
            {
                null => new JsonArray(),
                JsonArray existing => existing,
                _ => throw new FormatException($"\"hooks.{name}\" is not an array."),
            };
            if (hooks[name] is null) hooks[name] = groups;

            // JsonNode-typed construction: the generic Add<T> overloads are not trim-safe.
            var entry = new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout };
            var group = new JsonObject();
            if (matcher is not null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray(entry);
            groups.Add((JsonNode)group);
        }

        return Write(root, settingsJson);
    }

    /// <summary>Removes only our entries. Containers we emptied are removed too; everything else is left as it was.</summary>
    /// <exception cref="FormatException">The content is not a JSON object.</exception>
    public static string Uninstall(string? settingsJson)
    {
        var root = Parse(settingsJson);
        RemoveOurs(root);
        return Write(root, settingsJson);
    }

    /// <summary>True when both texts hold the same JSON (formatting aside).</summary>
    public static bool SameJson(string? a, string? b) => JsonNode.DeepEquals(Parse(a), Parse(b));

    /// <summary>How many of our entries the settings contain, of either kind.</summary>
    public static int CountOurs(string? settingsJson) => OurEntries(settingsJson).Count();

    /// <summary>How many of our entries still use the old helper program (<c>island-hook.exe hook</c>).</summary>
    public static int CountHelperEntries(string? settingsJson) => OurEntries(settingsJson).Count(e => IsIslandCommand(CommandOf(e)));

    /// <summary>The URL our http entries call, or null when there are none.</summary>
    public static string? HttpUrlOf(string? settingsJson) =>
        OurEntries(settingsJson).Where(e => StringOf(e, "type") == "http").Select(e => StringOf(e, "url")).FirstOrDefault();

    private static IEnumerable<JsonObject> OurEntries(string? settingsJson)
    {
        if (Parse(settingsJson)["hooks"] is not JsonObject hooks) return Enumerable.Empty<JsonObject>();
        return hooks
            .Select(e => e.Value)
            .OfType<JsonArray>()
            .SelectMany(groups => groups.OfType<JsonObject>())
            .Select(group => group["hooks"])
            .OfType<JsonArray>()
            .SelectMany(entries => entries.OfType<JsonObject>())
            .Where(IsIslandEntry)
            .ToList();
    }

    private static JsonObject Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text.TrimStart('﻿'));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new FormatException("The settings file is not valid JSON: " + ex.Message, ex);
        }
        return node as JsonObject ?? throw new FormatException("The settings file is not a JSON object.");
    }

    private static void RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        var eventsBefore = hooks.Count;

        foreach (var (eventName, value) in hooks.ToList())
        {
            if (value is not JsonArray groups) continue;
            var groupsBefore = groups.Count;

            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray entries) continue;
                var ours = entries.OfType<JsonObject>().Where(IsIslandEntry).ToList();
                if (ours.Count == 0) continue;

                foreach (var entry in ours) entries.Remove(entry);
                if (entries.Count == 0) groups.Remove(group);
            }

            if (groups.Count == 0 && groupsBefore > 0) hooks.Remove(eventName);
        }

        // Drop "hooks" only when we emptied it; a file that already had "hooks": {} keeps it.
        if (hooks.Count == 0 && eventsBefore > 0) root.Remove("hooks");
    }

    private static string? CommandOf(JsonObject entry) => StringOf(entry, "command");

    private static string? StringOf(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Two-space indented JSON, keeping the original's line endings (LF for a new file) and a final newline.</summary>
    private static string Write(JsonObject root, string? original)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-ASCII paths readable
               }))
        {
            root.WriteTo(writer);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n");
        var newline = original is not null && original.Contains("\r\n") ? "\r\n" : "\n";
        return text.Replace("\n", newline) + newline;
    }
}
