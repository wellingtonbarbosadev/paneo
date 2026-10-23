// Substitutos explícitos: estes testes verificam o servidor TLS real, não WGC/SendInput.
namespace WindowProbe.Remote
{
    internal sealed class FakeWindowCommands(long windowId, RemoteWindowMenus? menus = null) : IWindowCommands
    {
        private bool minimized, maximized, reading;
        public RemoteWindowState State => new(windowId, minimized, maximized, reading, true);
        public RemoteWindowCommandResult Execute(System.Text.Json.JsonElement command)
        {
            string action = command.GetProperty("action").GetString()!;
            switch (action)
            {
                case "minimize": minimized = true; break;
                case "maximize": reading = false; maximized = true; minimized = false; break;
                case "restore": reading = false; maximized = false; minimized = false; break;
                case "close": reading = false; break;
                case "reading": reading = command.GetProperty("enabled").GetBoolean(); break;
                case "state": break;
                default: return new(action, false, "unknown");
            }
            return new(action, true, "OK");
        }
        public RemoteWindowMenus GetMenus() => menus ?? new(true, [new("fake:1", "Arquivo", true, false, false, [])]);
        public RemoteWindowCommandResult InvokeMenu(string id) => id == "fake:1" ? new("menu", true, "OK") : new("menu", false, "stale");
        public void Dispose() { }
    }

    internal sealed class RemoteInput(IntPtr hwnd) : IDisposable
    {
        public RemoteInput(IntPtr monitor, bool fullScreen) : this(monitor) { _ = fullScreen; }
        public bool AllowHover { get; set; } = true;
        public void Dispose() { }
        public string? TryApply(System.Text.Json.JsonElement message, double inputScaleX = 1, double inputScaleY = 1)
        {
            _ = hwnd;
            _ = message;
            _ = inputScaleX;
            _ = inputScaleY;
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
        public static (int Width, int Height) RawSize { get; set; } = (320, 240);

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
                    var (width, height) = RawSize;
                    var pixels = new byte[width * height * 4];
                    Array.Fill(pixels, (byte)128);
                    await options.RawFrame(new RawPixelFrame(++sequence, pixels, width, height, width * 4, watch.Elapsed), ct);
                    continue;
                }
                await callback(new CapturedFrame(++sequence, png, 4, 3, watch.Elapsed, TimeSpan.Zero), ct);
            }
        }
    }
}
