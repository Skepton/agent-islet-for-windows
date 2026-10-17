using System.Globalization;
using System.Text;
using Island.Core;

namespace Island.ClaudeCode;

/// <param name="Changed">False when the file already had exactly this content (nothing was written).</param>
/// <param name="BackupPath">Copy of the previous file, when one was made.</param>
/// <param name="NewText">The content written (or, for a dry run, that would be written).</param>
public sealed record SettingsChange(bool Changed, string? BackupPath, string NewText);

/// <summary>
/// Applies a <see cref="HookSettings"/> transform to a settings file on disk. Invalid JSON aborts with nothing changed.
/// Before any write the old file is copied to <c>settings.json.island-backup-&lt;timestamp&gt;</c>; the new file is
/// written atomically, as UTF-8 without a BOM (a BOM in the original is dropped).
/// </summary>
public static class SettingsFile
{
    public static SettingsChange Install(string path, string exePath, bool dryRun, DateTimeOffset now) =>
        Apply(path, text => HookSettings.Install(text, exePath), dryRun, now);

    /// <summary>Registers the island's http hooks pointing at <paramref name="url"/> (replacing older entries of either kind).</summary>
    public static SettingsChange InstallHttp(string path, string url, bool dryRun, DateTimeOffset now) =>
        Apply(path, text => HookSettings.InstallHttp(text, url), dryRun, now);

    public static SettingsChange Uninstall(string path, bool dryRun, DateTimeOffset now) =>
        Apply(path, HookSettings.Uninstall, dryRun, now);

    /// <summary>The file's text without a BOM, or null when it does not exist.</summary>
    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        return Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart('﻿');
    }

    private static SettingsChange Apply(string path, Func<string?, string> transform, bool dryRun, DateTimeOffset now)
    {
        var original = Read(path);
        var updated = transform(original); // throws FormatException on invalid JSON, before anything is touched

        var exists = original is not null;
        if (exists && HookSettings.SameJson(original, updated)) return new SettingsChange(false, null, original!);
        if (!exists && HookSettings.SameJson("{}", updated)) return new SettingsChange(false, null, updated); // uninstall with no file
        if (dryRun) return new SettingsChange(true, null, updated);

        string? backup = null;
        if (exists)
        {
            backup = FreeBackupName(path, now);
            File.Copy(path, backup, overwrite: false); // never replace an earlier backup: it may be the only copy of the original
        }

        AtomicFile.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(updated));
        return new SettingsChange(true, backup, updated);
    }

    /// <summary><c>settings.json.island-backup-20261012165908</c>, then <c>…-2</c>, <c>…-3</c> when that name is taken.</summary>
    private static string FreeBackupName(string path, DateTimeOffset now)
    {
        var stem = $"{path}.island-backup-{now.ToLocalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        var candidate = stem;
        for (var n = 2; File.Exists(candidate); n++) candidate = $"{stem}-{n}";
        return candidate;
    }
}
