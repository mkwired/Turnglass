using Microsoft.UI.Xaml;

namespace Turnglass.Desktop;

public sealed partial class App : Application, IAsyncDisposable
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    public ValueTask DisposeAsync() => _window?.DisposeAsync() ?? ValueTask.CompletedTask;
}
