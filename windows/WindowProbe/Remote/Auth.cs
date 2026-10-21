using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WindowProbe.Remote;

/// <summary>Dispositivo (Mac) autorizado a reconectar sem redigitar o PIN.</summary>
public sealed record PairedDevice(string Id, string Name, byte[] Token, DateTimeOffset CreatedUtc);

/// <summary>
/// Credenciais do host: salt e hash do PIN, mais os dispositivos pareados.
/// Cross-platform de propósito: tests/TransportTests usa este arquivo sem DPAPI.
/// A persistência protegida fica em PairingStore (somente Windows).
/// </summary>
public sealed class HostCredentials
{
    public const int PinMinLength = 6;
    public const int PinMaxLength = 128;
    public const int PinIterations = 600_000;
    public const int SaltBytes = 32;
    public const int ProofBytes = 32;
    public const int TokenBytes = 32;
    public const int MaxDevices = 32;

    private const string PinContext = "remoteDesktop-v2/pin";
    private const string DeviceContext = "remoteDesktop-v2/device";

    public byte[] Salt { get; }
    public byte[] PinHash { get; }
    public IReadOnlyList<PairedDevice> Devices { get; }

    private HostCredentials(byte[] salt, byte[] pinHash, IReadOnlyList<PairedDevice> devices)
    {
        Salt = salt;
        PinHash = pinHash;
        Devices = devices;
    }

    /// <summary>Reconstrói credenciais já persistidas (não deriva nada).</summary>
    public static HostCredentials FromStored(byte[] salt, byte[] pinHash, IEnumerable<PairedDevice>? devices = null)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(pinHash);
        if (salt.Length != SaltBytes) throw new ArgumentException("Salt inválido.", nameof(salt));
        if (pinHash.Length != ProofBytes) throw new ArgumentException("Hash do PIN inválido.", nameof(pinHash));
        return new HostCredentials([.. salt], [.. pinHash], [.. (devices ?? [])]);
    }

    public static string NormalizePin(string pin) =>
        (pin ?? "").Trim().Normalize(NormalizationForm.FormKC);

    public static bool IsAcceptablePin(string pin)
    {
        string normalized = NormalizePin(pin);
        return normalized.Length is >= PinMinLength and <= PinMaxLength;
    }

    /// <summary>Cria credenciais novas com salt aleatório e PIN escolhido pelo usuário.</summary>
    public static HostCredentials Create(string pin)
    {
        if (!IsAcceptablePin(pin))
        {
            throw new ArgumentException($"O PIN precisa ter entre {PinMinLength} e {PinMaxLength} caracteres.", nameof(pin));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new HostCredentials(salt, DeriveKey(pin, salt), []);
    }

    /// <summary>Troca o PIN mantendo salt e dispositivos (o PIN novo vale na próxima conexão).</summary>
    public HostCredentials WithPin(string pin)
    {
        if (!IsAcceptablePin(pin))
        {
            throw new ArgumentException($"O PIN precisa ter entre {PinMinLength} e {PinMaxLength} caracteres.", nameof(pin));
        }

        return new HostCredentials([.. Salt], DeriveKey(pin, Salt), Devices);
    }

    public (HostCredentials Updated, PairedDevice Device) AddDevice(string name, string? id = null)
    {
        if (Devices.Count >= MaxDevices)
        {
            throw new InvalidOperationException("Limite de dispositivos pareados atingido.");
        }

        var device = new PairedDevice(
            string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            string.IsNullOrWhiteSpace(name) ? "Mac" : name.Trim(),
            RandomNumberGenerator.GetBytes(TokenBytes),
            DateTimeOffset.UtcNow);
        return (new HostCredentials([.. Salt], [.. PinHash], [.. Devices, device]), device);
    }

    public HostCredentials RemoveDevice(string id) =>
        new([.. Salt], [.. PinHash], Devices.Where(device => device.Id != id).ToArray());

    public HostCredentials WithoutDevices() => new([.. Salt], [.. PinHash], []);

    public static byte[] DeriveKey(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(NormalizePin(pin)), salt, PinIterations, HashAlgorithmName.SHA256, ProofBytes);

    public static byte[] PinProof(byte[] pinHash, byte[] nonce) => Proof(pinHash, PinContext, nonce);

    public static byte[] DeviceProof(byte[] token, byte[] nonce) => Proof(token, DeviceContext, nonce);

    private static byte[] Proof(byte[] key, string context, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nonce);
        byte[] contextBytes = Encoding.ASCII.GetBytes(context);
        byte[] message = new byte[contextBytes.Length + 1 + nonce.Length];
        contextBytes.CopyTo(message, 0);
        message[contextBytes.Length] = 0;
        nonce.CopyTo(message, contextBytes.Length + 1);
        return HMACSHA256.HashData(key, message);
    }

    /// <summary>
    /// Verifica a prova recebida. Devolve o dispositivo reconhecido (modo device)
    /// ou <c>null</c> no modo PIN. Nunca lança para prova inválida.
    /// </summary>
    public bool TryVerify(string mode, byte[] proof, byte[] nonce, out PairedDevice? device)
    {
        device = null;
        if (proof.Length != ProofBytes || nonce.Length == 0) return false;

        if (string.Equals(mode, "pin", StringComparison.Ordinal))
        {
            return CryptographicOperations.FixedTimeEquals(PinProof(PinHash, nonce), proof);
        }

        if (!string.Equals(mode, "device", StringComparison.Ordinal)) return false;

        foreach (PairedDevice candidate in Devices)
        {
            if (candidate.Token.Length != TokenBytes) continue;
            if (!CryptographicOperations.FixedTimeEquals(DeviceProof(candidate.Token, nonce), proof)) continue;
            device = candidate;
            return true;
        }

        return false;
    }
}

/// <summary>Bloqueio progressivo por IP depois de falhas de autenticação.</summary>
public sealed class AuthRateLimiter
{
    private const int FailuresBeforeLockout = 5;
    private static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(5);
    private const int MaxTracked = 64;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> order = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly TimeProvider clock;

    public AuthRateLimiter(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public TimeSpan RemainingLockout(IPAddress address)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(Key(address), out Entry? entry)) return TimeSpan.Zero;
            TimeSpan remaining = entry.LockedUntil - clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public TimeSpan RegisterFailure(IPAddress address)
    {
        lock (gate)
        {
            string key = Key(address);
            if (!entries.TryGetValue(key, out Entry? entry))
            {
                if (entries.Count >= MaxTracked) EvictOldest();
                entry = new Entry();
                entries[key] = entry;
                order.Add(key);
            }

            entry.Failures++;
            if (entry.Failures < FailuresBeforeLockout) return TimeSpan.Zero;

            int exponent = Math.Min(entry.Failures - FailuresBeforeLockout, 10);
            double seconds = Math.Min(BaseLockout.TotalSeconds * Math.Pow(2, exponent), MaxLockout.TotalSeconds);
            TimeSpan lockout = TimeSpan.FromSeconds(seconds);
            entry.LockedUntil = clock.GetUtcNow() + lockout;
            return lockout;
        }
    }

    public void Reset(IPAddress address)
    {
        lock (gate)
        {
            string key = Key(address);
            entries.Remove(key);
            order.Remove(key);
        }
    }

    private void EvictOldest()
    {
        string? first = order.FirstOrDefault();
        if (first is null) return;
        order.Remove(first);
        entries.Remove(first);
    }

    private static string Key(IPAddress address) => address?.ToString() ?? "desconhecido";
}
