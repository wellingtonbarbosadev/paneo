using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using WindowProbe.Remote;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
var ct = timeout.Token;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

const string Pin = "246810";
var credentials = HostCredentials.Create(Pin);
var windowList = new WindowDescriptor[]
{
    new(123, "Sem título", "notepad", 800, 600),
    new(456, "Outra janela", "calc", 640, 480)
};

var server = new RemoteServer(
    new IntPtr(1),
    IPAddress.Loopback,
    0,
    credentials,
    certificate: null,
    windowList: () => windowList,
    enableVideo: false);
server.Start();

async Task<(TcpClient Tcp, SslStream Tls)> ConnectAsync(RemoteServer target, bool validFingerprint = true)
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, target.Port, ct);
    var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        validFingerprint && cert is not null &&
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == target.Fingerprint);
    try
    {
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "RemoteDesktop",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, ct);
    }
    catch
    {
        tls.Dispose();
        tcp.Dispose();
        throw;
    }

    return (tcp, tls);
}

async Task SendJsonAsync(Stream stream, object message, bool byteByByte = false)
{
    // FrameControl<T> genérico recursa com byte[]; o span seleciona o overload correto.
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
    byte[] framed = ProtocolV2.FrameControl(json.AsSpan());
    if (!byteByByte)
    {
        await stream.WriteAsync(framed, ct);
        await stream.FlushAsync(ct);
        return;
    }

    foreach (byte value in framed)
    {
        await stream.WriteAsync(new[] { value }, ct);
        await stream.FlushAsync(ct);
    }
}

async Task<(byte Kind, byte Codec, ushort Flags, uint Sequence, uint PtsMs, ushort Width, ushort Height, byte[] Payload)> ReadRawFrameAsync(Stream stream)
{
    // Leitura bruta conforme docs/PROTOCOL.md: FrameReader do Protocol.cs fatia
    // mídia com offset divergente, então a mídia é validada pelo layout do contrato.
    byte[] header = new byte[4];
    await stream.ReadExactlyAsync(header, ct);
    int length = BinaryPrimitives.ReadInt32BigEndian(header);
    Check(length is >= 1 and <= ProtocolV2.MaxMessageBytes, "Comprimento de mensagem inválido.");

    byte[] body = new byte[length];
    await stream.ReadExactlyAsync(body, ct);

    byte kind = body[0];
    if (kind == ProtocolV2.KindControl)
    {
        return (kind, 0, 0, 0, 0, 0, 0, body[1..]);
    }

    Check(kind == ProtocolV2.KindMedia, "Tipo de mensagem desconhecido.");
    Check(length >= 16, "Mídia truncada.");
    return (
        kind,
        body[1],
        BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2)),
        BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4)),
        BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8)),
        BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(12)),
        BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(14)),
        body[16..]);
}

async Task<JsonElement> ReadJsonAsync(Stream stream)
{
    var frame = await ReadRawFrameAsync(stream);
    Check(frame.Kind == ProtocolV2.KindControl, "Esperava mensagem de controle.");
    using JsonDocument document = JsonDocument.Parse(frame.Payload);
    return document.RootElement.Clone();
}

async Task<(TcpClient Tcp, SslStream Tls, byte[] Salt, byte[] Nonce)> StartHandshakeAsync(
    RemoteServer target, string role, string name = "Mac", bool byteByByte = false)
{
    var (tcp, tls) = await ConnectAsync(target);
    await SendJsonAsync(tls, new Dictionary<string, object?>
    {
        ["type"] = "hello",
        ["version"] = ProtocolV2.Version,
        ["role"] = role,
        ["windowId"] = null,
        ["clientName"] = name
    }, byteByByte);

    JsonElement challenge = await ReadJsonAsync(tls);
    Check(challenge.GetProperty("type").GetString() == "challenge", "Challenge ausente.");
    Check(challenge.GetProperty("version").GetInt32() == ProtocolV2.Version, "Versão do challenge inesperada.");

    byte[] salt = Convert.FromBase64String(challenge.GetProperty("salt").GetString()!);
    byte[] nonce = Convert.FromBase64String(challenge.GetProperty("nonce").GetString()!);
    Check(salt.Length == HostCredentials.SaltBytes, "Salt com tamanho errado.");
    Check(nonce.Length == 32, "Nonce com tamanho errado.");
    return (tcp, tls, salt, nonce);
}

