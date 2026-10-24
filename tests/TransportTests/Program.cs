using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowProbe.Remote;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
var ct = timeout.Token;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var outerFrame = new WindowCommands.Bounds(100, 80, 1016, 618);
var visibleFrame = new WindowCommands.Bounds(108, 110, 1008, 610);
var roomyWorkArea = new WindowCommands.Bounds(0, 0, 2000, 1200);
Check(WindowCommands.TryCalculateReadingBounds(960, 540, 96, outerFrame, visibleFrame, roomyWorkArea, out var fitted)
    && fitted == new WindowCommands.Bounds(100, 80, 1076, 658), "Reading não ajustou o quadro 16:9 à área de vídeo.");
Check(WindowCommands.TryCalculateReadingBounds(640, 360, 144, outerFrame, visibleFrame,
        new WindowCommands.Bounds(0, 0, 1024, 768), out fitted)
    && fitted.Width == 976 && fitted.Height == 578, "Reading não converteu pontos por DPI mantendo proporção.");
Check(WindowCommands.TryCalculateReadingBounds(1920, 1080, 96, outerFrame, visibleFrame,
        new WindowCommands.Bounds(0, 0, 1000, 700), out fitted)
    && fitted.Width == 1000 && fitted.Height <= 700
    && Math.Abs((double)(fitted.Width - 16) / (fitted.Height - 38) - 16d / 9) < .003,
    $"Limite do work area alterou a proporção do quadro: {fitted.Width}×{fitted.Height}.");
Check(WindowCommands.TryCalculateReadingBounds(600, 1200, 96, outerFrame, visibleFrame,
        new WindowCommands.Bounds(0, 0, 500, 900), out fitted)
    && fitted.Width <= 500 && fitted.Height <= 900
    && Math.Abs((double)(fitted.Width - 16) / (fitted.Height - 38) - .5) < .003,
    "Limite do work area alterou a proporção de um quadro retrato.");
Check(WindowCommands.TryCalculateReadingBounds(15000, 5000, 384, outerFrame, visibleFrame,
        new WindowCommands.Bounds(0, 0, 10000, 7000), out fitted)
    && fitted.Width <= 10000 && fitted.Height <= 7000
    && Math.Abs((double)(fitted.Width - 16) / (fitted.Height - 38) - 3) < .001,
    "Pedido panorâmico em DPI alto não manteve proporção sob limite do monitor.");
Check(WindowCommands.TryCalculateReadingBounds(15000, 5000, 384, outerFrame, visibleFrame,
        new WindowCommands.Bounds(0, 0, 40000, 25000), out fitted)
    && fitted.Width - 16 <= 32768
    && Math.Abs((double)(fitted.Width - 16) / (fitted.Height - 38) - 3) < .001,
    "Teto de dimensão alterou a proporção do pedido em DPI alto.");
Check(WindowCommands.TryCalculateReadingBounds(1, 1, 96, outerFrame, visibleFrame, roomyWorkArea, out fitted)
    && fitted.Width == 17 && fitted.Height == 39, "Dimensões mínimas válidas não foram preservadas.");
Check(!WindowCommands.TryCalculateReadingBounds(0, 1, 96, outerFrame, visibleFrame, roomyWorkArea, out _)
    && !WindowCommands.TryCalculateReadingBounds(1, 1, 0, outerFrame, visibleFrame, roomyWorkArea, out _)
    && !WindowCommands.TryCalculateReadingBounds(100, 100, 96, outerFrame, visibleFrame,
        new WindowCommands.Bounds(0, 0, 16, 38), out _)
    && !WindowCommands.TryCalculateReadingBounds(100, 100, 96, outerFrame,
        new WindowCommands.Bounds(90, 110, 1008, 610), roomyWorkArea, out _),
    "Geometria inválida ou impossível foi aceita.");
Console.WriteLine("PASS geometria de reading preserva proporção, DPI, insets e limites do monitor");

// A cadência mantém 30/60 FPS com jitter e não libera rajadas após pausas.
foreach (int targetFps in new[] { 30, 60 })
foreach (int intervalMs in (targetFps == 30 ? new[] { 16, 17, 32, 33 } : new[] { 8, 9, 15, 16 }))
{
    var pacer = new VideoFramePacer(targetFps);
    int accepted = 0;
    for (int ms = 0; ms < 10_000; ms += intervalMs)
        if (pacer.TryAccept(TimeSpan.FromMilliseconds(ms))) accepted++;
    Check(accepted >= targetFps * 10 - 1 && accepted <= targetFps * 10, $"Cadência incorreta: {accepted} em 10 s ({intervalMs} ms).");
    Check(pacer.TryAccept(TimeSpan.FromSeconds(20)), "Não retomou após pausa.");
    Check(!pacer.TryAccept(TimeSpan.FromSeconds(20)), "Aceitou rajada no mesmo instante.");
}
Console.WriteLine("PASS cadência de 30/60 FPS com jitter e retomada sem rajadas");

