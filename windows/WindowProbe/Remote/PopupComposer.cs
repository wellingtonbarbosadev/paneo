using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowProbe.Capture;

namespace WindowProbe.Remote;

/// <summary>Retângulo em coordenadas de tela (pixels físicos).</summary>
internal readonly record struct ScreenRect(int L, int T, int R, int B)
{
    public int Width => R - L;
    public int Height => B - T;
}

/// <summary>
/// A captura WGC cobre apenas a janela autorizada, então menus de contexto e
/// outros popups (janelas separadas do mesmo processo) não aparecem no vídeo.
/// Este compositor copia esses popups da tela e os desenha sobre o quadro da
/// janela, expandindo o quadro para o retângulo envolvente enquanto houver
/// popups. O retângulo vigente fica disponível em <see cref="TryGetViewport"/>
/// para que a entrada remota mapeie as coordenadas normalizadas corretamente.
/// </summary>
internal sealed class PopupComposer
{
    private const int MaxCanvasSide = 4096;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(33);
    private static readonly ConcurrentDictionary<IntPtr, ScreenRect> Viewports = new();

    private readonly IntPtr hwnd;
    private readonly uint processId;
    private readonly List<ScreenRect> popups = [];
    private long lastScan;
    private byte[]? canvas;

    public PopupComposer(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        GetWindowThreadProcessId(hwnd, out processId);
    }

    /// <summary>
    /// Posição e tamanho da janela autorizada dentro do último quadro composto
    /// (em pixels do quadro). O Mac usa isso para manter a escala da janela
    /// estável quando o quadro cresce para caber um popup.
    /// </summary>
    public (int X, int Y, int Width, int Height) Base { get; private set; }

    /// <summary>Retângulo de tela coberto pelo último quadro enviado.</summary>
    public static bool TryGetViewport(IntPtr hwnd, out ScreenRect rect) => Viewports.TryGetValue(hwnd, out rect);

    /// <summary>Descarta o retângulo publicado ao encerrar a sessão.</summary>
    public void Release() => Viewports.TryRemove(hwnd, out _);

    /// <summary>Devolve o próprio quadro quando não há popups; senão, o quadro composto.</summary>
    public RawPixelFrame Compose(RawPixelFrame frame)
    {
        try
        {
            return ComposeCore(frame);
        }
        catch
        {
            // Melhor esforço: nunca derruba o vídeo por causa de popups.
            Viewports.TryRemove(hwnd, out _);
            Base = (0, 0, frame.Width, frame.Height);
            return frame;
        }
    }

    private RawPixelFrame ComposeCore(RawPixelFrame frame)
    {
        if (!TryGetBounds(hwnd, out ScreenRect bounds))
        {
            Viewports.TryRemove(hwnd, out _);
            return frame;
        }

        // O quadro WGC define o tamanho em pixels; ancora no canto da janela.
        var window = new ScreenRect(bounds.L, bounds.T, bounds.L + frame.Width, bounds.T + frame.Height);
        RefreshPopups(window);

        if (popups.Count == 0)
        {
            Viewports[hwnd] = bounds;
            Base = (0, 0, frame.Width, frame.Height);
            return frame;
        }

        int left = window.L, top = window.T, right = window.R, bottom = window.B;
        foreach (ScreenRect p in popups)
        {
            left = Math.Min(left, p.L);
            top = Math.Min(top, p.T);
            right = Math.Max(right, p.R);
            bottom = Math.Max(bottom, p.B);
        }

        int width = right - left, height = bottom - top;
        if (width > MaxCanvasSide || height > MaxCanvasSide)
        {
            Viewports[hwnd] = bounds;
            Base = (0, 0, frame.Width, frame.Height);
            return frame;
        }

        int stride = width * 4;
        int needed = stride * height;
        if (canvas is null || canvas.Length != needed) canvas = new byte[needed];
        else Array.Clear(canvas);

        int offsetX = window.L - left, offsetY = window.T - top;
        for (int y = 0; y < frame.Height; y++)
        {
            Buffer.BlockCopy(frame.Bgra, y * frame.Stride, canvas, (offsetY + y) * stride + offsetX * 4, frame.Width * 4);
        }

        foreach (ScreenRect p in popups) DrawPopup(p, left, top, stride);

        Viewports[hwnd] = new ScreenRect(left, top, right, bottom);
        Base = (offsetX, offsetY, frame.Width, frame.Height);
        return new RawPixelFrame(frame.Sequence, canvas, width, height, stride, frame.Elapsed);
    }

    private void RefreshPopups(ScreenRect window)
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastScan, now) < ScanInterval) return;
        lastScan = now;

        popups.Clear();
        EnumWindows((candidate, _) =>
        {
            if (candidate == hwnd || !IsWindowVisible(candidate) || IsIconic(candidate)) return true;
            if (GetWindowThreadProcessId(candidate, out uint pid) == 0 || pid != processId) return true;
            if (!IsPopupOf(candidate)) return true;
            if (!TryGetBounds(candidate, out ScreenRect rect) || rect.Width <= 0 || rect.Height <= 0) return true;
            if (rect.Width > MaxCanvasSide || rect.Height > MaxCanvasSide) return true;
            popups.Add(rect);
            return popups.Count < 8;
        }, IntPtr.Zero);
    }

    /// <summary>Menu clássico (#32768) ou janela cujo dono é a janela autorizada.</summary>
    private bool IsPopupOf(IntPtr candidate)
    {
        var name = new System.Text.StringBuilder(64);
        if (GetClassName(candidate, name, name.Capacity) > 0 && name.ToString() == "#32768") return true;
        IntPtr owner = GetWindow(candidate, 4); // GW_OWNER
        return owner == hwnd;
    }

    private void DrawPopup(ScreenRect rect, int originLeft, int originTop, int canvasStride)
    {
        int w = rect.Width, h = rect.Height;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return;
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(memory, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return;
            previous = SelectObject(memory, bitmap);
            // SRCCOPY | CAPTUREBLT: inclui janelas em camadas (menus com sombra).
            if (!BitBlt(memory, 0, 0, w, h, screen, rect.L, rect.T, 0x00CC0020 | 0x40000000)) return;

            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bits + y * w * 4, row, 0, row.Length);
                int destination = (rect.T - originTop + y) * canvasStride + (rect.L - originLeft) * 4;
                Buffer.BlockCopy(row, 0, canvas!, destination, row.Length);
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool TryGetBounds(IntPtr window, out ScreenRect rect)
    {
        if (DwmGetWindowAttribute(window, 9, out NativeRect r, Marshal.SizeOf<NativeRect>()) >= 0
            || GetWindowRect(window, out r))
        {
            rect = new ScreenRect(r.L, r.T, r.R, r.B);
            return true;
        }
        rect = default;
        return false;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out NativeRect rect, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop);
}
