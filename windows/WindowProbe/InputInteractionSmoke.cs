using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WindowProbe.Remote;

namespace WindowProbe;

internal static class InputInteractionSmoke
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    internal static async Task<int> RunAsync(string report)
    {
        var editor = new TextBox { FontSize = 24, Text = "Teste de arraste e composição.", AcceptsReturn = true };
        var window = new Window { Width = 640, Height = 360, Topmost = true, Content = editor, Title = "RemoteDesktop — entrada sintética" };
        var other = new Window { Width = 200, Height = 100, Topmost = true, Title = "RemoteDesktop — troca de foco sintética" };
        var checks = new List<string>();
        RemoteInput? input = null;
        void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        void Apply(object command)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(command));
            string? rejection = input!.TryApply(json.RootElement);
            if (rejection is not null) throw new InvalidOperationException(rejection);
        }
        bool LeftDown() => (GetAsyncKeyState(1) & 0x8000) != 0;
        try
        {
            window.Show(); window.Activate(); editor.Focus(); await Task.Delay(400);
            input = new RemoteInput(new WindowInteropHelper(window).Handle);
            Apply(new { type = "pointer", action = "down", x = .2, y = .5, button = "left" });
            await Task.Delay(100);
            Require(LeftDown(), "Mouse pressionado no Windows");
            Apply(new { type = "pointer", action = "move", x = .8, y = .5, button = "left" });
            Apply(new { type = "pointer", action = "up", x = .8, y = .5, button = "left" });
            await Task.Delay(100);
            Require(!LeftDown(), "Mouse liberado após arraste");
            Apply(new { type = "key", key = "a", ctrl = true });
            Apply(new { type = "text", text = "ação, açúcar, bênção, avó, à, ü, ç" });
            await Task.Delay(150);
            Require(editor.Text == "ação, açúcar, bênção, avó, à, ü, ç", "Unicode PT-BR recebido no editor");
            Apply(new { type = "pointer", action = "down", x = .2, y = .5, button = "left" });
            bool rejected = false;
            try { Apply(new { type = "pointer", action = "move", x = 2.0, y = .5, button = "left" }); }
            catch (InvalidOperationException) { rejected = true; }
            await Task.Delay(100);
            Require(rejected && !LeftDown(), "Coordenada inválida rejeitada e botão liberado");
            Apply(new { type = "pointer", action = "down", x = .2, y = .5, button = "left" });
            await Task.Delay(150); // Processa o down antes de solicitar a troca de foco.
            other.Show(); other.Activate(); await Task.Delay(350);
            Require(GetForegroundWindow() == new WindowInteropHelper(other).Handle, "Foco realmente transferido à segunda janela");
            Require(!LeftDown(), "Watchdog libera mouse ao perder foco no PC");
            other.Close(); window.Activate(); editor.Focus(); await Task.Delay(150);
            Apply(new { type = "pointer", action = "down", x = .2, y = .5, button = "left" });
            input.Dispose(); input = null; await Task.Delay(100);
            Require(!LeftDown(), "Dispose libera botão de sessão encerrada");
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = true, checks, scope = "WPF sintético Topmost; não aprova arraste em apps reais nem teclado físico." }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = false, checks, error = ex.GetType().Name, detail = ex.Message }));
            return 1;
        }
        finally { input?.Dispose(); other.Close(); window.Close(); }
    }
}
