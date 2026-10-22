using System.Runtime.InteropServices;

namespace WindowProbe.Remote;

/// <summary>
/// Mantém tela e sistema acordados durante uma visualização autenticada e
/// sustenta o monitor ligado.
///
/// ES_DISPLAY_REQUIRED impede o desligamento por timeout, mas não traz de volta
/// um monitor que já entrou em stand-by: nesse estado o Windows deixa de
/// produzir quadros para o WGC e o cliente remoto fica na última imagem. Por
/// isso o lock envia SC_MONITORPOWER na abertura e reafirma o pedido de energia
/// periodicamente durante a sessão, para cobrir uma nova entrada em stand-by.
///
/// O retorno de SetThreadExecutionState é o estado ANTERIOR à chamada (não um
/// booleano): por isso Acquired reflete o pedido aceito, não o valor retornado.
/// </summary>
internal sealed class DisplayWakeLock : IDisposable
{
    internal const uint Continuous = 0x80000000;
    internal const uint SystemRequired = 0x00000001;
    internal const uint DisplayRequired = 0x00000002;
    internal const uint Active = Continuous | SystemRequired | DisplayRequired;

    /// <summary>WM_SYSCOMMAND.</summary>
    internal const uint SystemCommand = 0x0112;
    /// <summary>SC_MONITORPOWER: liga, baixa ou desliga o monitor.</summary>
    internal const int MonitorPower = 0xF170;
    /// <summary>Valor de lParam que liga o monitor.</summary>
    internal const int MonitorOn = -1;

    /// <summary>SMTO_ABORTIFHUNG: não espera janela que não atende.</summary>
    private const uint AbortIfHung = 0x0002;
    private const uint MonitorTimeoutMs = 100;

    /// <summary>Intervalo de reafirmação do pedido durante a sessão.</summary>
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly ManualResetEventSlim stop = new(false);
    private readonly Thread? worker;
    private int disposed;
    private int wakes;

    /// <summary>Pedido de energia efetuado (não é o código de retorno da API).</summary>
    public bool Acquired { get; private set; }
    /// <summary>Comando de ligar o monitor enviado.</summary>
    public bool MonitorWoken { get; private set; }
    /// <summary>Quantas vezes o monitor foi acordado (abertura + reafirmações).</summary>
    public int MonitorWakeCount => Volatile.Read(ref wakes);

    // As APIs são ligadas à thread: adquirir e liberar em continuations
    // diferentes do pool deixaria a solicitação ativa após a desconexão.
    internal DisplayWakeLock(
        Func<uint, uint>? setState = null,
        Action? wakeMonitor = null,
        TimeSpan? keepAlive = null)
    {
        if (setState is null && !OperatingSystem.IsWindows()) return;
        var apply = setState ?? SetThreadExecutionState;
        var wake = wakeMonitor ?? SendMonitorOn;
        var interval = keepAlive ?? KeepAliveInterval;
        using var ready = new ManualResetEventSlim(false);
        worker = new Thread(() =>
        {
            try
            {
                // O retorno é o estado anterior; 0 indica falha.
                Acquired = apply(Active) != 0;

                // Sem a tela ligada a captura por composição fica sem quadros:
                // pedir antes de qualquer espera de vídeo.
                if (Acquired)
                {
                    wake();
                    Interlocked.Increment(ref wakes);
                    MonitorWoken = true;
                }
            }
            catch
            {
                Acquired = false;
                MonitorWoken = false;
            }
            finally { ready.Set(); }

            try
            {
                // Sustenta a tela enquanto a sessão durar: reafirma o pedido e
                // reenvia o acordar do monitor, cobrindo nova entrada em
                // stand-by (o timeout de tela pode ser curto).
                while (!stop.Wait(interval))
                {
                    if (!Acquired) continue;
                    try
                    {
                        apply(Active);
                        wake();
                        Interlocked.Increment(ref wakes);
                    }
                    catch
                    {
                        // Uma falha pontual não encerra a sessão: a próxima
                        // rodada tenta de novo.
                    }
                }
            }
            finally { if (Acquired) apply(Continuous); }
        }) { IsBackground = true, Name = "remote-display-awake" };
        worker.Start();
        ready.Wait();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        worker?.Join();
        stop.Dispose();
    }

    /// <summary>
    /// Liga o monitor com SC_MONITORPOWER (-1) por HWND_BROADCAST. O envio usa
    /// prazo porque um destinatário travado prende SendMessage para sempre.
    /// Numa tela já acesa o comando não a apaga.
    /// </summary>
    private static void SendMonitorOn()
    {
        var broadcast = new IntPtr(0xFFFF);
        _ = SendMessageTimeout(broadcast, SystemCommand, new IntPtr(MonitorPower), new IntPtr(MonitorOn),
            AbortIfHung, MonitorTimeoutMs, out _);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
