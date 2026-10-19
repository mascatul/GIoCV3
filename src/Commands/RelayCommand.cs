namespace GIoC;

/// <summary>
/// A synchronous command wrapper. Generated automatically by [RelayCommand] on void methods.
/// </summary>
public sealed class RelayCommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute() => _canExecute?.Invoke() ?? true;

    public void Execute()
    {
        if (CanExecute()) _execute();
    }

    /// <summary>Call when CanExecute result has changed to notify consumers.</summary>
    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
