using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Este prototipo precisa de uma sessao interativa no Windows.");
    return 1;
}

if (args.Length == 1 && args[0] == "--list")
{
    Native.EnumWindows((hwnd, _) =>
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return true;
        var title = new StringBuilder(256);
        Native.GetWindowText(hwnd, title, title.Capacity);
        if (title.Length != 0) Console.WriteLine($"0x{hwnd:X}  PID {pid}  {title}");
        return true;
    }, 0);
    return 0;
}

if (args.Length != 2 || args[0] != "--send-test" ||
    !long.TryParse(args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
        System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out var value) || value == 0)
{
    Console.WriteLine("Uso: InputProbe --list | --send-test <HWND hexadecimal>");
    Console.WriteLine("Use uma janela de Bloco de Notas vazia, sem dados pessoais.");
    return 2;
}

var target = new IntPtr(value);
if (!Native.IsWindow(target) || !Native.IsWindowVisible(target) || Native.IsIconic(target))
{
    Console.Error.WriteLine("Janela invalida, oculta ou minimizada. Liste as janelas novamente.");
    return 1;
}
Native.GetWindowThreadProcessId(target, out var originalPid);
// Keep a process handle so exit can be detected before injecting input.
using var process = Process.GetProcessById((int)originalPid);
Console.WriteLine($"Teste local de entrada na janela 0x{target:X} (PID {originalPid}).");
Console.WriteLine("Enviara SOMENTE o texto fixo: Teste remoto: ação 123.");
Console.WriteLine("A janela sera ativada. Confirme que e um editor de teste vazio.");
Console.Write("Digite ENVIAR para continuar: ");
if (Console.ReadLine() != "ENVIAR") return 0;

if (!TargetIsValid() || !Native.SetForegroundWindow(target))
{
    Console.Error.WriteLine("O Windows recusou a ativacao da janela. Nenhuma entrada enviada.");
    return 1;
}
Thread.Sleep(150);
foreach (char character in "Teste remoto: ação 123.")
{
    if (!TargetIsValid() || Native.GetForegroundWindow() != target)
    {
        Console.Error.WriteLine("Teste interrompido: janela fechada ou foco alterado.");
        return 1;
    }
    var events = new[] { Native.UnicodeKey(character, false), Native.UnicodeKey(character, true) };
    if (Native.SendInput((uint)events.Length, events, Marshal.SizeOf<Native.Input>()) != events.Length)
    {
        // Best effort key-up only if the same target still owns foreground focus.
        if (Native.GetForegroundWindow() == target)
        {
            var release = new[] { Native.UnicodeKey(character, true) };
            Native.SendInput(1, release, Marshal.SizeOf<Native.Input>());
        }
        Console.Error.WriteLine("O Windows recusou entrada. Verifique privilegios/UIPI. Teste encerrado.");
        return 1;
    }
}
Console.WriteLine("Entrada enviada; confira visualmente o texto no editor. Isso nao valida mouse ou streaming.");
return 0;

bool TargetIsValid()
{
    if (process.HasExited || !Native.IsWindow(target) || Native.IsIconic(target)) return false;
    Native.GetWindowThreadProcessId(target, out var currentPid);
    return currentPid == originalPid;
}

internal static class Native
{
    internal delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal UIntPtr ExtraInfo;
    }
    internal static Input UnicodeKey(char character, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = 0x0004u | (up ? 0x0002u : 0) } }
    };
}
