using System.Runtime.InteropServices;

namespace WindowProbe.Models;

/// <summary>Monitor físico do PC disponível para captura de tela inteira.</summary>
public sealed record MonitorInfo(long Id, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>
/// Lista os monitores ligados ao desktop. O identificador é o próprio
/// HMONITOR, que é o que `IGraphicsCaptureItemInterop.CreateForMonitor` usa.
/// </summary>
internal static class MonitorEnumerator
{
    private const uint MonitorInfoPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    /// <summary>Monitores atuais; o primário vem primeiro, depois da esquerda para a direita.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            if (TryDescribe(monitor, out MonitorInfo info)) result.Add(info);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result
            .OrderByDescending(monitor => monitor.Primary)
            .ThenBy(monitor => monitor.X)
            .ThenBy(monitor => monitor.Y)
            .ToArray();
    }

    /// <summary>Descreve um monitor pelo handle; falso se ele foi desconectado.</summary>
    public static bool TryDescribe(IntPtr monitor, out MonitorInfo info)
    {
        var raw = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref raw))
        {
            info = new MonitorInfo(0, "", 0, 0, 0, 0, false);
            return false;
        }

        int width = raw.Monitor.Right - raw.Monitor.Left;
        int height = raw.Monitor.Bottom - raw.Monitor.Top;
        info = new MonitorInfo(
            monitor.ToInt64(),
            raw.Device ?? "",
            raw.Monitor.Left,
            raw.Monitor.Top,
            width,
            height,
            (raw.Flags & MonitorInfoPrimary) != 0);
        return width > 0 && height > 0;
    }

    /// <summary>Procura um monitor atual pelo identificador publicado ao Mac.</summary>
    public static bool TryFind(long id, out MonitorInfo info)
    {
        foreach (MonitorInfo monitor in GetMonitors())
        {
            if (monitor.Id != id) continue;
            info = monitor;
            return true;
        }
        info = new MonitorInfo(0, "", 0, 0, 0, 0, false);
        return false;
    }
}
