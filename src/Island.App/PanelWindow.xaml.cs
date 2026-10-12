using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Island.App;

/// <summary>
/// The control panel (tray icon → open): sessions, projects and the debug log, styled like the island.
/// Closing it hides it; the tray menu quits. While visible it refreshes every few seconds so relative times stay current.
/// </summary>
public partial class PanelWindow : Window
{
    private readonly PanelViewModel _viewModel;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _quitting;

    public PanelWindow(PanelViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _refresh.Tick += (_, _) => _viewModel.Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _refresh.Start();
            else _refresh.Stop();
        };
        StateChanged += (_, _) =>
        {
            // A maximised borderless window overhangs the screen by its resize border; keep the content on screen.
            RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    public void ReallyClose()
    {
        _quitting = true;
        _refresh.Stop();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.UseRoundedDarkFrame(hwnd); // Windows 11: rounded corners and a dark hairline border; ignored elsewhere
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}
