namespace Island.ClaudeCode;

/// <param name="NotConnected">No island hooks in Claude Code's settings.</param>
/// <param name="Connected">Hooks that call the app's built-in local endpoint (the current way).</param>
/// <param name="ViaHelper">Hooks that run the old helper program (<c>island-hook.exe</c>); still works, but Connect replaces them.</param>
/// <param name="Unreadable">The settings file could not be read.</param>
public enum SetupState { NotConnected, Connected, ViaHelper, Unreadable }

/// <param name="Hooks">How many of the island's hook entries the settings file holds.</param>
/// <param name="Text">One line for the UI.</param>
public sealed record SetupStatus(SetupState State, int Hooks, string Text);

public sealed record SetupResult(bool Ok, string Message);

/// <summary>
/// Connects Claude Code to the island from inside the app by registering hooks that call the app's local endpoint in
/// Claude Code's user settings, or by taking them out again. Nothing is copied or installed: the hooks are just a URL.
/// Always backs the settings file up first, touches only the island's own entries and never changes anything when the
/// file is not valid JSON. The app asks the user before calling <see cref="Connect"/> or <see cref="Disconnect"/>.
/// </summary>
public sealed class ClaudeCodeSetup
{
    private readonly string _settingsPath;
    private readonly Func<string?> _hookUrl;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="settingsPath">Claude Code's user settings (<c>~/.claude/settings.json</c>); a temp file in tests.</param>
    /// <param name="hookUrl">The URL the hooks should call, or null while the app's endpoint is not running.</param>
    public ClaudeCodeSetup(string settingsPath, Func<string?> hookUrl, Func<DateTimeOffset>? clock = null)
    {
        _settingsPath = settingsPath;
        _hookUrl = hookUrl;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string SettingsPath => _settingsPath;

    public SetupStatus Status()
    {
        try
        {
            var text = SettingsFile.Read(_settingsPath);
            var count = HookSettings.CountOurs(text);
            if (count == 0) return new SetupStatus(SetupState.NotConnected, 0, "Claude Code is not connected yet.");
            if (HookSettings.CountHelperEntries(text) > 0)
                return new SetupStatus(SetupState.ViaHelper, count,
                    "Claude Code is connected through the older helper program. \"Switch to the built-in link\" replaces it; nothing else changes.");

            var url = HookSettings.HttpUrlOf(text);
            var current = _hookUrl();
            return current is not null && url is not null && !string.Equals(url, current, StringComparison.Ordinal)
                ? new SetupStatus(SetupState.ViaHelper, count, "Claude Code is connected, but to an older address of this app. \"Switch to the built-in link\" updates it.")
                : new SetupStatus(SetupState.Connected, count, $"Claude Code is connected ({count} hooks in {_settingsPath}).");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return new SetupStatus(SetupState.Unreadable, 0, $"Could not read {_settingsPath}.");
        }
    }

    /// <summary>What connecting will do, in plain words, for the confirmation the user sees first.</summary>
    public string ConnectSummary() =>
        "Agent Islet will add "
        + $"{HookSettings.HttpEvents.Count} hooks to Claude Code's settings file\n    {_settingsPath}\n"
        + $"({string.Join(", ", HookSettings.HttpEvents.Select(e => e.Event).Distinct())}).\n\n"
        + "Each hook only sends Claude Code's events to this app, on your own computer (a local address that needs a secret "
        + "key). Nothing is installed or copied, and nothing leaves your machine.\n\n"
        + "A backup of the settings file is saved next to it first (settings.json.island-backup-...), and nothing else in "
        + "it is touched. If this app is not running, Claude Code behaves exactly as before. You can remove the hooks again "
        + "at any time with Disconnect.\n\nConnect Claude Code?";

    public string DisconnectSummary() =>
        $"This removes the island's hooks from\n{_settingsPath}\n(a backup is saved next to it first). Other hooks are left alone, "
        + "and Claude Code carries on as before.\n\nDisconnect Claude Code?";

    public SetupResult Connect()
    {
        var url = _hookUrl();
        if (url is null)
            return new SetupResult(false, "The app's local endpoint is not running (its port may be in use by another program), so there is nothing for Claude Code to call. Nothing was changed.");

        try
        {
            var change = SettingsFile.InstallHttp(_settingsPath, url, dryRun: false, _clock());
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
}
