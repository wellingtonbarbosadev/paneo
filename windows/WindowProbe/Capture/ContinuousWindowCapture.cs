using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using WindowProbe.Interop;

namespace WindowProbe.Capture;

/// <summary>
/// Um frame PNG já codificado em memória. O array <see cref="Png"/> pertence ao
/// chamador; o fluxo não guarda referência depois que o callback retorna.
/// </summary>
public sealed record CapturedFrame(
    long Sequence,
    byte[] Png,
    int Width,
    int Height,
    TimeSpan Elapsed,
    TimeSpan EncodeDuration);

/// <summary>Resumo de uma sessão contínua encerrada.</summary>
public sealed record ContinuousCaptureSummary(
    long FramesDelivered,
    int FramesCoalesced,
    int Resizes,
    TimeSpan Duration);

/// <summary>Opções da captura contínua.</summary>
public sealed class ContinuousCaptureOptions
{
    /// <summary>
    /// Tempo máximo de espera por um novo frame. Janelas estáticas podem não
    /// produzir frames; nesse caso o fluxo termina com CaptureTimeoutException.
    /// O timeout cobre a espera por frame, não a duração do callback: um
    /// consumidor lento aplica backpressure em vez de derrubar a sessão.
    /// </summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Buffers do pool WGC. Este protótipo exige pelo menos 2.</summary>
    public int BufferCount { get; init; } = 2;

    /// <summary>Inclui o cursor no conteúdo capturado. Desligado por padrão.</summary>
    public bool CaptureCursor { get; init; }
}

