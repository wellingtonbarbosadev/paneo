// O SDK do WPF não inclui System.IO nos implicit usings; Protocol.cs usa Stream
// sem importar o namespace. Um global using daqui mantém aquele arquivo intacto.
global using System.IO;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WindowProbe.Capture;
using WindowProbe.Models;

namespace WindowProbe.Remote;

/// <summary>
/// Host TLS do protocolo v2 (docs/PROTOCOL.md). Não depende de WPF, DPAPI nem
/// DLLs nativas: a captura e a entrada chegam por WindowProbe.Capture e
/// WindowProbe.Remote.RemoteInput (substituídos em tests/TransportTests).
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    private const string WindowSelectionUnavailable = "Seleção de janela pelo Mac ainda não está disponível.";
    private const int HandshakeTimeoutSeconds = 10;
    private const int IdleTimeoutSeconds = 10;

    private readonly IntPtr hwnd;
    private readonly X509Certificate2 certificate;
    private readonly bool ownsCertificate;
    private readonly Func<IReadOnlyList<WindowDescriptor>>? windowList;
    private readonly bool enableVideo;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly AuthRateLimiter rateLimiter = new();
    private readonly object credentialsGate = new();
    private readonly object sessionsGate = new();
    private readonly List<Task> sessions = [];
    private HostCredentials credentials;
    private int started;
    private int stopped;
    private int stopRequested;
    private long framesSent;
    private long inputsApplied;
    private volatile bool allowHover = true;

    /// <summary>Intervalo de verificação de mudanças do clipboard do Windows.</summary>
    private const int ClipboardPollMilliseconds = 300;
    private readonly WindowsClipboard clipboard;
    /// <summary>Último texto aplicado vindo do Mac, para não devolver eco.</summary>
    private string? lastRemoteClipboardText;

    /// <summary>Lançador injetável; em produção usa a lista segura do AppLauncher.</summary>
    private readonly Func<string, bool> launchApp;

    /// <summary>Receptor de arquivos enviados pelo Mac (injetável nos testes).</summary>
    private readonly FileReceiver fileReceiver;
    private readonly bool ownsFileReceiver;

    /// <summary>SHA-256 do certificado DER, em hexadecimal maiúsculo.</summary>
    public string Fingerprint { get; }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public string? LastFailure { get; private set; }
    public long FramesSent => Interlocked.Read(ref framesSent);
    /// <summary>Diagnóstico do último pipeline de vídeo (só o smoke lê).</summary>
    public object? VideoTimings { get; private set; }
    /// <summary>Diagnóstico do smoke: pedido de energia e de acordar o monitor.</summary>
    public bool? DisplayAwake { get; private set; }
    public bool? MonitorWoken { get; private set; }
    public string? VideoError => lastVideo?.Error;

    public bool VideoIsHardware => lastVideo?.LastHardware ?? false;

    private VideoChannel? lastVideo;
    public long InputsApplied => Interlocked.Read(ref inputsApplied);
    public Action<string>? Status { get; set; }

    /// <summary>Disparado quando o modo PIN cadastra um dispositivo (persistir sem bloquear).</summary>
    public event EventHandler<HostCredentials>? CredentialsChanged;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public RemoteServer(
        IntPtr window,
        IPAddress address,
        int port,
        HostCredentials credentials,
        X509Certificate2? certificate = null,
        Func<IReadOnlyList<WindowDescriptor>>? windowList = null,
        bool enableVideo = true,
        WindowsClipboard? clipboard = null,
        Func<string, bool>? appLauncher = null,
        FileReceiver? fileReceiver = null)
    {
        hwnd = window;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.windowList = windowList;
        this.enableVideo = enableVideo;
        this.clipboard = clipboard ?? WindowsClipboard.Shared;
        launchApp = appLauncher ?? AppLauncher.Launch;
        if (fileReceiver is null)
        {
            this.fileReceiver = new FileReceiver();
            ownsFileReceiver = true;
        }
        else
        {
            this.fileReceiver = fileReceiver;
        }
        ArgumentNullException.ThrowIfNull(address);

        if (certificate is null)
        {
            this.certificate = CreateEphemeralCertificate();
            ownsCertificate = true;
        }
        else
        {
            this.certificate = certificate;
        }

        Fingerprint = this.certificate.GetCertHashString(HashAlgorithmName.SHA256);
        listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
    }

    /// <summary>
    /// Troca as credenciais em uso (rotação de PIN). Sem isso o servidor
    /// continuaria aceitando o PIN antigo durante todo o compartilhamento.
    /// </summary>
    public void UpdateCredentials(HostCredentials updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (credentialsGate) credentials = updated;
    }

    /// <summary>Permite (ou bloqueia) que o Mac mova o cursor do PC.</summary>
    public bool AllowHover
    {
        get => allowHover;
        set => allowHover = value;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Servidor já iniciado.");
        if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException(nameof(RemoteServer));
        listener.Start();
        Completion = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (stop.IsCancellationRequested) { break; }

                client.NoDelay = true;
                Task session = HandleSafeAsync(client);
                lock (sessionsGate) sessions.Add(session);
                _ = session.ContinueWith(
                    completed => { lock (sessionsGate) sessions.Remove(completed); },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleSafeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await HandleAsync(client, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LastFailure ??= ex.GetType().Name + ":" + ex.HResult + "/" +
                    ex.InnerException?.GetType().Name + ":" + ex.InnerException?.HResult;
                Status?.Invoke("Conexão encerrada ou recusada. Aguardando Mac.");
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken shutdown)
    {
        LastFailure = null;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        // O handshake TLS também tem prazo: sem isso, um cliente que abre o TCP
        // e não fala TLS segura a tarefa e o socket indefinidamente.
        session.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds + 10));
        using var tls = new SslStream(client.GetStream(), false);

        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, session.Token).ConfigureAwait(false);

        HandshakeResult? handshake;
        try
        {
            handshake = await PerformHandshakeAsync(tls, client, session.Token).ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
            await TrySendErrorAsync(tls, "Mensagem inválida.", session.Token).ConfigureAwait(false);
            return;
        }

        if (handshake is null) return;
        // Após autenticar, cada leitura usa seu próprio prazo de heartbeat.
        session.CancelAfter(Timeout.InfiniteTimeSpan);

        var ready = new Dictionary<string, object?>
        {
            ["type"] = "ready",
            ["version"] = ProtocolV2.Version,
            ["role"] = handshake.Role,
            ["host"] = Environment.MachineName
        };
        if (handshake.DeviceToken is not null) ready["deviceToken"] = handshake.DeviceToken;

        await SendControlAsync(tls, ready, session.Token).ConfigureAwait(false);

        // O clipboard é acompanhado em qualquer sessão autenticada (controle ou
        // visualização): mudanças locais viram mensagens de controle assíncronas,
        // serializadas pelo gate de escrita da conexão.
        using var clipboardStop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        Task clipboardWatch = WatchClipboardAsync(tls, clipboardStop.Token);
        try
        {
            if (handshake.Role == "control")
            {
                Status?.Invoke("Mac autenticado. Lista de janelas disponível.");
                await RunControlAsync(tls, session).ConfigureAwait(false);
            }
            else
            {
                Status?.Invoke("Mac autenticado. Compartilhando janela autorizada.");
                try { await RunViewAsync(tls, session, handshake.WindowId, handshake.MonitorId).ConfigureAwait(false); }
                finally
                {
                    if (lastVideo is not null) VideoTimings = lastVideo.TimingSnapshot();
                }
            }
        }
        finally
        {
            clipboardStop.Cancel();
            try { await clipboardWatch.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private sealed record HandshakeResult(string Role, string? DeviceToken, long? WindowId, long? MonitorId = null);

    private async Task<HandshakeResult?> PerformHandshakeAsync(
        SslStream tls, TcpClient client, CancellationToken sessionToken)
    {
        IncomingFrame? helloFrame = await ReadWithDeadlineAsync(
            tls, sessionToken, TimeSpan.FromSeconds(HandshakeTimeoutSeconds)).ConfigureAwait(false);
        if (helloFrame is null) return null;
        if (!helloFrame.IsControl)
        {
            await TrySendErrorAsync(tls, "Mensagem inválida.", sessionToken).ConfigureAwait(false);
            return null;
        }

        string role;
        string clientName;
        long? requestedWindow = null;
        long? requestedMonitor = null;
        using (JsonDocument hello = helloFrame.ParseControl())
        {
            JsonElement root = hello.RootElement;
            if (GetString(root, "type") != "hello")
            {
                await TrySendErrorAsync(tls, "Esperava hello.", sessionToken).ConfigureAwait(false);
                return null;
            }

            if (!TryGetInt32(root, "version", out int version) || version != ProtocolV2.Version)
            {
                await TrySendErrorAsync(tls, "Versão de protocolo incompatível.", sessionToken).ConfigureAwait(false);
                return null;
            }

            role = GetString(root, "role") ?? "";
            if (role is not ("view" or "control"))
            {
                await TrySendErrorAsync(tls, "Papel de sessão inválido.", sessionToken).ConfigureAwait(false);
                return null;
            }

            clientName = GetString(root, "clientName") ?? "Mac";
            if (clientName.Length > 64)
            {
                await TrySendErrorAsync(tls, "Nome do cliente muito longo.", sessionToken).ConfigureAwait(false);
                return null;
            }

            if (root.TryGetProperty("windowId", out JsonElement windowId) &&
                windowId.ValueKind != JsonValueKind.Null)
            {
                if (windowId.ValueKind != JsonValueKind.Number || !windowId.TryGetInt64(out long value) || value <= 0)
                {
                    await TrySendErrorAsync(tls, "Mensagem inválida.", sessionToken).ConfigureAwait(false);
                    return null;
                }
                requestedWindow = value;
            }

            // Tela inteira: `monitorId` é o HMONITOR publicado na lista `windows`.
            if (root.TryGetProperty("monitorId", out JsonElement monitorId) &&
                monitorId.ValueKind != JsonValueKind.Null)
            {
                if (monitorId.ValueKind != JsonValueKind.Number || !monitorId.TryGetInt64(out long value) || value <= 0)
                {
                    await TrySendErrorAsync(tls, "Mensagem inválida.", sessionToken).ConfigureAwait(false);
                    return null;
                }
                requestedMonitor = value;
            }
        }

        IPAddress? remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        remote ??= IPAddress.None;

        TimeSpan lockout = rateLimiter.RemainingLockout(remote);
        if (lockout > TimeSpan.Zero)
        {
            await SendAuthFailedAsync(
                tls, "Muitas tentativas. Aguarde e tente novamente.",
                CeilSeconds(lockout), sessionToken).ConfigureAwait(false);
            return null;
        }

        HostCredentials snapshot;
        lock (credentialsGate) snapshot = credentials;
        byte[] nonce = RandomNumberGenerator.GetBytes(32);

        await SendControlAsync(tls, new
        {
            type = "challenge",
            version = ProtocolV2.Version,
            salt = Convert.ToBase64String(snapshot.Salt),
            nonce = Convert.ToBase64String(nonce)
        }, sessionToken).ConfigureAwait(false);

        IncomingFrame? authFrame = await ReadWithDeadlineAsync(
            tls, sessionToken, TimeSpan.FromSeconds(HandshakeTimeoutSeconds)).ConfigureAwait(false);
        if (authFrame is null) return null;
        if (!authFrame.IsControl)
        {
            await TrySendErrorAsync(tls, "Mensagem inválida.", sessionToken).ConfigureAwait(false);
            return null;
        }

        string? mode;
        byte[] proof;
        using (JsonDocument auth = authFrame.ParseControl())
        {
            JsonElement root = auth.RootElement;
            if (GetString(root, "type") != "auth")
            {
                await TrySendErrorAsync(tls, "Esperava auth.", sessionToken).ConfigureAwait(false);
                return null;
            }

            if (!TryGetInt32(root, "version", out int version) || version != ProtocolV2.Version)
            {
                await TrySendErrorAsync(tls, "Versão de protocolo incompatível.", sessionToken).ConfigureAwait(false);
                return null;
            }

            mode = GetString(root, "mode");
            try { proof = Convert.FromBase64String(GetString(root, "proof") ?? ""); }
            catch (FormatException) { proof = []; }
        }

        bool accepted;
        lock (credentialsGate) accepted = credentials.TryVerify(mode ?? "", proof, nonce, out _);

        if (!accepted)
        {
            TimeSpan wait = rateLimiter.RegisterFailure(remote);
            await SendAuthFailedAsync(
                tls, "PIN incorreto.", CeilSeconds(wait), sessionToken).ConfigureAwait(false);
            return null;
        }

        rateLimiter.Reset(remote);

        string? deviceToken = null;
        if (string.Equals(mode, "pin", StringComparison.Ordinal))
        {
            HostCredentials updated;
            PairedDevice created;
            try
            {
                lock (credentialsGate)
                {
                    (updated, created) = credentials.AddDevice(clientName);
                    credentials = updated;
                }
            }
            catch (InvalidOperationException)
            {
                await TrySendErrorAsync(tls, "Limite de dispositivos pareados atingido.", sessionToken).ConfigureAwait(false);
                return null;
            }

            deviceToken = Convert.ToBase64String(created.Token);
            CredentialsChanged?.Invoke(this, updated);
        }

        // No modo device o token recebido não é reenviado em ready.
        return new HandshakeResult(role, deviceToken, requestedWindow, requestedMonitor);
    }

    private async Task RunControlAsync(SslStream tls, CancellationTokenSource session)
    {
        await SendWindowsAsync(tls, session.Token).ConfigureAwait(false);

        while (!session.IsCancellationRequested)
        {
            IncomingFrame? frame;
            try
            {
                frame = await ReadWithDeadlineAsync(
                    tls, session.Token, TimeSpan.FromSeconds(IdleTimeoutSeconds)).ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
                await TrySendErrorAsync(tls, "Mensagem inválida.", session.Token).ConfigureAwait(false);
                return;
            }

            if (frame is null) return;
            if (!frame.IsControl)
            {
                await TrySendErrorAsync(tls, "Mensagem inválida.", session.Token).ConfigureAwait(false);
                return;
            }

            using JsonDocument document = frame.ParseControl();
            JsonElement root = document.RootElement;
            switch (GetString(root, "type"))
            {
                case "list":
                    await SendWindowsAsync(tls, session.Token).ConfigureAwait(false);
                    break;
                case "ping":
                    await SendControlAsync(tls, new { type = "pong" }, session.Token).ConfigureAwait(false);
                    break;
                case "open":
                    await TrySendErrorAsync(tls, WindowSelectionUnavailable, session.Token).ConfigureAwait(false);
                    return;
                case "clipboard":
                    ApplyRemoteClipboard(root);
                    break;
                case "fileStart":
                case "fileChunk":
                    await HandleFileAsync(tls, root, session.Token).ConfigureAwait(false);
                    break;
                case "launch":
                    // Abrir um aplicativo também é permitido na sessão de
                    // controle, que é a que lista os apps ao Mac.
                    LaunchRemoteApp(root);
                    break;
                case "click":
                case "pointer":
                case "scroll":
                case "text":
                case "key":
                    // Sessão de controle não aplica entrada na Fase 1.
                    break;
                default:
                    await TrySendErrorAsync(tls, "Mensagem desconhecida.", session.Token).ConfigureAwait(false);
                    return;
            }
        }
    }

    /// <summary>Estado do pipeline de vídeo de uma sessão de visualização.</summary>
    internal sealed class VideoChannel : IDisposable
    {
        private readonly object gate = new();
        private MfH264Encoder? encoder;
        private int width;
        private int height;
        private bool preferHardware = true;

        public string? Error { get; set; }
        public bool Announced { get; set; }
        public bool LastHardware { get; private set; }
        public bool EncoderUnavailable { get; private set; }

        /// <summary>Contador único de sequência da sessão (PNG e H.264).</summary>
        private uint sequence;

        /// <summary>Cadência corrente; o pacer é recriado quando o Mac pede outra.</summary>
        private readonly object pacerGate = new();
        private VideoFramePacer pacer = new(VideoFps);
        private int fps = VideoFps;
        private int bitrate = VideoBitrate;
        /// <summary>Pedido de quality: o encoder é recriado no próximo quadro.</summary>
        private bool reconfigure;


        public int ConsecutiveEncodeFailures { get; set; }
        public uint NextSequence() => Interlocked.Increment(ref sequence);
        public bool ShouldAcceptFrame(TimeSpan elapsed)
        {
            lock (pacerGate) return pacer.TryAccept(elapsed);
        }

        /// <summary>Cadência que o encoder e o anúncio `video` usam agora.</summary>
        public int TargetFps { get { lock (gate) return fps; } }

        /// <summary>Bitrate corrente do encoder.</summary>
        public int TargetBitrate { get { lock (gate) return bitrate; } }

        /// <summary>
        /// Reconfigura a cadência. O pacer vale imediatamente e o encoder é
        /// recriado no próximo quadro, sem bloquear esta thread de recepção.
        /// </summary>
        public void UpdateFps(int targetFps)
        {
            int value = Math.Clamp(targetFps, MinVideoFps, MaxVideoFps);
            lock (gate)
            {
                if (value == fps) return;
                fps = value;
                reconfigure = true;
            }

            lock (pacerGate) pacer = new VideoFramePacer(value);
        }

        /// <summary>
        /// Reconfigura o bitrate. O Media Foundation não aceita mudar o CBR de
        /// um MFT em streaming, então o encoder é recriado no próximo quadro.
        /// </summary>
        public void UpdateBitrate(int targetBitrate)
        {
            int value = Math.Clamp(targetBitrate, MinVideoBitrate, MaxVideoBitrate);
            lock (gate)
            {
                if (value == bitrate) return;
                bitrate = value;
                reconfigure = true;
            }
        }

        // ---- diagnóstico de desempenho (só lido pelo host-performance-smoke) ----

        private readonly object timingGate = new();
        private double encodeMsTotal;
        private double sendMsTotal;
        private double sendMsMax;
        private int timedUnits;
        private int timedFrames;
        private double copyMsTotal;
        private double copyMsMax;

        /// <summary>Acumula encode e envio por quadro; custo irrelevante.</summary>
        public void RecordTiming(TimeSpan encode, TimeSpan send, int units)
        {
            lock (timingGate)
            {
                encodeMsTotal += encode.TotalMilliseconds;
                sendMsTotal += send.TotalMilliseconds;
                sendMsMax = Math.Max(sendMsMax, send.TotalMilliseconds);
                timedFrames++;
                timedUnits += units;
            }
        }

        /// <summary>Cópia GPU/CPU acumulada pelo callback de captura.</summary>
        public void RecordCopy(TimeSpan copy)
        {
            lock (timingGate)
            {
                copyMsTotal += copy.TotalMilliseconds;
                copyMsMax = Math.Max(copyMsMax, copy.TotalMilliseconds);
            }
        }

        public object TimingSnapshot() => Snapshot();

        /// <summary>Alimenta a cópia GPU/CPU medida pela captura.</summary>
        public Action<TimeSpan, TimeSpan> Timings => (copy, _) => RecordCopy(copy);

        private Dictionary<string, object?> Snapshot()
        {
            lock (timingGate)
            {
                int frames = Math.Max(1, timedFrames);
                return new()
                {
                    ["timedFrames"] = timedFrames,
                    ["accessUnits"] = timedUnits,
                    ["copyMsAvg"] = Math.Round(copyMsTotal / frames, 2),
                    ["copyMsMax"] = Math.Round(copyMsMax, 2),
                    ["encodeMsAvg"] = Math.Round(encodeMsTotal / frames, 2),
                    ["sendMsAvg"] = Math.Round(sendMsTotal / frames, 2),
                    ["sendMsMax"] = Math.Round(sendMsMax, 2),
                };
            }
        }

        /// <summary>Devolve o encoder do tamanho pedido, recriando quando a janela muda.</summary>
        public MfH264Encoder? EnsureEncoder(int newWidth, int newHeight, out string? failure)
        {
            lock (gate)
            {
                failure = null;
                if (encoder is not null && (width != newWidth || height != newHeight))
                {
                    encoder.Dispose();
                    encoder = null;
                    // O tamanho mudou: o cliente precisa da nova geometria.
                    Announced = false;
                }

                // quality: o MFT não permite trocar fps/bitrate em streaming.
                // A recriação acontece aqui, na thread de encode, para não
                // descartar o encoder enquanto ele está codificando um quadro.
                if (reconfigure && encoder is not null)
                {
                    encoder.Dispose();
                    encoder = null;
                    Announced = false;
                }
                reconfigure = false;

                if (encoder is not null) return encoder;

                try
                {
                    try { encoder = new MfH264Encoder(newWidth, newHeight, fps, bitrate, preferHardware); }
                    catch when (preferHardware)
                    {
                        preferHardware = false;
                        encoder = new MfH264Encoder(newWidth, newHeight, fps, bitrate, preferHardware: false);
                    }
                    encoder.RequestKeyframe();
                    LastHardware = encoder.IsHardware;
                    width = newWidth;
                    height = newHeight;
                    return encoder;
                }
                catch (Exception ex)
                {
                    EncoderUnavailable = true;
                    failure = ex.Message;
                    Error = ex.Message;
                    return null;
                }
            }
        }

        public void UseSoftwareEncoder()
        {
            lock (gate)
            {
                preferHardware = false;
                LastHardware = false;
                encoder?.Dispose();
                encoder = null;
                Announced = false;
            }
        }

        public void RequestKeyframe()
        {
            lock (gate) { encoder?.RequestKeyframe(); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                encoder?.Dispose();
                encoder = null;
            }
        }
    }

    internal const int VideoFps = 60;
    internal const int MinVideoFps = 1;
    internal const int MaxVideoFps = 120;
    internal const int VideoBitrate = 12_000_000;
    internal const int MinVideoBitrate = 100_000;
    internal const int MaxVideoBitrate = 50_000_000;

    /// <summary>Falhas seguidas do encoder de hardware antes de cair para software.</summary>
    private const int HardwareFallbackFailures = 5;

    /// Resolve a janela que o Mac pediu. O identificador é o próprio handle da
    /// janela, publicado na lista `windows`.
    private IntPtr? ResolveWindow(long? requested)
    {
        if (requested is null)
        {
            return hwnd != IntPtr.Zero ? hwnd : null;
        }

        IReadOnlyList<WindowDescriptor> list;
        try { list = windowList?.Invoke() ?? []; }
        catch { return null; }

        foreach (WindowDescriptor descriptor in list)
        {
            if (descriptor.Id != requested.Value) continue;
            var handle = new IntPtr(descriptor.Id);
            return handle != IntPtr.Zero ? handle : null;
        }
        return null;
    }

    private async Task RunViewAsync(
        SslStream tls, CancellationTokenSource session, long? requestedWindow, long? requestedMonitor = null)
    {
        IntPtr monitorHandle = IntPtr.Zero;
        IntPtr windowHandle = IntPtr.Zero;
        if (requestedMonitor is not null)
        {
            if (!MonitorEnumerator.TryFind(requestedMonitor.Value, out MonitorInfo monitorInfo))
            {
                await TrySendErrorAsync(tls, "Monitor não encontrado neste PC.", session.Token).ConfigureAwait(false);
                return;
            }
            monitorHandle = new IntPtr(monitorInfo.Id);
        }
        else
        {
            IntPtr? target = ResolveWindow(requestedWindow);
            if (target is null)
            {
                await TrySendErrorAsync(tls, "Janela não encontrada neste PC.", session.Token).ConfigureAwait(false);
                return;
            }
            windowHandle = target.Value;
        }

        using var wake = new DisplayWakeLock();
        DisplayAwake = wake.Acquired;
        MonitorWoken = wake.MonitorWoken;
        Status?.Invoke($"Sessão view: tela acordada={wake.Acquired} monitor religado={wake.MonitorWoken}");
        using var input = monitorHandle != IntPtr.Zero
            ? new RemoteInput(monitorHandle, fullScreen: true)
            : new RemoteInput(windowHandle);
        input.AllowHover = allowHover;
        using var mediaInFlight = new SemaphoreSlim(2, 2);
        var video = new VideoChannel();
        lastVideo = video;
        video.RequestKeyframe();
        Task receive = ReceiveViewAsync(tls, input, video, session);

        try
        {
            // Primeiro tenta vídeo H.264; se o Windows não tiver encoder, cai para PNG.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool raw = enableVideo && attempt == 0;
                try
                {
                    await RunCaptureAsync(tls, video, raw, mediaInFlight, windowHandle, session, monitorHandle).ConfigureAwait(false);
                    return;
                }
                catch (VideoEncoderUnavailable)
                {
                    Status?.Invoke("Sem encoder H.264 neste Windows; enviando PNG.");
                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        catch (CaptureException ex)
        {
            LastFailure = ex.GetType().Name + ": " + ex.Message;
            Status?.Invoke("Captura encerrada: " + ex.Message);
        }
        catch (Exception ex)
        {
            // Falha inesperada do pipeline não pode sair silenciosa: o relatório
            // de desempenho precisa do motivo (HRESULT, tipo e mensagem).
            string hr = ex is System.Runtime.InteropServices.COMException com ? " 0x" + com.HResult.ToString("X8") : "";
            LastFailure = ex.GetType().Name + hr + ": " + ex.Message.Replace("\r\n", " ");
            Status?.Invoke("Captura encerrada: " + LastFailure);
        }
        finally
        {
            session.Cancel();
            video.Dispose();
            try { await receive.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private sealed class VideoEncoderUnavailable : Exception { }

    private async Task RunCaptureAsync(
        SslStream tls,
        VideoChannel video,
        bool raw,
        SemaphoreSlim mediaInFlight,
        IntPtr windowHandle,
        CancellationTokenSource session,
        IntPtr monitorHandle = default)
    {
        using var pipelineStop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        // Tela inteira já mostra tudo: não há popups para compor.
        PopupComposer? popupComposer = monitorHandle == IntPtr.Zero ? new PopupComposer(windowHandle) : null;
        (int X, int Y, int Width, int Height) lastBase = default;
        var options = new ContinuousCaptureOptions
        {
            FrameTimeout = TimeSpan.FromHours(8),
            RawPixels = raw,
            // Filtro antes da cópia GPU/CPU: só quadros na cadência do produto
            // pagam a cópia e o encode.
            AcceptFrame = raw ? video.ShouldAcceptFrame : null,
            // O cursor do PC precisa entrar no quadro: o Mac esconde o cursor
            // local sobre a imagem, então sem isso não há ponteiro na tela.
            CaptureCursor = true,
            // Menus de contexto e popups são janelas separadas: o compositor
            // os desenha sobre o quadro da janela autorizada.
            RawFrame = raw ? async (rawFrame, ct) =>
            {
                RawPixelFrame composed = popupComposer is null ? rawFrame : popupComposer.Compose(rawFrame);
                // Informa ao Mac onde fica a janela dentro do quadro (muda só
                // quando um popup abre/fecha) para manter a escala estável.
                var baseRect = popupComposer?.Base ?? default;
                if (popupComposer is not null && baseRect != lastBase)
                {
                    lastBase = baseRect;
                    await SendControlAsync(
                        tls,
                        new { type = "viewport", x = baseRect.X, y = baseRect.Y, width = baseRect.Width, height = baseRect.Height },
                        ct).ConfigureAwait(false);
                }
                await SendVideoFrameAsync(tls, video, composed, mediaInFlight, ct).ConfigureAwait(false);
            } : null,
            Timings = raw ? video.Timings : null,
            MonitorHandle = monitorHandle
        };

        try
        {
            await ContinuousWindowCapture.RunAsync(
                windowHandle,
                async (frame, ct) =>
                {
                    if (frame.Width is <= 0 or > ushort.MaxValue || frame.Height is <= 0 or > ushort.MaxValue) return;

                    byte[] framed = FrameMedia(
                        ProtocolV2.CodecPng,
                        keyframe: true,
                        video.NextSequence(),
                        (uint)Math.Clamp(frame.Elapsed.TotalMilliseconds, 0d, uint.MaxValue),
                        (ushort)frame.Width,
                        (ushort)frame.Height,
                        frame.Png);

                    await mediaInFlight.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await WriteAsync(tls, framed, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref framesSent);
                    }
                    finally { mediaInFlight.Release(); }

                    await Task.Delay(125, ct).ConfigureAwait(false);
                },
                options,
                null,
                pipelineStop.Token).ConfigureAwait(false);
        }
        finally { popupComposer?.Release(); }
    }

    private async Task SendVideoFrameAsync(
        SslStream tls,
        VideoChannel video,
        RawPixelFrame rawFrame,
        SemaphoreSlim mediaInFlight,
        CancellationToken ct)
    {
        if (rawFrame.Width is <= 0 or > ushort.MaxValue || rawFrame.Height is <= 0 or > ushort.MaxValue) return;

        long pts = (long)rawFrame.Elapsed.TotalMilliseconds;
        long encodeStart = Stopwatch.GetTimestamp();

        MfH264Encoder? encoder = video.EnsureEncoder(rawFrame.Width, rawFrame.Height, out string? failure);
        if (encoder is null)
        {
            throw new VideoEncoderUnavailable();
        }

        // O encoder pode codificar com dimensões alinhadas em 16 (padding); o
        // cabeçalho de mídia deve refletir o que foi realmente codificado.
        int encodedWidth = encoder.Width;
        int encodedHeight = encoder.Height;

        if (!video.Announced)
        {
            video.Announced = true;
            await SendControlAsync(
                tls,
                new { type = "video", codec = "h264", width = encodedWidth, height = encodedHeight, fps = video.TargetFps, hardware = encoder.IsHardware },
                ct).ConfigureAwait(false);
        }

        List<EncodedAccessUnit> units;
        try
        {
            units = encoder.Encode(rawFrame.Bgra, rawFrame.Stride, pts);
            video.ConsecutiveEncodeFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha de codificação não encerra a sessão: descarta o quadro,
            // pede keyframe e conta. Depois de muitas falhas seguidas, o
            // pipeline cai para PNG. Uma falha isolada do encoder de hardware
            // mantém o caminho; só a insistência troca para software, para não
            // reativar o MFT a cada quadro.
            video.ConsecutiveEncodeFailures++;
            video.Error = "encode: " + ex.Message;
            video.RequestKeyframe();
            if (encoder.IsHardware && video.ConsecutiveEncodeFailures >= HardwareFallbackFailures)
            {
                video.UseSoftwareEncoder();
                return;
            }
            if (video.ConsecutiveEncodeFailures >= 30) throw new VideoEncoderUnavailable();
            return;
        }

        TimeSpan encode = Stopwatch.GetElapsedTime(encodeStart);
        long sendStart = Stopwatch.GetTimestamp();
        foreach (EncodedAccessUnit unit in units)
        {
            byte[] framed = FrameMedia(
                ProtocolV2.CodecH264,
                unit.Keyframe,
                video.NextSequence(),
                (uint)Math.Clamp(unit.PtsMs, 0d, uint.MaxValue),
                (ushort)encodedWidth,
                (ushort)encodedHeight,
                unit.Data);

            await mediaInFlight.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WriteAsync(tls, framed, ct).ConfigureAwait(false);
                Interlocked.Increment(ref framesSent);
            }
            finally { mediaInFlight.Release(); }
        }
        video.RecordTiming(encode, Stopwatch.GetElapsedTime(sendStart), units.Count);
    }

    private async Task ReceiveViewAsync(SslStream tls, RemoteInput input, VideoChannel video, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                IncomingFrame? frame;
                try
                {
                    frame = await ReadWithDeadlineAsync(
                        tls, session.Token, TimeSpan.FromSeconds(IdleTimeoutSeconds)).ConfigureAwait(false);
                }
                catch (ProtocolException)
                {
                    await TrySendErrorAsync(tls, "Mensagem inválida.", session.Token).ConfigureAwait(false);
                    return;
                }

                if (frame is null) return;
                if (!frame.IsControl)
                {
                    await TrySendErrorAsync(tls, "Mensagem inválida.", session.Token).ConfigureAwait(false);
                    return;
                }

                using JsonDocument document = frame.ParseControl();
                JsonElement root = document.RootElement;
                switch (GetString(root, "type"))
                {
                    case "ping":
                        await SendControlAsync(tls, new { type = "pong" }, session.Token).ConfigureAwait(false);
                        break;
                    case "requestKeyframe":
                        video.RequestKeyframe();
                        break;
                    case "bye":
                        // Encerramento limpio pedido pelo cliente.
                        return;
                    case "quality":
                        ApplyQuality(root, video);
                        break;
                    case "list":
                        // Mensagem opcional: aceita e ignorada nesta versão.
                        // Não pode derrubar a sessão de vídeo.
                        break;
                    case "launch":
                        LaunchRemoteApp(root);
                        break;
                    case "open":
                        await TrySendErrorAsync(tls, WindowSelectionUnavailable, session.Token).ConfigureAwait(false);
                        return;
                    case "clipboard":
                        ApplyRemoteClipboard(root);
                        break;
                    case "fileStart":
                    case "fileChunk":
                        await HandleFileAsync(tls, root, session.Token).ConfigureAwait(false);
                        break;
                    case "click":
                    case "pointer":
                    case "scroll":
                    case "text":
                    case "key":
                    {
                        string? rejection = input.TryApply(root);
                        if (rejection is null)
                        {
                            Interlocked.Increment(ref inputsApplied);
                        }
                        else
                        {
                            // Recusa pontual não encerra a sessão.
                            LastFailure = rejection;
                            Status?.Invoke("Entrada recusada: " + rejection);
                            await SendControlAsync(
                                tls, new { type = "inputRejected", message = rejection }, session.Token).ConfigureAwait(false);
                        }
                        break;
                    }
                    default:
                        // Mensagem desconhecida não fecha a sessão: o host
                        // continua servindo; apenas avisa.
                        await TrySendErrorAsync(tls, "Mensagem desconhecida.", session.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { session.Cancel(); }
    }

    /// <summary>
    /// Aplica o pedido de qualidade do Mac. Cada campo é opcional; valor
    /// ausente mantém o atual e valor fora da faixa é limitado, nunca encerra
    /// a sessão.
    /// </summary>
    private static void ApplyQuality(JsonElement root, VideoChannel video)
    {
        if (TryGetInt32(root, "fps", out int fps)) video.UpdateFps(fps);
        if (TryGetInt32(root, "bitrate", out int bitrate)) video.UpdateBitrate(bitrate);
    }

    /// <summary>
    /// Abre o aplicativo pedido pelo Mac. O lançador valida a lista segura;
    /// recusa ou falha do Windows não encerram a sessão.
    /// </summary>
    private void LaunchRemoteApp(JsonElement root)
    {
        string? app = GetString(root, "app");
        if (string.IsNullOrWhiteSpace(app) || app.Length > 64) return;

        try
        {
            if (!launchApp(app)) Status?.Invoke("Aplicativo remoto não iniciado: " + app);
        }
        catch (Exception)
        {
            // Falha de inicialização é best-effort, como o clipboard.
        }
    }

    /// <summary>
    /// Aplica o texto vindo do Mac. Falha do clipboard é best-effort: não
    /// encerra a sessão de vídeo nem muda o contrato do protocolo.
    /// </summary>
    private void ApplyRemoteClipboard(JsonElement root)
    {
        string? text = GetString(root, "text");
        if (text is null) return;
        if (Encoding.UTF8.GetByteCount(text) > WindowsClipboard.MaxTextBytes) return;

        // Marca antes da escrita: a mudança de sequência provocada por ela não
        // pode voltar ao Mac como se fosse uma cópia local.
        Volatile.Write(ref lastRemoteClipboardText, text);
        try { clipboard.SetText(text); }
        catch (Exception) { /* clipboard indisponível: ignora esta mensagem */ }
    }

    /// <summary>
    /// Recebe um arquivo em blocos do Mac. <c>fileStart</c> abre a
    /// transferência; <c>fileChunk</c> acrescenta bytes. O bloco com
    /// <c>isLast</c> fecha o arquivo e responde com <c>fileReceived</c> e o nome
    /// higienizado. Bloco inválido, grande demais ou falha de disco não encerram
    /// a sessão: o Mac recebe <c>error</c> e pode recomeçar.
    /// </summary>
    private async Task HandleFileAsync(SslStream tls, JsonElement root, CancellationToken token)
    {
        bool isStart = string.Equals(GetString(root, "type"), "fileStart", StringComparison.Ordinal);
        bool isLast = GetBool(root, "isLast");
        bool isFirst = isStart || GetBool(root, "isFirst");

        byte[] data = [];
        string? encoded = GetString(root, "data");
        if (encoded is not null)
        {
            try { data = Convert.FromBase64String(encoded); }
            catch (FormatException)
            {
                await TrySendErrorAsync(tls, "Bloco de arquivo inválido.", token).ConfigureAwait(false);
                return;
            }
        }

        if (data.Length > FileReceiver.MaxChunkBytes)
        {
            await TrySendErrorAsync(tls, "Bloco de arquivo acima do limite.", token).ConfigureAwait(false);
            return;
        }

        string saved;
        try
        {
            saved = fileReceiver.SaveChunk(GetString(root, "name"), data, isFirst, isLast);
        }
        catch (Exception)
        {
            // Nome impossível, disco cheio ou pasta removida: a mensagem é
            // descartada sem derrubar o streaming da sessão.
            await TrySendErrorAsync(tls, "Arquivo recusado.", token).ConfigureAwait(false);
            return;
        }

        if (!isLast) return;

        await SendControlAsync(
            tls, new { type = "fileReceived", name = Path.GetFileName(saved) }, token).ConfigureAwait(false);
        Status?.Invoke("Arquivo recebido do Mac: " + Path.GetFileName(saved));
    }

    /// <summary>
    /// Envia ao Mac o texto copiado no Windows. A sequência é lida a cada
    /// intervalo; só uma mudança real dispara a leitura do conteúdo, evitando
    /// abrir o clipboard sem necessidade.
    /// </summary>
    private async Task WatchClipboardAsync(SslStream tls, CancellationToken token)
    {
        uint lastSequence = clipboard.SequenceNumber;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(ClipboardPollMilliseconds, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            uint sequence = clipboard.SequenceNumber;
            if (sequence == lastSequence) continue;
            lastSequence = sequence;

            string text;
            try
            {
                if (!clipboard.TryGetText(out text) || text.Length == 0) continue;
            }
            catch (Exception) { continue; }

            if (Encoding.UTF8.GetByteCount(text) > WindowsClipboard.MaxTextBytes) continue;
            if (string.Equals(text, Volatile.Read(ref lastRemoteClipboardText), StringComparison.Ordinal)) continue;

            try
            {
                await SendControlAsync(tls, new { type = "clipboard", text }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ProtocolException) { /* acima do limite de controle: descarta */ }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    private async Task SendWindowsAsync(SslStream tls, CancellationToken token)
    {
        IReadOnlyList<WindowDescriptor> windows;
        try { windows = windowList?.Invoke() ?? []; }
        catch { windows = []; }

        IReadOnlyList<MonitorInfo> monitors;
        try { monitors = MonitorEnumerator.GetMonitors(); }
        catch { monitors = []; }

        await SendControlAsync(tls, new
        {
            type = "windows",
            windows = windows.Select(window => new
            {
                id = window.Id,
                title = window.Title,
                process = window.Process,
                width = window.Width,
                height = window.Height,
                icon = window.IconBase64
            }).ToArray(),
            monitors = monitors.Select((monitor, index) => new
            {
                id = monitor.Id,
                name = $"Monitor {index + 1}",
                width = monitor.Width,
                height = monitor.Height,
                x = monitor.X,
                y = monitor.Y,
                primary = monitor.Primary
            }).ToArray(),
            // Lista segura de aplicativos que o Mac pode pedir para abrir.
            // `cmd` é o nome de campo esperado por mac/.../Protocol.swift.
            apps = AppLauncher.GetKnownApps().Select(app => new
            {
                id = app.Id,
                name = app.Name,
                cmd = app.Command,
                icon = app.Icon
            }).ToArray()
        }, token).ConfigureAwait(false);
    }

    private Task SendAuthFailedAsync(SslStream tls, string message, int retryAfterSeconds, CancellationToken token) =>
        SendControlAsync(tls, new
        {
            type = "authFailed",
            version = ProtocolV2.Version,
            message,
            retryAfterSeconds
        }, token);

    private async Task SendControlAsync(SslStream tls, object message, CancellationToken token)
    {
        // O overload genérico de FrameControl<T> entra em recursão quando recebe
        // byte[]; aqui o JSON é passado como ReadOnlySpan<byte> de propósito.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] framed = ProtocolV2.FrameControl(json.AsSpan());
        if (framed.Length - 5 > ProtocolV2.MaxControlHostToClient)
        {
            throw new ProtocolException("Controle acima do limite.");
        }

        await WriteAsync(tls, framed, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Gate de escrita por conexão. Um gate global congelaria o vídeo de todas
    /// as sessões quando um cliente lento parasse de ler.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SslStream, SemaphoreSlim> writeGates = new();

    private async Task WriteAsync(SslStream tls, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        SemaphoreSlim gate = writeGates.GetOrAdd(tls, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await tls.WriteAsync(bytes, token).ConfigureAwait(false);
            await tls.FlushAsync(token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task TrySendErrorAsync(SslStream tls, string message, CancellationToken token)
    {
        try
        {
            await SendControlAsync(tls, new { type = "error", message }, token).ConfigureAwait(false);
        }
        catch (Exception) { /* conexão já caiu */ }
    }

    private static async Task<IncomingFrame?> ReadWithDeadlineAsync(
        Stream stream, CancellationToken outer, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        deadline.CancelAfter(timeout);
        try
        {
            return await FrameReader.ReadAsync(stream, ProtocolV2.MaxControlClientToHost, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            // Timeout de handshake ou de heartbeat.
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt32(JsonElement root, string name, out int result)
    {
        result = 0;
        return root.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out result);
    }

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static int CeilSeconds(TimeSpan value) =>
        value <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(value.TotalSeconds);

    /// <summary>
    /// Enquadramento de mídia conforme docs/PROTOCOL.md: kind + codec + flags +
    /// seq + ptsMs + largura + altura + bytes do quadro. Feito localmente porque
    /// ProtocolV2.FrameMedia/FrameReader divergem do contrato no layout de mídia.
    /// </summary>
    private static byte[] FrameMedia(
        byte codec,
        bool keyframe,
        uint sequence,
        uint ptsMs,
        ushort width,
        ushort height,
        ReadOnlySpan<byte> payload)
    {
        const int headerBytes = 1 + 1 + 2 + 4 + 4 + 2 + 2; // kind + 15 bytes de cabeçalho
        if (payload.Length == 0 || payload.Length + headerBytes > ProtocolV2.MaxMessageBytes)
        {
            throw new ProtocolException("Quadro de mídia inválido.");
        }

        byte[] framed = new byte[4 + headerBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, headerBytes + payload.Length);
        int at = 4;
        framed[at++] = ProtocolV2.KindMedia;
        framed[at++] = codec;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(at), keyframe ? ProtocolV2.FlagKeyframe : (ushort)0);
        at += 2;
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(at), sequence);
        at += 4;
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(at), ptsMs);
        at += 4;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(at), width);
        at += 2;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(at), height);
        at += 2;
        payload.CopyTo(framed.AsSpan(at));
        return framed;
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteDesktop experimental", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(8));

        // Schannel precisa de um container temporário de chave no perfil do usuário.
        // Sem PersistKeySet: .NET remove o container ao descartar o certificado.
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        try { listener.Stop(); } catch (SocketException) { }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0)
        {
            try { await Completion.ConfigureAwait(false); } catch { }
            return;
        }

        Dispose();
        try { await Completion.ConfigureAwait(false); } catch { }

        Task[] pending;
        lock (sessionsGate) pending = [.. sessions];
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }

        if (ownsCertificate) certificate.Dispose();
        if (ownsFileReceiver) fileReceiver.Dispose();
        foreach (SemaphoreSlim gate in writeGates.Values) gate.Dispose();
        writeGates.Clear();
        stop.Dispose();
    }
}
