using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;

namespace WindowProbe.Remote;

/// <summary>
/// Prova de sessão completa: sobe um host real (TLS + PIN + vídeo H.264 +
/// entrada) sobre uma janela sintética e grava as credenciais efêmeras em um
/// arquivo privado para o cliente Mac. Nada de segredo vai para stdout.
///
/// Uso: WindowProbe.exe --host-smoke &lt;pasta&gt;
/// </summary>
internal static class HostSmoke
{
    internal static async Task<int> RunAsync(string directory, bool performance = false)
    {
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        string sessionFile = Path.Combine(root, "session.json");
        string reportFile = Path.Combine(root, "host-result.json");

        var editor = new TextBox { FontSize = 24, Height = 80, Margin = new Thickness(24) };
        int clicks = 0;
        editor.PreviewMouseLeftButtonDown += (_, _) => clicks++;
        var count = new TextBlock { FontSize = 24, Margin = new Thickness(24), Text = "Quadro 0" };
        var panel = new StackPanel();
        panel.Children.Add(count);
        panel.Children.Add(editor);
        var window = new Window
        {
            Title = "RemoteDesktop — prova de sessão",
            Width = performance ? 1920 : 640,
            Height = performance ? 1080 : 360,
            Topmost = true,
            Left = 0,
            Top = 0,
            WindowStyle = performance ? WindowStyle.None : WindowStyle.SingleBorderWindow,
            ResizeMode = performance ? ResizeMode.NoResize : ResizeMode.CanResize,
            Content = panel
        };

        RemoteServer? server = null;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(performance ? 1 : 33) };
        int counter = 0;
        timer.Tick += (_, _) => count.Text = "Quadro " + ++counter;
        TimeSpan lastRender = TimeSpan.MinValue;
        EventHandler render = (_, e) =>
        {
            if (e is RenderingEventArgs args && args.RenderingTime != lastRender)
            {
                lastRender = args.RenderingTime;
                count.Text = "Quadro " + ++counter;
            }
        };
        var watch = Stopwatch.StartNew();
        long lastFrames = 0;
        DateTime lastProgress = DateTime.UtcNow;

        try
        {
            window.Show();
            if (performance)
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                window.Width = 1920 / dpi.DpiScaleX;
                window.Height = 1080 / dpi.DpiScaleY;
            }
            window.Activate();
            // Janela parada: exercita a atualização de janela estática do host,
            // que precisa alimentar o Mac sem depender de FrameArrived.
            if (performance) CompositionTarget.Rendering += render;
            else timer.Start();
            await Task.Delay(700);

            string pin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var credentials = HostCredentials.Create(pin);
            server = new RemoteServer(
                new WindowInteropHelper(window).Handle,
                IPAddress.Any,
                performance ? 9444 : 9443,
                credentials,
                certificate: null,
                windowList: () => [new WindowDescriptor(new WindowInteropHelper(window).Handle, window.Title, "WindowProbe", 640, 360)]);
            if (performance) server.Status += message =>
            {
                lock (server) File.AppendAllText(Path.Combine(root, "host-status.log"),
                    message + " failure=" + server.LastFailure + Environment.NewLine);
            };
            server.Start();

            await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(new
            {
                host = "192.168.0.100",
                port = server.Port,
                fingerprint = server.Fingerprint,
                pin,
                durationSeconds = performance ? (double?)15 : null
            }));

            // Encerra no primeiro motivo, não no prazo: o relatório precisa da
            // taxa do período ativo, sem espera ociosa diluindo o FPS.
            var deadline = DateTime.UtcNow.AddSeconds(performance ? 45 : 90);
            while (DateTime.UtcNow < deadline)
            {
                if (server.LastFailure is not null) break;
                if (performance)
                {
                    // O cliente encerra após a janela de medição; esse fim pelo
                    // cliente não é falha do host. Para quando o streaming já
                    // avançou e ficou 3 s sem quadros novos.
                    if (server.FramesSent >= 100 && DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(3)) break;
                }
                else if (server.FramesSent >= 10 && clicks > 0 && editor.Text == "Teste remoto: ação 123.") break;
                if (performance && server.FramesSent != lastFrames)
                {
                    lastFrames = server.FramesSent;
                    lastProgress = DateTime.UtcNow;
                }
                await Task.Delay(100);
            }

            double seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
            bool success = performance ? server.FramesSent >= 100 && server.LastFailure is null
                : server.FramesSent >= 10 && clicks > 0 && editor.Text == "Teste remoto: ação 123.";

            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                success,
                frames = server.FramesSent,
                inputs = server.InputsApplied,
                clickVerified = clicks > 0,
                textVerified = editor.Text == "Teste remoto: ação 123.",
                effectiveFps = Math.Round(server.FramesSent / seconds, 1),
                durationSeconds = Math.Round(seconds, 1),
                timings = server.VideoTimings,
                hardware = server.VideoIsHardware,
                displayAwake = server.DisplayAwake,
                monitorWoken = server.MonitorWoken,
                status = File.Exists(Path.Combine(root, "host-status.log"))
                    ? File.ReadAllLines(Path.Combine(root, "host-status.log"))
                    : [],
                videoError = server.VideoError,
                failure = server.LastFailure
            }));
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new { success = false, error = ex.GetType().Name, detail = ex.Message }));
            return 1;
        }
        finally
        {
            timer.Stop();
            CompositionTarget.Rendering -= render;
            if (server is not null) await server.StopAsync();
            if (File.Exists(sessionFile)) File.Delete(sessionFile);
            window.Close();
        }
    }
}
