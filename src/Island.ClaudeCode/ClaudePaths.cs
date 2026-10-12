namespace Island.ClaudeCode;

/// <summary>Where Claude Code keeps its files.</summary>
public static class ClaudePaths
{
    /// <summary>%CLAUDE_CONFIG_DIR% when set, otherwise %USERPROFILE%\.claude.</summary>
    public static string ConfigDir()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    /// <summary>The user-level settings file, which applies to every project.</summary>
    public static string UserSettings() => Path.Combine(ConfigDir(), "settings.json");
}
