using System.Windows.Input;

namespace Island.App;

public sealed class RelayCommand(Action execute) : ICommand
{
    // Always executable, so the event never fires.
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
