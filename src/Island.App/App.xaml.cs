using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Island.ClaudeCode;
using Island.Core;

namespace Island.App;

public partial class App : Application
{
    /// <summary>Named event that asks the running island to quit (used when updating it).</summary>
    public const string QuitSignalName = @"Local\AgentIslet-Quit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _quitWait;
    private CancellationTokenSource? _shutdown;
    private DispatcherTimer? _pruneTimer;
    private TrayIconHost? _tray;
    private PanelViewModel? _panel;
    private PanelWindow? _panelWindow;
    private OverlayController? _overlay;
    private DemoDriver? _demo;
    private HttpClient? _http;
    private UsageMonitor? _usage;
    private CancellationTokenSource? _usageStop;
    private ConfigStore? _configStore;
    private IslandConfig _config = new();
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AgentIslet", out var createdNew);
        if (!createdNew)
        {
            // Another instance already owns the pipe; just leave.
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // Carry the user's choices over from the folder the app used before it was renamed.
        IslandPaths.MigrateLegacyData();

        // scripts/install-local.ps1 sets this event to ask a running island to quit cleanly before updating it.
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(
            _quitSignal, (_, _) => Dispatcher.InvokeAsync(Quit), null, Timeout.Infinite, executeOnlyOnce: true);

        string pipeName;
        try
        {
            pipeName = IslandPipe.ResolveName();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not determine the pipe name:\n{ex.Message}", "Agent Islet", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
            return;
        }

        // Every supported agent is listed here; each lives in its own integration library.
        var agents = new IAgentIntegration[] { new ClaudeCodeAgent() };

        // Only projects the user chose to show reach the island. --allow-all-projects skips that (smoke scripts, debugging).
        var askAboutProjects = !e.Args.Contains("--allow-all-projects");
        var projects = askAboutProjects ? new ProjectPolicy(new ProjectPolicyStore()) : null;

        var hub = new IslandHub(
            new SessionStore(),
            new PermissionBroker(),
            agents,
            effects => Dispatcher.InvokeAsync(() =>
            {
                _panel?.Apply(effects);
                _overlay?.OnEffects(effects);
            }),
            projects: projects);

        _configStore = new ConfigStore();
        _config = _configStore.Load();
        if (e.Args.Contains("--poll-usage") && !_config.UsageEnabled)
        {
            // Passing the flag is the user's explicit yes; from here on the Usage tab controls it.
            _config = _config with { UsageEnabled = true, UsageConsentAt = DateTimeOffset.UtcNow };
            SaveConfig();
        }

        _panel = new PanelViewModel(
            hub,
            new ProjectsViewModel(hub, askAboutProjects),
            new UsageSettingsViewModel(_config, ApplyConfig),
            HookStatus,
            "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"),
            OpenDataFolder);
        _panelWindow = new PanelWindow(_panel);
        _overlay = new OverlayController(hub, _panel.LogLine);
        _tray = new TrayIconHost(() => ShowPanel(PanelTab.Sessions), () => ShowPanel(PanelTab.Projects), Quit);

        _shutdown = new CancellationTokenSource();

        // Usage polling reads Claude Code's login token, so it only runs once the user has switched it on in the Usage tab.
        _overlay.SetUsageBar(_config.UsageBar);
        if (_config.UsageEnabled) StartUsagePolling();

        new IslandServer(pipeName, (frame, ct) =>
            {
                _usage?.NoteActivity();
                return hub.HandleAsync(frame, ct);
            })
            .RunAsync(_shutdown.Token)
            .ContinueWith(
                task => Dispatcher.InvokeAsync(() => OnServerFailed(task.Exception?.GetBaseException())),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _pruneTimer.Tick += (_, _) => hub.Prune();
        _pruneTimer.Start();

        _overlay.Start();
        if (e.Args.Contains("--demo"))
        {
            _demo = new DemoDriver(_overlay);
            _demo.Start();
        }

        if (!e.Args.Contains("--tray")) ShowPanel(PanelTab.Sessions);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Quit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    /// <summary>Called by the Usage tab whenever the user changes a setting.</summary>
    private void ApplyConfig(IslandConfig config)
    {
        _config = config;
        SaveConfig();
        _overlay?.SetUsageBar(config.UsageBar);

        if (config.UsageEnabled && _usage is null) StartUsagePolling();
        else if (!config.UsageEnabled && _usage is not null) StopUsagePolling();
    }

    private void SaveConfig()
    {
        try
        {
            _configStore?.Save(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _panel?.LogLine($"could not save settings: {ex.Message}");
        }
    }

    private void StartUsagePolling()
    {
        if (_shutdown is null) return;

        var store = new MilestoneStateStore();
        var engine = new MilestoneEngine(state: store.Load());

        _http ??= new HttpClient();
        var source = new OAuthUsageSource(_http, new OAuthCredentialsReader());

        UsageMonitor? monitor = null;
        monitor = new UsageMonitor(source, engine, update => Dispatcher.InvokeAsync(() =>
        {
            // A poll that finishes after the user switched usage off must not bring the numbers back.
            if (!ReferenceEquals(_usage, monitor)) return;
            _panel?.ApplyUsage(update);
            _overlay?.OnUsage(update);
        }), store);
        _usage = monitor;
        _usageStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        _panel?.UsageStarted();
        _ = monitor.RunAsync(_usageStop.Token);
    }

    private void StopUsagePolling()
    {
        _usage = null;
        _usageStop?.Cancel();
        _usageStop?.Dispose();
        _usageStop = null;

        _panel?.UsageStopped();
        _overlay?.ClearUsage();
    }

    private void ShowPanel(PanelTab tab)
    {
        if (_panel is null || _panelWindow is null) return;
        _panel.Tab = tab;
        _panel.Refresh();
        _panel.Projects.Refresh();
        _panelWindow.Show();
        if (_panelWindow.WindowState == WindowState.Minimized) _panelWindow.WindowState = WindowState.Normal;
        _panelWindow.Activate();
    }

    /// <summary>One line for the panel: are the Claude Code hooks installed, and which exe do they run? Reads settings, never writes.</summary>
    private static string HookStatus()
    {
        var path = ClaudePaths.UserSettings();
        try
        {
            var count = HookSettings.CountOurs(SettingsFile.Read(path));
            return count == 0
                ? "Claude Code hooks: not installed (run scripts\\install-local.ps1)"
                : $"Claude Code hooks: {count} installed in {path}";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return $"Claude Code hooks: could not read {path}";
        }
    }

    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(IslandPaths.DataDir);
            System.Diagnostics.Process.Start("explorer.exe", IslandPaths.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Nothing to show; the path is in the README.
        }
    }

    private void OnServerFailed(Exception? error)
    {
        MessageBox.Show(
            $"The island could not listen for Claude Code events:\n{error?.Message}",
            "Agent Islet", MessageBoxButton.OK, MessageBoxImage.Error);
        Quit();
    }

    private void Quit()
    {
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _shutdown?.Cancel();
        _quitWait?.Unregister(null);
        _quitSignal?.Dispose();
        _http?.Dispose();
        _pruneTimer?.Stop();
        _demo?.Stop();
        _overlay?.Dispose();
        _tray?.Dispose();
        _panelWindow?.ReallyClose();

        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the OS releases it when the process ends.
        }
        _singleInstance?.Dispose();
    }
}
