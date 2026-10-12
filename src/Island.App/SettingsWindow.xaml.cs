using System.ComponentModel;
using System.Windows;

namespace Island.App;

/// <summary>Settings (for now: which projects the island shows). Closing it hides it.</summary>
public partial class SettingsWindow : Window
{
    private bool _quitting;

    public SettingsWindow(SettingsViewModel viewModel)
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
