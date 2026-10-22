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
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private IntPtr _d3dDevice;
    private IntPtr _immediateContext;
    private IntPtr _dxgiDevice;
    private IntPtr _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private uint _stagingFormat;
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

        Release(ref _stagingTexture);
        Release(ref _dxgiDevice);
        Release(ref _immediateContext);
        Release(ref _d3dDevice);
    }

    /// <summary>
    /// Copia a superfície WGC diretamente via Staging Texture D3D11 (~1.5 ms em
    /// 2560x1368 contra ~23 ms de SoftwareBitmap.CreateCopyFromSurfaceAsync).
    /// </summary>
    public unsafe bool TryCopySurfaceToBgra(
        IDirect3DSurface surface,
        ref byte[]? destination,
        out int width,
        out int height,
        out int stride)
    {
        width = 0;
        height = 0;
        stride = 0;
        if (_disposed || _d3dDevice == IntPtr.Zero || _immediateContext == IntPtr.Zero) return false;

        IntPtr accessPtr = IntPtr.Zero;
        IntPtr srcTexture = IntPtr.Zero;
        try
        {
            var objRef = WinRT.MarshalInspectable<object>.CreateMarshaler2(surface);
            try
            {
                IntPtr abi = objRef.GetAbi();
                if (abi == IntPtr.Zero) return false;
                Guid accessIid = DxgiInterfaceAccessIid;
                if (Marshal.QueryInterface(abi, in accessIid, out accessPtr) < 0 || accessPtr == IntPtr.Zero)
                {
                    return false;
                }
            }
            finally
            {
                objRef.Dispose();
            }

            Guid texIid = Texture2DIid;
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(IntPtr**)accessPtr + 3));
            if (getInterface(accessPtr, &texIid, &srcTexture) < 0 || srcTexture == IntPtr.Zero)
            {
                return false;
            }

            D3D11_TEXTURE2D_DESC desc;
            var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)(*(*(IntPtr**)srcTexture + 10));
            getDesc(srcTexture, &desc);
            if (desc.Width == 0 || desc.Height == 0) return false;

            if (_stagingTexture == IntPtr.Zero || _stagingWidth != desc.Width || _stagingHeight != desc.Height || _stagingFormat != desc.Format)
            {
                Release(ref _stagingTexture);
                var stagingDesc = new D3D11_TEXTURE2D_DESC
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Usage = 3, // D3D11_USAGE_STAGING
                    BindFlags = 0,
                    CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
                    MiscFlags = 0
                };
                var createTexture2D = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)(*(*(IntPtr**)_d3dDevice + 5));
                IntPtr created = IntPtr.Zero;
                if (createTexture2D(_d3dDevice, &stagingDesc, IntPtr.Zero, &created) < 0 || created == IntPtr.Zero)
                {
                    return false;
                }
                _stagingTexture = created;
                _stagingWidth = desc.Width;
                _stagingHeight = desc.Height;
                _stagingFormat = desc.Format;
            }

            var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)(*(*(IntPtr**)_immediateContext + 47));
            copyResource(_immediateContext, _stagingTexture, srcTexture);

            D3D11_MAPPED_SUBRESOURCE mapped;
            var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)(*(*(IntPtr**)_immediateContext + 14));
            if (map(_immediateContext, _stagingTexture, 0, 1 /* D3D11_MAP_READ */, 0, &mapped) < 0 || mapped.pData == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                width = (int)desc.Width;
                height = (int)desc.Height;
                stride = width * 4;
                int needed = stride * height;
                if (destination is null || destination.Length != needed)
                {
                    destination = new byte[needed];
                }

                fixed (byte* dstBase = destination)
                {
                    byte* srcBase = (byte*)mapped.pData;
                    int rowPitch = (int)mapped.RowPitch;
                    if (rowPitch == stride)
                    {
                        Buffer.MemoryCopy(srcBase, dstBase, needed, needed);
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(srcBase + (long)y * rowPitch, dstBase + (long)y * stride, stride, stride);
                        }
                    }
                }
                return true;
            }
            finally
            {
                var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)(*(*(IntPtr**)_immediateContext + 15));
                unmap(_immediateContext, _stagingTexture, 0);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(ref srcTexture);
            Release(ref accessPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
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
