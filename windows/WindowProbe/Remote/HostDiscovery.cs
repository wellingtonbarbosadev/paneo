using System.Runtime.InteropServices;
using System.Text;

namespace WindowProbe.Remote;

/// <summary>Anúncio Bonjour do host enquanto existe ao menos um compartilhamento.</summary>
internal sealed class HostDiscovery : IDisposable
{
    private const uint DnsQueryRequestVersion1 = 1;
    private const uint DnsRequestPending = 9506;
    private const int Registering = 0, Deregistering = 1, Complete = 2;
    private static readonly RegisterComplete Callback = OnComplete;
    private readonly TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource deregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object stopGate = new();
    private GCHandle self;
    private IntPtr instance;
    private IntPtr request;
    private int phase = Registering;
    private int released;
    private Task? stopTask;

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr Completion;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        [MarshalAs(UnmanagedType.Bool)] public bool UnicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr serviceInstance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName,
        IntPtr ipv4, IntPtr ipv6, ushort port, ushort priority, ushort weight,
        uint propertyCount, IntPtr keys, IntPtr values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr serviceInstance);

    public Task Registered => registered.Task;

    public HostDiscovery(int port)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        self = GCHandle.Alloc(this);
        try
        {
            string serviceName = MakeServiceName(Environment.MachineName, port);
            instance = DnsServiceConstructInstance(serviceName, Environment.MachineName + ".local.",
                IntPtr.Zero, IntPtr.Zero, (ushort)port, 0, 0, 0, IntPtr.Zero, IntPtr.Zero);
            if (instance == IntPtr.Zero) throw new InvalidOperationException("DnsServiceConstructInstance failed.");

            var data = new RegisterRequest
            {
                Version = DnsQueryRequestVersion1,
                ServiceInstance = instance,
                Completion = Marshal.GetFunctionPointerForDelegate(Callback),
                QueryContext = GCHandle.ToIntPtr(self),
                UnicastEnabled = false
            };
            request = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterRequest>());
            Marshal.StructureToPtr(data, request, false);
            uint status = DnsServiceRegister(request, IntPtr.Zero);
            if (status != DnsRequestPending) throw new InvalidOperationException($"DnsServiceRegister failed: {status}.");
        }
        catch
        {
            Release();
            throw;
        }
    }

    public Task StopAsync()
    {
        lock (stopGate) return stopTask ??= StopCoreAsync();
    }

    public void Dispose() => _ = StopAsync();

    private async Task StopCoreAsync()
    {
        try { await registered.Task.ConfigureAwait(false); }
        catch (Exception) { Release(); return; }

        Volatile.Write(ref phase, Deregistering);
        uint status = DnsServiceDeRegister(request, IntPtr.Zero);
        if (status != DnsRequestPending)
        {
            Release();
            return;
        }
        await deregistered.Task.ConfigureAwait(false);
        Release();
    }

    private static string MakeServiceName(string machineName, int port)
    {
        string label = new(machineName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        label = label.Trim('-');
        if (label.Length == 0) label = "windows";
        label = label[..Math.Min(label.Length, 36)];
        string unique = Guid.NewGuid().ToString("N")[..8];
        return $"Paneo-{label}-{port}-{unique}._paneo._tcp.local.";
    }

    private static void OnComplete(uint status, IntPtr context, IntPtr serviceInstance)
    {
        if (serviceInstance != IntPtr.Zero) DnsServiceFreeInstance(serviceInstance);
        if (context == IntPtr.Zero) return;
        try
        {
            var owner = (HostDiscovery?)GCHandle.FromIntPtr(context).Target;
            if (owner is null) return;
            if (Volatile.Read(ref owner.phase) == Deregistering) owner.deregistered.TrySetResult();
            else if (status == 0) owner.registered.TrySetResult();
            else owner.registered.TrySetException(new InvalidOperationException($"mDNS registration failed: {status}."));
        }
        catch (InvalidOperationException) { }
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        IntPtr requestToFree = Interlocked.Exchange(ref request, IntPtr.Zero);
        IntPtr instanceToFree = Interlocked.Exchange(ref instance, IntPtr.Zero);
        if (requestToFree != IntPtr.Zero) Marshal.FreeHGlobal(requestToFree);
        if (instanceToFree != IntPtr.Zero) DnsServiceFreeInstance(instanceToFree);
        if (self.IsAllocated) self.Free();
    }
}
