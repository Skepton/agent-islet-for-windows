using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Island.ClaudeCode;
using Island.Core;

namespace Island.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private CancellationTokenSource? _shutdown;
    private DispatcherTimer? _pruneTimer;
    private TrayIconHost? _tray;
    private DebugViewModel? _viewModel;
    private MainWindow? _window;
    private SettingsViewModel? _settings;
    private SettingsWindow? _settingsWindow;
    private OverlayController? _overlay;
    private DemoDriver? _demo;
    private HttpClient? _http;
    private UsageMonitor? _usage;
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\ClaudeCodeIsland", out var createdNew);
        if (!createdNew)
        {
            // Another instance already owns the pipe; just leave.
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        string pipeName;
        try
        {
            pipeName = IslandPipe.ResolveName();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not determine the pipe name:\n{ex.Message}", "Claude Code Island", MessageBoxButton.OK, MessageBoxImage.Error);
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
                _viewModel?.Apply(effects);
                _overlay?.OnEffects(effects);
                if (effects.Any(x => x is ProjectDecided)) _settings?.Refresh();
            }),
            projects: projects);

        _viewModel = new DebugViewModel(hub);
        _window = new MainWindow(_viewModel);
        _settings = new SettingsViewModel(hub, askAboutProjects);
        _settingsWindow = new SettingsWindow(_settings);
        _overlay = new OverlayController(hub, _viewModel.LogLine, ShowWindow);
        _tray = new TrayIconHost(ShowWindow, ShowSettings, Quit);

        _shutdown = new CancellationTokenSource();

        // Usage polling reads Claude Code's login token, so it only starts on an explicit opt-in.
        // (The first-run consent dialog arrives with the settings UI; until then this flag is the opt-in.)
        if (e.Args.Contains("--poll-usage")) StartUsagePolling(_shutdown.Token);

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

        if (!e.Args.Contains("--tray")) ShowWindow();
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

    private void StartUsagePolling(CancellationToken shutdown)
    {
        var store = new MilestoneStateStore();
        var engine = new MilestoneEngine(state: store.Load());

        _http = new HttpClient();
        var source = new OAuthUsageSource(_http, new OAuthCredentialsReader());
        _usage = new UsageMonitor(source, engine, update => Dispatcher.InvokeAsync(() =>
        {
            _viewModel?.ApplyUsage(update);
            _overlay?.OnUsage(update);
        }), store);

        _ = _usage.RunAsync(shutdown);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _viewModel?.Refresh();
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null) return;
        _settings?.Refresh();
        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void OnServerFailed(Exception? error)
    {
        MessageBox.Show(
            $"The island could not listen for Claude Code events:\n{error?.Message}",
            "Claude Code Island", MessageBoxButton.OK, MessageBoxImage.Error);
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
        _http?.Dispose();
        _pruneTimer?.Stop();
        _demo?.Stop();
        _overlay?.Dispose();
        _tray?.Dispose();
        _window?.ReallyClose();
        _settingsWindow?.ReallyClose();

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
