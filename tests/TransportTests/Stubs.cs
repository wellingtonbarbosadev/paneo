// Substitutos explícitos: estes testes verificam o servidor TLS real, não WGC/SendInput.
namespace WindowProbe.Remote
{
    internal sealed class RemoteInput(IntPtr hwnd) : IDisposable
    {
        public void Dispose() { }
        public void Apply(System.Text.Json.JsonElement message) { _ = hwnd; throw new InvalidOperationException("Entrada não habilitada no teste de protocolo."); }
    }
}
namespace WindowProbe.Capture
{
    public sealed class ContinuousCaptureOptions { public TimeSpan FrameTimeout { get; init; } }
    public sealed record CapturedFrame(long Sequence, int Width, int Height, byte[] Png);
    public static class ContinuousWindowCapture
    {
        public static async Task RunAsync(IntPtr hwnd, Func<CapturedFrame, CancellationToken, Task> callback,
            ContinuousCaptureOptions options, object? log, CancellationToken ct)
        {
            long id = 0;
            while (true) { ct.ThrowIfCancellationRequested(); await callback(new(++id, 1, 1, [1, 2, 3]), ct); }
        }
    }
}
