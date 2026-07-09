using Microsoft.UI.Xaml;

namespace GuardWui3;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // Last-chance crash record: a fail-fast or unhandled XAML exception
        // otherwise vanishes without a trace on end-user machines.
        UnhandledException += (_, e) => Services.DebugLog.Crash(e.Exception, "XAML");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
