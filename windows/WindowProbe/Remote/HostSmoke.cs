using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

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
    internal static async Task<int> RunAsync(string directory)
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
            Width = 640,
            Height = 360,
            Topmost = true,
            Content = panel
        };

        RemoteServer? server = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        int counter = 0;
        timer.Tick += (_, _) => count.Text = "Quadro " + ++counter;
        var watch = Stopwatch.StartNew();

        try
        {
            window.Show();
            window.Activate();
            timer.Start();
            await Task.Delay(700);

            string pin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var credentials = HostCredentials.Create(pin);
            server = new RemoteServer(
                new WindowInteropHelper(window).Handle,
                IPAddress.Any,
                9443,
                credentials,
                certificate: null,
                windowList: () => [new WindowDescriptor(new WindowInteropHelper(window).Handle, window.Title, "WindowProbe", 640, 360)]);
            server.Start();

            await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(new
            {
                host = "192.168.0.100",
                port = server.Port,
                fingerprint = server.Fingerprint,
                pin
            }));

            // Espera o cliente Mac conectar, receber vídeo e aplicar entrada.
            // O cliente de teste encerra depois de 10 quadros, então o mínimo
            // exigido aqui precisa ser compatível com esse encerramento.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (server.LastFailure is not null) break;
                if (server.FramesSent >= 10 && clicks > 0 && editor.Text == "Teste remoto: ação 123.") break;
                await Task.Delay(100);
            }

            double seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
            bool success = server.FramesSent >= 10 && clicks > 0 && editor.Text == "Teste remoto: ação 123.";

            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                success,
                frames = server.FramesSent,
                inputs = server.InputsApplied,
                clickVerified = clicks > 0,
                textVerified = editor.Text == "Teste remoto: ação 123.",
                effectiveFps = Math.Round(server.FramesSent / seconds, 1),
                durationSeconds = Math.Round(seconds, 1),
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
            if (server is not null) await server.StopAsync();
            if (File.Exists(sessionFile)) File.Delete(sessionFile);
            window.Close();
        }
    }
}
