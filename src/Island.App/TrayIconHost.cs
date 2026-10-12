namespace Island.App;

/// <summary>Notification-area icon with a Sessions / Settings / Quit menu.</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;

    public TrayIconHost(Action onShow, Action onSettings, Action onQuit)
    {
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Items.Add("Show sessions", null, (_, _) => onShow());
        _menu.Items.Add("Settings...", null, (_, _) => onSettings());
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => onQuit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information, // placeholder until the app has its own icon
            Text = "Claude Code Island",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onShow();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
