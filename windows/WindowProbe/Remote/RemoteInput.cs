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

    /// <summary>
    /// Permite que o Mac mova o cursor do PC (hover). Desligado quando o
    /// usuário está usando o PC ao mesmo tempo e não quer o cursor saltando.
    /// </summary>
    public bool AllowHover { get; set; } = true;

    private bool allowHover => AllowHover;
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
    // Melhor esforço: alterna para a janela sem lançar exceção se o Windows negar o foco.
    private static void BringToForeground(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd);
        WaitForForeground(hwnd, 250);
        if (GetForegroundWindow() == hwnd) return;
        IntPtr current = GetForegroundWindow();
        uint currentThread = current == IntPtr.Zero ? 0 : GetWindowThreadProcessId(current, out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        bool attached = currentThread != 0 && targetThread != 0 && currentThread != targetThread
            && AttachThreadInput(currentThread, targetThread, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(currentThread, targetThread, false); }
        WaitForForeground(hwnd, 250);
    }
    private static void WaitForForeground(IntPtr hwnd, int milliseconds)
    {
        // Ativação entre threads é assíncrona: dê tempo à UI para processar o foco.
        for (int elapsed = 0; elapsed < milliseconds && GetForegroundWindow() != hwnd; elapsed += 10) Thread.Sleep(10);
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public uint Data2; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Key { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Payload { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Key Key; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Payload Value; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect rect, int size);

    private static Input Keyboard(ushort vk, bool up = false, ushort scan = 0, bool unicode = false) => new()
    { Type = 1, Value = new Payload { Key = new Key { Vk = vk, Scan = scan, Flags = (up ? 2u : 0u) | (unicode ? 4u : 0u) | (!unicode && vk is >= 0x21 and <= 0x2e ? 1u : 0u) } } };
    private static Input MouseEvent(uint flags, int data = 0, int data2 = 0) => new()
    { Type = 0, Value = new Payload { Mouse = new Mouse { Flags = flags, Data = unchecked((uint)data), Data2 = unchecked((uint)data2) } } };

    public string? TryApply(JsonElement command)
    {
        lock (inputGate)
        {
            try { ApplyCore(command); return null; }
            catch (InvalidOperationException ex) { ReleaseButton(); return ex.Message; }
            // Falha inesperada também não derruba a sessão: libera o botão e informa o Mac.
            catch { ReleaseButton(); return "Falha interna ao aplicar a entrada."; }
        }
    }
    /// <summary>
    /// Move o cursor do PC para o ponto indicado sem enviar evento de botão.
    /// Aceita janela visível e ponto não coberto; não exige foco.
    /// </summary>
    private void ApplyHover(JsonElement command)
    {
        // O usuário pode desligar o hover quando está usando o PC ao mesmo
        // tempo: o cursor do Windows passaria a saltar para o Mac.
        if (!allowHover) return;

        double x = command.GetProperty("x").GetDouble(), y = command.GetProperty("y").GetDouble();
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x >= 1 || y < 0 || y >= 1) return;
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return;

        int boundsResult = DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<Rect>());
        if (boundsResult < 0) return;
        var point = new Point { X = rect.L + (int)(x * (rect.R - rect.L)), Y = rect.T + (int)(y * (rect.B - rect.T)) };
        if (GetAncestor(WindowFromPoint(point), 2) != hwnd) return;
        MoveCursor(point.X, point.Y);
    }

    /// <summary>
    /// Move o cursor do PC. Usa SendInput absoluto (o mesmo caminho das outras
    /// entradas) e cai para SetCursorPos quando a área virtual não pôde ser
    /// obtida.
    /// </summary>
    private void MoveCursor(int x, int y)
    {
        if (GetSystemMetrics(76) is var virtualWidth && virtualWidth > 0
            && GetSystemMetrics(77) is var virtualHeight && virtualHeight > 0
            && GetSystemMetrics(78) is var virtualLeft && GetSystemMetrics(79) is var virtualTop)
        {
            int normalizedX = (int)Math.Round((x - virtualLeft) * 65535.0 / Math.Max(1, virtualWidth - 1));
            int normalizedY = (int)Math.Round((y - virtualTop) * 65535.0 / Math.Max(1, virtualHeight - 1));
            var move = MouseEvent(0x0001 | 0x8000, normalizedX, normalizedY);
            if (SendInput(1, [move], Marshal.SizeOf<Input>()) == 1) return;
        }

        SetCursorPos(x, y);
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
        if (type == "pointer" && action == "hover")
        {
            // Hover: move o ponteiro do PC sem clicar, sem exigir foco e sem
            // liberar botão nenhum. Recusa é silenciosa (não é entrada crítica).
            ApplyHover(command);
            return;
        }
        if (type == "pointer" && action == "move")
        {
            // Movimento só interessa como arraste de um botão já mantido por esta sessão.
            string? movedButton = command.TryGetProperty("button", out var pressed) && pressed.ValueKind == JsonValueKind.String
                ? pressed.GetString() : null;
            if (heldButton is null || movedButton != heldButton) return;
        }
        GetWindowThreadProcessId(hwnd, out uint currentPid);
        if (process.HasExited || currentPid != process.Id || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            throw new InvalidOperationException("Janela encerrada, oculta ou minimizada.");
        BringToForeground(hwnd);
        bool activates = type == "click" || type == "pointer" && action == "down";
        if (!activates && GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("Windows recusou o foco da janela autorizada. Clique primeiro na janela visível.");
        List<Input> events = [];
        if (type is "click" or "scroll" or "pointer")
        {
            double x = command.GetProperty("x").GetDouble(), y = command.GetProperty("y").GetDouble();
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x >= 1 || y < 0 || y >= 1)
                throw new InvalidOperationException("Coordenadas inválidas.");
            int boundsResult = DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<Rect>());
            if (boundsResult < 0) throw new InvalidOperationException("Não foi possível obter os limites da janela.");
            var point = new Point { X = rect.L + (int)(x * (rect.R - rect.L)), Y = rect.T + (int)(y * (rect.B - rect.T)) };
            if (GetAncestor(WindowFromPoint(point), 2) != hwnd)
                throw new InvalidOperationException("Ponto coberto por outra janela; entrada recusada.");
            if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Não foi possível posicionar o cursor.");
            if (type == "pointer")
            {
                string? button = command.GetProperty("button").GetString();
                if (button is not ("left" or "right")) throw new InvalidOperationException("Botão inválido.");
                if (action == "down")
                {
                    // Botão já mantido (por exemplo, clique duplicado): ignora sem enviar entrada.
                    if (heldButton is not null) return;
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
            // UIPI ou troca de desktop interrompeu o lote: libera o que ficou
            // pressionado. O filtro precisa pegar os eventos de PRESSIONAMENTO
            // (sem KEYUP / sem BUTTONUP) e reenviá-los como soltura, senão uma
            // tecla modificadora fica presa no PC.
            var releases = inputs
                .Where(i => i.Type == 1 && (i.Value.Key.Flags & 2) == 0
                         || i.Type == 0 && (i.Value.Mouse.Flags & 0x14) == 0)
                .Select(i => i.Type == 1
                    ? Keyboard(i.Value.Key.Vk, true)
                    : MouseUpFor(i.Value.Mouse.Flags))
                .ToArray();
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows recusou parte da entrada (possível UIPI).");
        }
    }

    /// <summary>Evento de soltura correspondente ao botão pressionado.</summary>
    private static Input MouseUpFor(uint mouseFlags)
    {
        uint button = (mouseFlags & 0x02) != 0 ? 0x0004u : 0x0002u; // RIGHTUP : LEFTUP
        return MouseEvent(button);
    }
}
