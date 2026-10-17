using System.Windows.Input;
using Island.ClaudeCode;

namespace Island.App;

/// <summary>
/// The panel's "Claude Code" tab: is Claude Code connected, and buttons to connect or disconnect. Both ask first, in
/// plain words, through <paramref name="confirm"/>, because they edit Claude Code's own settings file. UI thread only.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private readonly ClaudeCodeSetup _setup;
    private readonly Func<string, bool> _confirm;
    private SetupStatus _status;
    private string _resultText = "";

    /// <param name="confirm">Shows the text and returns true when the user agrees.</param>
    public SetupViewModel(ClaudeCodeSetup setup, Func<string, bool> confirm)
    {
        _setup = setup;
        _confirm = confirm;
        _status = setup.Status();
        Connect = new RelayCommand(DoConnect);
        Disconnect = new RelayCommand(DoDisconnect);
    }

    public ICommand Connect { get; }
    public ICommand Disconnect { get; }

    public SetupStatus Status => _status;

    public string StatusText => _status.Text;

    /// <summary>There are island hooks in Claude Code's settings (of either kind), so there is something to remove.</summary>
    public bool CanDisconnect => _status.State is SetupState.Connected or SetupState.ViaHelper;

    /// <summary>Connecting again is harmless (it repairs and updates), so the button stays available.</summary>
    public string ConnectLabel => _status.State switch
    {
        SetupState.Connected => "Repair connection",
        SetupState.ViaHelper => "Switch to the built-in link",
        _ => "Connect Claude Code",
    };

    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    /// <summary>Nothing is connected yet (an older helper connection still works, so it does not count).</summary>
    public bool NeedsSetup => _status.State is SetupState.NotConnected or SetupState.Unreadable;

    public void Refresh()
    {
        _status = _setup.Status();
        Raise(nameof(StatusText));
        Raise(nameof(CanDisconnect));
        Raise(nameof(ConnectLabel));
        Raise(nameof(NeedsSetup));
    }

    private void DoConnect()
    {
        if (!_confirm(_setup.ConnectSummary())) return;
        ResultText = _setup.Connect().Message;
        Refresh();
    }

    private void DoDisconnect()
    {
        if (!_confirm(_setup.DisconnectSummary())) return;
        ResultText = _setup.Disconnect().Message;
        Refresh();
    }
}
