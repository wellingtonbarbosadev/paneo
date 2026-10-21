using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WindowProbe.Interop;

namespace WindowProbe.Capture;

/// <summary>
/// Cria o dispositivo D3D11 (com BGRA) e o projeta para
/// Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice via
/// CreateDirect3D11DeviceFromDXGIDevice.
/// </summary>
internal sealed class CaptureD3DDevice : IDisposable
{
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private IntPtr _d3dDevice;
    private IntPtr _immediateContext;
    private IntPtr _dxgiDevice;
    private IDirect3DDevice? _winrtDevice;
    private bool _disposed;

    private CaptureD3DDevice(
        IDirect3DDevice winrtDevice,
        IntPtr d3dDevice,
        IntPtr immediateContext,
        IntPtr dxgiDevice,
        int featureLevel,
        bool isWarp)
    {
        _winrtDevice = winrtDevice;
        _d3dDevice = d3dDevice;
        _immediateContext = immediateContext;
        _dxgiDevice = dxgiDevice;
        FeatureLevel = FeatureLevelName(featureLevel);
        IsWarp = isWarp;
    }

    public IDirect3DDevice WinRtDevice =>
        _winrtDevice ?? throw new ObjectDisposedException(nameof(CaptureD3DDevice));

    public string FeatureLevel { get; }

    public bool IsWarp { get; }

    public static CaptureD3DDevice Create(Action<string>? log)
    {
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr dxgi = IntPtr.Zero;
        IntPtr winrtPointer = IntPtr.Zero;

        try
        {
            int hr = NativeMethods.D3D11CreateDevice(
                IntPtr.Zero,
                NativeMethods.D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero,
                0,
                NativeMethods.D3D11_SDK_VERSION,
                out device,
                out int featureLevel,
                out context);

            bool isWarp = false;

            if (hr < 0)
            {
                log?.Invoke($"D3D11 hardware indisponível (HRESULT 0x{hr:X8}); tentando WARP.");
                Release(ref device);
                Release(ref context);

                hr = NativeMethods.D3D11CreateDevice(
                    IntPtr.Zero,
                    NativeMethods.D3D_DRIVER_TYPE_WARP,
                    IntPtr.Zero,
                    NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero,
                    0,
                    NativeMethods.D3D11_SDK_VERSION,
                    out device,
                    out featureLevel,
                    out context);
                isWarp = true;
            }

            Marshal.ThrowExceptionForHR(hr);

            Guid dxgiIid = DxgiDeviceIid;
            hr = Marshal.QueryInterface(device, in dxgiIid, out dxgi);
            Marshal.ThrowExceptionForHR(hr);

            hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out winrtPointer);
            Marshal.ThrowExceptionForHR(hr);

            IDirect3DDevice winrtDevice =
                WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(winrtPointer);

            log?.Invoke($"Dispositivo D3D11 criado: feature level {FeatureLevelName(featureLevel)} (WARP={isWarp}).");

            var result = new CaptureD3DDevice(winrtDevice, device, context, dxgi, featureLevel, isWarp);

            device = IntPtr.Zero;
            context = IntPtr.Zero;
            dxgi = IntPtr.Zero;

            return result;
        }
        finally
        {
            if (winrtPointer != IntPtr.Zero)
            {
                Marshal.Release(winrtPointer);
            }

            Release(ref dxgi);
            Release(ref context);
            Release(ref device);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        WinRtInterop.CloseQuietly(_winrtDevice);
        _winrtDevice = null;

        Release(ref _dxgiDevice);
        Release(ref _immediateContext);
        Release(ref _d3dDevice);
    }

    private static string FeatureLevelName(int featureLevel) => featureLevel switch
    {
        0xC100 => "12_1",
        0xC000 => "12_0",
        0xB100 => "11_1",
        0xB000 => "11_0",
        0xA100 => "10_1",
        0xA000 => "10_0",
        0x9300 => "9_3",
        0x9200 => "9_2",
        0x9100 => "9_1",
        _ => $"0x{featureLevel:X}",
    };

    private static void Release(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.Release(pointer);
        pointer = IntPtr.Zero;
    }
}
