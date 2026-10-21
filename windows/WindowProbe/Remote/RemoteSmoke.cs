using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WindowProbe.Remote;

/// <summary>Somente janela sintética. Credenciais de teste efêmeras em arquivo privado, nunca stdout.</summary>
internal static class RemoteSmoke
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
        var panel = new StackPanel(); panel.Children.Add(count); panel.Children.Add(editor);
        var window = new Window { Title = "RemoteDesktop — teste remoto sintético", Width = 640, Height = 360, Topmost = true, Content = panel };
        RemoteServer? server = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        int counter = 0;
        timer.Tick += (_, _) => count.Text = "Quadro " + ++counter;
        try
        {
            window.Show(); window.Activate(); editor.Focus(); timer.Start();
            await Task.Delay(700);
            string pin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var credentials = HostCredentials.Create(pin);
            server = new RemoteServer(new WindowInteropHelper(window).Handle, IPAddress.Any, 9443, credentials, certificate: null);
            server.Start();
            await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(new
            { host = "192.168.0.100", port = server.Port, fingerprint = server.Fingerprint, pin }));
            var deadline = DateTime.UtcNow.AddMinutes(3);
            // Mac transmite texto fixo; o resultado é conferido na UI real do Windows.
            while (DateTime.UtcNow < deadline && !(server.FramesSent >= 10 && clicks > 0 && editor.Text == "Teste remoto: ação 123."))
            {
                if (server.LastFailure is not null) break;
                await Task.Delay(100);
            }
            bool success = server.FramesSent >= 10 && clicks > 0 && editor.Text == "Teste remoto: ação 123.";
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            { success, frames = server.FramesSent, inputs = server.InputsApplied, clickVerified = clicks > 0, failure = server.LastFailure, textVerified = editor.Text == "Teste remoto: ação 123." }));
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new { success = false, error = ex.GetType().Name }));
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
