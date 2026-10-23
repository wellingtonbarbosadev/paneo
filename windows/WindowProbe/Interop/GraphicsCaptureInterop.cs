using System.Runtime.InteropServices;

namespace WindowProbe.Interop;

internal static class GraphicsCaptureInterop
{
    private const string GraphicsCaptureItemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    // IID de IGraphicsCaptureItem (interface padrão do runtime class).
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // IID de IGraphicsCaptureItemInterop.
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>
    /// Cria um GraphicsCaptureItem para uma janela de nível superior usando
    /// RoGetActivationFactory + IGraphicsCaptureItemInterop.CreateForWindow.
    /// </summary>
    public static Windows.Graphics.Capture.GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("Handle de janela inválido (NULL).", nameof(hwnd));
        }

        return CreateItem(hwnd, forMonitor: false);
    }

    /// <summary>Cria um GraphicsCaptureItem para um monitor inteiro (HMONITOR).</summary>
    public static Windows.Graphics.Capture.GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            throw new ArgumentException("Handle de monitor inválido (NULL).", nameof(monitor));
        }

        return CreateItem(monitor, forMonitor: true);
    }

    private static Windows.Graphics.Capture.GraphicsCaptureItem CreateItem(IntPtr handle, bool forMonitor)
    {

        IntPtr hstring = IntPtr.Zero;
        IntPtr factoryPointer = IntPtr.Zero;
        IntPtr itemPointer = IntPtr.Zero;

        try
        {
            int hr = NativeMethods.WindowsCreateString(
                GraphicsCaptureItemClassName,
                GraphicsCaptureItemClassName.Length,
                out hstring);
            Marshal.ThrowExceptionForHR(hr);

            Guid interopIid = InteropIid;
            hr = NativeMethods.RoGetActivationFactory(hstring, ref interopIid, out factoryPointer);
            Marshal.ThrowExceptionForHR(hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);

            Guid itemIid = GraphicsCaptureItemIid;
            hr = forMonitor
                ? interop.CreateForMonitor(handle, ref itemIid, out itemPointer)
                : interop.CreateForWindow(handle, ref itemIid, out itemPointer);
            Marshal.ThrowExceptionForHR(hr);

            if (itemPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateForWindow retornou um ponteiro nulo.");
            }

            // FromAbi cria o wrapper gerenciado e mantém sua própria referência;
            // o ponteiro ABI original é liberado no finally.
            return WinRT.MarshalInspectable<Windows.Graphics.Capture.GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != IntPtr.Zero)
            {
                Marshal.Release(itemPointer);
            }

            if (factoryPointer != IntPtr.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }
}
