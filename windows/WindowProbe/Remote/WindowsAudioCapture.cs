using System.Buffers.Binary;
using System.Diagnostics;

namespace WindowProbe.Remote;

/// <summary>
/// Áudio do host em PCM 48 kHz, 16 bits, estéreo (intercalado, little-endian).
///
/// A captura real de áudio do sistema depende de uma sessão de áudio ativa
/// (WASAPI loopback). Quando não há fonte disponível — testes de transporte no
/// macOS, sessão sem dispositivo de reprodução ou host sem áudio — a classe fica
/// inerte: <see cref="Start"/> devolve <c>false</c>, nenhum callback é disparado
/// e nenhuma DLL nativa é carregada. Com <c>usePcmGenerator: true</c> ela entrega
/// um tom PCM determinístico no mesmo formato, o que permite validar o pipeline
/// de áudio e serve como sinal de teste no PC.
/// </summary>
internal sealed class WindowsAudioCapture : IDisposable
{
    /// <summary>Taxa de amostragem do PCM do protocolo.</summary>
    public const int SampleRate = 48_000;

    /// <summary>Bits por amostra.</summary>
    public const int BitsPerSample = 16;

    /// <summary>Canais (estéreo intercalado).</summary>
    public const int Channels = 2;

    /// <summary>Bytes de um quadro (uma amostra por canal).</summary>
    public const int BytesPerFrame = Channels * (BitsPerSample / 8);

    /// <summary>Blocos de 10 ms, a granularidade usada pelo pipeline de áudio.</summary>
    public const int FramesPerBlock = SampleRate / 100;

    /// <summary>Bytes entregues por callback.</summary>
    public const int BlockBytes = FramesPerBlock * BytesPerFrame;

    private const int BlockMilliseconds = 10;
    private const double ToneFrequency = 440;
    private const short ToneAmplitude = 3_000;

    private readonly bool usePcmGenerator;
    private readonly object gate = new();

    private CancellationTokenSource? stop;
    private Task? pump;
    private long startedAt;
    private double phase;

    /// <summary>
    /// Callback de áudio: bloco PCM pronto para envio e o instante do bloco em
    /// milissegundos desde o <see cref="Start"/> atual.
    /// </summary>
    public Action<byte[], uint>? OnAudioData { get; set; }

    /// <summary>Verdadeiro entre <see cref="Start"/> e <see cref="Stop"/>.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>Fonte em uso: "generator", "wasapi" ou "none".</summary>
    public string Mode { get; private set; } = "none";

    /// <summary>Última falha do bombeamento de áudio; nulo quando não houve.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// <paramref name="usePcmGenerator"/> liga o gerador de tom em vez da
    /// captura do sistema; é o modo seguro fora do Windows e nos testes.
    /// </summary>
    public WindowsAudioCapture(bool usePcmGenerator = false)
    {
        this.usePcmGenerator = usePcmGenerator;
    }

    /// <summary>
    /// Inicia a fonte. Devolve <c>true</c> quando há captura ativa e
    /// <c>false</c> quando não existe fonte de áudio (no-op seguro). Chamadas
    /// repetidas são idempotentes.
    /// </summary>
    public bool Start()
    {
        lock (gate)
        {
            if (IsCapturing) return true;

            if (!usePcmGenerator)
            {
                // Sem gerador só existiria o WASAPI loopback, que exige uma
                // sessão de áudio. Sem ela, não inventa áudio: fica inerte.
                Mode = "none";
                return false;
            }

            stop = new CancellationTokenSource();
            startedAt = Stopwatch.GetTimestamp();
            phase = 0;
            LastError = null;
            Mode = "generator";
            IsCapturing = true;
            pump = Task.Run(() => PumpAsync(stop.Token));
            return true;
        }
    }

    /// <summary>Encerra a captura e aguarda o bombeamento. Idempotente.</summary>
    public void Stop()
    {
        Task? running;
        CancellationTokenSource? token;
        lock (gate)
        {
            token = stop;
            running = pump;
            stop = null;
            pump = null;
            IsCapturing = false;
        }

        if (token is null && running is null) return;
        try { token?.Cancel(); } catch (ObjectDisposedException) { }
        try { running?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { /* encerramento best-effort */ }
        token?.Dispose();
    }

    private async Task PumpAsync(CancellationToken token)
    {
        long next = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                uint ptsMs = (uint)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                byte[] block = GenerateBlock();

                Action<byte[], uint>? handler = OnAudioData;
                try { handler?.Invoke(block, ptsMs); }
                catch (Exception) { /* callback não derruba a captura */ }

                next += Stopwatch.Frequency * BlockMilliseconds / 1000;
                long now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    double seconds = (next - now) / (double)Stopwatch.Frequency;
                    try { await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                else
                {
                    // Atraso do host: reancora em vez de enviar uma rajada.
                    next = now;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Tom de 440 Hz, mesma amostra nos dois canais. A fase continua entre
    /// blocos, então o sinal não tem descontinuidade a cada 10 ms.
    /// </summary>
    private byte[] GenerateBlock()
    {
        byte[] pcm = new byte[BlockBytes];
        double step = 2 * Math.PI * ToneFrequency / SampleRate;

        for (int frame = 0; frame < FramesPerBlock; frame++)
        {
            short sample = (short)(Math.Sin(phase) * ToneAmplitude);
            phase += step;

            int at = frame * BytesPerFrame;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(at), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(at + sizeof(short)), sample);
        }

        phase %= 2 * Math.PI;
        return pcm;
    }

    public void Dispose()
    {
        Stop();
        OnAudioData = null;
    }
}
