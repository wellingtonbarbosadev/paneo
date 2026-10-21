using System.IO;
using System.Security.Cryptography;
using System.Text;
using WindowProbe.Remote;

string directory = Path.Combine(Path.GetTempPath(), "RemoteDesktop-pairing-test-" + Guid.NewGuid().ToString("N"));
string path = Path.Combine(directory, "pairing.dat");
try
{
    var first = PairingStore.LoadOrCreate(path);
    using var firstCertificate = first.Certificate;
    var second = PairingStore.LoadOrCreate(path);
    using var secondCertificate = second.Certificate;
    Require(first.Token == second.Token && firstCertificate.Thumbprint == secondCertificate.Thumbprint, "Persistência");
    Require(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(first.Token), "Sem token em claro");
    PairingStore.Revoke(path);
    var third = PairingStore.LoadOrCreate(path);
    using var thirdCertificate = third.Certificate;
    Require(third.Token != first.Token && thirdCertificate.Thumbprint != firstCertificate.Thumbprint, "Revogação e rotação");
    byte[] damaged = File.ReadAllBytes(path); damaged[^1] ^= 0xff; File.WriteAllBytes(path, damaged);
    bool rejected = false;
    try { var invalid = PairingStore.LoadOrCreate(path); invalid.Certificate.Dispose(); }
    catch (CryptographicException) { rejected = true; }
    Require(rejected, "DPAPI rejeita corrupção sem substituir autorização");
    PairingStore.Revoke(path); PairingStore.Revoke(path);
    Console.WriteLine("PASS: persistência, DPAPI, rotação/revogação e corrupção; somente pasta temporária.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
