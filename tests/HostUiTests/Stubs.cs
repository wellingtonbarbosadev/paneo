using System.Net;

namespace WindowProbe.Remote
{
    internal sealed class HostDiscovery
    {
        public static bool ListenerStoppedBeforeDiscovery { get; private set; }
        public HostDiscovery(int port) { }
        public Task StopAsync()
        {
            ListenerStoppedBeforeDiscovery = RemoteServer.StopCount > 0;
            return Task.CompletedTask;
        }
    }

    internal sealed class Mutex : IDisposable
    {
        public Mutex(bool initiallyOwned, string name) { }
        public bool WaitOne(int millisecondsTimeout) => true;
        public void ReleaseMutex() { }
        public void Dispose() { }
    }

    internal sealed class DisplayWakeLock : IDisposable
    {
        public void Dispose() { }
    }

    internal sealed record HostCredentials(string Pin)
    {
        public const int PinMinLength = 6;
        public const int PinMaxLength = 128;
        public static bool IsAcceptablePin(string value) => value.Length is >= PinMinLength and <= PinMaxLength;
        public HostCredentials WithPin(string value) => new(value);
        public static HostCredentials Create(string value) => new(value);
    }

    internal sealed class StubCertificate : IDisposable
    {
        public void Dispose() { }
    }

    internal sealed record HostState(HostCredentials? Credentials, StubCertificate Certificate, bool HasPin);

    internal static class PairingStore
    {
        private static HostCredentials? credentials;
        public static int Saves { get; private set; }
        public static HostState LoadOrCreate() => new(credentials, new StubCertificate(), credentials is not null);
        public static HostState Save(HostCredentials value) { credentials = value; Saves++; return LoadOrCreate(); }
        public static void Revoke() { credentials = null; }
    }

    internal sealed record HostSettings(int Port, bool AllowHover)
    {
        public static HostSettings Load() => new(9443, true);
        public void Save() { }
    }

    internal static class HostStartup
    {
        public static bool IsEnabled() => false;
        public static void SetEnabled(bool enabled) { }
    }

    internal sealed record WindowDescriptor(long Handle, string Title, string Process, int Width, int Height, string? IconBase64);

    internal sealed class RemoteServer
    {
        public static int StartCount { get; private set; }
        public static int StopCount { get; private set; }
        public int Port { get; }
        public Action<string>? Status { get; set; }
        public bool AllowHover { get; set; }
        public event Action<object?, HostCredentials>? CredentialsChanged;

        public RemoteServer(IntPtr hwnd, IPAddress address, int port, HostCredentials credentials, StubCertificate certificate,
            Func<WindowDescriptor[]> windows) => Port = port;
        public void Start() => StartCount++;
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public void UpdateCredentials(HostCredentials credentials) => CredentialsChanged?.Invoke(this, credentials);
    }

    internal static class WindowEnumerator
    {
        public static IEnumerable<WindowProbe.Models.WindowInfo> GetVisibleWindows() => [];
    }
}

namespace WindowProbe.Models
{
    internal sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int Width, int Height, string? IconBase64);
}
