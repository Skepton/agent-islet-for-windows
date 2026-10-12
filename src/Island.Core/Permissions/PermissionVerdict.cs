namespace Island.Core;

/// <summary>
/// A way to "allow and remember" that the agent offered with a permission request.
/// </summary>
/// <param name="ButtonLabel">Short text for the card's button ("Always Allow (Bash)").</param>
/// <param name="Detail">Longer label naming the exact rule or folder, for the debug window.</param>
/// <param name="Payload">Agent-specific data handed back to the integration when chosen. Opaque to the island.</param>
public sealed record RememberOption(string ButtonLabel, string Detail, string Payload);

/// <summary>
/// The user's answer to a permission card. An <see cref="IAgentIntegration"/> turns it into hook output.
/// "No decision" (answer in the terminal) is represented by a null verdict, not by a subtype.
/// </summary>
public abstract record PermissionVerdict
{
    public static PermissionVerdict Allow { get; } = new AllowVerdict();

    /// <param name="message">Told to the agent; the integration picks a default when null.</param>
    public static PermissionVerdict Deny(string? message = null) => new DenyVerdict(message);

    public static PermissionVerdict Remember(RememberOption option) => new RememberVerdict(option);
}

public sealed record AllowVerdict : PermissionVerdict;

public sealed record DenyVerdict(string? Message) : PermissionVerdict;

public sealed record RememberVerdict(RememberOption Option) : PermissionVerdict;
