namespace Island.ClaudeCode;

public enum SetupState { NotConnected, Connected, Unreadable }

/// <param name="Hooks">How many of the island's hook entries the settings file holds.</param>
/// <param name="Text">One line for the UI.</param>
public sealed record SetupStatus(SetupState State, int Hooks, string Text);

public sealed record SetupResult(bool Ok, string Message);

/// <summary>
/// Connects Claude Code to the island from inside the app: puts the small forwarder (<c>island-hook.exe</c>) in a
/// stable folder and registers it in Claude Code's user settings, or takes the entries out again. Always backs the
/// settings file up first, touches only the island's own entries and never changes anything when the file is not valid
/// JSON. The app asks the user before calling <see cref="Connect"/> or <see cref="Disconnect"/>.
/// </summary>
public sealed class ClaudeCodeSetup
{
    private readonly string _settingsPath;
    private readonly string _forwarderSource;
    private readonly string _forwarderTarget;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="settingsPath">Claude Code's user settings (<c>~/.claude/settings.json</c>); a temp file in tests.</param>
    /// <param name="forwarderSource">Where the forwarder ships: next to the app.</param>
    /// <param name="forwarderTarget">Where the hooks will point: a folder that survives moving or updating the app.</param>
    public ClaudeCodeSetup(string settingsPath, string forwarderSource, string forwarderTarget, Func<DateTimeOffset>? clock = null)
    {
        _settingsPath = settingsPath;
        _forwarderSource = forwarderSource;
        _forwarderTarget = forwarderTarget;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string SettingsPath => _settingsPath;

    public string ForwarderTarget => _forwarderTarget;

    public SetupStatus Status()
    {
        try
        {
            var count = HookSettings.CountOurs(SettingsFile.Read(_settingsPath));
            return count == 0
                ? new SetupStatus(SetupState.NotConnected, 0, "Claude Code is not connected yet.")
                : new SetupStatus(SetupState.Connected, count, $"Claude Code is connected ({count} hooks in {_settingsPath}).");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return new SetupStatus(SetupState.Unreadable, 0, $"Could not read {_settingsPath}.");
        }
    }

    /// <summary>What connecting will do, in plain words, for the confirmation the user sees first.</summary>
    public string ConnectSummary() =>
        "Agent Islet will:\n\n"
        + $"1. Copy its small helper, {HookSettings.ExeName}, to\n    {Path.GetDirectoryName(_forwarderTarget)}\n\n"
        + $"2. Add {HookSettings.Events.Count} hooks to Claude Code's settings file\n    {_settingsPath}\n"
        + $"    ({string.Join(", ", HookSettings.Events.Select(e => e.Event).Distinct())}).\n\n"
        + "3. Save a backup of that file next to it first (settings.json.island-backup-...).\n\n"
        + "Nothing else in the file is touched. If the island is not running, Claude Code behaves exactly as before. "
        + "You can remove the hooks again at any time with Disconnect.\n\nConnect Claude Code?";

    public string DisconnectSummary() =>
        $"This removes the island's hooks from\n{_settingsPath}\n(a backup is saved next to it first). Other hooks are left alone, "
        + "and Claude Code carries on as before.\n\nDisconnect Claude Code?";

    public SetupResult Connect()
    {
        if (!File.Exists(_forwarderSource) && !File.Exists(_forwarderTarget))
            return new SetupResult(false, $"{HookSettings.ExeName} was not found next to the app ({Path.GetDirectoryName(_forwarderSource)}). Keep the two files together.");

        try
        {
            var copyError = CopyForwarder();
            if (copyError is not null) return new SetupResult(false, copyError);

            var change = SettingsFile.Install(_settingsPath, _forwarderTarget, dryRun: false, _clock());
            var count = HookSettings.CountOurs(change.NewText);
            var text = change.Changed
                ? $"Connected: {count} hooks added to {_settingsPath}."
                : $"Already connected ({count} hooks). Nothing changed.";
            if (change.BackupPath is not null) text += $" Backup of the old file: {change.BackupPath}";
            return new SetupResult(true, text + " Running Claude Code sessions pick it up on their next event.");
        }
        catch (FormatException ex)
        {
            return new SetupResult(false, $"{_settingsPath}: {ex.Message} Nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupResult(false, $"Could not update {_settingsPath}: {ex.Message}");
        }
    }

    public SetupResult Disconnect()
    {
        try
        {
            var change = SettingsFile.Uninstall(_settingsPath, dryRun: false, _clock());
            if (!change.Changed) return new SetupResult(true, "There were no island hooks to remove.");
            return new SetupResult(true, "Disconnected: the island's hooks were removed."
                + (change.BackupPath is not null ? $" Backup of the old file: {change.BackupPath}" : ""));
        }
        catch (FormatException ex)
        {
            return new SetupResult(false, $"{_settingsPath}: {ex.Message} Nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupResult(false, $"Could not update {_settingsPath}: {ex.Message}");
        }
    }

    /// <returns>An error message, or null when the forwarder is in place.</returns>
    private string? CopyForwarder()
    {
        if (!File.Exists(_forwarderSource)) return null; // already installed earlier: reuse it
        if (SamePath(_forwarderSource, _forwarderTarget)) return null;

        var source = new FileInfo(_forwarderSource);
        if (File.Exists(_forwarderTarget))
        {
            var current = new FileInfo(_forwarderTarget);
            if (current.Length == source.Length && current.LastWriteTimeUtc == source.LastWriteTimeUtc) return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_forwarderTarget)!);
            var staging = _forwarderTarget + ".new";
            File.Copy(_forwarderSource, staging, overwrite: true);
            File.Move(staging, _forwarderTarget, overwrite: true);
            return null;
        }
        catch (IOException ex)
        {
            // Usually: a Claude Code request is waiting on the installed forwarder, so the file is in use.
            return $"Could not copy {HookSettings.ExeName} to {_forwarderTarget} ({ex.Message}). If a Claude Code permission request is waiting, answer it and try again.";
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
