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
        if (e.Args.Length == 2 && e.Args[0] == "--host-performance-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Remote.HostSmoke.RunAsync(e.Args[1], performance: true));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--host-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Remote.HostSmoke.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--interop-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await InteropProbe.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--encoder-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Task.Run(() => EncoderProbe.RunAsync(e.Args[1])));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--video-dump")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await VideoDump.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length >= 4 && e.Args.Length <= 6 && e.Args[0] == "--video-dump")
        {
            // --video-dump <pasta> <largura> <altura> [--no-keyframe]
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool keyframe = !e.Args.Contains("--no-keyframe");
            Shutdown(await VideoDump.RunAsync(e.Args[1],
                                              int.Parse(e.Args[2]),
                                              int.Parse(e.Args[3]),
                                              keyframe));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--video-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await VideoSmoke.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--capture-window")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            long rawHwnd = long.Parse(e.Args[1]);
            string destination = e.Args[2];
            var svc = new Capture.WindowCaptureService();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var result = await svc.CaptureAsync(new IntPtr(rawHwnd), destination, TimeSpan.FromSeconds(5), null, cts.Token);
                string report = destination + ".json";
                await System.IO.File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new
                {
                    success = true,
                    bytes = result.Bytes,
                    width = result.Width,
                    height = result.Height,
                    path = result.Path
                }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                string report = destination + ".json";
                await System.IO.File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.GetType().Name,
                    message = ex.Message
                }));
                Shutdown(1);
            }
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
        if (e.Args.Length == 1 && e.Args[0] == "--probe")
        {
            // Tela antiga de captura, preservada para depuração.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new MainWindow();
            MainWindow.Show();
            return;
        }

        // Sem argumentos: host de compartilhamento por PIN.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new Remote.RemoteHostWindow();
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
