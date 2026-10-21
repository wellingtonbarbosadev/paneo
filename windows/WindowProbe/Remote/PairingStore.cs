using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WindowProbe.Remote;

/// <summary>
/// Estado persistido do host: certificado TLS e credenciais (sal, hash do PIN,
/// dispositivos). <see cref="Credentials"/> é <c>null</c> enquanto o host ainda
/// não definiu um PIN.
/// </summary>
internal sealed record HostState(HostCredentials? Credentials, X509Certificate2 Certificate)
{
    internal bool HasPin => Credentials is not null;
}

/// <summary>
/// Persistência em <c>%LOCALAPPDATA%\RemoteDesktop\state.dat</c>, protegida pelo
/// DPAPI (CurrentUser) e escrita de forma atômica (tmp + File.Move).
/// O PIN em claro nunca é gravado.
/// </summary>
internal static class PairingStore
{
    private const int Version = 2;
    private const int DeviceNameMaxLength = 64;
    private const int MaxDevices = HostCredentials.MaxDevices;

    private static readonly object Gate = new();
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesktop");
    private static readonly string StatePath = Path.Combine(DirectoryPath, "state.dat");
    private static readonly string LegacyPath = Path.Combine(DirectoryPath, "pairing.dat");

    private sealed record StoredState(
        int Version,
        byte[] Certificate,
        byte[] Salt,
        byte[] PinHash,
        StoredDevice[] Devices);

    private sealed record StoredDevice(
        string Id,
        string Name,
        byte[] Token,
        DateTimeOffset CreatedUtc);

    internal static bool HasStored()
    {
        lock (Gate) return File.Exists(StatePath);
    }

    /// <summary>
    /// Carrega o estado salvo ou cria somente o certificado. Sem state.dat o
    /// host ainda não tem PIN: <see cref="HostState.Credentials"/> fica nulo até
    /// a janela chamar <c>HostCredentials.Create(pin)</c> e <see cref="Save"/>.
    /// O pairing.dat antigo (v1, sem PIN) é apagado.
    /// </summary>
    internal static HostState LoadOrCreate()
    {
        lock (Gate)
        {
            if (!File.Exists(StatePath))
            {
                DeleteLegacy();
                return new HostState(null, CreateCertificate());
            }

            StoredState stored = ReadState();

            HostCredentials credentials;
            try
            {
                credentials = HostCredentials.FromStored(stored.Salt, stored.PinHash, BuildDevices(stored.Devices));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Credenciais salvas inválidas.", ex);
            }

            X509Certificate2 certificate = LoadCertificate(stored.Certificate);
            if (IsExpired(certificate))
            {
                certificate.Dispose();
                certificate = CreateCertificate();
                WriteState(stored with { Certificate = ExportCertificate(certificate) });
            }

            return new HostState(credentials, certificate);
        }
    }

    /// <summary>
    /// Grava as credenciais reutilizando o certificado salvo. Se ainda não houver
    /// certificado persistido, cria um novo.
    /// </summary>
    internal static HostState Save(HostCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        lock (Gate)
        {
            StoredState? existing = File.Exists(StatePath) ? ReadState() : null;

            X509Certificate2 certificate;
            byte[] pfx;
            if (existing is not null)
            {
                certificate = LoadCertificate(existing.Certificate);
                if (IsExpired(certificate))
                {
                    certificate.Dispose();
                    (certificate, pfx) = CreateCertificateWithPfx();
                }
                else
                {
                    pfx = existing.Certificate;
                }
            }
            else
            {
                (certificate, pfx) = CreateCertificateWithPfx();
            }

            var stored = new StoredState(
                Version,
                pfx,
                [.. credentials.Salt],
                [.. credentials.PinHash],
                credentials.Devices.Select(device => new StoredDevice(
                    device.Id,
                    device.Name,
                    [.. device.Token],
                    device.CreatedUtc)).ToArray());

            WriteState(stored);
            return new HostState(credentials, certificate);
        }
    }

    /// <summary>Apaga o state.dat e o pairing.dat antigo.</summary>
    internal static void Revoke()
    {
        lock (Gate)
        {
            TryDelete(StatePath);
            DeleteLegacy();
        }
    }

    private static StoredState ReadState()
    {
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(
                File.ReadAllBytes(StatePath), null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Não foi possível abrir o estado salvo.", ex);
        }

        try
        {
            StoredState? stored = JsonSerializer.Deserialize<StoredState>(plain);
            if (stored is null) throw new InvalidDataException("Estado salvo vazio.");
            if (stored.Version != Version) throw new InvalidDataException("Versão de estado incompatível.");
            if (stored.Certificate.Length == 0) throw new InvalidDataException("Certificado salvo ausente.");
            if (stored.Salt.Length != HostCredentials.SaltBytes) throw new InvalidDataException("Salt inválido.");
            if (stored.PinHash.Length != HostCredentials.ProofBytes) throw new InvalidDataException("Hash do PIN inválido.");
            if (stored.Devices.Length > MaxDevices) throw new InvalidDataException("Dispositivos demais.");
            foreach (StoredDevice device in stored.Devices)
            {
                if (string.IsNullOrWhiteSpace(device.Id)) throw new InvalidDataException("Dispositivo sem id.");
                if (device.Token.Length != HostCredentials.TokenBytes) throw new InvalidDataException("Token de dispositivo inválido.");
            }

            return stored;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static IEnumerable<PairedDevice> BuildDevices(IEnumerable<StoredDevice> devices) =>
        devices.Select(device => new PairedDevice(
            device.Id,
            NormalizeName(device.Name),
            [.. device.Token],
            device.CreatedUtc));

    private static string NormalizeName(string? name)
    {
        string value = (name ?? "").Trim();
        if (value.Length == 0) return "Mac";
        return value.Length <= DeviceNameMaxLength ? value : value[..DeviceNameMaxLength];
    }

    private static void WriteState(StoredState stored)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(stored);
        try
        {
            byte[] encrypted = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(DirectoryPath);
            string temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(encrypted);
                    file.Flush(true);
                }

                File.Move(temporary, StatePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static X509Certificate2 CreateCertificate() => CreateCertificateWithPfx().Certificate;

    private static (X509Certificate2 Certificate, byte[] Pfx) CreateCertificateWithPfx()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteDesktop paired host", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));

        // Schannel precisa de um container temporário de chave no perfil do usuário.
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        return (X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet), pfx);
    }

    private static byte[] ExportCertificate(X509Certificate2 certificate) =>
        certificate.Export(X509ContentType.Pfx);

    private static X509Certificate2 LoadCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    private static bool IsExpired(X509Certificate2 certificate) =>
        certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow;

    private static void DeleteLegacy() => TryDelete(LegacyPath);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
