using System.Text.Json;

namespace Island.Core;

/// <summary>Holds a bearer token. Printing it (ToString, logging, string interpolation) yields a placeholder.</summary>
public sealed class SecretToken
{
    private readonly string _value;

    public SecretToken(string value)
    {
        _value = value;
    }

    /// <summary>Only call this when building the Authorization header.</summary>
    public string Reveal() => _value;

    public override string ToString() => "[redacted]";
}

public enum CredentialsStatus { Found, Missing, NotOAuth, Expired, Unreadable }

public sealed record CredentialsResult(CredentialsStatus Status, SecretToken? Token = null);

/// <summary>
/// Reads Claude Code's stored OAuth access token. Read-only by design: it never touches the refresh token,
/// never refreshes anything, and never writes the file. Claude Code owns this file and rotates the tokens.
/// </summary>
public sealed class OAuthCredentialsReader
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;

    public OAuthCredentialsReader(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? DefaultPath();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>%CLAUDE_CONFIG_DIR%\.credentials.json when set, otherwise %USERPROFILE%\.claude\.credentials.json.</summary>
    public static string DefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        return Path.Combine(directory, ".credentials.json");
    }

    public CredentialsResult Read()
    {
        string text;
        try
        {
            // Claude Code may be rewriting the file at this moment; share read/write so we do not block it.
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CredentialsResult(CredentialsStatus.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CredentialsResult(CredentialsStatus.Unreadable);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (JsonUtil.Prop(doc.RootElement, "claudeAiOauth") is not { } oauth)
                return new CredentialsResult(CredentialsStatus.NotOAuth);

            var accessToken = JsonUtil.Str(oauth, "accessToken");
            if (string.IsNullOrEmpty(accessToken))
                return new CredentialsResult(CredentialsStatus.NotOAuth);

            var expiresAtMs = JsonUtil.Int64(oauth, "expiresAt");
            if (expiresAtMs > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) <= _clock() + ExpirySkew)
                return new CredentialsResult(CredentialsStatus.Expired);

            return new CredentialsResult(CredentialsStatus.Found, new SecretToken(accessToken));
        }
        catch (JsonException)
        {
            return new CredentialsResult(CredentialsStatus.Unreadable);
        }
    }
}
