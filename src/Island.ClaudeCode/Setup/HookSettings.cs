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

    /// <summary>
    /// The events the island listens to, with each hook's timeout in seconds. PermissionRequest waits for an answer
    /// (the forwarder gives up at 540 s, before this). The others return in well under a second; their timeout only
    /// bounds a hung forwarder. SessionEnd has a tiny budget in Claude Code. PreToolUse is deliberately not used.
    /// </summary>
    public static readonly IReadOnlyList<(string Event, int TimeoutSeconds)> Events =
    [
        ("SessionStart", 10),
        ("UserPromptSubmit", 10),
        ("PermissionRequest", 600),
        ("PostToolUse", 10),
        ("Notification", 10),
        ("Stop", 10),
        ("SessionEnd", 2),
    ];

    /// <summary>Claude Code runs hooks under Git Bash on Windows: forward slashes, quoted (paths may contain spaces).</summary>
    public static string CommandFor(string exePath) => $"\"{exePath.Replace('\\', '/')}\" hook";

    public static bool IsIslandCommand(string? command) =>
        command is not null
        && command.Contains(ExeName, StringComparison.OrdinalIgnoreCase)
        && command.TrimEnd().EndsWith(" hook", StringComparison.Ordinal);

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
        foreach (var (name, timeout) in Events)
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
            groups.Add((JsonNode)new JsonObject { ["hooks"] = new JsonArray(entry) });
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

    /// <summary>How many of our entries the settings contain.</summary>
    public static int CountOurs(string? settingsJson)
    {
        if (Parse(settingsJson)["hooks"] is not JsonObject hooks) return 0;
        return hooks
            .Select(e => e.Value)
            .OfType<JsonArray>()
            .SelectMany(groups => groups.OfType<JsonObject>())
            .Select(group => group["hooks"])
            .OfType<JsonArray>()
            .SelectMany(entries => entries.OfType<JsonObject>())
            .Count(entry => IsIslandCommand(CommandOf(entry)));
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
                var ours = entries.OfType<JsonObject>().Where(e => IsIslandCommand(CommandOf(e))).ToList();
                if (ours.Count == 0) continue;

                foreach (var entry in ours) entries.Remove(entry);
                if (entries.Count == 0) groups.Remove(group);
            }

            if (groups.Count == 0 && groupsBefore > 0) hooks.Remove(eventName);
        }

        // Drop "hooks" only when we emptied it; a file that already had "hooks": {} keeps it.
        if (hooks.Count == 0 && eventsBefore > 0) root.Remove("hooks");
    }

    private static string? CommandOf(JsonObject entry) =>
        entry["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;

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