/// <summary>
/// Captura contínua WGC de uma janela, reutilizando um único dispositivo D3D11,
/// pool e GraphicsCaptureSession durante toda a sessão.
///
/// Backpressure: o callback é aguardado antes de buscar o próximo frame. Frames
/// que chegam enquanto o callback está ocupado ficam limitados ao pool
/// (BufferCount buffers) e são coalescidos para o mais recente, nunca
/// enfileirados sem limite; os descartados são contados em
/// <see cref="ContinuousCaptureSummary.FramesCoalesced"/>.
/// O callback é sempre chamado sequencialmente, na thread de captura, e não deve
/// bloquear a thread que aguarda RunAsync.
/// Redimensionamentos recriam apenas o frame pool, mantendo a sessão.
/// </summary>
public static class ContinuousWindowCapture
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Executa a captura até <paramref name="cancellationToken"/> ser cancelado,
    /// a janela fechar, ocorrer um erro ou estourar o timeout de frame.
    /// </summary>
    /// <returns>
    /// Resumo da sessão quando o cancelamento externo encerra o fluxo de forma
    /// cooperativa. Erros, timeout e fechamento da janela lançam exceção.
    /// </returns>
    public static Task<ContinuousCaptureSummary> RunAsync(
        IntPtr windowHandle,
        Func<CapturedFrame, CancellationToken, Task> onFrame,
        ContinuousCaptureOptions? options,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Handle de janela inválido (NULL).", nameof(windowHandle));
        }

        var settings = options ?? new ContinuousCaptureOptions();
        if (settings.FrameTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FrameTimeout deve ser maior que zero.");
        }

        if (settings.BufferCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BufferCount deve ser 2 ou maior.");
        }

        // Mantém o apartamento inicializado na mesma thread durante toda a sessão.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(NativeMethods.RoInitialize(NativeMethods.RO_INIT_MULTITHREADED));
            try
            {
                return RunCoreAsync(windowHandle, onFrame, settings, log, cancellationToken)
                    .GetAwaiter().GetResult();
            }
            finally { NativeMethods.RoUninitialize(); }
        }, cancellationToken);
    }

    private static async Task<ContinuousCaptureSummary> RunCoreAsync(
        IntPtr hwnd,
        Func<CapturedFrame, CancellationToken, Task> onFrame,
        ContinuousCaptureOptions options,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new CaptureException("Captura indisponível nesta sessão ou neste dispositivo.");
        }

        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            throw new CaptureException("A janela foi fechada, ocultada ou minimizada. Atualize a lista.");
        }

        using var device = CaptureD3DDevice.Create(message => log?.Report(message));
        var item = GraphicsCaptureInterop.CreateItemForWindow(hwnd);

        try
        {
            SizeInt32 size = GetItemSize(item);
            if (size.Width <= 0 || size.Height <= 0)
            {
                throw new CaptureException("A janela não tem tamanho válido.");
            }

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device.WinRtDevice, PixelFormat, options.BufferCount, size);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = options.CaptureCursor;

            using var closedCts = new CancellationTokenSource();
            int closedFlag = 0;
            TypedEventHandler<GraphicsCaptureItem, object> onClosed = (_, _) =>
            {
                Interlocked.Exchange(ref closedFlag, 1);
                try { closedCts.Cancel(); }
                catch (ObjectDisposedException) { /* fluxo já encerrando */ }
            };

            long sequence = 0;
            int coalesced = 0;
            int resizes = 0;

            item.Closed += onClosed;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, closedCts.Token);
                CancellationToken token = linked.Token;

                session.StartCapture();
                log?.Report($"Captura contínua iniciada em {size.Width}x{size.Height}.");

                SizeInt32 currentSize = size;
                TimeSpan lastFrameAt = watch.Elapsed;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    if (NativeMethods.IsIconic(hwnd))
                    {
                        throw new CaptureException("A janela foi minimizada durante a captura contínua.");
                    }

                    if (!NativeMethods.IsWindowVisible(hwnd))
                    {
                        throw new CaptureException("A janela foi ocultada ou fechada durante a captura contínua.");
                    }

                    using Direct3D11CaptureFrame? frame = TakeNewestFrame(pool, ref coalesced);
                    if (frame is null)
                    {
                        if (watch.Elapsed - lastFrameAt >= options.FrameTimeout)
                        {
                            throw new CaptureTimeoutException(
                                $"Nenhum frame em {options.FrameTimeout.TotalSeconds:0.#} segundos. " +
                                "A janela pode estar estática, indisponível ou protegida.");
                        }

                        await Task.Delay(PollInterval, token).ConfigureAwait(false);
                        continue;
                    }

                    SizeInt32 frameSize = frame.ContentSize;
                    if (!IsValid(frameSize))
                    {
                        continue;
                    }

                    // Frame ainda no tamanho antigo logo após o resize: descarta e
                    // recria o pool para o tamanho informado pelo frame.
                    if (!SameSize(frameSize, currentSize))
                    {
                        frame.Dispose();
                        pool.Recreate(device.WinRtDevice, PixelFormat, options.BufferCount, frameSize);
                        currentSize = frameSize;
                        resizes++;
                        lastFrameAt = watch.Elapsed;
                        log?.Report($"Pool ajustado para o frame {frameSize.Width}x{frameSize.Height}.");
                        continue;
                    }

                    using SoftwareBitmap bitmap = await SoftwareBitmap
                        .CreateCopyFromSurfaceAsync(frame.Surface).AsTask(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    var encodeWatch = Stopwatch.StartNew();
                    byte[] png = await PngWriter.EncodeAsync(bitmap, token).ConfigureAwait(false);
                    TimeSpan encodeDuration = encodeWatch.Elapsed;

                    var captured = new CapturedFrame(
                        sequence + 1,
                        png,
                        bitmap.PixelWidth,
                        bitmap.PixelHeight,
                        watch.Elapsed,
                        encodeDuration);

                    // PNG já possui os bytes; libera GPU/bitmap antes de esperar a rede.
                    bitmap.Dispose();
                    frame.Dispose();
                    await onFrame(captured, token).ConfigureAwait(false);
                    sequence++;
                    lastFrameAt = watch.Elapsed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Encerramento cooperativo: o chamador cancelou; o resumo abaixo
                // informa quantos frames foram entregues e coalescidos.
                log?.Report($"Captura contínua encerrada após {sequence} frame(s).");
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref closedFlag) != 0)
            {
                throw new CaptureException("A janela foi fechada durante a captura contínua.");
            }
            finally
            {
                try { item.Closed -= onClosed; }
                catch { /* melhor esforço ao encerrar */ }
            }

            return new ContinuousCaptureSummary(sequence, coalesced, resizes, watch.Elapsed);
        }
        finally
        {
            // GraphicsCaptureItem não expõe IClosable; o wrapper é liberado aqui.
            WinRtInterop.CloseQuietly(item);
        }
    }

    /// <summary>
    /// Consome todos os frames disponíveis e devolve apenas o mais recente.
    /// Os anteriores são descartados e contados como coalescidos.
    /// </summary>
    private static Direct3D11CaptureFrame? TakeNewestFrame(
        Direct3D11CaptureFramePool pool, ref int coalesced)
    {
        Direct3D11CaptureFrame? newest = pool.TryGetNextFrame();
        if (newest is null)
        {
            return null;
        }

        // Limite finito mesmo se o produtor continuar entregando durante a drenagem.
        for (int drained = 0; drained < 8; drained++)
        {
            Direct3D11CaptureFrame? next = pool.TryGetNextFrame();
            if (next is null)
            {
                return newest;
            }

            newest.Dispose();
            newest = next;
            coalesced++;
        }
        return newest;
    }

    private static SizeInt32 GetItemSize(GraphicsCaptureItem item)
    {
        try
        {
            return item.Size;
        }
        catch (Exception ex)
        {
            throw new CaptureException("A janela foi fechada ou não está mais disponível.", ex);
        }
    }

    private static bool IsValid(SizeInt32 size) => size.Width > 0 && size.Height > 0;

    private static bool SameSize(SizeInt32 left, SizeInt32 right) =>
        left.Width == right.Width && left.Height == right.Height;
}
