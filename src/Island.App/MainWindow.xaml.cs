using System.ComponentModel;
using System.Windows;

namespace Island.App;

/// <summary>Debug view of sessions and pending permission requests. Closing it hides it; the tray menu quits.</summary>
public partial class MainWindow : Window
{
    private bool _quitting;

    public MainWindow(DebugViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void ReallyClose()
    {
        _quitting = true;
        Close();
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
}
