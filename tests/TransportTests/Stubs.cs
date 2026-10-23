// Substitutos explícitos: estes testes verificam o servidor TLS real, não WGC/SendInput.
namespace WindowProbe.Remote
{
    internal sealed class RemoteInput(IntPtr hwnd) : IDisposable
    {
        public RemoteInput(IntPtr monitor, bool fullScreen) : this(monitor) { _ = fullScreen; }
        public bool AllowHover { get; set; } = true;
        public void Dispose() { }
        public string? TryApply(System.Text.Json.JsonElement message)
        {
            _ = hwnd;
            _ = message;
            return "Entrada não habilitada no teste de protocolo.";
        }
    }
}

namespace WindowProbe.Capture
{
    public sealed class ContinuousCaptureOptions
    {
        public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public int BufferCount { get; init; } = 2;
        public bool CaptureCursor { get; init; }
        /// <summary>Filtro de cadência do produto: descarta antes de entregar.</summary>
        public Func<TimeSpan, bool>? AcceptFrame { get; init; }

        /// <summary>Métrica opcional do pipeline; nula no teste de protocolo.</summary>
        public Action<TimeSpan, TimeSpan>? Timings { get; init; }

        public IntPtr MonitorHandle { get; init; }
        public bool RawPixels { get; init; }
        public Func<RawPixelFrame, CancellationToken, Task>? RawFrame { get; init; }
    }

    public sealed record RawPixelFrame(
        long Sequence,
        byte[] Bgra,
        int Width,
        int Height,
        int Stride,
        TimeSpan Elapsed);

    public sealed record ContinuousCaptureSummary(
        long FramesDelivered,
        int FramesCoalesced,
        int Resizes,
        TimeSpan Duration);

    public sealed record CapturedFrame(
        long Sequence,
        byte[] Png,
        int Width,
        int Height,
        TimeSpan Elapsed,
        TimeSpan EncodeDuration);

    public static class ContinuousWindowCapture
    {
        public static async Task<ContinuousCaptureSummary> RunAsync(
            IntPtr hwnd,
            Func<CapturedFrame, CancellationToken, Task> callback,
            ContinuousCaptureOptions? options,
            IProgress<string>? log,
            CancellationToken ct)
        {
            _ = hwnd;
            _ = options;
            _ = log;

            byte[] png = [1, 2, 3, 4, 5];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long sequence = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (options?.AcceptFrame is not null && !options.AcceptFrame(watch.Elapsed))
                {
                    await Task.Delay(1, ct);
                    continue;
                }
                if (options is not null && options.RawPixels && options.RawFrame is not null)
                {
                    // Quadro BGRA 320x240 sintético (múltiplo de 16, aceito pelo encoder).
                    var pixels = new byte[320 * 240 * 4];
                    Array.Fill(pixels, (byte)128);
                    await options.RawFrame(new RawPixelFrame(++sequence, pixels, 320, 240, 320 * 4, watch.Elapsed), ct);
                    continue;
                }
                await callback(new CapturedFrame(++sequence, png, 4, 3, watch.Elapsed, TimeSpan.Zero), ct);
            }
        }
    }
}
