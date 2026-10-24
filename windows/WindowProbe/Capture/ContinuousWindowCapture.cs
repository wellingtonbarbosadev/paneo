using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
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

/// <summary>
/// Quadro cru em BGRA (memória do sistema), usado pelo encoder H.264.
/// No caminho contínuo, <see cref="RawPixelFrame.Bgra"/> pode ser o mesmo array
/// do quadro anterior reaproveitado; o consumidor não deve retê-lo além do
/// retorno do callback.
/// </summary>
public sealed record RawPixelFrame(
    long Sequence,
    byte[] Bgra,
    int Width,
    int Height,
    int Stride,
    TimeSpan Elapsed);

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

    /// <summary>
    /// Entrega os pixels em BGRA em vez de PNG. Usado pelo pipeline de vídeo
    /// H.264; evita codificar PNG apenas para decodificar de novo.
    /// </summary>
    public bool RawPixels { get; init; }

    /// <summary>Recebe cada quadro cru quando <see cref="RawPixels"/> está ligado.</summary>
    public Func<RawPixelFrame, CancellationToken, Task>? RawFrame { get; init; }

    /// <summary>
    /// Filtra antes da cópia GPU/CPU; evita copiar quadros fora da cadência.
    /// Recebe o instante monotônico do quadro.
    /// </summary>
    public Func<TimeSpan, bool>? AcceptFrame { get; init; }

    /// <summary>
    /// Diagnóstico opcional das provas de desempenho: recebe o tempo do
    /// callback de entrega. Nulo mantém o caminho do produto.
    /// </summary>
    public Action<TimeSpan, TimeSpan>? Timings { get; init; }

    /// <summary>
    /// HMONITOR a capturar em tela inteira. Quando diferente de zero, o
    /// handle de janela é ignorado e não há checagem de visibilidade.
    /// </summary>
    public IntPtr MonitorHandle { get; init; }
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
        if (windowHandle == IntPtr.Zero && options?.MonitorHandle is null or 0)
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

        bool monitorMode = options.MonitorHandle != IntPtr.Zero;
        if (!monitorMode && (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)))
        {
            throw new CaptureException("A janela foi fechada, ocultada ou minimizada. Atualize a lista.");
        }

        using var device = CaptureD3DDevice.Create(message => log?.Report(message));
        var item = monitorMode
            ? GraphicsCaptureInterop.CreateItemForMonitor(options.MonitorHandle)
            : GraphicsCaptureInterop.CreateItemForWindow(hwnd);

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
            // WGC duplica o cursor nas janelas encobertas; compomos apenas no alvo real.
            session.IsCursorCaptureEnabled = false;
            using var cursor = options.CaptureCursor ? new CursorOverlay() : null;

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
            // Buffer de pixels crus reaproveitado entre quadros (evita alocar
            // ~8 MB por frame; realoca apenas quando o tamanho muda).
            byte[]? pixels = null;
            int lastWidth = 0;
            int lastHeight = 0;
            int lastStride = 0;
            bool hasPixels = false;

            // Sinal de frame novo: substitui o polling cego por espera com prazo.
            // Um sinal pendente basta; o consumo na espera zera CurrentCount.
            var frameSignal = new SemaphoreSlim(0, 1);
            TypedEventHandler<Direct3D11CaptureFramePool, object> onFrameArrived = (_, _) =>
            {
                // Libera só quando não há sinal pendente: evita
                // SemaphoreFullException em corrida entre arrivals; descarta em
                // silêncio quando o fluxo já está encerrando.
                try { if (frameSignal.CurrentCount == 0) frameSignal.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { /* fluxo já encerrando */ }
            };

            pool.FrameArrived += onFrameArrived;
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

                    if (!monitorMode && NativeMethods.IsIconic(hwnd))
                    {
                        // Minimizar pausa o envio, mas não derruba a sessão: o
                        // contrato exige que o vídeo volte ao restaurar.
                        await Task.Delay(PollInterval, token).ConfigureAwait(false);
                        lastFrameAt = watch.Elapsed;
                        continue;
                    }

                    if (!monitorMode && !NativeMethods.IsWindowVisible(hwnd))
                    {
                        throw new CaptureException("A janela foi ocultada ou fechada durante a captura contínua.");
                    }

                    // No modo RawPixels, depois do primeiro quadro capturado,
                    // cadencia pelo pacer de 60 FPS antes de drenar o pool:
                    // assim nem descarta um frame novo por chegar fora de slot,
                    // nem cai para 1-15 FPS quando a janela está parada (ex.: Discord).
                    if (options.RawPixels && options.RawFrame is not null && hasPixels && options.AcceptFrame is not null)
                    {
                        if (!options.AcceptFrame(watch.Elapsed))
                        {
                            await frameSignal.WaitAsync(TimeSpan.FromMilliseconds(2), token).ConfigureAwait(false);
                            continue;
                        }
                    }

                    while (frameSignal.Wait(0)) { }
                    using Direct3D11CaptureFrame? frame = TakeNewestFrame(pool, ref coalesced);
                    if (frame is null)
                    {
                        if (hasPixels && pixels is not null
                            && (options.RawPixels && options.RawFrame is not null || cursor is not null))
                        {
                            TimeSpan repeatAt = watch.Elapsed;
                            long repeatStart = options.Timings is null ? 0 : Stopwatch.GetTimestamp();
                            var rawRepeat = new RawPixelFrame(
                                sequence + 1,
                                pixels,
                                lastWidth,
                                lastHeight,
                                lastStride,
                                repeatAt);
                            if (options.RawPixels && options.RawFrame is not null)
                            {
                                await options.RawFrame(rawRepeat with
                                {
                                    Bgra = cursor?.Compose(pixels, lastWidth, lastHeight, lastStride, hwnd, options.MonitorHandle) ?? pixels
                                }, token).ConfigureAwait(false);
                            }
                            else
                            {
                                await onFrame(await EncodePngWithCursorAsync(rawRepeat, cursor!, hwnd, options.MonitorHandle, token).ConfigureAwait(false), token).ConfigureAwait(false);
                                await frameSignal.WaitAsync(PollInterval, token).ConfigureAwait(false);
                            }
                            if (options.Timings is not null)
                            {
                                options.Timings(TimeSpan.Zero, Stopwatch.GetElapsedTime(repeatStart));
                            }
                            sequence++;
                            lastFrameAt = watch.Elapsed;
                            continue;
                        }

                        if (watch.Elapsed - lastFrameAt >= options.FrameTimeout)
                        {
                            throw new CaptureTimeoutException(
                                $"Nenhum frame em {options.FrameTimeout.TotalSeconds:0.#} segundos. " +
                                "A janela pode estar estática, indisponível ou protegida.");
                        }

                        await frameSignal.WaitAsync(PollInterval, token).ConfigureAwait(false);
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
                        hasPixels = false;
                        resizes++;
                        lastFrameAt = watch.Elapsed;
                        log?.Report($"Pool ajustado para o frame {frameSize.Width}x{frameSize.Height}.");
                        continue;
                    }

                    TimeSpan capturedAt = watch.Elapsed;
                    lastFrameAt = capturedAt;
                    if (!(options.RawPixels && options.RawFrame is not null && hasPixels)
                        && options.AcceptFrame is not null && !options.AcceptFrame(capturedAt))
                    {
                        continue;
                    }

                    long callbackStart = options.Timings is null ? 0 : Stopwatch.GetTimestamp();
                    if (options.RawPixels && options.RawFrame is not null)
                    {
                        long copyStart = Stopwatch.GetTimestamp();
                        if (!device.TryCopySurfaceToBgra(frame.Surface, ref pixels, out int rawWidth, out int rawHeight, out int rawStride)
                            || pixels is null)
                        {
                            using SoftwareBitmap fallbackBitmap = await SoftwareBitmap
                                .CreateCopyFromSurfaceAsync(frame.Surface).AsTask(token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            rawWidth = fallbackBitmap.PixelWidth;
                            rawHeight = fallbackBitmap.PixelHeight;
                            rawStride = rawWidth * 4;
                            int neededLength = rawStride * rawHeight;
                            if (pixels is null || pixels.Length != neededLength)
                            {
                                pixels = new byte[neededLength];
                            }
                            fallbackBitmap.CopyToBuffer(pixels.AsBuffer());
                        }
                        TimeSpan copyElapsed = Stopwatch.GetElapsedTime(copyStart);
                        frame.Dispose();

                        lastWidth = rawWidth;
                        lastHeight = rawHeight;
                        lastStride = rawStride;
                        hasPixels = true;

                        var raw = new RawPixelFrame(
                            sequence + 1,
                            cursor?.Compose(pixels, rawWidth, rawHeight, rawStride, hwnd, options.MonitorHandle) ?? pixels,
                            rawWidth,
                            rawHeight,
                            rawStride,
                            capturedAt);

                        await options.RawFrame(raw, token).ConfigureAwait(false);
                        if (options.Timings is not null)
                        {
                            options.Timings(copyElapsed, Stopwatch.GetElapsedTime(callbackStart));
                        }
                        sequence++;
                        lastFrameAt = watch.Elapsed;
                        continue;
                    }

                    using SoftwareBitmap bitmap = await SoftwareBitmap
                        .CreateCopyFromSurfaceAsync(frame.Surface).AsTask(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    CapturedFrame captured;
                    if (cursor is not null)
                    {
                        lastWidth = bitmap.PixelWidth;
                        lastHeight = bitmap.PixelHeight;
                        lastStride = lastWidth * 4;
                        int needed = checked(lastStride * lastHeight);
                        if (pixels is null || pixels.Length != needed) pixels = new byte[needed];
                        bitmap.CopyToBuffer(pixels.AsBuffer());
                        hasPixels = true;
                        captured = await EncodePngWithCursorAsync(new RawPixelFrame(sequence + 1, pixels,
                            lastWidth, lastHeight, lastStride, capturedAt), cursor, hwnd, options.MonitorHandle, token).ConfigureAwait(false);
                    }
                    else
                    {
                        var encodeWatch = Stopwatch.StartNew();
                        byte[] png = await PngWriter.EncodeAsync(bitmap, token).ConfigureAwait(false);
                        captured = new CapturedFrame(sequence + 1, png, bitmap.PixelWidth,
                            bitmap.PixelHeight, watch.Elapsed, encodeWatch.Elapsed);
                    }

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
                // Desassina antes de descartar: o handler tolera corrida com
                // um sinal em curso (ObjectDisposedException capturado nele).
                pool.FrameArrived -= onFrameArrived;
                frameSignal.Dispose();
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

    private static async Task<CapturedFrame> EncodePngWithCursorAsync(
        RawPixelFrame frame, CursorOverlay cursor, IntPtr hwnd, IntPtr monitor, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        byte[] composed = cursor.Compose(frame.Bgra, frame.Width, frame.Height, frame.Stride, hwnd, monitor);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(composed.AsBuffer(), BitmapPixelFormat.Bgra8,
            frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);
        byte[] png = await PngWriter.EncodeAsync(bitmap, token).ConfigureAwait(false);
        return new CapturedFrame(frame.Sequence, png, frame.Width, frame.Height, frame.Elapsed, watch.Elapsed);
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