// O lançador só aceita os aplicativos da lista segura; comando vazio, caminho
// arbitrário ou linha de comando composta não chegam ao Process.Start.
var knownApps = AppLauncher.GetLegacyApps();
Check(knownApps.Count == 7, $"Lista de aplicativos inesperada: {knownApps.Count}.");
foreach (string id in new[] { "notepad", "calc", "explorer", "mspaint", "powershell", "cmd", "edge" })
    Check(knownApps.Any(app => app.Id == id), $"Aplicativo ausente na lista: {id}.");
Check(knownApps.All(app => app.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
    "Aplicativo conhecido sem executável .exe.");
Check(!AppLauncher.Launch(null) && !AppLauncher.Launch("") && !AppLauncher.Launch("  "),
    "Lançador aceitou comando vazio.");
Check(!AppLauncher.Launch("cmd.exe /c calc"), "Lançador aceitou linha de comando composta.");
Check(!AppLauncher.Launch("../../Windows/System32/calc.exe"), "Lançador aceitou caminho arbitrário.");
Check(!AppLauncher.Launch("notepad2"), "Lançador aceitou aplicativo desconhecido.");
Console.WriteLine("PASS lançador restrito à lista segura de aplicativos");

// quality reconfigura a cadência de entrega e limita valores fora da faixa;
// o encoder é recriado no próximo quadro, sem bloquear a recepção.
using (var qualityChannel = new RemoteServer.VideoChannel())
{
    Check(qualityChannel.TargetFps == RemoteServer.VideoFps
        && qualityChannel.TargetBitrate == RemoteServer.VideoBitrate,
        "Qualidade inicial diferente do padrão.");
    int acceptedAt60 = 0;
    for (int ms = 0; ms < 1000; ms += 16)
        if (qualityChannel.ShouldAcceptFrame(TimeSpan.FromMilliseconds(ms))) acceptedAt60++;
    Check(acceptedAt60 is >= 59 and <= 63, $"Cadência inicial inesperada: {acceptedAt60}.");

    qualityChannel.UpdateFps(30);
    Check(qualityChannel.TargetFps == 30, "fps não foi atualizado.");
    int acceptedAt30 = 0;
    for (int ms = 0; ms < 1000; ms += 16)
        if (qualityChannel.ShouldAcceptFrame(TimeSpan.FromMilliseconds(ms))) acceptedAt30++;
    Check(acceptedAt30 is >= 29 and <= 31, $"Cadência de 30 FPS inesperada: {acceptedAt30}.");

    qualityChannel.UpdateBitrate(4_000_000);
    Check(qualityChannel.TargetBitrate == 4_000_000, "bitrate não foi atualizado.");

    qualityChannel.UpdateFps(0);
    qualityChannel.UpdateBitrate(-1);
    Check(qualityChannel.TargetFps == RemoteServer.MinVideoFps
        && qualityChannel.TargetBitrate == RemoteServer.MinVideoBitrate,
        "Valores abaixo da faixa não foram limitados.");

    qualityChannel.UpdateFps(10_000);
    qualityChannel.UpdateBitrate(int.MaxValue);
    Check(qualityChannel.TargetFps == RemoteServer.MaxVideoFps
        && qualityChannel.TargetBitrate == RemoteServer.MaxVideoBitrate,
        "Valores acima da faixa não foram limitados.");
}
Console.WriteLine("PASS quality reconfigura pacer/bitrate e limita valores extremos");

// Solicitação e liberação de energia precisam ocorrer na mesma thread, e o
// monitor precisa ser acordado uma vez, antes de qualquer espera de vídeo.
var powerCalls = new List<(uint Flags, int Thread)>();
int monitorWakes = 0;
var wakeProbe = new DisplayWakeLock(
    flags =>
    {
        powerCalls.Add((flags, Environment.CurrentManagedThreadId));
        // O retorno da API é o estado anterior; qualquer valor não nulo indica
        // que a chamada foi aceita.
        return DisplayWakeLock.Active;
    },
    () => monitorWakes++);
Check(wakeProbe.Acquired, "Solicitação de energia não adquirida.");
Check(wakeProbe.MonitorWoken && monitorWakes == 1, "Monitor não foi acordado exatamente uma vez.");
wakeProbe.Dispose();
wakeProbe.Dispose();
Check(powerCalls.Count == 2 && powerCalls[0].Flags == DisplayWakeLock.Active
    && powerCalls[1].Flags == DisplayWakeLock.Continuous
    && powerCalls[0].Thread == powerCalls[1].Thread, "Solicitação de energia vazou ou mudou de thread.");
Check(monitorWakes == 1, "Monitor foi acordado de novo ao liberar.");
Console.WriteLine("PASS tela acordada, monitor religado e liberação na mesma thread");

// Durante a sessão o pedido é reafirmado e o monitor é reacordado, para cobrir
// nova entrada em stand-by; ao liberar, para de reafirmar.
int keepAliveCalls = 0;
var keepAlive = new DisplayWakeLock(_ => { keepAliveCalls++; return DisplayWakeLock.Active; },
    () => { }, TimeSpan.FromMilliseconds(20));
Check(keepAlive.Acquired, "Lock com reafirmação não foi adquirido.");
Thread.Sleep(200);
int afterWait = keepAliveCalls;
Check(afterWait >= 3, $"Pedido de energia não foi reafirmado (chamadas: {afterWait}).");
Check(keepAlive.MonitorWakeCount >= 3, $"Monitor não foi reacordado (vezes: {keepAlive.MonitorWakeCount}).");
keepAlive.Dispose();
int afterDispose = keepAliveCalls;
Thread.Sleep(120);
Check(keepAliveCalls == afterDispose, "Pedido de energia continuou após o Dispose.");
Console.WriteLine("PASS tela sustentada durante a sessão e liberada ao encerrar");

// A API devolve o estado anterior. Qualquer valor não nulo indica chamada
// aceita; 0 é falha e não pode ser mascarada como sucesso.
int monitorWakesOnFailure = 0;
var failedWake = new DisplayWakeLock(_ => 0, () => monitorWakesOnFailure++);
Check(!failedWake.Acquired, "Lock alegou tela acordada com retorno 0 da API.");
Check(monitorWakesOnFailure == 0, "Monitor foi acordado mesmo com o pedido recusado.");
failedWake.Dispose();
Console.WriteLine("PASS falha do pedido de energia não é mascarada");

const string Pin = "246810";
var credentials = HostCredentials.Create(Pin);
var windowList = new WindowDescriptor[]
{
    new(123, "Sem título", "notepad", 800, 600),
    new(456, "Outra janela", "calc", 640, 480)
};

// Clipboard falso controlado pelo teste: o transporte também é validado no Mac,
// onde não existe user32.dll. A escrita simula a mudança de sequência que o
// Windows produz ao copiar texto.
uint clipboardSequence = 1;
string clipboardText = "";
string? clipboardWritten = null;
var clipboard = new WindowsClipboard(
    sequenceNumber: () => Volatile.Read(ref clipboardSequence),
    read: () => (true, Volatile.Read(ref clipboardText)),
    write: value =>
    {
        Volatile.Write(ref clipboardWritten, value);
        Volatile.Write(ref clipboardText, value);
        Interlocked.Increment(ref clipboardSequence);
        return true;
    });

var server = new RemoteServer(
    new IntPtr(1),
    IPAddress.Loopback,
    0,
    credentials,
    certificate: null,
    windowList: () => windowList,
    enableVideo: false,
    clipboard: clipboard);
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

async Task<JsonElement> ReadControlJsonAsync(Stream stream)
{
    for (int i = 0; i < 256; i++)
    {
        var frame = await ReadRawFrameAsync(stream);
        if (frame.Kind != ProtocolV2.KindControl) continue;
        using JsonDocument document = JsonDocument.Parse(frame.Payload);
        return document.RootElement.Clone();
    }
    throw new Exception("Controle não recebido antes do limite de quadros.");
}

async Task<(TcpClient Tcp, SslStream Tls, byte[] Salt, byte[] Nonce)> StartHandshakeAsync(
    RemoteServer target, string role, string name = "Mac", bool byteByByte = false, bool appCatalog = false,
    long? windowId = null, bool windowCommands = false)
{
    var (tcp, tls) = await ConnectAsync(target);
    await SendJsonAsync(tls, new Dictionary<string, object?>
    {
        ["type"] = "hello",
        ["version"] = ProtocolV2.Version,
        ["role"] = role,
        ["windowId"] = windowId,
        ["clientName"] = name,
        ["appCatalog"] = appCatalog,
        ["windowCommands"] = windowCommands
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
            // A sessão autenticada deve sobreviver ao prazo inicial de 20 s.
            for (int heartbeat = 0; heartbeat < 7; heartbeat++)
            {
                await Task.Delay(3000, ct);
                await SendJsonAsync(tls, new { type = "ping" });
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "pong",
                    "Sessão autenticada terminou com o prazo do handshake.");
            }
            Console.WriteLine("PASS sessão autenticada permanece ativa após o prazo de handshake");
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

    // 5b. Lançador de aplicativos: a lista segura acompanha a mensagem windows
    // e a mensagem launch chega ao lançador nas duas sessões. O lançador é
    // injetado para o teste não abrir processos reais na máquina de teste.
    {
        var launched = new ConcurrentQueue<string>();
        string installedAppId = "app-" + new string('a', 64);
        using var appServer = new RemoteServer(
            new IntPtr(1),
            IPAddress.Loopback,
            0,
            credentials,
            certificate: null,
            windowList: () => windowList,
            enableVideo: false,
            clipboard: clipboard,
            appLauncher: app => { launched.Enqueue(app); return true; });
        appServer.Start();
        try
        {
            var (tcp, tls, salt, nonce) = await StartHandshakeAsync(appServer, "control");
            using (tcp)
            using (tls)
            {
                // Servidor isolado: o PIN cria um deviceToken próprio para ele.
                await SendPinAsync(tls, Pin, salt, nonce);
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");
                JsonElement windows = await ReadJsonAsync(tls);
                Check(windows.GetProperty("type").GetString() == "windows", "windows ausente.");
                JsonElement apps = windows.GetProperty("apps");
                Check(apps.GetArrayLength() == 7, "Lista de aplicativos não foi enviada.");
                JsonElement notepad = apps.EnumerateArray()
                    .First(app => app.GetProperty("id").GetString() == "notepad");
                Check(notepad.GetProperty("name").GetString()!.Length > 0, "Nome do aplicativo vazio.");
                Check(notepad.GetProperty("cmd").GetString() == "notepad.exe", "Comando do aplicativo inesperado.");

                await SendJsonAsync(tls, new { type = "launch", app = "notepad" });
                for (int i = 0; i < 100 && !launched.Contains("notepad"); i++) await Task.Delay(20, ct);
                Check(launched.Contains("notepad"), "launch não chegou ao lançador na sessão de controle.");
                await SendJsonAsync(tls, new { type = "launch", app = installedAppId });
                for (int i = 0; i < 100 && launched.Count(value => value == installedAppId) < 1; i++) await Task.Delay(20, ct);
                Check(launched.Count(value => value == installedAppId) == 1, "ID opaco de 68 caracteres foi recusado no controle.");

                await SendJsonAsync(tls, new { type = "ping" });
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "pong", "pong ausente após launch.");
            }

            var (viewTcp, viewTls, viewSalt, viewNonce) = await StartHandshakeAsync(appServer, "view");
            using (viewTcp)
            using (viewTls)
            {
                await SendPinAsync(viewTls, Pin, viewSalt, viewNonce);
                Check((await ReadJsonAsync(viewTls)).GetProperty("type").GetString() == "ready",
                    "ready ausente na sessão de visualização.");
                await SendJsonAsync(viewTls, new { type = "launch", app = "calc" });
                for (int i = 0; i < 100 && !launched.Contains("calc"); i++) await Task.Delay(20, ct);
                Check(launched.Contains("calc"), "launch não chegou ao lançador na sessão de visualização.");
                await SendJsonAsync(viewTls, new { type = "launch", app = installedAppId });
                for (int i = 0; i < 100 && launched.Count(value => value == installedAppId) < 2; i++) await Task.Delay(20, ct);
                Check(launched.Count(value => value == installedAppId) == 2, "ID opaco foi recusado na visualização.");
            }

            var (catalogTcp, catalogTls, catalogSalt, catalogNonce) = await StartHandshakeAsync(appServer, "control", appCatalog: true);
            using (catalogTcp)
            using (catalogTls)
            {
                await SendPinAsync(catalogTls, Pin, catalogSalt, catalogNonce);
                Check((await ReadJsonAsync(catalogTls)).GetProperty("type").GetString() == "ready", "ready do catálogo ausente.");
                Check((await ReadJsonAsync(catalogTls)).GetProperty("type").GetString() == "windows", "windows do catálogo ausente.");
                var catalogIds = new HashSet<string>(StringComparer.Ordinal);
                int chunks = 1;
                for (int index = 0; index < chunks; index++)
                {
                    JsonElement part = await ReadJsonAsync(catalogTls);
                    Check(part.GetProperty("type").GetString() == "apps" && part.GetProperty("index").GetInt32() == index,
                        "Catálogo não foi recebido em ordem.");
                    chunks = part.GetProperty("total").GetInt32();
                    Check(chunks is >= 1 and <= 4096 && Encoding.UTF8.GetByteCount(part.GetRawText()) <= ProtocolV2.MaxControlHostToClient,
                        "Chunk do catálogo excede o contrato.");
                    foreach (JsonElement app in part.GetProperty("apps").EnumerateArray())
                        Check(catalogIds.Add(app.GetProperty("cmd").GetString()!), "ID duplicado no catálogo.");
                }
                Check(catalogIds.Count == Math.Min(4096, AppLauncher.GetKnownApps().Count), "Catálogo negociado incompleto.");
                await SendJsonAsync(catalogTls, new { type = "ping" });
                Check((await ReadJsonAsync(catalogTls)).GetProperty("type").GetString() == "pong", "Catálogo interrompeu o controle.");
            }

            Console.WriteLine("PASS launch legado/ID opaco nas duas sessões e catálogo negociado em chunks");
        }
        finally
        {
            await appServer.StopAsync();
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

    // 6b. Clipboard: recebe o texto copiado no Mac e envia mudanças locais do
    // Windows, sem devolver como eco o texto que acabou de ser aplicado.
    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "control");
        using (tcp)
        using (tls)
        {
            await SendDeviceAsync(tls, deviceToken, nonce);
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "windows", "windows ausente.");

            const string FromMac = "Texto copiado no Mac ✓";
            await SendJsonAsync(tls, new { type = "clipboard", text = FromMac });

            // A escrita é aplicada no ponto de leitura do servidor; aguarda o efeito.
            for (int i = 0; i < 100 && Volatile.Read(ref clipboardWritten) != FromMac; i++)
                await Task.Delay(20, ct);
            Check(Volatile.Read(ref clipboardWritten) == FromMac, "Texto do Mac não foi aplicado ao clipboard.");

            // Mudança local posterior à última recebida vira mensagem para o Mac.
            const string FromWindows = "Texto copiado no Windows";
            Volatile.Write(ref clipboardText, FromWindows);
            Interlocked.Increment(ref clipboardSequence);
            JsonElement changed = await ReadJsonAsync(tls);
            Check(changed.GetProperty("type").GetString() == "clipboard", "Host não enviou mensagem clipboard.");
            Check(changed.GetProperty("text").GetString() == FromWindows, "Texto enviado ao Mac inesperado.");

            // O caminho de leitura continua vivo depois do clipboard.
            await SendJsonAsync(tls, new { type = "ping" });
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "pong", "pong ausente após clipboard.");
            Console.WriteLine("PASS clipboard recebe texto do Mac e envia mudanças locais");
        }
    }

    // 6c. Qualidade adaptativa: fps e bitrate são aceitos na sessão de
    // visualização, valores ausentes/extremos não encerram o streaming.
    {
        var (tcp, tls, _, nonce) = await StartHandshakeAsync(server, "view");
        using (tcp)
        using (tls)
        {
            await SendDeviceAsync(tls, deviceToken, nonce);
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");

            await SendJsonAsync(tls, new { type = "quality", fps = 30, bitrate = 4_000_000 });
            await SendJsonAsync(tls, new { type = "quality", fps = 0, bitrate = -1 });
            await SendJsonAsync(tls, new { type = "quality" });

            await SendJsonAsync(tls, new { type = "ping" });
            bool pong = false;
            for (int i = 0; i < 30 && !pong; i++)
            {
                var frame = await ReadRawFrameAsync(tls);
                if (frame.Kind != ProtocolV2.KindControl) continue;
                using JsonDocument document = JsonDocument.Parse(frame.Payload);
                if (document.RootElement.GetProperty("type").GetString() == "pong") pong = true;
            }

            Check(pong, "pong ausente após quality.");
            Console.WriteLine("PASS quality ajusta fps/bitrate e não encerra a sessão");
        }
    }

    // 6d. Arquivos: fileStart/fileChunk montam o arquivo no destino e o último
    // bloco confirma com fileReceived. O nome do Mac nunca vira caminho.
    {
        string inbox = Path.Combine(
            Path.GetTempPath(), "remoteDesktop-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inbox);
        using var fileReceiver = new FileReceiver(() => inbox);
        using var fileServer = new RemoteServer(
            new IntPtr(1),
            IPAddress.Loopback,
            0,
            credentials,
            certificate: null,
            windowList: () => windowList,
            enableVideo: false,
            fileReceiver: fileReceiver);
        fileServer.Start();
        try
        {
            var (tcp, tls, salt, nonce) = await StartHandshakeAsync(fileServer, "control");
            using (tcp)
            using (tls)
            {
                await SendPinAsync(tls, Pin, salt, nonce);
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente.");
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "windows", "windows ausente.");

                const string FromMac = "../../relatorio:final?.txt";
                byte[] first = Encoding.UTF8.GetBytes("Olá, ");
                byte[] second = Encoding.UTF8.GetBytes("arquivo remoto ✓");

                await SendJsonAsync(tls, new { type = "fileStart", name = FromMac });
                await SendJsonAsync(tls, new
                {
                    type = "fileChunk",
                    name = FromMac,
                    data = Convert.ToBase64String(first),
                    isFirst = true
                });
                await SendJsonAsync(tls, new
                {
                    type = "fileChunk",
                    name = FromMac,
                    data = Convert.ToBase64String(second),
                    isLast = true
                });

                JsonElement received = await ReadJsonAsync(tls);
                Check(received.GetProperty("type").GetString() == "fileReceived", "fileReceived ausente.");
                string savedName = received.GetProperty("name").GetString()!;
                Check(savedName == "relatorio_final_.txt", $"Nome higienizado inesperado: {savedName}.");

                string fullPath = Path.Combine(inbox, savedName);
                Check(File.Exists(fullPath), "Arquivo não foi salvo em Downloads/customDir.");
                Check(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fullPath, ct)) == "Olá, arquivo remoto ✓",
                    "Conteúdo do arquivo recebido inesperado.");

                // O caminho de leitura continua vivo depois do arquivo.
                await SendJsonAsync(tls, new { type = "ping" });
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "pong", "pong ausente após arquivo.");
                Console.WriteLine("PASS arquivo em chunks é salvo com nome higienizado e confirmado");
            }
        }
        finally
        {
            await fileServer.StopAsync();
            try { Directory.Delete(inbox, recursive: true); } catch (IOException) { }
        }
    }

    // 6e. Áudio PCM: o gerador entrega blocos de 10 ms em 48 kHz/16-bit/estéreo
    // mesmo sem sessão de áudio; Start/Stop são idempotentes e seguros.
    {
        using var audio = new WindowsAudioCapture(usePcmGenerator: true);
        var blocks = new ConcurrentQueue<(byte[] Pcm, uint PtsMs)>();
        audio.OnAudioData = (pcm, pts) => blocks.Enqueue((pcm, pts));

        Check(audio.Start(), "Gerador de áudio não iniciou.");
        Check(audio.Start(), "Start repetido não foi idempotente.");
        Check(audio.IsCapturing, "Gerador não ficou ativo.");

        for (int i = 0; i < 100 && blocks.Count < 3; i++) await Task.Delay(10, ct);

        audio.Stop();
        audio.Stop();
        Check(!audio.IsCapturing, "Stop não encerrou o gerador.");
        Check(blocks.Count >= 3, $"Poucos blocos de áudio: {blocks.Count}.");
        Check(blocks.All(block => block.Pcm.Length == WindowsAudioCapture.BlockBytes),
            "Bloco de áudio com tamanho diferente de 10 ms estéreo.");
        Check(blocks.Any(block => block.Pcm.Any(value => value != 0)), "PCM gerado está em silêncio.");
        uint previousPts = 0;
        foreach ((byte[] _, uint ptsMs) in blocks)
        {
            Check(ptsMs >= previousPts, "PTS do áudio não é monotônico.");
            previousPts = ptsMs;
        }

        // Fora do Windows não existe sessão WASAPI: a captura fica inerte em
        // vez de falhar ou inventar áudio.
        using var inert = new WindowsAudioCapture();
        if (!OperatingSystem.IsWindows()) Check(!inert.Start(), "Captura sem WASAPI não deveria iniciar fora do Windows.");
        inert.Stop();
        Console.WriteLine("PASS áudio PCM em 48 kHz/16-bit/estéreo com Start/Stop seguros");
    }

    // 6f. A sessão de visualização trata fileStart/fileChunk no mesmo laço que
    // recebe o vídeo. Roda só no Windows: no Mac o caminho PNG para em
    // PopupComposer, limitação preexistente e anterior a este escopo.
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("SKIP arquivo na sessão view exige captura real (Windows)");
    }
    else
    {
        string viewInbox = Path.Combine(
            Path.GetTempPath(), "remoteDesktop-files-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(viewInbox);
        using var viewReceiver = new FileReceiver(() => viewInbox);
        using var viewServer = new RemoteServer(
            new IntPtr(1),
            IPAddress.Loopback,
            0,
            credentials,
            certificate: null,
            windowList: () => windowList,
            enableVideo: false,
            fileReceiver: viewReceiver);
        viewServer.Start();
        try
        {
            var (tcp, tls, salt, nonce) = await StartHandshakeAsync(viewServer, "view");
            using (tcp)
            using (tls)
            {
                await SendPinAsync(tls, Pin, salt, nonce);
                Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready",
                    "ready ausente na sessão view.");

                await SendJsonAsync(tls, new
                {
                    type = "fileChunk",
                    name = "vista.txt",
                    data = Convert.ToBase64String(Encoding.UTF8.GetBytes("via sessão view")),
                    isFirst = true,
                    isLast = true
                });

                bool confirmed = false;
                for (int i = 0; i < 60 && !confirmed; i++)
                {
                    var frame = await ReadRawFrameAsync(tls);
                    if (frame.Kind != ProtocolV2.KindControl) continue;
                    using JsonDocument document = JsonDocument.Parse(frame.Payload);
                    if (document.RootElement.GetProperty("type").GetString() != "fileReceived") continue;

                    Check(document.RootElement.GetProperty("name").GetString() == "vista.txt",
                        "Nome do arquivo da sessão view inesperado.");
                    confirmed = true;
                }

                Check(confirmed, "fileReceived ausente na sessão de visualização.");
                Check(File.Exists(Path.Combine(viewInbox, "vista.txt")),
                    "Arquivo da sessão view não foi salvo.");
                Console.WriteLine("PASS sessão de visualização também recebe arquivo durante o vídeo");
            }
        }
        finally
        {
            await viewServer.StopAsync();
            try { Directory.Delete(viewInbox, recursive: true); } catch (IOException) { }
        }
    }

    // WINDOWPLACEMENT em Windows não inclui rcDevice (campo exclusivo de builds
    // de header para Mac); Get/SetWindowPlacement exigem sizeof=44.
    {
        Type placement = typeof(WindowCommands).GetNestedType("WINDOWPLACEMENT", System.Reflection.BindingFlags.NonPublic)!;
        Check(System.Runtime.InteropServices.Marshal.SizeOf(placement) == 44, "WINDOWPLACEMENT deve ter 44 bytes no Windows.");
        Check(WindowCommands.NormalizeMenuText("A&&B &File") == "A&B File", "Texto de menu deve preservar ampersand literal e remover mnemônico.");
    }

    // 6e. Negociação de controles de janela: estado, ações e snapshot de menu.
    {
        var fakeCommands = new FakeWindowCommands(123);
        using var commandsServer = new RemoteServer(
            IntPtr.Zero, IPAddress.Loopback, 0, credentials,
            windowList: () => windowList, enableVideo: false,
            windowCommandsFactory: (_, _) => fakeCommands);
        commandsServer.Start();
        var (tcp, tls, salt, nonce) = await StartHandshakeAsync(
            commandsServer, "view", windowId: 123, windowCommands: true);
        using (tcp)
        using (tls)
        {
            await SendPinAsync(tls, Pin, salt, nonce);
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente nos controles de janela.");
            JsonElement initial = await ReadControlJsonAsync(tls);
            Check(initial.GetProperty("type").GetString() == "windowState" && initial.GetProperty("windowId").GetInt64() == 123,
                "Estado inicial da janela ausente ou com HWND incorreto.");

            await SendJsonAsync(tls, new { type = "windowAction", action = "reading", enabled = true, width = 800, height = 600 });
            JsonElement readingResult = await ReadControlJsonAsync(tls);
            JsonElement readingState = await ReadControlJsonAsync(tls);
            Check(readingResult.GetProperty("type").GetString() == "windowCommandResult" && readingResult.GetProperty("success").GetBoolean(),
                "Ativação de leitura não foi confirmada.");
            Check(readingState.GetProperty("reading").GetBoolean(), "Estado de leitura não foi atualizado.");

            await SendJsonAsync(tls, new { type = "windowMenus" });
            JsonElement menus = await ReadControlJsonAsync(tls);
            Check(menus.GetProperty("type").GetString() == "windowMenus" && menus.GetProperty("supported").GetBoolean(),
                "Resposta de menus ausente.");
            await SendJsonAsync(tls, new { type = "windowMenuInvoke", id = "fake:1" });
            JsonElement menuResult = await ReadControlJsonAsync(tls);
            Check(menuResult.GetProperty("type").GetString() == "windowCommandResult" && menuResult.GetProperty("success").GetBoolean(),
                "Invocação de menu não foi confirmada.");

            await SendJsonAsync(tls, new { type = "windowAction", action = "maximize" });
            JsonElement maximizeResult = await ReadControlJsonAsync(tls);
            JsonElement maximizeState = await ReadControlJsonAsync(tls);
            Check(maximizeResult.GetProperty("success").GetBoolean() && maximizeState.GetProperty("maximized").GetBoolean()
                && !maximizeState.GetProperty("reading").GetBoolean(), "Maximizar não encerrou leitura antes da ação.");
            Console.WriteLine("PASS windowCommands negociados publicam estado, menus e resultados de ação");
        }
        await commandsServer.StopAsync();
    }

    // 6f. Menus maiores que o limite viram fallback sem encerrar a sessão view.
    {
        var largeItems = Enumerable.Range(0, 128)
            .Select(index => new RemoteWindowMenuItem("large:" + index, new string('界', 128), true, false, false, []))
            .ToArray();
        Check(JsonSerializer.SerializeToUtf8Bytes(largeItems).Length > ProtocolV2.MaxControlHostToClient,
            "Fixture de menu grande não ultrapassa o limite JSON do protocolo.");
        var fakeCommands = new FakeWindowCommands(123, new RemoteWindowMenus(true, largeItems));
        using var commandsServer = new RemoteServer(
            IntPtr.Zero, IPAddress.Loopback, 0, credentials,
            windowList: () => windowList, enableVideo: false,
            windowCommandsFactory: (_, _) => fakeCommands);
        commandsServer.Start();
        var (tcp, tls, salt, nonce) = await StartHandshakeAsync(
            commandsServer, "view", windowId: 123, windowCommands: true);
        using (tcp)
        using (tls)
        {
            await SendPinAsync(tls, Pin, salt, nonce);
            Check((await ReadJsonAsync(tls)).GetProperty("type").GetString() == "ready", "ready ausente no menu grande.");
            _ = await ReadControlJsonAsync(tls); // windowState
            await SendJsonAsync(tls, new { type = "windowMenus" });
            JsonElement menus = await ReadControlJsonAsync(tls);
            Check(menus.GetProperty("type").GetString() == "windowMenus"
                && !menus.GetProperty("supported").GetBoolean()
                && menus.GetProperty("items").GetArrayLength() == 0
                && !string.IsNullOrWhiteSpace(menus.GetProperty("reason").GetString()),
                "Menu acima do limite não retornou fallback curto.");
            await SendJsonAsync(tls, new { type = "ping" });
            Check((await ReadControlJsonAsync(tls)).GetProperty("type").GetString() == "pong",
                "Sessão encerrou após fallback de menu grande.");
            Console.WriteLine("PASS menu grande retorna unsupported sem encerrar a sessão view");
        }
        await commandsServer.StopAsync();
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
            WindowProbe.Capture.ContinuousWindowCapture.RawSize = (640, 360);
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

                JsonElement viewport = await ReadJsonAsync(tls);
                Check(viewport.GetProperty("type").GetString() == "viewport" &&
                      viewport.GetProperty("width").GetInt32() == 640 && viewport.GetProperty("height").GetInt32() == 360,
                      "Viewport inicial não representa os pixels ativos.");
                JsonElement video = await ReadJsonAsync(tls);
                Check(video.GetProperty("type").GetString() == "video", "Mensagem 'video' ausente antes do quadro.");
                Check(video.GetProperty("codec").GetString() == "h264", "Codec anunciado diferente de h264.");

                var frame = await ReadRawFrameAsync(tls);
                Check(frame.Kind == ProtocolV2.KindMedia, "Esperava mídia de vídeo.");
                Check(frame.Codec == ProtocolV2.CodecH264, "Esperava codec H.264 (1).");
                Check(frame.Width == 640 && frame.Height == 368, "Padding inicial do frame de vídeo inesperado.");
                Check(frame.Payload.Length > 0, "Frame de vídeo vazio.");

                await SendJsonAsync(tls, new { type = "quality", fps = 30, bitrate = 3_000_000, maxDimension = 320 });
                bool scaledViewport = false, scaledVideo = false, scaledMedia = false;
                for (int i = 0; i < 60 && !scaledMedia; i++)
                {
                    var next = await ReadRawFrameAsync(tls);
                    if (next.Kind == ProtocolV2.KindControl)
                    {
                        using var message = JsonDocument.Parse(next.Payload);
                        JsonElement root = message.RootElement;
                        if (root.GetProperty("type").GetString() == "viewport")
                            scaledViewport = root.GetProperty("width").GetInt32() == 320 && root.GetProperty("height").GetInt32() == 180;
                        if (root.GetProperty("type").GetString() == "video")
                            scaledVideo = root.GetProperty("width").GetInt32() == 320 && root.GetProperty("height").GetInt32() == 192
                                && root.GetProperty("maxDimension").GetBoolean();
                    }
                    else if (next.Width == 320 && next.Height == 192 && next.Codec == ProtocolV2.CodecH264)
                        scaledMedia = true;
                }
                Check(scaledViewport && scaledVideo && scaledMedia, "Mudança de resolução não atualizou viewport, anúncio e mídia H.264.");
                Console.WriteLine("PASS H.264 e redução real de resolução com viewport ativo e padding");
            }
            WindowProbe.Capture.ContinuousWindowCapture.RawSize = (320, 240);
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
