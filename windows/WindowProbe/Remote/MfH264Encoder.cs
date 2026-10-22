using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WindowProbe.Remote;

/// <summary>
/// Encoder H.264 por Media Foundation (hardware quando existir, software como
/// fallback). Entrada em BGRA (memória do sistema), conversão NV12 na CPU para
/// evitar interop D3D11 adicional nesta primeira versão. Sem B-frames, CBR e
/// keyframe sob demanda (docs/VIDEO.md).
///
/// MFTs de hardware (ex.: "NVIDIA H.264 Encoder MFT") são assíncronos: depois
/// de ativar, o atributo MF_TRANSFORM_ASYNC é lido e, quando verdadeiro,
/// MF_TRANSFORM_ASYNC_UNLOCK é gravado antes de qualquer configuração. Os
/// eventos NeedInput/HaveOutput/DrainComplete são consumidos por uma thread
/// dedicada via IMFMediaEventGenerator, e ProcessInput/ProcessOutput só são
/// chamados quando o MFT pediu, conforme a documentação de MFTs assíncronas.
/// Todas as esperas têm prazo; nenhuma chamada de processamento bloqueia sem
/// limite.
/// </summary>
internal sealed class MfH264Encoder : IDisposable
{
    private const int MF_VERSION = 0x20070;
    private const uint MFSTARTUP_FULL = 0;
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    private const uint MF_E_TRANSFORM_NEED_MORE_INPUT = 0xC00D6D72;
    private const uint MF_E_TRANSFORM_STREAM_CHANGE = 0xC00D6D61;

    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (mftransform.h).</summary>
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    private const int MF_E_TRANSFORM_ASYNC_LOCKED = unchecked((int)0xC00D6D77);

    private const int MEError = 1;
    private const int METransformNeedInput = 601;
    private const int METransformHaveOutput = 602;
    private const int METransformDrainComplete = 603;
    private const int METransformMarker = 604;
    private const int METransformInputStreamStateChanged = 605;

    // Prazos curtos: um encoder saudável responde em microssegundos. Estourar
    // o prazo devolve erro ao chamador em vez de travar o pipeline.
    private const int InputEventTimeoutMs = 250;
    private const int OutputEventTimeoutMs = 2;
    private const int DrainTimeoutMs = 2_000;
    private const int PumpStopTimeoutMs = 1_000;

    private const int MFVideoInterlace_Progressive = 2;

    private readonly int width;
    private readonly int height;
    private readonly int sourceWidth;
    private readonly int sourceHeight;
    private readonly int fps;
    private readonly bool isAsync;
    private readonly object gate = new();
    private IMFTransform? transform;
    private IMFMediaEventGenerator? eventGenerator;
    private Thread? eventPump;
    private ManualResetEventSlim? pumpStop;
    private readonly object eventLock = new();
    private int needInputCount;
    private int haveOutputCount;
    private bool drainCompleteSignaled;
    private bool awaitingDrain;
    private int pumpError;
    private bool disposed;
    private long frameIndex;
    /// <summary>Buffer NV12 reaproveitado: evita alocar 3 MB por quadro.</summary>
    private byte[]? nv12Buffer;
    private uint outputStreamFlags;
    private uint outputBufferSize;

    private static string lastDiagnostic = "";
    public static string LastDiagnostic => lastDiagnostic;
    public string Name { get; } = "desconhecido";
    /// <summary>Largura realmente codificada (alinhada em 16).</summary>
    public int Width => width;
    /// <summary>Altura realmente codificada (alinhada em 16).</summary>
    public int Height => height;
    /// <summary>Largura da janela capturada, antes do alinhamento.</summary>
    public int SourceWidth => sourceWidth;
    /// <summary>Altura da janela capturada, antes do alinhamento.</summary>
    public int SourceHeight => sourceHeight;
    public bool IsHardware { get; }
    /// <summary>Verdadeiro quando o MFT escolhido processa por eventos (assíncrono).</summary>
    public bool IsAsync => isAsync;
    public int Bitrate { get; }

