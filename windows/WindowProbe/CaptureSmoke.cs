using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WindowProbe.Capture;

namespace WindowProbe;

/// <summary>Teste real WGC apenas sobre uma janela sintetica deste processo.</summary>
internal static class CaptureSmoke
{
    internal static async Task<int> RunAsync(string outputPath, bool testInput = false)
    {
        string path = Path.GetFullPath(outputPath);
        var editor = new TextBox { FontSize = 24, Margin = new Thickness(24), MinHeight = 70 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "RemoteDesktop\nTeste de captura e entrada\nTexto legível: ação, conexão, 12345",
            Margin = new Thickness(24, 16, 24, 0), Foreground = Brushes.Black,
            FontSize = 24, TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(editor);
        var window = new Window
        {
            Title = "RemoteDesktop — teste de captura",
            Width = 640, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            Content = panel
        };
        try
        {
            window.Show();
            await Task.Delay(700);
            if (testInput)
            {
                window.Activate();
                editor.Focus();
                await Task.Delay(200);
                string probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "../../../../InputProbe/bin/Release/net10.0/InputProbe.exe"));
                var start = new ProcessStartInfo(probe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("--send-test");
                start.ArgumentList.Add($"0x{new WindowInteropHelper(window).Handle:X}");
                using var process = Process.Start(start) ?? throw new InvalidOperationException("InputProbe nao iniciou.");
                // Autoriza somente a janela sintetica criada acima. O modo manual
                // de InputProbe continua exigindo confirmacao explicita.
                await process.StandardInput.WriteLineAsync("ENVIAR");
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { if (!process.HasExited) process.Kill(); throw; }
                string details = await stdout + await stderr;
                if (process.ExitCode != 0) throw new InvalidOperationException("InputProbe falhou: " + details);
                await Task.Delay(200);
                if (editor.Text != "Teste remoto: ação 123.")
                    throw new InvalidOperationException("Texto recebido difere do texto de teste esperado.");
            }
            var service = new WindowCaptureService();
            var result = await service.CaptureAsync(new WindowInteropHelper(window).Handle,
                path, TimeSpan.FromSeconds(12), null, CancellationToken.None);
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
            {
                success = true, result.Width, result.Height, result.Bytes,
                elapsedMs = result.Duration.TotalMilliseconds,
                inputVerified = testInput,
                test = "Janela sintetica: captura WGC" + (testInput ? " e entrada Unicode verificada." : ".")
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
            {
                success = false, error = ex.ToString(),
                sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { window.Close(); }
    }
}
