using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WindowProbe.Capture;

namespace WindowProbe.Remote;

internal sealed class RemoteServer : IDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener listener;
    private readonly IntPtr hwnd;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Action<string>? Status { get; set; }
    public string? LastFailure { get; private set; }
    public long FramesSent { get; private set; }
    public long InputsApplied { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public RemoteServer(IntPtr window, IPAddress address, int port, X509Certificate2? pairedCertificate = null, string? pairedToken = null)
    {
        hwnd = window;
        if ((pairedCertificate is null) != (pairedToken is null)) throw new ArgumentException("Credenciais incompletas.");
        if (pairedCertificate is not null)
        {
            certificate = pairedCertificate;
            Token = pairedToken!;
            listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            return;
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemoteDesktop experimental", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(8));
        // Schannel precisa de um container temporário de chave no perfil do usuário.
        // Sem PersistKeySet: .NET remove o container ao descartar o certificado.
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
    }

    public void Start() { listener.Start(1); Completion = RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                client.NoDelay = true;
                try { await HandleAsync(client, stop.Token); }
                catch (Exception ex) when (!stop.IsCancellationRequested) { LastFailure ??= ex.GetType().Name + ":" + ex.HResult + "/" + ex.InnerException?.GetType().Name + ":" + ex.InnerException?.HResult; Status?.Invoke("Conexão encerrada ou recusada. Aguardando Mac."); }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken shutdown)
    {
        LastFailure = null;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var tls = new SslStream(client.GetStream(), false);
        using (var authDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            authDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, authDeadline.Token);
            using var auth = await ReadAsync(tls, authDeadline.Token);
            var root = auth.RootElement;
            if (root.GetProperty("type").GetString() != "auth" || root.GetProperty("version").GetInt32() != 1) return;
            byte[] supplied = Encoding.UTF8.GetBytes(root.GetProperty("token").GetString() ?? "");
            if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(Token))) return;
        }
        using var writer = new SemaphoreSlim(1);
        async Task Send(object message, CancellationToken ct)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
            if (payload.Length > 16 * 1024 * 1024) throw new IOException("Frame excede limite.");
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await writer.WaitAsync(deadline.Token);
            try { await tls.WriteAsync(header, deadline.Token); await tls.WriteAsync(payload, deadline.Token); }
            finally { writer.Release(); }
        }
        await Send(new { type = "ready", version = 1 }, session.Token);
        Status?.Invoke("Mac autenticado. Compartilhando janela autorizada.");
        long pendingId = 0;
        TaskCompletionSource? ack = null;
        using var input = new RemoteInput(hwnd);
        var gate = new object();
        var receive = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using var message = await ReadAsync(tls, deadline.Token);
                    var root = message.RootElement;
                    string? type = root.GetProperty("type").GetString();
                    if (type == "ack")
                    {
                        lock (gate) { if (root.GetProperty("id").GetInt64() != pendingId || ack is null) throw new IOException("ACK inválido."); ack.TrySetResult(); }
                    }
                    else if (type == "ping") await Send(new { type = "pong" }, session.Token);
                    else
                    {
                        // Executa sequencialmente. Nenhuma fila sobrevive à conexão.
                        session.Token.ThrowIfCancellationRequested();
                        string? rejection = input.TryApply(root);
                        if (rejection is null) InputsApplied++;
                        else
                        {
                            // Recusa pontual não encerra a sessão: informa e segue o streaming.
                            LastFailure = rejection;
                            Status?.Invoke("Entrada recusada: " + rejection);
                            await Send(new { type = "inputRejected", message = rejection }, session.Token);
                        }
                    }
                }
            }
            finally { session.Cancel(); }
        }, session.Token);
        try
        {
            await ContinuousWindowCapture.RunAsync(hwnd, async (frame, ct) =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate) { pendingId = frame.Sequence; ack = completion; }
                await Send(new { type = "frame", id = frame.Sequence, width = frame.Width, height = frame.Height, png = Convert.ToBase64String(frame.Png) }, ct);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                FramesSent++;
                await Task.Delay(125, ct);
            }, new ContinuousCaptureOptions { FrameTimeout = TimeSpan.FromHours(8) }, null, session.Token);
        }
        finally
        {
            session.Cancel();
            try { await receive; } catch (Exception) { }
        }
    }
    private static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        // Comandos recebidos são pequenos; limite de imagens vale apenas na direção PC -> Mac.
        if (size is <= 0 or > 8192) throw new IOException("Mensagem inválida.");
        byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); /* certificado liberado após o loop */ }
    public async Task StopAsync() { Dispose(); await Completion; certificate.Dispose(); stop.Dispose(); }
}