    public MfH264Encoder(int width, int height, int fps, int bitrate, bool preferHardware = true)
    {
        if (width <= 0 || height <= 0 || fps <= 0 || bitrate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Parâmetros de vídeo inválidos.");
        }

        this.sourceWidth = width & ~1;
        this.sourceHeight = height & ~1;
        this.width = Align16(this.sourceWidth);
        this.height = Align16(this.sourceHeight);
        this.fps = fps;
        Bitrate = bitrate;

        int hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (hr < 0) throw new InvalidOperationException("MFStartup falhou: 0x" + hr.ToString("X8"));

        try
        {
            var created = CreateTransform(this.width, this.height, fps, bitrate, preferHardware);
            transform = created.Transform;
            Name = created.Name;
            IsHardware = created.Hardware;
            isAsync = created.Async;

            if (transform.GetOutputStreamInfo(0, out MFTOutputStreamInfo info) >= 0)
            {
                outputStreamFlags = info.Flags;
                outputBufferSize = info.Size;
            }

            // A thread de eventos precisa existir antes do StartOfStream: o MFT
            // só envia NeedInput a partir daí, mas nada garante que ele espere
            // a primeira chamada de Encode para enfileirar o evento.
            if (isAsync) StartEventPump();

            transform.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero);
            int started = transform.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);
            if (started < 0) throw new InvalidOperationException("StartOfStream falhou: 0x" + started.ToString("X8"));
            RequestKeyframe();
        }
        catch
        {
            // Sem este balanço, uma tentativa falha deixa o MF iniciado e um
            // MFT pendurado a cada resize da janela.
            bool pumpStopped = StopEventPump();
            if (transform is not null && pumpStopped)
            {
                try { Marshal.ReleaseComObject(transform); } catch { }
                transform = null;
            }
            if (pumpStopped)
            {
                try { MFShutdown(); } catch { }
            }
            throw;
        }
    }

    /// <summary>Codifica um quadro BGRA. Devolve zero ou mais access units Annex-B.</summary>
    public List<EncodedAccessUnit> Encode(ReadOnlySpan<byte> bgra, int stride, long ptsMs)
        => Encode(bgra.ToArray(), stride, ptsMs);

    /// <summary>
    /// Codifica um quadro BGRA sem copiar o array de origem: quando o chamador
    /// já passa byte[] (caminho de pixels crus), evita alocar e copiar ~8 MB
    /// por quadro. O array não é retido além desta chamada.
    /// </summary>
    public List<EncodedAccessUnit> Encode(byte[] bgra, int stride, long ptsMs)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Assíncrono: recolhe saídas já prontas antes de esperar por mais
            // entrada. Isso evita que um HaveOutput pendente (e a consequente
            // espera do MFT por ProcessOutput) bloqueie o NeedInput seguinte.
            var collected = isAsync ? new List<EncodedAccessUnit>() : null;
            if (collected is not null) DrainAsyncOutputs(collected, 0);

            // Destino com folga para stride: 16 bytes por linha acima de 1088p.
            int nv12Size = width * height * 3 / 2;
            byte[] nv12 = nv12Buffer is { } cached && cached.Length >= nv12Size
                ? cached
                : nv12Buffer = new byte[nv12Size];
            BgraToNv12(bgra, stride, sourceWidth, sourceHeight, width, height, nv12);
            IMFMediaBuffer buffer = CreateBuffer(nv12.Length);
            try
            {
                buffer.Lock(out IntPtr pointer, out _, out _);
                try { Marshal.Copy(nv12, 0, pointer, nv12.Length); }
                finally { buffer.Unlock(); }

                buffer.SetCurrentLength((uint)nv12.Length);

                IntPtr samplePointer = CreateSample();
                try
                {
                    var sample = new MfSample(samplePointer);
                    IntPtr bufferUnknown = Marshal.GetComInterfaceForObject(buffer, typeof(IMFMediaBuffer));
                    try { sample.AddBuffer(bufferUnknown); }
                    finally { Marshal.Release(bufferUnknown); }
                    sample.SetSampleTime(ptsMs * 10_000);
                    sample.SetSampleDuration(10_000_000L / fps);
                    if (isAsync)
                    {
                        SubmitInputAsync(samplePointer, collected!);
                    }
                    else
                    {
                        int hr = transform!.ProcessInput(0, samplePointer, 0);
                        if (hr < 0) throw new InvalidOperationException(DescribeHResult("ProcessInput falhou", hr));
                    }
                }
                finally { Marshal.Release(samplePointer); }
            }
            finally { Marshal.ReleaseComObject(buffer); }

            frameIndex++;
            if (collected is null) return DrainOutputs();
            // Espera a saída deste quadro: quem chama codifica um quadro por
            // vez, e uma saída sinalizada depois do prazo ficaria órfã (o
            // contador persistiria, mas o access unit já teria sido perdido).
            if (WaitForHaveOutput(OutputEventTimeoutMs))
            {
                TakeHaveOutput();
                DoProcessOneOutput(collected, out bool streamChanged);
                if (streamChanged) NegotiateOutputType();
            }
            DrainAsyncOutputs(collected, 0);
            return collected;
        }
    }

    /// <summary>Entrega os quadros retidos pelo encoder ao finalizar a entrada.</summary>
    public List<EncodedAccessUnit> Complete()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!isAsync)
            {
                Marshal.ThrowExceptionForHR(transform!.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero));
                Marshal.ThrowExceptionForHR(transform.ProcessMessage(MFTMessageType.CommandDrain, IntPtr.Zero));
                return DrainOutputs();
            }

            var result = new List<EncodedAccessUnit>();
            ThrowIfPumpFailed();
            // Saídas já sinalizadas por Encode não podem ser perdidas ao fechar
            // a entrada: são colhidas antes de EOS/Drain.
            DrainAsyncOutputs(result, 0);

            Marshal.ThrowExceptionForHR(transform!.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero));
            // Só aceita DrainComplete gerado depois deste ponto: um evento
            // atrasado de um drain anterior não pode encerrar este.
            lock (eventLock)
            {
                awaitingDrain = true;
                drainCompleteSignaled = false;
            }
            Marshal.ThrowExceptionForHR(transform.ProcessMessage(MFTMessageType.CommandDrain, IntPtr.Zero));

            // Durante o drain o MFT só emite HaveOutput e, por último,
            // DrainComplete. As duas esperas compartilham o mesmo prazo total.
            long deadline = Environment.TickCount64 + DrainTimeoutMs;
            bool drained = false;
            while (true)
            {
                ThrowIfPumpFailed();
                if (TakeDrainComplete())
                {
                    drained = true;
                    break;
                }
                if (TakeHaveOutput())
                {
                    DoProcessOneOutput(result, out bool streamChanged);
                    if (streamChanged) NegotiateOutputType();
                    continue;
                }

                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) break;
                WaitForDrainProgress(remaining);
            }

            // Drain completo ainda pode ter HaveOutput na fila em MFTs que
            // emitem o evento e o status em ordens diferentes.
            DrainAsyncOutputs(result, 0);

            if (!drained)
                throw new TimeoutException($"Drain não concluiu em {DrainTimeoutMs} ms.");

            // Deixa o MFT reutilizável: flush limpa estado e StartOfStream
            // reabre a emissão de NeedInput (docs de MFT assíncrona).
            ResetEventState();
            Marshal.ThrowExceptionForHR(transform.ProcessMessage(MFTMessageType.CommandFlush, IntPtr.Zero));
            int restarted = transform.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);
            if (restarted < 0) throw new InvalidOperationException(DescribeHResult("StartOfStream falhou", restarted));
            return result;
        }
    }

    public void RequestKeyframe()
    {
        lock (gate)
        {
            if (disposed) return;
            // A API de codec é opcional: o MFT ignora se não suportar.
            if (transform is not ICodecAPI codecApi) return;
            // AVEncVideoForceKeyFrame exige ULONG (VT_UI4 = 19).
            SetVariant(codecApi, ref CodecApiGuids.AVEncVideoForceKeyFrame, 19 /* VT_UI4 */, 1);
        }
    }

    /// <summary>Drenagem síncrona: processa até o MFT devolver NEED_MORE_INPUT.</summary>
    private List<EncodedAccessUnit> DrainOutputs()
    {
        var result = new List<EncodedAccessUnit>();
        while (true)
        {
            bool produced = DoProcessOneOutput(result, out bool streamChanged);
            if (streamChanged) { NegotiateOutputType(); continue; }
            if (!produced) break;
        }
        return result;
    }

    /// <summary>
    /// Drenagem assíncrona: só chama ProcessOutput quando existe um evento
    /// HaveOutput pendente (exigência dos MFTs assíncronos). firstWaitMs é o
    /// prazo para o primeiro evento; os seguintes são colhidos sem espera.
    /// </summary>
    private void DrainAsyncOutputs(List<EncodedAccessUnit> result, int firstWaitMs)
    {
        int wait = firstWaitMs;
        while (true)
        {
            if (!WaitForHaveOutput(wait)) return;
            TakeHaveOutput();
            wait = 0;

            bool produced = DoProcessOneOutput(result, out bool streamChanged);
            if (streamChanged)
            {
                NegotiateOutputType();
                continue;
            }
            if (!produced)
            {
                // Evento obsoleto (ou E_UNEXPECTED): descarta e segue.
                continue;
            }
        }
    }

    /// <summary>
    /// Executa um ProcessOutput. Devolve true quando um access unit foi
    /// produzido; streamChanged sinaliza que o tipo de saída foi renegociado e
    /// o chamador deve tentar novamente.
    /// </summary>
    private bool DoProcessOneOutput(List<EncodedAccessUnit> result, out bool streamChanged)
    {
        streamChanged = false;

        // MFTs sem MFT_OUTPUT_STREAM_PROVIDES_SAMPLES exigem que o chamador
        // forneça o sample de saída; sem isso ProcessOutput devolve E_INVALIDARG.
        bool providesSamples = (outputStreamFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        IntPtr providedSample = IntPtr.Zero;
        IntPtr providedBuffer = IntPtr.Zero;
        var output = default(MFTOutputDataBuffer);
        if (!providesSamples)
        {
            uint size = Math.Max(outputBufferSize, 1024u * 1024u);
            if (MFCreateMemoryBuffer(size, out IMFMediaBuffer buffer) >= 0)
            {
                providedBuffer = Marshal.GetComInterfaceForObject(buffer, typeof(IMFMediaBuffer));
                Marshal.ReleaseComObject(buffer);
                providedSample = CreateSample();
                var wrapper = new MfSample(providedSample);
                wrapper.AddBuffer(providedBuffer);
                output.Sample = providedSample;
            }
        }

        int hr;
        try { hr = transform!.ProcessOutput(0, 1, ref output, out _); }
        finally { if (output.Events != IntPtr.Zero) Marshal.Release(output.Events); }

        if (hr == unchecked((int)MF_E_TRANSFORM_STREAM_CHANGE))
        {
            ReleaseProvidedOutput(providedSample, providedBuffer);
            streamChanged = true;
            return false;
        }

        // MFT assíncrono devolve E_UNEXPECTED se ProcessOutput for chamado sem
        // HaveOutput; tratamos como "nada pronto" em vez de derrubar a sessão.
        if (hr == unchecked((int)MF_E_TRANSFORM_NEED_MORE_INPUT) || (isAsync && hr == E_UNEXPECTED))
        {
            ReleaseProvidedOutput(providedSample, providedBuffer);
            return false;
        }

        if (hr < 0)
        {
            ReleaseProvidedOutput(providedSample, providedBuffer);
            throw new InvalidOperationException(DescribeHResult("ProcessOutput falhou", hr));
        }

        if (output.Sample == IntPtr.Zero)
        {
            ReleaseProvidedOutput(providedSample, providedBuffer);
            return false;
        }

        try
        {
            var sample = new MfSample(output.Sample);
            uint count = sample.BufferCount;
            if (count == 0) return false;
            IntPtr bufferPointer = sample.BufferAt(0);
            if (bufferPointer == IntPtr.Zero) return false;
            try
            {
                var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPointer);
                try
                {
                    buffer.Lock(out IntPtr pointer, out _, out uint current);
                    byte[] data = new byte[current];
                    Marshal.Copy(pointer, data, 0, (int)current);
                    buffer.Unlock();
                    // O atributo de clean point é opcional: a detecção pelo
                    // NAL IDR garante a marcação de keyframe mesmo sem ele.
                    bool keyframe = sample.IsKeyframe || ContainsIdr(data);
                    result.Add(new EncodedAccessUnit(data, keyframe, sample.SampleTime / 10_000));
                    return true;
                }
                finally { Marshal.ReleaseComObject(buffer); }
            }
            finally { Marshal.Release(bufferPointer); }
        }
        finally
        {
            // providedSample é o mesmo output.Sample quando fomos nós que o
            // criamos; liberar output.Sample cobre as duas origens. Se o MFT
            // devolveu outro sample, o nosso ainda precisa ser liberado.
            if (providedSample != IntPtr.Zero && output.Sample != providedSample) Marshal.Release(providedSample);
            if (output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
            if (providedBuffer != IntPtr.Zero) Marshal.Release(providedBuffer);
        }
    }

    private static void ReleaseProvidedOutput(IntPtr sample, IntPtr buffer)
    {
        if (sample != IntPtr.Zero) Marshal.Release(sample);
        if (buffer != IntPtr.Zero) Marshal.Release(buffer);
    }

    private void RefreshOutputStreamInfo()
    {
        if (transform!.GetOutputStreamInfo(0, out MFTOutputStreamInfo info) >= 0)
        {
            outputStreamFlags = info.Flags;
            outputBufferSize = info.Size;
        }
    }

    /// <summary>
    /// Envia um sample depois que o MFT pediu entrada. MF_E_NOTACCEPTING é
    /// tratado como pedido desatualizado e a espera recomeça, sempre com prazo.
    /// </summary>
    private void SubmitInputAsync(IntPtr sample, List<EncodedAccessUnit> collected)
    {
        long deadline = Environment.TickCount64 + InputEventTimeoutMs;
        while (true)
        {
            DrainAsyncOutputs(collected, 0);
            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining > 0 && !WaitForNeedInput(Math.Min(remaining, 2))) continue;
            if (remaining <= 0)
            {
                ThrowIfPumpFailed();
                throw new InvalidOperationException(
                    $"Encoder {Name} sem entrada em {InputEventTimeoutMs} ms após {frameIndex} quadros (saídas pendentes: {haveOutputCount}).");
            }

            int hr = transform!.ProcessInput(0, sample, 0);
            if (hr >= 0) return;
            if (hr == MF_E_NOTACCEPTING)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new InvalidOperationException("Encoder assíncrono recusou entrada: MF_E_NOTACCEPTING.");
                }
                continue;
            }
            throw new InvalidOperationException(DescribeHResult("ProcessInput falhou", hr));
        }
    }

    private string DescribeHResult(string prefix, int hr)
    {
        string text = prefix + ": 0x" + hr.ToString("X8");
        if (hr == MF_E_TRANSFORM_ASYNC_LOCKED)
        {
            text += " (MF_TRANSFORM_ASYNC_UNLOCK não foi aceito antes da configuração)";
        }
        return text;
    }

    // ---- eventos do MFT assíncrono ----

    private void StartEventPump()
    {
        if (transform is not IMFMediaEventGenerator generator)
        {
            throw new InvalidOperationException("MFT assíncrono não expôs IMFMediaEventGenerator.");
        }

        eventGenerator = generator;
        var stop = new ManualResetEventSlim(false);
        pumpStop = stop;
        var thread = new Thread(EventPumpLoop)
        {
            IsBackground = true,
            Name = "mf-h264-events",
        };
        eventPump = thread;
        thread.Start(stop);
    }

    /// <summary>
    /// Devolve true quando a thread de eventos terminou. Se ela não parar
    /// dentro do prazo, o chamador não deve liberar o MFT (evita use-after-free).
    /// </summary>
    private bool StopEventPump()
    {
        var stop = pumpStop;
        pumpStop = null;
        var thread = eventPump;
        eventPump = null;
        eventGenerator = null;
        if (stop is null) return true;

        stop.Set();
        // Shutdown acorda GetEvent(0); evita polling e sua granularidade de timer.
        if (transform is IMFShutdown shutdown) shutdown.Shutdown();
        bool stopped = thread is null || !thread.IsAlive || thread.Join(PumpStopTimeoutMs);
        if (stopped) stop.Dispose();
        return stopped;
    }

    private void EventPumpLoop(object? state)
    {
        var stop = (ManualResetEventSlim)state!;
        var generator = eventGenerator;
        if (generator is null) return;

        while (!stop.IsSet)
        {
            int hr;
            IMFMediaEvent? mediaEvent = null;
            try
            {
                hr = generator.GetEvent(0, out mediaEvent);
            }
            catch (Exception ex)
            {
                if (!stop.IsSet)
                {
                    SetPumpError(ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005));
                }
                return;
            }

            if (hr < 0)
            {
                if (!stop.IsSet) SetPumpError(hr);
                return;
            }
            if (mediaEvent is null) continue;

            try
            {
                mediaEvent.GetType(out int type);
                mediaEvent.GetStatus(out int status);
                switch (type)
                {
                    case METransformNeedInput:
                        lock (eventLock)
                        {
                            // Contador, não booleano: MFTs podem ter mais de um
                            // crédito de entrada em aberto.
                            needInputCount++;
                            Monitor.PulseAll(eventLock);
                        }
                        break;
                    case METransformHaveOutput:
                        lock (eventLock)
                        {
                            haveOutputCount++;
                            Monitor.PulseAll(eventLock);
                        }
                        break;
                    case METransformDrainComplete:
                        lock (eventLock)
                        {
                            if (awaitingDrain)
                            {
                                drainCompleteSignaled = true;
                                Monitor.PulseAll(eventLock);
                            }
                        }
                        break;
                    case METransformMarker:
                    case METransformInputStreamStateChanged:
                        // Sem uso no caminho de encode; o stream change real
                        // chega por MF_E_TRANSFORM_STREAM_CHANGE.
                        break;
                    case MEError:
                        SetPumpError(status != 0 ? status : unchecked((int)0x80004005));
                        return;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                // Exceção na thread de fundo derrubaria o processo; converte em
                // erro do encoder para o chamador decidir.
                if (!stop.IsSet)
                {
                    SetPumpError(ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005));
                }
                return;
            }
            finally
            {
                if (mediaEvent is not null) Marshal.ReleaseComObject(mediaEvent);
            }
        }
    }

    private void SetPumpError(int hr)
    {
        lock (eventLock)
        {
            pumpError = hr;
            Monitor.PulseAll(eventLock);
        }
    }

    private void ThrowIfPumpFailed()
    {
        int error = Volatile.Read(ref pumpError);
        if (error != 0)
        {
            throw new InvalidOperationException("Evento de erro do encoder assíncrono: 0x" + error.ToString("X8"));
        }
    }

    private bool WaitForNeedInput(int timeoutMs)
    {
        ThrowIfPumpFailed();
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        lock (eventLock)
        {
            while (needInputCount == 0)
            {
                ThrowIfPumpFailed();
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(eventLock, (int)remaining);
            }
            needInputCount--;
            return true;
        }
    }

    private bool WaitForHaveOutput(int timeoutMs)
    {
        ThrowIfPumpFailed();
        if (timeoutMs <= 0)
        {
            lock (eventLock) { return haveOutputCount > 0; }
        }

        long deadline = Environment.TickCount64 + timeoutMs;
        lock (eventLock)
        {
            while (haveOutputCount == 0)
            {
                ThrowIfPumpFailed();
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(eventLock, (int)remaining);
            }
            return true;
        }
    }

    /// <summary>Espera até haver HaveOutput ou DrainComplete (usado no drain).</summary>
    private void WaitForDrainProgress(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        lock (eventLock)
        {
            while (!drainCompleteSignaled && haveOutputCount == 0)
            {
                if (pumpError != 0) return;
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return;
                Monitor.Wait(eventLock, (int)remaining);
            }
        }
    }

    private bool TakeHaveOutput()
    {
        lock (eventLock)
        {
            if (haveOutputCount == 0) return false;
            haveOutputCount--;
            return true;
        }
    }

    private bool TakeDrainComplete()
    {
        lock (eventLock)
        {
            if (!drainCompleteSignaled) return false;
            drainCompleteSignaled = false;
            return true;
        }
    }

    private void ResetEventState()
    {
        lock (eventLock)
        {
            awaitingDrain = false;
            needInputCount = 0;
            haveOutputCount = 0;
            drainCompleteSignaled = false;
        }
    }

    /// <summary>
    /// Detecta um NAL IDR (tipo 5) num fluxo Annex-B. Sem isso, um MFT que
    /// não preenche o atributo de clean point nunca marcaria keyframe e o
    /// cliente descartaria todos os quadros.
    /// </summary>
    internal static bool ContainsIdr(ReadOnlySpan<byte> annexB)
    {
        int index = 0;
        while (index + 3 < annexB.Length)
        {
            // Procura o próximo start code (00 00 01 ou 00 00 00 01).
            if (annexB[index] == 0 && annexB[index + 1] == 0 && annexB[index + 2] == 1)
            {
                int payload = index + 3;
                if (payload < annexB.Length && (annexB[payload] & 0x1F) == 5) return true;
                index = payload;
                continue;
            }
            if (annexB[index] == 0 && annexB[index + 1] == 0 && annexB[index + 2] == 0
                && index + 3 < annexB.Length && annexB[index + 3] == 1)
            {
                int payload = index + 4;
                if (payload < annexB.Length && (annexB[payload] & 0x1F) == 5) return true;
                index = payload;
                continue;
            }
            index++;
        }
        return false;
    }

    private void NegotiateOutputType()
    {
        for (uint index = 0; ; index++)
        {
            int hr = transform!.GetOutputAvailableType(0, index, out IMFMediaType type);
            if (hr < 0) throw new InvalidOperationException("Encoder sem tipo de saída H.264.");
            try
            {
                bool isH264 = type.GetGUID(ref MfGuids.MF_MT_SUBTYPE, out Guid subtype) >= 0 && subtype == MfGuids.MFVideoFormat_H264;
                if (!isH264) continue;
                int set = transform.SetOutputType(0, type, 0);
                if (set < 0) throw new InvalidOperationException("SetOutputType falhou: 0x" + set.ToString("X8"));
                // O tamanho do buffer de saída pode mudar junto com o tipo.
                RefreshOutputStreamInfo();
                return;
            }
            finally { Marshal.ReleaseComObject(type); }
        }
    }

    private static void Configure(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        if (transform.GetAttributes(out IMFAttributes attributes) >= 0 && attributes is not null)
        {
            try { attributes.SetUINT32(ref MfGuids.MF_LOW_LATENCY, 1); }
            finally { Marshal.ReleaseComObject(attributes); }
        }

        // Encoders de H.264 costumam aceitar o tipo de saída antes da entrada.
        if (!TrySetOutputType(transform, width, height, fps, bitrate))
        {
            throw new InvalidOperationException("Encoder recusou o tipo de saída H.264. " + LastDiagnostic);
        }

        if (!TrySetInputType(transform, width, height, fps))
        {
            throw new InvalidOperationException("Encoder recusou entrada NV12. " + LastDiagnostic);
        }

        if (transform is ICodecAPI codecApi)
        {
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonRateControlMode, 19 /* VT_UI4 */, 0); // CBR
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonMeanBitRate, 19, (uint)bitrate);
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonQualityVsSpeed, 19, 50);
            SetVariant(codecApi, ref CodecApiGuids.AVEncMPVDefaultBPictureCount, 19, 0);
            SetVariant(codecApi, ref CodecApiGuids.AVEncCommonLowLatency, 11 /* VT_BOOL */, 1);
            SetVariant(codecApi, ref CodecApiGuids.CODECAPI_AVLowLatencyMode, 11 /* VT_BOOL */, 1);
            SetVariant(codecApi, ref CodecApiGuids.AVEncMPVGOPSize, 19 /* VT_UI4 */, 30);
        }
    }

    private static bool TrySetOutputType(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        for (uint index = 0; ; index++)
        {
            int hr = transform.GetOutputAvailableType(0, index, out IMFMediaType available);
            if (hr < 0) break;
            try
            {
                bool h264 = available.GetGUID(ref MfGuids.MF_MT_SUBTYPE, out Guid subtype) >= 0 && subtype == MfGuids.MFVideoFormat_H264;
                if (!h264) continue;

                available.SetUINT32(ref MfGuids.MF_MT_AVG_BITRATE, (uint)bitrate);
                MFSetAttributeSize(available, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
                MFSetAttributeRatio(available, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
                int set = transform.SetOutputType(0, available, 0);
                if (set >= 0)
                {
                    return true;
                }
                lastDiagnostic += $" Saída[{index}] recusada: 0x{set:X8}.";
            }
            finally { Marshal.ReleaseComObject(available); }
        }

        // Nenhum tipo disponível: monta do zero.
        int created = MFCreateMediaType(out IMFMediaType outputType);
        if (created < 0) return false;
        try
        {
            outputType.SetGUID(ref MfGuids.MF_MT_MAJOR_TYPE, ref MfGuids.MFMediaType_Video);
            outputType.SetGUID(ref MfGuids.MF_MT_SUBTYPE, ref MfGuids.MFVideoFormat_H264);
            outputType.SetUINT32(ref MfGuids.MF_MT_AVG_BITRATE, (uint)bitrate);
            outputType.SetUINT32(ref MfGuids.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            MFSetAttributeSize(outputType, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            MFSetAttributeRatio(outputType, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
            MFSetAttributeRatio(outputType, ref MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            int set = transform.SetOutputType(0, outputType, 0);
            if (set >= 0)
            {
                return true;
            }
            lastDiagnostic += $" Saída montada recusada: 0x{set:X8}.";
            return false;
        }
        finally { Marshal.ReleaseComObject(outputType); }
    }

    private static bool TrySetInputType(IMFTransform transform, int width, int height, int fps)
    {
        for (uint index = 0; ; index++)
        {
            int hr = transform.GetInputAvailableType(0, index, out IMFMediaType available);
            if (hr < 0) break;
            try
            {
                bool nv12 = available.GetGUID(ref MfGuids.MF_MT_SUBTYPE, out Guid subtype) >= 0 && subtype == MfGuids.MFVideoFormat_NV12;
                if (!nv12) continue;

                MFSetAttributeSize(available, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
                MFSetAttributeRatio(available, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
                available.SetUINT32(ref MfGuids.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                int set = transform.SetInputType(0, available, 0);
                if (set >= 0)
                {
                    return true;
                }
                lastDiagnostic += $" Entrada[{index}] recusada: 0x{set:X8}.";
            }
            finally { Marshal.ReleaseComObject(available); }
        }

        int created = MFCreateMediaType(out IMFMediaType inputType);
        if (created < 0) return false;
        try
        {
            inputType.SetGUID(ref MfGuids.MF_MT_MAJOR_TYPE, ref MfGuids.MFMediaType_Video);
            inputType.SetGUID(ref MfGuids.MF_MT_SUBTYPE, ref MfGuids.MFVideoFormat_NV12);
            inputType.SetUINT32(ref MfGuids.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            MFSetAttributeSize(inputType, ref MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            MFSetAttributeRatio(inputType, ref MfGuids.MF_MT_FRAME_RATE, (uint)fps, 1);
            MFSetAttributeRatio(inputType, ref MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            int set = transform.SetInputType(0, inputType, 0);
            if (set >= 0) return true;
            lastDiagnostic += $" Entrada montada recusada: 0x{set:X8}.";
            return false;
        }
        finally { Marshal.ReleaseComObject(inputType); }
    }

    private static void SetVariant(ICodecAPI api, ref Guid guid, ushort variantType, uint value)
    {
        IntPtr pointer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(new byte[24], 0, pointer, 24);
            Marshal.WriteInt16(pointer, 0, unchecked((short)variantType));
            if (variantType == 11) Marshal.WriteInt16(pointer, 8, -1); // VARIANT_TRUE
            else Marshal.WriteInt32(pointer, 8, unchecked((int)value));
            api.SetValue(ref guid, pointer);
        }
        catch (Exception) { /* melhor esforço */ }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static (IMFTransform Transform, string Name, bool Hardware, bool Async) CreateTransform(int width, int height, int fps, int bitrate, bool preferHardware)
    {
        lastDiagnostic = "";
        // Hardware (inclui MFTs assíncronos como o da NVIDIA), depois hardware
        // síncrono e, por último, o encoder de software da Microsoft.
        foreach (var (flags, hardware) in new[] {
            (MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, true),
            (MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, true),
            (MFT_ENUM_FLAG_SYNCMFT, false) })
        {
            if (hardware && !preferHardware) continue;
            var outputType = new MFTRegisterTypeInfo { MajorType = MfGuids.MFMediaType_Video, SubType = MfGuids.MFVideoFormat_H264 };
            int hr = MFTEnumEx(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, ref outputType, out IntPtr activates, out uint count);
            if (hr < 0 || count == 0) { lastDiagnostic += $" Enumerar 0x{flags:X}: hr=0x{hr:X8} count={count}."; if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates); continue; }
            try
            {
                for (uint index = 0; index < count; index++)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(activates, (int)index * IntPtr.Size);
                    if (pointer == IntPtr.Zero) continue;
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    try
                    {
                        string name = ReadName(activate);
                        // MF_LOW_LATENCY é lido por alguns encoders de hardware
                        // ainda na ativação; falha aqui é inofensiva.
                        _ = activate.SetUINT32(ref MfGuids.MF_LOW_LATENCY, 1);

                        var iid = typeof(IMFTransform).GUID;
                        int activated = activate.ActivateObject(ref iid, out IntPtr transformPointer);
                        if (activated < 0 || transformPointer == IntPtr.Zero) continue;
                        var transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPointer);
                        Marshal.Release(transformPointer);
                        if (TryConfigure(transform, width, height, fps, bitrate, out bool async, out string error))
                        {
                            return (transform, name, hardware, async);
                        }
                        lastDiagnostic += $" [{name}: {error}]";
                        Marshal.ReleaseComObject(transform);
                    }
                    finally { Marshal.ReleaseComObject(activate); }
                }
            }
            finally { Marshal.FreeCoTaskMem(activates); }
        }

        throw new InvalidOperationException("Nenhum encoder H.264 disponível." + lastDiagnostic);
    }

    /// <summary>Lista os encoders H.264 registrados (hardware e software).</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        foreach (var (flags, label) in new[] {
            (MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, "hardware"),
            (MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, "hardware-sync"),
            (MFT_ENUM_FLAG_SYNCMFT, "software") })
        {
            var outputType = new MFTRegisterTypeInfo { MajorType = MfGuids.MFMediaType_Video, SubType = MfGuids.MFVideoFormat_H264 };
            int hr = MFTEnumEx(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, ref outputType, out IntPtr activates, out uint count);
            lines.Add($"enum {label}: hr=0x{hr:X8} count={count}");
            if (hr < 0 || activates == IntPtr.Zero) { if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates); continue; }
            try
            {
                for (uint index = 0; index < count; index++)
                {
                    IntPtr pointer = Marshal.ReadIntPtr(activates, (int)index * IntPtr.Size);
                    if (pointer == IntPtr.Zero) continue;
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    try
                    {
                        var attributes = (IMFAttributes)activate;
                        bool async = attributes.GetUINT32(ref MfGuids.MF_TRANSFORM_ASYNC, out uint asyncValue) >= 0 && asyncValue != 0;
                        lines.Add($"{label}[{index}] \"{ReadName(activate)}\" async={async}");
                    }
                    finally { Marshal.ReleaseComObject(activate); }
                }
            }
            finally { Marshal.FreeCoTaskMem(activates); }
        }
        return lines;
    }

    /// <summary>
    /// Desbloqueia e testa o encoder. MF_TRANSFORM_ASYNC_UNLOCK precisa ser
    /// gravado no attribute store do MFT ativado antes de SetOutputType,
    /// SetInputType e ProcessMessage, senão todo método devolve
    /// MF_E_TRANSFORM_ASYNC_LOCKED (documentado em "Asynchronous MFTs").
    /// </summary>
    private static bool TryConfigure(IMFTransform transform, int width, int height, int fps, int bitrate, out bool async, out string error)
    {
        async = false;
        try
        {
            async = ReadAsyncFlag(transform);
            if (async)
            {
                if (!UnlockAsync(transform))
                {
                    throw new InvalidOperationException("MFT assíncrono recusou MF_TRANSFORM_ASYNC_UNLOCK.");
                }
                // Sem o gerador de eventos não há como dirigir o MFT pelo modelo
                // assíncrono; rejeita agora para tentar o próximo da lista.
                if (transform is not IMFMediaEventGenerator)
                {
                    throw new InvalidOperationException("MFT assíncrono não expõe IMFMediaEventGenerator.");
                }
            }

            Configure(transform, width, height, fps, bitrate);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Lê MF_TRANSFORM_ASYNC do MFT; o gerador de eventos é o sinal de reserva.</summary>
    private static bool ReadAsyncFlag(IMFTransform transform)
    {
        try
        {
            if (transform.GetAttributes(out IMFAttributes attributes) >= 0 && attributes is not null)
            {
                try
                {
                    if (attributes.GetUINT32(ref MfGuids.MF_TRANSFORM_ASYNC, out uint value) >= 0) return value != 0;
                }
                finally { Marshal.ReleaseComObject(attributes); }
            }
        }
        catch (Exception) { }

        // Síncronos não devem expor IMFMediaEventGenerator; assíncronos devem.
        return transform is IMFMediaEventGenerator;
    }

    private static bool UnlockAsync(IMFTransform transform)
    {
        try
        {
            if (transform.GetAttributes(out IMFAttributes attributes) >= 0 && attributes is not null)
            {
                try { return attributes.SetUINT32(ref MfGuids.MF_TRANSFORM_ASYNC_UNLOCK, 1) >= 0; }
                finally { Marshal.ReleaseComObject(attributes); }
            }
        }
        catch (Exception) { }
        return false;
    }

    private static string ReadName(IMFActivate activate)
    {
        var attributes = (IMFAttributes)activate;
        if (attributes.GetAllocatedString(ref MfGuids.MFT_FRIENDLY_NAME, out IntPtr pointer, out _) >= 0 && pointer != IntPtr.Zero)
        {
            try
            {
                string? name = Marshal.PtrToStringUni(pointer);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        return "encoder H.264";
    }

    private static IMFMediaBuffer CreateBuffer(int size)
    {
        int hr = MFCreateMemoryBuffer((uint)size, out IMFMediaBuffer buffer);
        if (hr < 0) throw new InvalidOperationException("MFCreateMemoryBuffer falhou: 0x" + hr.ToString("X8"));
        return buffer;
    }

    private static IntPtr CreateSample()
    {
        int hr = MFCreateSample(out IntPtr sample);
        if (hr < 0) throw new InvalidOperationException("MFCreateSample falhou: 0x" + hr.ToString("X8"));
        return sample;
    }

    private static int Align16(int value)
    {
        int aligned = (value + 15) & ~15;
        return aligned < 16 ? 16 : aligned;
    }

    /// <summary>BGRA → NV12 (I420 com UV intercalado) na CPU.</summary>
    internal static byte[] BgraToNv12(
        ReadOnlySpan<byte> bgra,
        int stride,
        int sourceWidth,
        int sourceHeight,
        int dstWidth,
        int dstHeight)
    {
        int ySize = dstWidth * dstHeight;
        byte[] nv12 = new byte[ySize + ySize / 2];
        BgraToNv12(bgra, stride, sourceWidth, sourceHeight, dstWidth, dstHeight, nv12);
        return nv12;
    }

    /// <summary>
    /// Converte para um NV12 já alocado (reaproveitado entre quadros: a
    /// conversão é a etapa mais cara do caminho de 60 FPS).
    /// </summary>
    internal static void BgraToNv12(
        ReadOnlySpan<byte> bgra,
        int stride,
        int sourceWidth,
        int sourceHeight,
        int dstWidth,
        int dstHeight,
        byte[] nv12) => BgraToNv12(bgra.ToArray(), stride, sourceWidth, sourceHeight, dstWidth, dstHeight, nv12);

    /// <summary>
    /// Conversão sobre um array. A cópia é paralela por faixas: `ReadOnlySpan`
    /// não pode ser capturado por lambda, e criar um array por quadro poria de
    /// volta o custo que a versão reaproveitada elimina.
    /// </summary>
    internal static void BgraToNv12(
        byte[] bgra,
        int stride,
        int sourceWidth,
        int sourceHeight,
        int dstWidth,
        int dstHeight,
        byte[] nv12)
    {
        if (stride < sourceWidth * 4) throw new ArgumentException("Stride inválido.", nameof(stride));
        if (dstWidth < sourceWidth || dstHeight < sourceHeight)
        {
            throw new ArgumentException("Dimensões de destino menores que a origem.", nameof(dstWidth));
        }

        int ySize = dstWidth * dstHeight;
        if (nv12.Length < ySize + ySize / 2) throw new ArgumentException("Destino NV12 pequeno.", nameof(nv12));

        // Linhas são independentes: faixas reduzem a etapa de CPU mais cara do
        // caminho de 60 FPS (8,8 ms -> ~2,2 ms em 1920x1088). Faixas com número
        // par de linhas alinham o plano UV, que amostra duas linhas por pixel
        // cromático.
        int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, dstHeight / 16));
        int rowsPerBand = (((dstHeight + bands - 1) / bands) + 1) & ~1;
        int bandCount = (dstHeight + rowsPerBand - 1) / rowsPerBand;
        int yPlane = ySize;

        void ConvertBand(int band)
        {
            int first = band * rowsPerBand;
            int last = Math.Min(dstHeight, first + rowsPerBand);
            for (int row = first; row < last; row++)
            {
                int sourceRow = Math.Min(row, sourceHeight - 1);
                int sourceOffset = sourceRow * stride;
                int yOffset = row * dstWidth;
                for (int col = 0; col < dstWidth; col++)
                {
                    int at = sourceOffset + Math.Min(col, sourceWidth - 1) * 4;
                    int b = bgra[at], g = bgra[at + 1], r = bgra[at + 2];
                    // BT.709 limitado. Amostras 0..255 dão 24..224 para luma:
                    // nenhum byte válido precisa ser preso, então o clamp sai
                    // sem ramificação.
                    int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    nv12[yOffset + col] = (byte)y;
                }
            }

            for (int row = first; row < last; row += 2)
            {
                int uvOffset = yPlane + (row / 2) * dstWidth;
                for (int col = 0; col < dstWidth; col += 2)
                {
                    int sumU = 0, sumV = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int sourceOffset = Math.Min(row + dy, sourceHeight - 1) * stride;
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int at = sourceOffset + Math.Min(col + dx, sourceWidth - 1) * 4;
                            int b = bgra[at], g = bgra[at + 1], r = bgra[at + 2];
                            sumU += ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                            sumV += ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                        }
                    }
                    nv12[uvOffset + col] = (byte)(sumU / 4);
                    nv12[uvOffset + col + 1] = (byte)(sumV / 4);
                }
            }
        }

        // Uma faixa só: já estamos na thread do pipeline e o custo do
        // Parallel.For seria maior que o ganho.
        if (bandCount == 1) { ConvertBand(0); return; }
        Parallel.For(0, bandCount, ConvertBand);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            // A thread de eventos precisa parar antes de qualquer Release: ela
            // está dentro de IMFMediaEventGenerator::GetEvent.
            if (transform is not null)
            {
                transform.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero);
                transform.ProcessMessage(MFTMessageType.NotifyEndStreaming, IntPtr.Zero);
            }
            bool pumpStopped = StopEventPump();
            try
            {
                if (transform is not null && pumpStopped)
                {
                    Marshal.ReleaseComObject(transform);
                    transform = null;
                }
            }
            catch (Exception) { }

            // Se a thread não parou, o MFT fica para o finalizador do RCW:
            // liberar agora permitiria acesso a memória já destruída.
            if (pumpStopped)
            {
                try { MFShutdown(); } catch (Exception) { }
            }
        }
    }

    // ---- interop mínimo de Media Foundation ----

    private enum MFTMessageType : uint
    {
        CommandFlush = 0x00000000,
        CommandDrain = 0x00000001,
        SetD3DManager = 0x00000002,
        DropSamples = 0x00000003,
        NotifyBeginStreaming = 0x10000000,
        NotifyEndStreaming = 0x10000001,
        NotifyEndOfStream = 0x10000002,
        NotifyStartOfStream = 0x10000003,
        NotifyReleaseResources = 0x10000004,
        NotifyReacquireResources = 0x10000005,
        Marker = 0x10000006,
        CommandSetOutputStreamProvider = 0x10000007,
        NotifySampleReady = 0x10000008,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFTOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint inputMin, out uint inputMax, out uint outputMin, out uint outputMax);
        [PreserveSig] int GetStreamCount(out uint inputCount, out uint outputCount);
        [PreserveSig] int GetStreamIDs(uint inputIdArraySize, [Out] uint[] inputIds, uint outputIdArraySize, [Out] uint[] outputIds);
        [PreserveSig] int GetInputStreamInfo(uint inputStreamId, out MFTInputStreamInfo info);
        [PreserveSig] int GetOutputStreamInfo(uint outputStreamId, out MFTOutputStreamInfo info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        [PreserveSig] int GetInputStreamAttributes(uint inputStreamId, out IMFAttributes attributes);
        [PreserveSig] int GetOutputStreamAttributes(uint outputStreamId, out IMFAttributes attributes);
        [PreserveSig] int DeleteInputStream(uint streamId);
        [PreserveSig] int AddInputStreams(uint streamCount, [In] uint[] streamIds);
        [PreserveSig] int GetInputAvailableType(uint inputStreamId, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(uint outputStreamId, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint inputStreamId, IMFMediaType type, uint flags);
        [PreserveSig] int SetOutputType(uint outputStreamId, IMFMediaType type, uint flags);
        [PreserveSig] int GetInputCurrentType(uint inputStreamId, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(uint outputStreamId, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(uint inputStreamId, out uint flags);
        [PreserveSig] int GetOutputStatus(out uint flags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint inputStreamId, IntPtr mediaEvent);
        [PreserveSig] int ProcessMessage(MFTMessageType message, IntPtr data);
        [PreserveSig] int ProcessInput(uint inputStreamId, IntPtr sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint outputBufferCount, ref MFTOutputDataBuffer outputData, out uint status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFTInputStreamInfo { public long MaxLatency; public uint Flags; public uint Size; public uint MaxLookahead; public uint Alignment; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFTOutputStreamInfo { public uint Flags; public uint Size; public uint Alignment; }

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType // herda IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType theirs, out uint flags);
        [PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr data);
        [PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr data);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    /// <summary>
    /// Fila de eventos out-of-band de MFTs assíncronos. A ordem dos métodos é
    /// a de mfobjects.h; BeginGetEvent/EndGetEvent ficam declarados como
    /// ponteiros para preservar os slots. GetEvent(0) bloqueia numa thread
    /// dedicada e IMFShutdown acorda essa espera no encerramento.
    /// </summary>
    [ComImport, Guid("97EC2EA4-0E42-4937-97AC-9D6D328824E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFShutdown
    {
        [PreserveSig] int Shutdown();
        [PreserveSig] int GetShutdownStatus(out int status);
    }

    [ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaEventGenerator
    {
        [PreserveSig] int GetEvent(uint flags, out IMFMediaEvent mediaEvent);
        [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
        [PreserveSig] int EndGetEvent(IntPtr result, out IMFMediaEvent mediaEvent);
        [PreserveSig] int QueueEvent(int type, ref Guid extendedType, int status, IntPtr value);
    }

    [ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaEvent // herda IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetExtendedType(out Guid extendedType);
        [PreserveSig] int GetStatus(out int status);
        [PreserveSig] int GetValue(IntPtr value);
    }

    /// <summary>
    /// Acesso ao IMFSample pela vtable. O objeto devolvido por MFCreateSample
    /// nesta build do Windows não aceita QI pelo IID clássico de IMFSample, mas
    /// a ordem dos métodos é a documentada (IMFAttributes ocupa 33 slots).
    /// </summary>
    internal readonly unsafe struct MfSample
    {
        private readonly IntPtr self;
        public MfSample(IntPtr pointer) { self = pointer; }

        private IntPtr Slot(int index) => *(*(IntPtr**)self + index);

        private int Call(int index, IntPtr argument)
        {
            if (self == IntPtr.Zero) return unchecked((int)0x80004003);
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(index);
            return method(self, argument);
        }

        private int CallLong(int index, long value)
        {
            if (self == IntPtr.Zero) return unchecked((int)0x80004003);
            var method = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(index);
            return method(self, value);
        }

        public int AddBuffer(IntPtr buffer) => Call(42, buffer);
        public int SetSampleTime(long value) => CallLong(36, value);
        public int SetSampleDuration(long value) => CallLong(38, value);
        public long SampleTime { get { if (self == IntPtr.Zero) return 0; long value = 0; var method = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(35); method(self, &value); return value; } }
        public uint BufferCount { get { if (self == IntPtr.Zero) return 0; uint value = 0; var method = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(39); method(self, &value); return value; } }
        public IntPtr BufferAt(uint index) { if (self == IntPtr.Zero) return IntPtr.Zero; IntPtr value = IntPtr.Zero; var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(40); method(self, index, &value); return value; }
        public bool IsKeyframe { get { if (self == IntPtr.Zero) return false; Guid key = MfGuids.MFSampleExtension_CleanPoint; uint value = 0; var method = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(7); method(self, &key, &value); return value != 0; } }
    }

    /// <summary>
    /// ICodecAPI na ordem documentada da vtable (icodecapi.h):
    /// IsSupported, IsModifiable, GetParameterRange, GetParameterValues,
    /// GetDefaultValue, GetValue, SetValue. Omitir GetDefaultValue faz SetValue
    /// chamar GetValue, impedindo a configuração correta das propriedades.
    /// </summary>
    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, IntPtr minValue, IntPtr maxValue, IntPtr steppingDelta);
        [PreserveSig] int GetParameterValues(ref Guid api, ref IntPtr values, ref uint valuesCount);
        [PreserveSig] int GetDefaultValue(ref Guid api, IntPtr value);
        [PreserveSig] int GetValue(ref Guid api, IntPtr value);
        [PreserveSig] int SetValue(ref Guid api, IntPtr value);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate // herda IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[] value, uint bufferSize, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buffer, uint bufferSize);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint items);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int ActivateObject(ref Guid iid, out IntPtr obj);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    private static class MfGuids
    {
        public static Guid MFT_CATEGORY_VIDEO_ENCODER = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
        public static Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        public static Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        public static Guid MFT_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
        public static Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
        public static Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        public static Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    }

    private static class CodecApiGuids
    {
        public static Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        public static Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
        public static Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
        public static Guid AVEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
        public static Guid CODECAPI_AVLowLatencyMode = new("9c387036-1440-4168-9f31-aab98966fd3a");
        public static Guid AVEncMPVGOPSize = new("95f31495-6dd9-4f6e-be15-309f69411c86");
        public static Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    /// <summary>MF_MT_FRAME_SIZE é um UINT64 empacotado (largura &lt;&lt; 32 | altura).</summary>
    private static void MFSetAttributeSize(IMFMediaType attributes, ref Guid key, uint width, uint height) =>
        attributes.SetUINT64(ref key, ((ulong)width << 32) | height);

    /// <summary>MF_MT_FRAME_RATE e assemelhados: numerador &lt;&lt; 32 | denominador.</summary>
    private static void MFSetAttributeRatio(IMFMediaType attributes, ref Guid key, uint numerator, uint denominator) =>
        attributes.SetUINT64(ref key, ((ulong)numerator << 32) | denominator);
    [StructLayout(LayoutKind.Sequential)]
    private struct MFTRegisterTypeInfo { public Guid MajorType; public Guid SubType; }

    // MFTEnumEx(GUID, UINT32, const MFT_REGISTER_TYPE_INFO* pInputType,
    //           const MFT_REGISTER_TYPE_INFO* pOutputType, IMFActivate***, UINT32*)
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFTEnumEx(Guid category, uint flags, IntPtr inputType, ref MFTRegisterTypeInfo outputType, out IntPtr activates, out uint count);
}

/// <summary>Access unit H.264 (Annex-B) pronto para o protocolo.</summary>
internal sealed record EncodedAccessUnit(byte[] Data, bool Keyframe, long PtsMs);
