using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using WindowProbe.Remote;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var ct = timeout.Token;
var server = new RemoteServer(new IntPtr(1), IPAddress.Loopback, 0);
server.Start();
async Task<(TcpClient, SslStream)> Connect(bool validPin = true)
{
    var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, server.Port, ct);
    var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        validPin && cert is not null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == server.Fingerprint);
    try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RemoteDesktop", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct); }
    catch { tls.Dispose(); tcp.Dispose(); throw; }
    return (tcp, tls);
}
async Task Send(Stream s, object value)
{
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value), header = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
    // Fragmentação deliberada de header/body.
    foreach (byte b in header) await s.WriteAsync(new byte[] { b }, ct);
    await s.WriteAsync(bytes, ct);
}
async Task<JsonElement> Read(Stream s)
{
    byte[] header = new byte[4]; await s.ReadExactlyAsync(header, ct);
    byte[] bytes = new byte[BinaryPrimitives.ReadInt32BigEndian(header)]; await s.ReadExactlyAsync(bytes, ct);
    using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone();
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
try
{
    try { var c = await Connect(false); c.Item2.Dispose(); c.Item1.Dispose(); throw new Exception("Pin incorreto aceito."); }
    catch (AuthenticationException) { Console.WriteLine("PASS certificado divergente rejeitado"); }
    var (badTcp, badTls) = await Connect();
    using (badTcp) using (badTls)
    {
        await Send(badTls, new { type = "auth", version = 1, token = "invalido" });
        Check(await badTls.ReadAsync(new byte[1], ct) == 0, "Token incorreto recebeu dados.");
        Console.WriteLine("PASS token incorreto encerrado sem frames");
    }
    var (tcp, tls) = await Connect();
    using (tcp) using (tls)
    {
        await Send(tls, new { type = "auth", version = 1, token = server.Token });
        Check((await Read(tls)).GetProperty("type").GetString() == "ready", "Ready ausente.");
        var frame = await Read(tls);
        Check(frame.GetProperty("type").GetString() == "frame", "Frame ausente.");
        await Task.Delay(300, ct);
        Check(tcp.Available == 0, "Servidor enviou frame sem ACK.");
        await Send(tls, new { type = "ping" });
        Check((await Read(tls)).GetProperty("type").GetString() == "pong", "Heartbeat bloqueado esperando ACK.");
        await Send(tls, new { type = "ack", id = frame.GetProperty("id").GetInt64() });
        Check((await Read(tls)).GetProperty("id").GetInt64() == 2, "ACK não avançou frame.");
        await Send(tls, new { type = "ack", id = 999 });
        Check(await tls.ReadAsync(new byte[1], ct) == 0, "ACK inválido aceito.");
        Console.WriteLine("PASS framing fragmentado, ACK, backpressure, heartbeat e ACK inválido");
    }
    var (largeTcp, largeTls) = await Connect();
    using (largeTcp) using (largeTls)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, 8193);
        await largeTls.WriteAsync(header, ct);
        Check(await largeTls.ReadAsync(new byte[1], ct) == 0, "Comprimento excessivo aceito.");
        Console.WriteLine("PASS tamanho de mensagem limitado antes da alocação");
    }
}
finally { await server.StopAsync(); }
Console.WriteLine("PASS encerramento do servidor; captura/entrada são substitutos neste teste");