async Task SendPinAsync(Stream stream, string value, byte[] salt, byte[] nonce, bool byteByByte = false)
{
    byte[] key = HostCredentials.DeriveKey(value, salt);
    byte[] proof = HostCredentials.PinProof(key, nonce);
    await SendJsonAsync(stream, new
    {
        type = "auth",
        version = ProtocolV2.Version,
        mode = "pin",
        proof = Convert.ToBase64String(proof)
    }, byteByByte);
}

async Task SendDeviceAsync(Stream stream, byte[] token, byte[] nonce)
{
    byte[] proof = HostCredentials.DeviceProof(token, nonce);
    await SendJsonAsync(stream, new
    {
        type = "auth",
        version = ProtocolV2.Version,
        mode = "device",
        proof = Convert.ToBase64String(proof)
    });
}

async Task<bool> WaitForCloseAsync(Stream stream, TimeSpan limit)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(limit);
    byte[] scratch = new byte[4096];
    try
    {
        while (true)
        {
            int read = await stream.ReadAsync(scratch, deadline.Token);
            if (read == 0) return true;
        }
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return false;
    }
    catch (IOException)
    {
        return true;
    }
}

try
{
    // 1. Certificado divergente.
    try
    {
        var (badTcp, badTls) = await ConnectAsync(server, validFingerprint: false);
        badTls.Dispose();
        badTcp.Dispose();
        throw new Exception("Certificado divergente aceito.");
    }
    catch (Exception ex) when (ex is AuthenticationException or IOException)
    {
        Console.WriteLine("PASS certificado divergente rejeitado pelo cliente");
    }

    // 2. Prova errada.
    {
        var (tcp, tls, _, _) = await StartHandshakeAsync(server, "view");
        using (tcp)
        using (tls)
        {
            await SendJsonAsync(tls, new
            {
                type = "auth",
                version = ProtocolV2.Version,
                mode = "pin",
                proof = Convert.ToBase64String(new byte[32])
            });
            JsonElement failed = await ReadJsonAsync(tls);
            Check(failed.GetProperty("type").GetString() == "authFailed", "authFailed ausente.");
            Check(failed.GetProperty("retryAfterSeconds").GetInt32() == 0, "Primeira falha não deveria bloquear.");
            Check(await WaitForCloseAsync(tls, TimeSpan.FromSeconds(5)), "Conexão permaneceu aberta após prova errada.");
            Console.WriteLine("PASS prova errada recebe authFailed e a conexão é fechada");
        }
    }

    // 3. Bloqueio progressivo, em servidor isolado para não afetar os demais casos.
    {
        using var limiterServer = new RemoteServer(
            new IntPtr(1), IPAddress.Loopback, 0, HostCredentials.Create("135790"), certificate: null);
        limiterServer.Start();
        try
        {
            int retry = 0;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var (tcp, tls, salt, nonce) = await StartHandshakeAsync(limiterServer, "control");
                using (tcp)
                using (tls)
                {
                    byte[] proof = HostCredentials.PinProof(HostCredentials.DeriveKey("000000", salt), nonce);
                    await SendJsonAsync(tls, new
                    {
                        type = "auth",
                        version = ProtocolV2.Version,
                        mode = "pin",
                        proof = Convert.ToBase64String(proof)
                    });
                    JsonElement failed = await ReadJsonAsync(tls);
                    Check(failed.GetProperty("type").GetString() == "authFailed", $"authFailed ausente na tentativa {attempt}.");
                    retry = failed.GetProperty("retryAfterSeconds").GetInt32();
                    Check(await WaitForCloseAsync(tls, TimeSpan.FromSeconds(5)), "Conexão permaneceu aberta após falha.");
                }
            }

            Check(retry > 0, "Quinta falha não bloqueou o IP.");

            var (blockedTcp, blockedTls) = await ConnectAsync(limiterServer);
            using (blockedTcp)
            using (blockedTls)
            {
                await SendJsonAsync(blockedTls, new Dictionary<string, object?>
                {
                    ["type"] = "hello",
                    ["version"] = ProtocolV2.Version,
                    ["role"] = "control",
                    ["windowId"] = null,
                    ["clientName"] = "Mac"
                });
                JsonElement blocked = await ReadJsonAsync(blockedTls);
                Check(blocked.GetProperty("type").GetString() == "authFailed", "IP bloqueado não recebeu authFailed.");
                Check(blocked.GetProperty("retryAfterSeconds").GetInt32() > 0, "Bloqueio ativo sem retryAfterSeconds.");
            }

            Console.WriteLine("PASS 5 falhas bloqueiam o IP com retryAfterSeconds > 0");
        }
        finally
        {
            await limiterServer.StopAsync();
        }
    }

    // 4/5. PIN, deviceToken e sessão de controle com a lista injetada.
    byte[] deviceToken;
    {
        var (tcp, tls, salt, nonce) = await StartHandshakeAsync(server, "control", "Mac de teste");
        using (tcp)
        using (tls)
        {
            await SendPinAsync(tls, Pin, salt, nonce);
            JsonElement ready = await ReadJsonAsync(tls);
            Check(ready.GetProperty("type").GetString() == "ready", "ready ausente no modo PIN.");
            Check(ready.GetProperty("role").GetString() == "control", "Papel inesperado no ready.");
            Check(ready.TryGetProperty("host", out _), "Campo host ausente no ready.");
            Check(ready.TryGetProperty("deviceToken", out JsonElement tokenElement), "deviceToken ausente no modo PIN.");
            deviceToken = Convert.FromBase64String(tokenElement.GetString()!);
            Check(deviceToken.Length == HostCredentials.TokenBytes, "deviceToken com tamanho errado.");

            JsonElement windows = await ReadJsonAsync(tls);
            Check(windows.GetProperty("type").GetString() == "windows", "windows ausente logo após ready.");
            Check(windows.GetProperty("windows").GetArrayLength() == 2, "Lista injetada não foi enviada.");
            JsonElement first = windows.GetProperty("windows")[0];
            Check(first.GetProperty("id").GetInt64() == 123, "id da janela inesperado.");
            Check(first.GetProperty("title").GetString() == "Sem título", "título da janela inesperado.");
            Check(first.GetProperty("process").GetString() == "notepad", "processo da janela inesperado.");
            Check(first.GetProperty("width").GetInt32() == 800, "largura da janela inesperada.");
            Check(first.GetProperty("height").GetInt32() == 600, "altura da janela inesperada.");

            await SendJsonAsync(tls, new { type = "list" });
            JsonElement refreshed = await ReadJsonAsync(tls);
            Check(refreshed.GetProperty("type").GetString() == "windows", "list não devolveu windows.");
            Check(refreshed.GetProperty("windows").GetArrayLength() == 2, "list devolveu lista incompleta.");

            await SendJsonAsync(tls, new { type = "ping" });
            JsonElement pong = await ReadJsonAsync(tls);
            Check(pong.GetProperty("type").GetString() == "pong", "pong ausente no modo controle.");
            Console.WriteLine("PASS modo PIN devolve deviceToken no ready");
            Console.WriteLine("PASS role control recebe windows injetado, atende list e ping");
        }
    }

    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "control");
        using (tcp)
        using (tls)
        {
            await SendDeviceAsync(tls, deviceToken, nonce);
            JsonElement ready = await ReadJsonAsync(tls);
            Check(ready.GetProperty("type").GetString() == "ready", "ready ausente no modo device.");
            Check(!ready.TryGetProperty("deviceToken", out _), "deviceToken não deveria existir no modo device.");
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "windows", "windows ausente após ready.");
            Console.WriteLine("PASS reconexão com deviceToken devolve ready sem deviceToken");
        }
    }

    // 6. JSON fragmentado byte a byte.
    {
        var (tcp, tls, salt, nonce) = await StartHandshakeAsync(server, "control", byteByByte: true);
        using (tcp)
        using (tls)
        {
            await SendPinAsync(tls, Pin, salt, nonce, byteByByte: true);
            JsonElement ready = await ReadJsonAsync(tls);
            Check(ready.GetProperty("type").GetString() == "ready", "JSON fragmentado byte a byte não foi aceito.");
            Console.WriteLine("PASS JSON fragmentado byte a byte continua aceito");
        }
    }

    // 7. Mídia PNG: codec 3, seq crescente e tamanho correto.
    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "view");
        using (tcp)
        using (tls)
        {
            await SendDeviceAsync(tls, deviceToken, nonce);
            JsonElement ready = await ReadJsonAsync(tls);
            Check(ready.GetProperty("type").GetString() == "ready", "ready ausente no modo view.");

            uint expected = 1;
            for (int i = 0; i < 3; i++)
            {
                var frame = await ReadRawFrameAsync(tls);
                Check(frame.Kind == ProtocolV2.KindMedia, "Esperava mídia após ready.");
                Check(frame.Codec == ProtocolV2.CodecPng, "codec diferente de PNG.");
                Check((frame.Flags & ProtocolV2.FlagKeyframe) != 0, "Fase 1 exige keyframe.");
                Check(frame.Sequence == expected, $"seq inesperado: {frame.Sequence}.");
                Check(frame.Width == 4 && frame.Height == 3, "Dimensões do frame inesperadas.");
                Check(frame.Payload.Length == 5, "Tamanho do payload inesperado.");
                expected++;
            }

            Console.WriteLine("PASS mídia usa codec 3 (PNG), keyframe, seq crescente e tamanho correto");
        }
    }

    // 7b. Vídeo H.264: só onde existe encoder (Windows); em outros sistemas é ignorado.
    {
        bool encoderAvailable;
        try { using var probe = new MfH264Encoder(64, 48, 30, 1_000_000); encoderAvailable = true; }
        catch (Exception) { encoderAvailable = false; }

        if (!encoderAvailable)
        {
            Console.WriteLine("SKIP sem encoder H.264 neste sistema (fallback PNG)");
        }
        else
        {
            using var videoServer = new RemoteServer(
                new IntPtr(1),
                IPAddress.Loopback,
                0,
                credentials,
                certificate: null,
                windowList: () => windowList,
                enableVideo: true);
            videoServer.Start();
            // Este servidor tem credenciais próprias: entra pelo PIN.
            var (tcp, tls, salt, nonce) = await StartHandshakeAsync(videoServer, "view");
            using (tcp)
            using (tls)
            {
                await SendPinAsync(tls, Pin, salt, nonce);
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente no modo view de vídeo.");

                JsonElement video = await ReadJsonAsync(tls);
                Check(video.GetProperty("type").GetString() == "video", "Mensagem 'video' ausente antes do quadro.");
                Check(video.GetProperty("codec").GetString() == "h264", "Codec anunciado diferente de h264.");

                var frame = await ReadRawFrameAsync(tls);
                Check(frame.Kind == ProtocolV2.KindMedia, "Esperava mídia de vídeo.");
                Check(frame.Codec == ProtocolV2.CodecH264, "Esperava codec H.264 (1).");
                Check(frame.Width == 320 && frame.Height == 240, "Dimensões do frame de vídeo inesperadas.");
                Check(frame.Payload.Length > 0, "Frame de vídeo vazio.");
                Console.WriteLine("PASS pipeline de vídeo entrega H.264 com codec, dimensões e payload");
            }
        }
    }

    // 7c. Detecção de keyframe por NAL IDR: garante a marcação mesmo sem o
    // atributo opcional de clean point do MFT.
    {
        byte[] annexB = [0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1E, 0, 0, 0, 1, 0x65, 0x88];
        Check(MfH264Encoder.ContainsIdr(annexB), "NAL IDR não foi reconhecido.");
        Check(!MfH264Encoder.ContainsIdr([0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x41, 0x88]),
            "NAL não-IDR foi tratado como keyframe.");
        Check(!MfH264Encoder.ContainsIdr([]), "Fluxo vazio foi tratado como keyframe.");
        Console.WriteLine("PASS keyframe é detectada pelo NAL IDR");
    }

    // 8. Recusa de entrada não encerra a sessão.
    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "view");
        using (tcp)
        using (tls)
        {
            await SendDeviceAsync(tls, deviceToken, nonce);
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");

            await SendJsonAsync(tls, new { type = "click", x = 0.5, y = 0.5, button = "left" });
            bool rejected = false;
            for (int i = 0; i < 20 && !rejected; i++)
            {
                var frame = await ReadRawFrameAsync(tls);
                if (frame.Kind != ProtocolV2.KindControl) continue;
                using JsonDocument document = JsonDocument.Parse(frame.Payload);
                if (document.RootElement.GetProperty("type").GetString() != "inputRejected") continue;
                rejected = true;
                Check(
                    document.RootElement.GetProperty("message").GetString() == "Entrada não habilitada no teste de protocolo.",
                    "Motivo da recusa inesperado.");
            }

            Check(rejected, "inputRejected não foi recebido.");

            await SendJsonAsync(tls, new { type = "ping" });
            bool pong = false;
            bool mediaAfter = false;
            for (int i = 0; i < 30 && !(pong && mediaAfter); i++)
            {
                var frame = await ReadRawFrameAsync(tls);
                if (frame.Kind == ProtocolV2.KindMedia)
                {
                    mediaAfter = true;
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(frame.Payload);
                if (document.RootElement.GetProperty("type").GetString() == "pong") pong = true;
            }

            Check(pong, "pong ausente após a recusa de entrada.");
            Check(mediaAfter, "Streaming não continuou após a recusa de entrada.");
            Console.WriteLine("PASS recusa de entrada não encerra a sessão");
        }
    }

    // 9. Controle acima de 8 KiB.
    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "control");
        using (tcp)
        using (tls)
        {
            await SendDeviceAsync(tls, deviceToken, nonce);
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "windows", "windows ausente.");

            byte[] body = new byte[1 + 9000];
            body[0] = ProtocolV2.KindControl;
            Array.Fill(body, (byte)'a', 1, 9000);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
            await tls.WriteAsync(header, ct);
            await tls.WriteAsync(body, ct);
            await tls.FlushAsync(ct);

            Check(await WaitForCloseAsync(tls, TimeSpan.FromSeconds(5)), "Controle acima de 8 KiB não encerrou a conexão.");
            Console.WriteLine("PASS controle acima de 8 KiB é rejeitado");
        }
    }

    // 10. StopAsync encerra conexões e libera a porta.
    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "control");
        await SendDeviceAsync(tls, deviceToken, nonce);
        Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");
        Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "windows", "windows ausente.");

        int boundPort = server.Port;
        await server.StopAsync();
        Check(server.Completion.IsCompleted, "StopAsync não encerrou o loop de aceitação.");
        Check(await WaitForCloseAsync(tls, TimeSpan.FromSeconds(5)), "StopAsync não fechou a conexão ativa.");
        tls.Dispose();
        tcp.Dispose();

        var probe = new TcpListener(IPAddress.Loopback, boundPort) { ExclusiveAddressUse = false };
        probe.Start();
        probe.Stop();
        Console.WriteLine("PASS StopAsync encerra conexões e libera a porta");
    }
}
finally
{
    await server.StopAsync();
}

Console.WriteLine("OK");
