using System.Windows;
using System.Windows.Threading;

namespace WindowProbe;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        if (e.Args.Length == 2 && e.Args[0] == "--input-interaction-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await InputInteractionSmoke.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--real-app-soak")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RealAppSoak.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--stream-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await StreamingSmoke.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--remote-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Remote.RemoteSmoke.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--host")
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new Remote.RemoteHostWindow();
            MainWindow.Show();
            return;
        }
        if (e.Args.Length == 2 && (e.Args[0] == "--smoke" || e.Args[0] == "--input-smoke"))
        {
            int code = await CaptureSmoke.RunAsync(e.Args[1], e.Args[0] == "--input-smoke");
            Shutdown(code);
            return;
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "Erro não tratado — WindowProbe",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;

        if (MainWindow is null)
        {
            Shutdown(-1);
        }
    }
}
