using System.Buffers.Binary;
using System.Text.Json;

namespace WindowProbe.Remote;

/// <summary>
/// Contrato do protocolo v2 (docs/PROTOCOL.md). Este arquivo é compartilhado
/// com tests/TransportTests, então não pode depender de WPF, DPAPI nem Win32.
/// </summary>
public static class ProtocolV2
{
    public const int Version = 2;
    public const int MaxMessageBytes = 16 * 1024 * 1024;
    public const int MaxControlClientToHost = 8 * 1024;
    public const int MaxControlHostToClient = 64 * 1024;

    public const byte KindControl = 1;
    public const byte KindMedia = 2;

    public const byte CodecH264 = 1;
    public const byte CodecJpeg = 2;
    public const byte CodecPng = 3;

    /// <summary>Áudio PCM 48 kHz, 16 bits, estéreo (intercalado, little-endian).</summary>
    public const byte CodecPcm = 4;

    /// <summary>tipo(1) + codec(1) + flags(2) + seq(4) + ptsMs(4) + largura(2) + altura(2).</summary>
    public const int MediaHeaderBytes = 16;

    public const ushort FlagKeyframe = 1;

    public static byte[] FrameControl(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0 || json.Length > MaxMessageBytes - 1)
        {
            throw new ProtocolException("Mensagem de controle inválida.");
        }

        byte[] framed = new byte[4 + 1 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, json.Length + 1);
        framed[4] = KindControl;
        json.CopyTo(framed.AsSpan(5));
        return framed;
    }

    public static byte[] FrameMedia(
        byte codec,
        bool keyframe,
        uint sequence,
        uint ptsMs,
        ushort width,
        ushort height,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length + MediaHeaderBytes > MaxMessageBytes)
        {
            throw new ProtocolException("Quadro de mídia inválido.");
        }

        byte[] framed = new byte[4 + MediaHeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, MediaHeaderBytes + payload.Length);
        int at = 4;
        framed[at++] = KindMedia;
        framed[at++] = codec;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(at), keyframe ? FlagKeyframe : (ushort)0);
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

}

/// <summary>Uma mensagem completa recebida do outro lado.</summary>
public sealed record IncomingFrame(
    byte Kind,
    byte Codec,
    ushort Flags,
    uint Sequence,
    uint PtsMs,
    ushort Width,
    ushort Height,
    byte[] Payload)
{
    public bool IsControl => Kind == ProtocolV2.KindControl;
    public bool IsKeyframe => (Flags & ProtocolV2.FlagKeyframe) != 0;

    public JsonDocument ParseControl()
    {
        if (!IsControl) throw new ProtocolException("Esperava mensagem de controle.");
        try
        {
            return JsonDocument.Parse(Payload, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw new ProtocolException("JSON inválido.");
        }
    }
}

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>Leitor incremental: aceita fragmentação arbitrária de TCP.</summary>
public static class FrameReader
{
    /// <summary>
    /// Lê um enquadramento completo. <paramref name="maxControlBytes"/> limita
    /// somente mensagens de controle; mídia pode chegar a 16 MiB.
    /// </summary>
    public static async Task<IncomingFrame?> ReadAsync(
        Stream stream, int maxControlBytes, CancellationToken token)
    {
        byte[] header = new byte[4];
        int read = await ReadAtMostAsync(stream, header, token).ConfigureAwait(false);
        if (read == 0) return null;                 // fim limpo
        if (read != 4) throw new ProtocolException("Cabeçalho incompleto.");

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > ProtocolV2.MaxMessageBytes)
        {
            throw new ProtocolException("Comprimento de mensagem inválido.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);

        byte kind = body[0];
        if (kind == ProtocolV2.KindControl)
        {
            if (length - 1 > maxControlBytes) throw new ProtocolException("Controle acima do limite.");
            return new IncomingFrame(kind, 0, 0, 0, 0, 0, 0, body[1..]);
        }

        if (kind != ProtocolV2.KindMedia) throw new ProtocolException("Tipo de mensagem desconhecido.");
        if (length < ProtocolV2.MediaHeaderBytes) throw new ProtocolException("Mídia truncada.");

        ReadOnlySpan<byte> span = body;
        return new IncomingFrame(
            kind,
            span[1],
            BinaryPrimitives.ReadUInt16BigEndian(span[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[14..]),
            body[ProtocolV2.MediaHeaderBytes..]);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}

/// <summary>
/// Janela visível do PC, enviada ao Mac. <paramref name="IconBase64"/> é o PNG
/// do ícone do aplicativo em Base64, ou <c>null</c> quando não foi possível
/// extraí-lo (o Mac cai no card de fallback).
/// </summary>
public sealed record WindowDescriptor(
    long Id,
    string Title,
    string Process,
    int Width,
    int Height,
    string? IconBase64 = null,
    uint ProcessId = 0,
    bool Minimized = false,
    bool CanResize = false);
