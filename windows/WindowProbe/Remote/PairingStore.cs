using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WindowProbe.Remote;

/// <summary>Uma autorização compartilhada, protegida pelo DPAPI da conta Windows.</summary>
internal static class PairingStore
{
    private static readonly string FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesktop", "pairing.dat");
    private sealed record Stored(byte[] Certificate, string Token);

    internal static (X509Certificate2 Certificate, string Token) LoadOrCreate(string? path = null)
    {
        path ??= FileName;
        if (File.Exists(path))
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<Stored>(plain) ?? throw new InvalidDataException();
                try
                {
                    if (saved.Token.Length != 64 || !saved.Token.All(Uri.IsHexDigit)) throw new InvalidDataException();
                    var certificate = X509CertificateLoader.LoadPkcs12(saved.Certificate, null, X509KeyStorageFlags.UserKeySet);
                    if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) { certificate.Dispose(); throw new InvalidDataException("Pareamento expirado; revogue e autorize novamente."); }
                    return (certificate, saved.Token);
                }
                finally { CryptographicOperations.ZeroMemory(saved.Certificate); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemoteDesktop paired host", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Stored(pfx, token));
        try
        {
            byte[] encrypted = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(encrypted); file.Flush(true); }
                File.Move(temporary, path, overwrite: false);
            }
            finally { File.Delete(temporary); }
            return (X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet), token);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); CryptographicOperations.ZeroMemory(payload); }
    }

    internal static void Revoke(string? path = null) => File.Delete(path ?? FileName);
}
