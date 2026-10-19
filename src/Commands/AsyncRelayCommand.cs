namespace GIoC;

/// <summary>
/// An async command wrapper. Generated automatically by [RelayCommand] on Task-returning methods.
/// Prevents concurrent execution while a task is running.
/// </summary>
public sealed class AsyncRelayCommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    /// <summary>True while the async task is executing.</summary>
    public bool IsRunning { get; private set; }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute() => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute()
    {
        if (!CanExecute()) return;
        IsRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            IsRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
