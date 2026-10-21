using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WindowProbe.Models;
using WindowProbe.Remote;

namespace WindowProbe;

/// <summary>Teste opt-in, somente documento descartável identificado por título único.</summary>
internal static class RealAppSoak
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = Path.Combine(directory, "host-result.json");
        var session = Path.Combine(directory, "session.json");
        string name = "RemoteDesktop-soak-" + Guid.NewGuid().ToString("N") + ".txt";
        string document = Path.Combine(directory, name);
        RemoteServer? server = null;
        IntPtr target = IntPtr.Zero;
        bool ownsWindow = false;
        var elapsed = Stopwatch.StartNew();
        try
        {
            await File.WriteAllLinesAsync(document, Enumerable.Range(1, 200).Select(i => $"Linha {i}: ação, coração, açúcar, São Paulo — teste descartável."));
            var existing = WindowEnumerator.GetVisibleWindows().Select(w => w.Handle).ToHashSet();
            using var launched = Process.Start(new ProcessStartInfo("notepad.exe", '"' + document + '"') { UseShellExecute = true });
            for (int attempt = 0; attempt < 60 && target == IntPtr.Zero; attempt++)
            {
                await Task.Delay(500);
                target = WindowEnumerator.GetVisibleWindows().FirstOrDefault(w => w.Title.Contains(name, StringComparison.Ordinal))?.Handle ?? IntPtr.Zero;
            }
            if (target == IntPtr.Zero) throw new InvalidOperationException("Janela exclusiva do documento de teste não encontrada.");
            ownsWindow = !existing.Contains(target);
            if (!ownsWindow) throw new InvalidOperationException("Bloco de Notas reutilizou janela existente. Abra uma instância separada para o teste.");
            RemoteInput.Activate(target);
            uint dpi = GetDpiForWindow(target);
            string pin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var credentials = HostCredentials.Create(pin);
            server = new RemoteServer(target, IPAddress.Any, 0, credentials, certificate: null);
            server.Start();
            await File.WriteAllTextAsync(session, JsonSerializer.Serialize(new { host = "192.168.0.100", port = server.Port, fingerprint = server.Fingerprint, pin }));
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(32) && !File.Exists(Path.Combine(directory, "client-done")))
            {
                if (server.LastFailure is not null) throw new InvalidOperationException("Falha no transporte ou foco: " + server.LastFailure);
                await Task.Delay(500);
            }
            bool success = File.Exists(Path.Combine(directory, "client-done")) && File.ReadAllText(Path.Combine(directory, "client-done")) == "passed" && server.FramesSent > 0 && deadline.Elapsed >= TimeSpan.FromMinutes(30);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success, application = "Notepad", elapsedSeconds = elapsed.Elapsed.TotalSeconds, dpi, frames = server.FramesSent, inputs = server.InputsApplied, scope = "Captura/transporte/rolagem automatizados; não aprova foco alternado, DPI múltiplo ou uso manual." }));
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = false, elapsedSeconds = elapsed.Elapsed.TotalSeconds, error = ex.GetType().Name, detail = ex.Message }));
            return 1;
        }
        finally
        {
            if (server is not null) await server.StopAsync();
            if (File.Exists(session)) File.Delete(session);
            // Sem digitação: documento não é modificado; fecha apenas a janela identificada.
            if (target != IntPtr.Zero && ownsWindow) PostMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero);
            File.Delete(document);
        }
    }
}
