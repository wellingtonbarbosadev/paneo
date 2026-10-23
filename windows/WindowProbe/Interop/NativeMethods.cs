using System.Runtime.InteropServices;
using System.Text;

namespace WindowProbe.Interop;

internal static class NativeMethods
{
    internal const int RO_INIT_MULTITHREADED = 1;
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    internal const int D3D_DRIVER_TYPE_HARDWARE = 1;
    internal const int D3D_DRIVER_TYPE_WARP = 5;
    internal const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    internal const uint D3D11_SDK_VERSION = 7;

    internal const int DWMWA_CLOAKED = 14;

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern void RoUninitialize();

    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr softwareRasterizer,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    internal static extern int GetWindowText(IntPtr hWnd, [Out] StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    internal static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        int copied = GetWindowText(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, copied).Trim() : string.Empty;
    }

    internal static bool IsWindowCloaked(IntPtr hwnd)
    {
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    internal static bool TryGetWindowSize(IntPtr hwnd, out int width, out int height)
    {
        if (GetWindowPlacement(hwnd, out WINDOWPLACEMENT placement))
        {
            width = Math.Max(0, placement.NormalPosition.Right - placement.NormalPosition.Left);
            height = Math.Max(0, placement.NormalPosition.Bottom - placement.NormalPosition.Top);
            if (width > 0 && height > 0) return true;
        }
        if (GetWindowRect(hwnd, out RECT rect))
        {
            width = Math.Max(0, rect.Right - rect.Left);
            height = Math.Max(0, rect.Bottom - rect.Top);
            return width > 0 && height > 0;
        }

        width = 0;
        height = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint Length, Flags, ShowCmd;
        public POINT MinPosition, MaxPosition;
        public RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
    private static extern bool GetWindowPlacementNative(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    private static bool GetWindowPlacement(IntPtr hwnd, out WINDOWPLACEMENT placement)
    {
        placement = new WINDOWPLACEMENT { Length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacementNative(hwnd, ref placement);
    }

    internal static bool CanResizeWindow(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -16).ToInt64() & 0x00040000) != 0;
    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, index)
        : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
}

/// <summary>
/// Interop clássico exposto pela fábrica de ativação de
/// Windows.Graphics.Capture.GraphicsCaptureItem.
/// </summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    [PreserveSig]
    int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

    [PreserveSig]
    int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
}
