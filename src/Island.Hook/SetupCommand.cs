using Island.ClaudeCode;

namespace Island.Hook;

/// <summary>
/// <c>island-hook install|uninstall [--settings &lt;path&gt;] [--dry-run]</c>: adds or removes the island's hooks in
/// Claude Code's settings (user level by default, so every project reaches the island; the island then asks per project).
/// Unlike <c>hook</c>, these are run by a person, so they print what they did and return a non-zero code on failure.
/// </summary>
internal static class SetupCommand
{
    public static int Run(string[] args)
    {
        var install = args[0] == "install";
        string? settings = null;
        var dryRun = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings" when i + 1 < args.Length:
                    settings = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    Console.Error.WriteLine("Usage: island-hook install|uninstall [--settings <path>] [--dry-run]");
                    return 2;
            }
        }

        var path = Path.GetFullPath(settings ?? ClaudePaths.UserSettings());
        try
        {
            var change = install
                ? SettingsFile.Install(path, ExePath(), dryRun, DateTimeOffset.Now)
                : SettingsFile.Uninstall(path, dryRun, DateTimeOffset.Now);
            Report(install, path, change, dryRun);
            return 0;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"{path}: {ex.Message}");
            Console.Error.WriteLine("Nothing was changed.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not update {path}: {ex.Message}");
            return 1;
        }
    }

    private static string ExePath()
    {
        var exe = Environment.ProcessPath ?? throw new IOException("Cannot tell where island-hook.exe is.");
        if (!string.Equals(Path.GetFileName(exe), HookSettings.ExeName, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Run install from {HookSettings.ExeName} itself (this is {Path.GetFileName(exe)}).");
        return exe;
    }

    private static void Report(bool install, string path, SettingsChange change, bool dryRun)
    {
        var count = HookSettings.CountOurs(change.NewText);
        if (!change.Changed)
        {
            Console.WriteLine(install
                ? $"Already installed in {path} ({count} hooks). Nothing changed."
                : $"No island hooks in {path}. Nothing changed.");
            return;
        }

        if (dryRun)
        {
            Console.WriteLine($"Dry run: {path} would become:");
            Console.WriteLine(change.NewText);
            return;
        }

        Console.WriteLine(install
            ? $"Installed {count} hooks in {path}, running {ExePath()}."
            : $"Removed the island's hooks from {path}.");
        if (change.BackupPath is not null) Console.WriteLine($"Backup of the previous file: {change.BackupPath}");
        if (install) Console.WriteLine("Running Claude Code sessions pick this up on their next hook event. Start the island app to see them.");
    }
}
