using System.Runtime.InteropServices;
using WindowProbe.Models;

namespace WindowProbe.Capture;

/// <summary>Compõe o cursor real apenas na janela sob o ponteiro, preservando os pixels capturados.</summary>
internal sealed class CursorOverlay : IDisposable
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint CursorShowing = 0x00000001;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const uint DiNormal = 0x0003;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    private byte[] scratch = Array.Empty<byte>();
    private IntPtr memoryDc;
    private IntPtr selectedBitmap;
    private IntPtr originalBitmap;
    private IntPtr dibBits;
    private int dibWidth;
    private int dibHeight;
    private bool disposed;

    /// <summary>
    /// Inclui o cursor visível sobre a janela ou monitor capturado.
    /// Devolve <paramref name="source"/> quando não há cursor para compor.
    /// </summary>
    public byte[] Compose(byte[] source, int width, int height, int stride, IntPtr hwnd, IntPtr monitorHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) || source.Length < checked(stride * height))
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensões ou stride BGRA inválidos.");
        if (monitorHandle == IntPtr.Zero && hwnd == IntPtr.Zero)
            throw new ArgumentException("É necessário informar a janela ou o monitor capturado.");

        if (!TryGetCursor(out CURSORINFO cursor) || (cursor.Flags & CursorShowing) == 0 || cursor.Cursor == IntPtr.Zero)
            return source;

        Rect bounds;
        if (monitorHandle != IntPtr.Zero)
        {
            if (!MonitorEnumerator.TryDescribe(monitorHandle, out MonitorInfo monitor)) return source;
            bounds = new Rect(monitor.X, monitor.Y, monitor.X + monitor.Width, monitor.Y + monitor.Height);
            if (!bounds.Contains(cursor.ScreenPosition.X, cursor.ScreenPosition.Y)) return source;
        }
        else
        {
            IntPtr hit = WindowFromPoint(cursor.ScreenPosition);
            if (hit == IntPtr.Zero || (hit != hwnd && GetAncestor(hit, GaRoot) != hwnd && GetAncestor(hit, GaRootOwner) != hwnd))
                return source;
            if (!TryGetWindowBounds(hwnd, out bounds)) return source;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0) return source;
        if (!GetIconInfo(cursor.Cursor, out ICONINFO icon)) return source;
        try
        {
            if (!TryGetCursorSize(icon, out int iconWidth, out int iconHeight)) return source;

            EnsureScratch(source.Length);
            Buffer.BlockCopy(source, 0, scratch, 0, source.Length);

            double scaleX = (double)width / bounds.Width;
            double scaleY = (double)height / bounds.Height;
            int hotX = (int)Math.Round((cursor.ScreenPosition.X - bounds.Left) * scaleX);
            int hotY = (int)Math.Round((cursor.ScreenPosition.Y - bounds.Top) * scaleY);
            int drawWidth = Math.Max(1, (int)Math.Round(iconWidth * scaleX));
            int drawHeight = Math.Max(1, (int)Math.Round(iconHeight * scaleY));
            int left = hotX - (int)Math.Round(icon.HotspotX * scaleX);
            int top = hotY - (int)Math.Round(icon.HotspotY * scaleY);
            int right = left + drawWidth;
            int bottom = top + drawHeight;
            int clipLeft = Math.Clamp(left, 0, width);
            int clipTop = Math.Clamp(top, 0, height);
            int clipRight = Math.Clamp(right, 0, width);
            int clipBottom = Math.Clamp(bottom, 0, height);
            if (clipLeft >= clipRight || clipTop >= clipBottom) return source;

            int roiWidth = clipRight - clipLeft;
            int roiHeight = clipBottom - clipTop;
            EnsureDib(roiWidth, roiHeight);
            CopyToDib(scratch, stride, clipLeft, clipTop, roiWidth, roiHeight);
            // shortcut: animações usam o primeiro quadro; evoluir se necessário reproduzir cursores .ani.
            if (!DrawIconEx(memoryDc, left - clipLeft, top - clipTop, cursor.Cursor,
                    drawWidth, drawHeight, 0, IntPtr.Zero, DiNormal) || !GdiFlush())
                return source;
            CopyFromDib(scratch, source, stride, clipLeft, clipTop, roiWidth, roiHeight);
            return scratch;
        }
        finally
        {
            if (icon.ColorBitmap != IntPtr.Zero) DeleteObject(icon.ColorBitmap);
            if (icon.MaskBitmap != IntPtr.Zero) DeleteObject(icon.MaskBitmap);
        }
    }

    private void EnsureScratch(int length)
    {
        if (scratch.Length != length) Array.Resize(ref scratch, length);
    }

    private void EnsureDib(int width, int height)
    {
        if (memoryDc != IntPtr.Zero && width == dibWidth && height == dibHeight) return;
        EnsureDeviceContexts();

        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb
            }
        };
        IntPtr bitmap = CreateDIBSection(memoryDc, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            throw new InvalidOperationException("CreateDIBSection falhou ao preparar o cursor.");
        }

        IntPtr previous = SelectObject(memoryDc, bitmap);
        if (previous == IntPtr.Zero || previous == new IntPtr(-1))
        {
            DeleteObject(bitmap);
            throw new InvalidOperationException("SelectObject falhou ao preparar o cursor.");
        }

        if (selectedBitmap == IntPtr.Zero) originalBitmap = previous;
        else DeleteObject(selectedBitmap);
        selectedBitmap = bitmap;
        dibBits = bits;
        dibWidth = width;
        dibHeight = height;
    }

    private void EnsureDeviceContexts()
    {
        if (memoryDc != IntPtr.Zero) return;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC falhou ao preparar o cursor.");
        try { memoryDc = CreateCompatibleDC(screenDc); }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }
        if (memoryDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC falhou ao preparar o cursor.");
    }

    private void CopyToDib(byte[] pixels, int stride, int x, int y, int width, int height)
    {
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
            Marshal.Copy(pixels, checked((y + row) * stride + x * 4), IntPtr.Add(dibBits, row * rowBytes), rowBytes);
    }

    private void CopyFromDib(byte[] pixels, byte[] source, int stride, int x, int y, int width, int height)
    {
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
        {
            Marshal.Copy(IntPtr.Add(dibBits, row * rowBytes), pixels, checked((y + row) * stride + x * 4), rowBytes);
            for (int column = 0; column < width; column++)
                pixels[(y + row) * stride + (x + column) * 4 + 3] = source[(y + row) * stride + (x + column) * 4 + 3];
        }
    }

    private static bool TryGetCursor(out CURSORINFO cursor)
    {
        cursor = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref cursor);
    }

    private static bool TryGetCursorSize(ICONINFO icon, out int width, out int height)
    {
        IntPtr bitmapHandle = icon.ColorBitmap != IntPtr.Zero ? icon.ColorBitmap : icon.MaskBitmap;
        if (bitmapHandle == IntPtr.Zero || GetObject(bitmapHandle, Marshal.SizeOf<BITMAP>(), out BITMAP bitmap) == 0)
        {
            width = height = 0;
            return false;
        }

        width = bitmap.Width;
        height = icon.ColorBitmap == IntPtr.Zero ? bitmap.Height / 2 : bitmap.Height;
        return width > 0 && height > 0;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out Rect bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<Rect>()) >= 0 && bounds.Width > 0 && bounds.Height > 0)
            return true;
        return GetWindowRect(hwnd, out bounds) && bounds.Width > 0 && bounds.Height > 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (memoryDc != IntPtr.Zero && originalBitmap != IntPtr.Zero) SelectObject(memoryDc, originalBitmap);
        if (selectedBitmap != IntPtr.Zero) DeleteObject(selectedBitmap);
        if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
        selectedBitmap = originalBitmap = dibBits = memoryDc = IntPtr.Zero;
        scratch = Array.Empty<byte>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public Rect(int left, int top, int right, int bottom) => (Left, Top, Right, Bottom) = (left, top, right, bottom);
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int Size; public uint Flags; public IntPtr Cursor; public POINT ScreenPosition; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX, HotspotY;
        public IntPtr MaskBitmap, ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Colors; }

    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetCursorInfo(ref CURSORINFO cursorInfo);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO iconInfo);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("gdi32.dll", ExactSpelling = true, EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern bool GdiFlush();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr flickerFreeBrush, uint flags);
}
