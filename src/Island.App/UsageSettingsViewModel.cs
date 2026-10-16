using Island.Core;

namespace Island.App;

/// <summary>
/// The panel's Usage tab: the on/off switch (with the consent text next to it) and how the numbers show. Every change is
/// handed to <paramref name="apply"/>, which saves it and starts or stops the polling. UI thread only.
/// </summary>
public sealed class UsageSettingsViewModel : ObservableObject
{
    public const string ConsentText =
        "Turning this on lets the island read the access token Claude Code stores when you sign in (in memory only: it is "
        + "never saved, logged or refreshed) and ask api.anthropic.com for your 5-hour and 7-day limits, about every 2 minutes "
        + "while Claude Code is in use and every 15 minutes otherwise. Nothing else is sent. Only subscription logins have these limits.";

    private readonly Action<IslandConfig> _apply;
    private readonly Func<DateTimeOffset> _clock;
    private IslandConfig _config;
    private string _status = "";

    public UsageSettingsViewModel(IslandConfig config, Action<IslandConfig> apply, Func<DateTimeOffset>? clock = null)
    {
        _config = config;
        _apply = apply;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IslandConfig Config => _config;

    public bool Enabled
    {
        get => _config.UsageEnabled;
        set
        {
            if (value == _config.UsageEnabled) return;
            Change(_config with
            {
                UsageEnabled = value,
                // When they agreed is kept after turning it off again; it is the last time they said yes.
                UsageConsentAt = value ? _clock() : _config.UsageConsentAt,
            });
            Raise(nameof(Enabled));
            Raise(nameof(BarOptionsEnabled));
        }
    }

    public bool BarOptionsEnabled => Enabled;

    public bool ThinBar
    {
        get => _config.UsageBar == UsageBarMode.ThinBar;
        set
        {
            if (value) SetBar(UsageBarMode.ThinBar);
        }
    }

    public bool OnChangeOnly
    {
        get => _config.UsageBar == UsageBarMode.OnChange;
        set
        {
            if (value) SetBar(UsageBarMode.OnChange);
        }
    }

    /// <summary>What the last poll found, or that tracking is off.</summary>
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Consent => ConsentText;

    /// <summary>Replaces the settings without applying them (used after the app itself changed them, e.g. a command-line flag).</summary>
    public void Reload(IslandConfig config)
    {
        _config = config;
        Raise(nameof(Enabled));
        Raise(nameof(BarOptionsEnabled));
        Raise(nameof(ThinBar));
        Raise(nameof(OnChangeOnly));
    }

    private void SetBar(UsageBarMode mode)
    {
        if (_config.UsageBar == mode) return;
        Change(_config with { UsageBar = mode });
        Raise(nameof(ThinBar));
        Raise(nameof(OnChangeOnly));
    }

    private void Change(IslandConfig config)
    {
        _config = config;
        _apply(config);
    }
}
