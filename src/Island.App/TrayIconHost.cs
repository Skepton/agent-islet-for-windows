using System.Drawing;
using System.Windows.Forms;

namespace Island.App;

/// <summary>Notification-area icon. Left-click opens the control panel; right-click shows a dark menu in the island's colours.</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayIconHost(Action onOpen, Action onProjects, Action onQuit)
    {
        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = DarkMenuColors.Background,
            ForeColor = DarkMenuColors.Text,
            ShowImageMargin = false,
            Padding = new Padding(4),
            Font = new Font("Segoe UI", 9.5f),
        };
        _menu.Items.Add(Item("Open panel", onOpen, bold: true));
        _menu.Items.Add(Item("Projects...", onProjects));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Quit", onQuit));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Information, // placeholder until the app has its own icon
            Text = "Claude Code Island",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) onOpen();
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private static ToolStripMenuItem Item(string text, Action onClick, bool bold = false)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = DarkMenuColors.Text, Padding = new Padding(6, 4, 6, 4) };
        if (bold) item.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static class DarkMenuColors
    {
        public static readonly Color Background = Color.FromArgb(0x0B, 0x0B, 0x0D);
        public static readonly Color Hover = Color.FromArgb(0x1E, 0x21, 0x28);
        public static readonly Color Border = Color.FromArgb(0x26, 0x26, 0x2A);
        public static readonly Color Text = Color.White;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = DarkMenuColors.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(DarkMenuColors.Border);
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkMenuColors.Background;
        public override Color MenuBorder => DarkMenuColors.Border;
        public override Color MenuItemBorder => DarkMenuColors.Hover;
        public override Color MenuItemSelected => DarkMenuColors.Hover;
        public override Color MenuItemSelectedGradientBegin => DarkMenuColors.Hover;
        public override Color MenuItemSelectedGradientEnd => DarkMenuColors.Hover;
        public override Color MenuItemPressedGradientBegin => DarkMenuColors.Hover;
        public override Color MenuItemPressedGradientEnd => DarkMenuColors.Hover;
        public override Color ImageMarginGradientBegin => DarkMenuColors.Background;
        public override Color ImageMarginGradientMiddle => DarkMenuColors.Background;
        public override Color ImageMarginGradientEnd => DarkMenuColors.Background;
        public override Color SeparatorDark => DarkMenuColors.Border;
        public override Color SeparatorLight => DarkMenuColors.Border;
    }
}
