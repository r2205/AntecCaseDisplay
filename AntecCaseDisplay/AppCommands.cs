using System.Windows.Input;

namespace AntecCaseDisplay;

/// <summary>
/// Lightweight ICommand implementation. RoutedCommand doesn't reliably bubble
/// from a tray icon's popup ContextMenu, so we use plain delegates instead.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add    { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
}

/// <summary>
/// Singleton command instances that the tray menu in App.xaml binds against
/// via {x:Static}. The actions are wired up at App startup.
/// </summary>
public static class AppCommands
{
    public static RelayCommand OpenSettingsCommand { get; } =
        new RelayCommand(() => App.Current.ShowSettingsWindow());

    public static RelayCommand PauseResumeCommand { get; } =
        new RelayCommand(() =>
        {
            var m = App.Current.Monitor;
            if (m.IsRunning) m.Stop(); else m.Start();
        });

    public static RelayCommand QuitCommand { get; } =
        new RelayCommand(() => App.Current.Shutdown());
}
