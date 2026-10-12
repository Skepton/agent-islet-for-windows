using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Island.App;

/// <summary>
/// The island itself: a borderless, topmost, never-activating window at the top-centre of the screen.
/// It only renders the <see cref="OverlayViewModel"/>; <see cref="OverlayController"/> decides what to show.
/// </summary>
public partial class IslandWindow : Window
{
    private const double DesignWidth = 480;
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(200));

    private IntPtr _hwnd;
    private bool _shown;

    public IslandWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        // Clicking a button must not make this window the active one (that would pull focus out of the terminal / editor).
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        return new IntPtr(NativeMethods.MA_NOACTIVATE);
    }

    /// <summary>When true, mouse clicks fall through to the window underneath (used while only the usage strip shows).</summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (_hwnd == IntPtr.Zero) return;

        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var updated = clickThrough ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~NativeMethods.WS_EX_TRANSPARENT;
        if (updated != style) NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(updated));
    }

    /// <summary>Top-centre of the work area of the monitor that holds the foreground window.</summary>
    public void PlaceAtTop()
    {
        if (_hwnd == IntPtr.Zero) return;

        var monitor = NativeMethods.MonitorFromWindow(NativeMethods.GetForegroundWindow(), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        uint dpi = 96;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0) dpi = dpiX;
        var scale = dpi / 96.0;

        var work = info.rcWork;
        var workWidthDip = (work.Right - work.Left) / scale;
        Width = Math.Min(DesignWidth, workWidthDip * 0.9);

        var widthPx = (int)Math.Round(Width * scale);
        var x = work.Left + ((work.Right - work.Left) - widthPx) / 2;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, work.Top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Drops the island down from the top edge. Does nothing when it is already showing.</summary>
    public void SlideIn()
    {
        if (_shown) return;
        _shown = true;

        if (!IsVisible) Show(); // ShowActivated=False, so this never takes focus
        PlaceAtTop();

        UpdateLayout();
        Animate(fromY: -(Panel.ActualHeight + 4), toY: 0, fromOpacity: 0, toOpacity: 1, onCompleted: null);
    }

    /// <summary>Retracts the island and hides the window afterwards (unless something showed it again meanwhile).</summary>
    public void SlideOut()
    {
        if (!_shown) return;
        _shown = false;

        Animate(fromY: 0, toY: -(Panel.ActualHeight + 4), fromOpacity: 1, toOpacity: 0, onCompleted: () =>
        {
            if (!_shown) Hide();
        });
    }

    private void Animate(double fromY, double toY, double fromOpacity, double toOpacity, Action? onCompleted)
    {
        var slide = new DoubleAnimation(fromY, toY, SlideDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(fromOpacity, toOpacity, SlideDuration);
        if (onCompleted is not null) fade.Completed += (_, _) => onCompleted();

        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
        Panel.BeginAnimation(OpacityProperty, fade);
    }
}
