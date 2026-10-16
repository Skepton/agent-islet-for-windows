namespace Island.ClaudeCode;

/// <summary>Where a Claude Code session's project really is, when its working folder is not it.</summary>
public static class ProjectFolder
{
    private const string WorktreeMarker = @"\.claude\worktrees\";

    /// <summary>
    /// A subagent that runs in its own worktree has <c>&lt;project&gt;\.claude\worktrees\&lt;name&gt;</c> as its working
    /// folder; this returns <c>&lt;project&gt;</c>. Null for any other folder (the caller then uses the folder itself).
    /// </summary>
    public static string? OfWorktree(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var normalized = cwd.Trim().Replace('/', '\\');
        var at = normalized.IndexOf(WorktreeMarker, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? normalized[..at] : null;
    }
}
