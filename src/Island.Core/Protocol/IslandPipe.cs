using System.Security.Principal;

namespace Island.Core;

public static class IslandPipe
{
    /// <summary>Largest frame accepted. Large tool inputs (e.g. a big Write) fit well inside this.</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    /// <summary>Overrides the pipe name. For tests and debugging; unset in normal use.</summary>
    public const string OverrideEnvVar = "ISLAND_PIPE_NAME";

    public static string ResolveName()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideEnvVar);
        return string.IsNullOrWhiteSpace(overridden) ? $"agent-islet-{CurrentUserSid()}" : overridden;
    }

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
    }
}
