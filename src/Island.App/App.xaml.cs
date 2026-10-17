using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using Island.ClaudeCode;
using Island.Core;

namespace Island.App;

public partial class App : Application
{
    /// <summary>Named event that asks the running island to quit (used when updating it).</summary>
    public static string QuitSignalName => @"Local\AgentIslet" + InstanceSuffix + "-Quit";

    /// <summary>
    /// Tests set ISLAND_INSTANCE_NAME to run a second island next to the real one: the single-instance lock and the quit
    /// event then carry the name. Unset in normal use, when there is only ever one.
    /// </summary>
    private static string InstanceSuffix =>
        Environment.GetEnvironmentVariable("ISLAND_INSTANCE_NAME") is { Length: > 0 } name ? "-" + name : "";

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
    private const int DefaultHookPort = 47821;

    private UsageMonitor? _usage;
    private string? _hookUrl;
    private CancellationTokenSource? _usageStop;
    private ConfigStore? _configStore;
    private IslandConfig _config = new();
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AgentIslet" + InstanceSuffix, out var createdNew);
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

        _shutdown = new CancellationTokenSource();

        // The local endpoint Claude Code's hooks call. Started before the panel, which needs its address for Connect.
        var endpointNote = StartHookEndpoint(hub);

        _panel = new PanelViewModel(
            hub,
            new ProjectsViewModel(hub, askAboutProjects),
            new UsageSettingsViewModel(_config, ApplyConfig),
            new SetupViewModel(new ClaudeCodeSetup(ClaudePaths.UserSettings(), () => _hookUrl), Confirm),
            "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"),
            OpenDataFolder);
        _panel.LogLine(endpointNote);
        _panelWindow = new PanelWindow(_panel);
        _overlay = new OverlayController(hub, _panel.LogLine);
        _tray = new TrayIconHost(() => ShowPanel(PanelTab.Sessions), () => ShowPanel(PanelTab.Projects), Quit);

        // Usage polling reads Claude Code's login token, so it only runs once the user has switched it on in the Usage tab.
        _overlay.SetUsageBar(_config.UsageBar);
        if (_config.UsageEnabled) StartUsagePolling();

        Task<string?> OnFrame(HookFrame frame, CancellationToken ct)
        {
            _usage?.NoteActivity();
            return hub.HandleAsync(frame, ct);
        }

        new IslandServer(pipeName, OnFrame)
            .RunAsync(_shutdown.Token)
            .ContinueWith(
                task => Dispatcher.InvokeAsync(() => OnServerFailed(task.Exception?.GetBaseException())),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        // Hooks installed before the rename run a forwarder that talks to the old pipe name. Keep answering it, so those
        // installs go on working until the user switches to the built-in link (which needs no forwarder at all).
        if (IslandPipe.ResolveLegacyName() is { } legacyPipe)
        {
            _ = new IslandServer(legacyPipe, OnFrame).RunAsync(_shutdown.Token).ContinueWith(
                task => Dispatcher.InvokeAsync(() => _panel?.LogLine($"the old-name pipe stopped: {task.Exception?.GetBaseException().Message}")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _pruneTimer.Tick += (_, _) => hub.Prune();
        _pruneTimer.Start();

        _overlay.Start();
        if (e.Args.Contains("--demo"))
        {
            _demo = new DemoDriver(_overlay);
            _demo.Start();
        }

        // First thing a new user needs is the connection to Claude Code, so open the panel there until it is made.
        if (!e.Args.Contains("--tray")) ShowPanel(_panel.Setup.NeedsSetup ? PanelTab.Setup : PanelTab.Sessions);
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

    /// <summary>
    /// Opens the local endpoint on the saved port (a free one when that is taken) with the saved secret, making both on the
    /// first run. <see cref="_hookUrl"/> stays null when nothing could be opened; Connect then says why.
    /// </summary>
    /// <returns>One line for the debug log.</returns>
    private string StartHookEndpoint(IslandHub hub)
    {
        var secret = _config.HookSecret ?? HookHttpServer.NewToken();
        var saved = _config.HookPort > 0 ? _config.HookPort : DefaultHookPort;

        foreach (var port in new[] { saved, 0 }) // the saved port first, then any free one
        {
            try
            {
                var server = new HookHttpServer(port, secret, (agent, request, ct) =>
                {
                    _usage?.NoteActivity();
                    return hub.HandleHttpAsync(agent, request, ct);
                });
                server.Start();

                if (server.Port != _config.HookPort || secret != _config.HookSecret)
                {
                    _config = _config with { HookPort = server.Port, HookSecret = secret };
                    SaveConfig();
                }
                _hookUrl = HookHttpServer.UrlFor(server.Port, ClaudeCodeAgent.AgentId, secret);
                _ = server.RunAsync(_shutdown!.Token);

                return server.Port == saved
                    ? $"local endpoint listening on 127.0.0.1:{server.Port}"
                    : $"port {saved} was in use; the local endpoint listens on 127.0.0.1:{server.Port} instead (Claude Code hooks need Connect again)";
            }
            catch (SocketException)
            {
                // Try the next port.
            }
        }
        return "could not open a local endpoint: Claude Code cannot reach the island until a port is free";
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

    /// <summary>Asks the user before the app edits Claude Code's settings (or undoes that).</summary>
    private bool Confirm(string text) =>
        MessageBox.Show(_panelWindow, text, "Agent Islet", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

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
