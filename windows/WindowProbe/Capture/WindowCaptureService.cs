using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WindowProbe.Interop;

namespace WindowProbe.Capture;

/// <summary>Prototipo de um frame: sem callbacks concorrentes nem transporte.</summary>
public sealed class WindowCaptureService
{
    public Task<CaptureResult> CaptureAsync(IntPtr windowHandle, string destinationPath,
        TimeSpan frameTimeout, IProgress<string>? log, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (frameTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frameTimeout));
        // Mantem o apartamento inicializado na mesma thread ate TODO o trabalho
        // terminar, inclusive as continuacoes assincronas. RoUninitialize e local
        // a thread e precisa estar pareado com RoInitialize.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(NativeMethods.RoInitialize(NativeMethods.RO_INIT_MULTITHREADED));
            try
            {
                return CaptureCoreAsync(windowHandle, destinationPath, frameTimeout, log, cancellationToken)
                    .GetAwaiter().GetResult();
            }
            finally { NativeMethods.RoUninitialize(); }
        }, cancellationToken);
    }

    private static async Task<CaptureResult> CaptureCoreAsync(IntPtr hwnd, string path,
        TimeSpan timeout, IProgress<string>? log, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (!GraphicsCaptureSession.IsSupported())
            throw new CaptureException("Captura indisponivel nesta sessao ou neste dispositivo.");
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            throw new CaptureException("A janela foi fechada, ocultada ou minimizada. Atualize a lista.");

        string fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fullPath += ".png";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        using var device = CaptureD3DDevice.Create(message => log?.Report(message));
        var item = GraphicsCaptureInterop.CreateItemForWindow(hwnd);
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0) throw new CaptureException("A janela nao tem tamanho valido.");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
        log?.Report($"Aguardando um frame {size.Width}x{size.Height}.");
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    await Task.Delay(16, token).ConfigureAwait(false);
                    continue;
                }
                // Redimensionar entre selecao e captura pode introduzir bordas
                // indefinidas. Para este prototipo, falhe e peca nova captura.
                if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                    throw new CaptureException("Janela redimensionada durante a captura. Tente novamente.");
                using var bitmap = await Windows.Graphics.Imaging.SoftwareBitmap
                    .CreateCopyFromSurfaceAsync(frame.Surface).AsTask(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // O frame fica vivo ate a copia GPU -> CPU terminar.
                long bytes = await PngWriter.SaveAsync(bitmap, fullPath, token).ConfigureAwait(false);
                log?.Report($"PNG salvo: {bitmap.PixelWidth}x{bitmap.PixelHeight}; {bytes} bytes.");
                return new CaptureResult(fullPath, bytes, bitmap.PixelWidth, bitmap.PixelHeight, watch.Elapsed);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CaptureTimeoutException($"Captura excedeu {timeout.TotalSeconds:0.#} segundos. A janela pode estar indisponivel ou protegida.");
        }
    }
}
