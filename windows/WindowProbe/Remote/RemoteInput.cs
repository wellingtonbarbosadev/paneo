using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;

namespace WindowProbe.Remote;

/// <summary>Entrada atômica limitada à janela autorizada; nunca registra texto.</summary>
internal sealed class RemoteInput(IntPtr hwnd) : IDisposable
{
    private readonly Process process = TargetProcess(hwnd);
    private readonly object inputGate = new();
    private string? heldButton;
    private Timer? releaseTimer;
    private static Process TargetProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        var process = Process.GetProcessById((int)pid);
        _ = process.Handle;
        return process;
    }
    public void Dispose()
    {
        lock (inputGate) { ReleaseButton(); releaseTimer?.Dispose(); releaseTimer = null; process.Dispose(); }
    }
    private void ReleaseButton()
    {
        if (heldButton is null) return;
        var releases = new[] { MouseEvent(heldButton == "left" ? 4u : 16u) };
        SendInput(1, releases, Marshal.SizeOf<Input>());
        heldButton = null;
        releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }
    internal static void Activate(IntPtr window) => SetForegroundWindow(window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Key { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Payload { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Key Key; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Payload Value; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect rect, int size);

    private static Input Keyboard(ushort vk, bool up = false, ushort scan = 0, bool unicode = false) => new()
    { Type = 1, Value = new Payload { Key = new Key { Vk = vk, Scan = scan, Flags = (up ? 2u : 0u) | (unicode ? 4u : 0u) | (!unicode && vk is >= 0x21 and <= 0x2e ? 1u : 0u) } } };
    private static Input MouseEvent(uint flags, int data = 0) => new()
    { Type = 0, Value = new Payload { Mouse = new Mouse { Flags = flags, Data = unchecked((uint)data) } } };

    public void Apply(JsonElement command)
    {
        lock (inputGate)
        {
            try { ApplyCore(command); }
            catch { ReleaseButton(); throw; }
        }
    }
    private void ApplyCore(JsonElement command)
    {
        string? type = command.GetProperty("type").GetString();
        string? action = type == "pointer" ? command.GetProperty("action").GetString() : null;
        if (type == "pointer" && action == "up")
        {
            // Liberação não depende da janela ainda existir ou conservar o foco.
            ReleaseButton(); return;
        }
        GetWindowThreadProcessId(hwnd, out uint currentPid);
        if (process.HasExited || currentPid != process.Id || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            throw new InvalidOperationException("Janela encerrada, oculta ou minimizada.");
        SetForegroundWindow(hwnd);
        // Ativação entre threads é assíncrona: dê tempo à UI para processar o foco.
        for (int attempt = 0; attempt < 25 && GetForegroundWindow() != hwnd; attempt++) Thread.Sleep(10);
        bool activates = type == "click" || type == "pointer" && action == "down";
        if (!activates && GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("Windows recusou o foco da janela autorizada. Clique primeiro na janela visível.");
        List<Input> events = [];
        if (type is "click" or "scroll" or "pointer")
        {
            double x = command.GetProperty("x").GetDouble(), y = command.GetProperty("y").GetDouble();
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x >= 1 || y < 0 || y >= 1)
                throw new InvalidOperationException("Coordenadas inválidas.");
            Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<Rect>()));
            var point = new Point { X = rect.L + (int)(x * (rect.R - rect.L)), Y = rect.T + (int)(y * (rect.B - rect.T)) };
            if (GetAncestor(WindowFromPoint(point), 2) != hwnd)
                throw new InvalidOperationException("Ponto coberto por outra janela; entrada recusada.");
            if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Não foi possível posicionar o cursor.");
            if (type == "pointer")
            {
                string? button = command.GetProperty("button").GetString();
                if (button is not ("left" or "right")) throw new InvalidOperationException("Botão inválido.");
                if (action == "down" && heldButton is null)
                {
                    heldButton = button;
                    events.Add(MouseEvent(button == "left" ? 2u : 8u));
                    releaseTimer ??= new Timer(_ => {
                        lock (inputGate) { if (heldButton is not null && (GetForegroundWindow() != hwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd))) ReleaseButton(); }
                    });
                    releaseTimer.Change(100, 100);
                }
                else if (action == "move" && heldButton == button) return;
                else throw new InvalidOperationException("Sequência de arraste inválida.");
            }
            else if (type == "scroll")
            {
                int delta = command.GetProperty("delta").GetInt32();
                if (Math.Abs((long)delta) > 1200) throw new InvalidOperationException("Rolagem inválida.");
                events.Add(MouseEvent(0x800, delta));
            }
            else
            {
                string? button = command.GetProperty("button").GetString();
                if (button is not ("left" or "right")) throw new InvalidOperationException("Botão inválido.");
                events.Add(MouseEvent(button == "left" ? 2u : 8u));
                events.Add(MouseEvent(button == "left" ? 4u : 16u));
            }
        }
        else if (type == "text")
        {
            string value = command.GetProperty("text").GetString() ?? "";
            if (value.Length is 0 or > 256) throw new InvalidOperationException("Tamanho de texto inválido.");
            foreach (char c in value) { events.Add(Keyboard(0, scan: c, unicode: true)); events.Add(Keyboard(0, true, c, true)); }
        }
        else if (type == "key")
        {
            ushort key = command.GetProperty("key").GetString() switch
            {
                "enter" => 13, "tab" => 9, "backspace" => 8, "escape" => 27,
                "left" => 37, "right" => 39, "up" => 38, "down" => 40,
                "delete" => 46, "home" => 36, "end" => 35,
                "pageup" => 33, "pagedown" => 34, "insert" => 45,
                "f1" => 112, "f2" => 113, "f3" => 114, "f4" => 115, "f5" => 116, "f6" => 117,
                "f7" => 118, "f8" => 119, "f9" => 120, "f10" => 121, "f11" => 122, "f12" => 123,
                string letter when letter.Length == 1 && letter[0] is >= 'a' and <= 'z' => (ushort)char.ToUpperInvariant(letter[0]),
                _ => throw new InvalidOperationException("Tecla não suportada.")
            };
            var modifiers = new List<ushort>();
            foreach (var (name, vk) in new[] { ("ctrl", (ushort)17), ("alt", (ushort)18), ("shift", (ushort)16) })
                if (command.TryGetProperty(name, out var enabled) && enabled.GetBoolean()) modifiers.Add(vk);
            events.AddRange(modifiers.Select(vk => Keyboard(vk)));
            events.Add(Keyboard(key)); events.Add(Keyboard(key, true));
            events.AddRange(modifiers.AsEnumerable().Reverse().Select(vk => Keyboard(vk, true)));
        }
        else throw new InvalidOperationException("Comando desconhecido.");
        // Um clique visível na janela autorizada pode ativá-la naturalmente.
        // Texto/teclas nunca são enviados antes de verificar o foco.
        if (!activates && GetForegroundWindow() != hwnd) throw new InvalidOperationException("O foco mudou; entrada descartada.");
        var inputs = events.ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            // Libera apenas teclas/botões deste lote caso UIPI ou troca de desktop interrompa a entrada.
            var releases = inputs.Where(i => i.Type == 1 && (i.Value.Key.Flags & 2) != 0 || i.Type == 0 && (i.Value.Mouse.Flags & 20) != 0).ToArray();
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows recusou parte da entrada (possível UIPI).");
        }
    }
}
