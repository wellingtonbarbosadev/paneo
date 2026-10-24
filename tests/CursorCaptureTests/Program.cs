using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowProbe.Capture;

internal static class Program
{
    private const int FramesPerPhase = 4;
    private const int RoiRadius = 48;
    private const int MinCursorPixels = 8;
    private static readonly TimeSpan StabilizeDelay = TimeSpan.FromMilliseconds(300);

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Este repro requer Windows e uma sessão interativa.");
            return 2;
        }

        string reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "cursor-capture-report.json");
        bool pngMode = args.Length > 1 && args[1].Equals("png", StringComparison.OrdinalIgnoreCase);
        if (args.Length > 1 && !pngMode)
        {
            Console.Error.WriteLine("Uso: CursorCaptureTests [caminho-relatorio.json] [png]");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Startup += async (_, _) =>
        {
            try { app.Shutdown(await RunAsync(reportPath, pngMode)); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                app.Shutdown(2);
            }
        };
        return app.Run();
    }

    private static async Task<int> RunAsync(string reportPath, bool pngMode)
    {
        bool hadOriginalCursor = GetCursorPos(out var originalCursor);
        IntPtr originalForeground = GetForegroundWindow();
        var back = MakeWindow("CaptureCursor repro — janela B", Color.FromRgb(35, 95, 205));
        var front = MakeWindow("CaptureCursor repro — janela A", Color.FromRgb(35, 175, 90));
        var phases = new List<PhaseResult>();
        var probes = new List<CaptureProbe>();
        string action = "criar janelas sintéticas";
        try
        {
            back.Show();
            await Task.Delay(300);
            front.Show();
            front.Activate();
            await Task.Delay(300);

            IntPtr frontHwnd = new WindowInteropHelper(front).Handle;
            IntPtr backHwnd = new WindowInteropHelper(back).Handle;
            if (frontHwnd == IntPtr.Zero || backHwnd == IntPtr.Zero) throw new InvalidOperationException("HWND não criado.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var frontOn = CreateProbe(frontHwnd, true, pngMode, timeout.Token, probes);
            await using var frontOff = CreateProbe(frontHwnd, false, false, timeout.Token, probes);
            await using var backOn = CreateProbe(backHwnd, true, pngMode, timeout.Token, probes);
            await using var backOff = CreateProbe(backHwnd, false, false, timeout.Token, probes);

            var center = ClientCenter(frontHwnd);
            action = "mover cursor e colocar janela A na frente";
            if (!SetCursorPos(center.X, center.Y)) throw new InvalidOperationException("SetCursorPos falhou.");
            BringToFront(frontHwnd);
            await Task.Delay(StabilizeDelay, timeout.Token);
            RequireWindowAtPoint(frontHwnd, center, "A");
            action = "capturar fase front_a";
            phases.Add(await SamplePhaseAsync("front_a", center, frontOn, frontOff, backOn, backOff, timeout.Token));

            action = "colocar janela B na frente";
            BringToFront(backHwnd);
            await Task.Delay(StabilizeDelay, timeout.Token);
            RequireWindowAtPoint(backHwnd, center, "B");
            action = "capturar fase front_b";
            phases.Add(await SamplePhaseAsync("front_b", center, frontOn, frontOff, backOn, backOff, timeout.Token));

            action = "mover cursor para fora das duas janelas";
            if (!SetCursorPos(10, 10)) throw new InvalidOperationException("Não foi possível mover o cursor para fora das janelas de teste.");
            await Task.Delay(StabilizeDelay, timeout.Token);
            action = "capturar fase outside";
            phases.Add(await SamplePhaseAsync("outside", center, frontOn, frontOff, backOn, backOff, timeout.Token));

            bool success = phases.All(phase => phase.Success);
            var report = new
            {
                success,
                result = success ? "PASS" : "FAIL",
                mode = pngMode ? "png" : "raw",
                phaseCount = phases.Count,
                framesPerPhase = FramesPerPhase,
                cursorThresholdPixels = MinCursorPixels,
                phases
            };
            WriteReport(reportPath, report);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            var report = new
            {
                success = false,
                result = "ERROR",
                mode = pngMode ? "png" : "raw",
                error = ex.GetType().Name + ": " + ex.Message,
                action,
                captureDiagnostics = probes.SelectMany(probe => probe.Diagnostics).ToArray(),
                phases
            };
            WriteReport(reportPath, report);
            Console.Error.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 2;
        }
        finally
        {
            front.Close();
            back.Close();
            if (hadOriginalCursor) SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != IntPtr.Zero) SetForegroundWindow(originalForeground);
        }
    }

    private static async Task<PhaseResult> SamplePhaseAsync(
        string name,
        (int X, int Y) cursorCenter,
        CaptureProbe frontOn,
        CaptureProbe frontOff,
        CaptureProbe backOn,
        CaptureProbe backOff,
        CancellationToken token)
    {
        frontOn.SetStage(name);
        frontOff.SetStage(name);
        backOn.SetStage(name);
        backOff.SetStage(name);
        Task<RawPixelFrame[]> frontOnFrames = frontOn.SampleAsync(token);
        Task<RawPixelFrame[]> frontOffFrames = frontOff.SampleAsync(token);
        Task<RawPixelFrame[]> backOnFrames = backOn.SampleAsync(token);
        Task<RawPixelFrame[]> backOffFrames = backOff.SampleAsync(token);
        await Task.WhenAll(frontOnFrames, frontOffFrames, backOnFrames, backOffFrames);

        int[] frontDelta = Compare(frontOnFrames.Result, frontOffFrames.Result, cursorCenter);
        int[] backDelta = Compare(backOnFrames.Result, backOffFrames.Result, cursorCenter);
        bool frontExpected = name switch
        {
            "front_a" => frontDelta.All(IsPresent) && backDelta.All(IsAbsent),
            "front_b" => backDelta.All(IsPresent) && frontDelta.All(IsAbsent),
            _ => frontDelta.All(IsAbsent) && backDelta.All(IsAbsent)
        };
        int[] controls = StableChanges(frontOffFrames.Result, cursorCenter)
            .Concat(StableChanges(backOffFrames.Result, cursorCenter))
            .ToArray();
        bool controlsClear = controls.All(IsAbsent);
        return new PhaseResult(name, frontDelta, backDelta, controls, frontExpected && controlsClear);
    }

    private static bool IsPresent(int pixels) => pixels >= MinCursorPixels;
    private static bool IsAbsent(int pixels) => pixels < MinCursorPixels;

    private static CaptureProbe CreateProbe(IntPtr hwnd, bool captureCursor, bool pngMode, CancellationToken token, List<CaptureProbe> probes)
    {
        var probe = new CaptureProbe(hwnd, captureCursor, pngMode, token);
        probes.Add(probe);
        return probe;
    }

    private static Window MakeWindow(string title, Color color) => new()
    {
        Title = title,
        Width = 640,
        Height = 400,
        Left = 150,
        Top = 150,
        WindowStyle = WindowStyle.None,
        Topmost = true,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Background = new SolidColorBrush(color),
        Content = new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 24,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0)
        }
    };

    private static int[] Compare(RawPixelFrame[] enabled, RawPixelFrame[] disabled, (int X, int Y) cursor)
    {
        if (enabled.Length != disabled.Length) throw new InvalidOperationException("Quantidade de frames pareados diferente.");
        return enabled.Zip(disabled, (on, off) => ChangedPixels(on, off, cursor)).ToArray();
    }

    private static int[] StableChanges(RawPixelFrame[] frames, (int X, int Y) cursor) =>
        frames.Skip(1).Select(frame => ChangedPixels(frame, frames[0], cursor)).ToArray();

    private static int ChangedPixels(RawPixelFrame on, RawPixelFrame off, (int X, int Y) cursor)
    {
        if (on.Width != off.Width || on.Height != off.Height) throw new InvalidOperationException("Dimensões de captura incompatíveis.");
        var origin = ClientCenterScreenToCapture(cursor, on);
        int left = Math.Max(0, origin.X - RoiRadius);
        int right = Math.Min(on.Width, origin.X + RoiRadius);
        int top = Math.Max(0, origin.Y - RoiRadius);
        int bottom = Math.Min(on.Height, origin.Y + RoiRadius);
        int changed = 0;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int onIndex = y * on.Stride + x * 4;
            int offIndex = y * off.Stride + x * 4;
            if (Math.Abs(on.Bgra[onIndex] - off.Bgra[offIndex]) > 16 ||
                Math.Abs(on.Bgra[onIndex + 1] - off.Bgra[offIndex + 1]) > 16 ||
                Math.Abs(on.Bgra[onIndex + 2] - off.Bgra[offIndex + 2]) > 16) changed++;
        }
        return changed;
    }

    private static (int X, int Y) ClientCenterScreenToCapture((int X, int Y) screenPoint, RawPixelFrame frame)
    {
        _ = screenPoint;
        return (frame.Width / 2, frame.Height / 2);
    }

    private static (int X, int Y) ClientCenter(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect)) throw new InvalidOperationException("GetClientRect falhou.");
        var point = new NativePoint { X = (rect.Right - rect.Left) / 2, Y = (rect.Bottom - rect.Top) / 2 };
        if (!ClientToScreen(hwnd, ref point)) throw new InvalidOperationException("ClientToScreen falhou.");
        return (point.X, point.Y);
    }

    private static void BringToFront(IntPtr hwnd)
    {
        if (!SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040))
            throw new InvalidOperationException("SetWindowPos não conseguiu reordenar a janela de teste.");
    }

    private static void RequireWindowAtPoint(IntPtr expected, (int X, int Y) point, string label)
    {
        IntPtr actual = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        if (actual != expected)
            throw new InvalidOperationException($"A janela {label} não está na frente no ponto de captura (esperado 0x{expected.ToInt64():X}, atual 0x{actual.ToInt64():X}).");
    }

    private static void WriteReport(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record PhaseResult(string Name, int[] FrontCursorPixels, int[] BackCursorPixels, int[] CursorDisabledControlPixels, bool Success);

    private sealed class CaptureProbe : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly List<RawPixelFrame> frames = new(FramesPerPhase);
        private readonly ConcurrentQueue<string> diagnostics = new();
        private readonly CancellationTokenSource stop;
        private readonly Task capture;
        private readonly IntPtr hwnd;
        private readonly bool captureCursor;
        private readonly bool pngMode;
        private TaskCompletionSource<RawPixelFrame[]>? sampleReady;
        private TimeSpan lastAccepted = TimeSpan.FromSeconds(-1);
        private string stage = "inicialização";

        public string[] Diagnostics => diagnostics.ToArray();

        public CaptureProbe(IntPtr hwnd, bool captureCursor, bool pngMode, CancellationToken token)
        {
            this.hwnd = hwnd;
            this.captureCursor = captureCursor;
            this.pngMode = pngMode && captureCursor;
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var options = new ContinuousCaptureOptions
            {
                CaptureCursor = captureCursor,
                RawPixels = !this.pngMode,
                FrameTimeout = TimeSpan.FromSeconds(5),
                AcceptFrame = elapsed =>
                {
                    if (elapsed - lastAccepted < TimeSpan.FromSeconds(1d / 30)) return false;
                    lastAccepted = elapsed;
                    return true;
                },
                RawFrame = this.pngMode ? null : RecordFrame
            };
            capture = ContinuousWindowCapture.RunAsync(
                hwnd, this.pngMode ? (frame, token) => RecordFrame(DecodePng(frame), token) : (_, _) => Task.CompletedTask, options,
                new InlineProgress(message => diagnostics.Enqueue(message)), stop.Token);
        }

        private static RawPixelFrame DecodePng(CapturedFrame frame)
        {
            using var stream = new MemoryStream(frame.Png, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return new RawPixelFrame(frame.Sequence, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, frame.Elapsed);
        }

        public void SetStage(string value) => stage = value;

        public Task<RawPixelFrame[]> SampleAsync(CancellationToken token)
        {
            Task<RawPixelFrame[]> pending;
            lock (gate)
            {
                if (sampleReady is not null) throw new InvalidOperationException("Já existe uma coleta de fase ativa.");
                frames.Clear();
                sampleReady = new TaskCompletionSource<RawPixelFrame[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = sampleReady.Task.WaitAsync(token);
            }
            return AwaitSampleAsync(pending);
        }

        private async Task<RawPixelFrame[]> AwaitSampleAsync(Task<RawPixelFrame[]> pending)
        {
            Task completed = await Task.WhenAny(pending, capture);
            if (completed == capture)
            {
                try { await capture; }
                catch (Exception ex) { throw CaptureFailure(ex); }
                throw CaptureFailure(new InvalidOperationException("A captura terminou antes de coletar os frames solicitados."));
            }

            try { return await pending; }
            catch (OperationCanceledException) when (capture.IsFaulted)
            {
                Exception failure = capture.Exception?.GetBaseException() ?? new InvalidOperationException("Falha WGC sem detalhe.");
                throw CaptureFailure(failure);
            }
        }

        private Exception CaptureFailure(Exception ex) => new InvalidOperationException(
            $"Captura WGC falhou na fase '{stage}' (HWND=0x{hwnd.ToInt64():X}, CaptureCursor={captureCursor}): {ex.GetType().Name}: {ex.Message}", ex);

        private sealed class InlineProgress(Action<string> report) : IProgress<string>
        {
            public void Report(string value) => report(value);
        }

        private Task RecordFrame(RawPixelFrame frame, CancellationToken token)
        {
            lock (gate)
            {
                if (sampleReady is null) return Task.CompletedTask;
                frames.Add(frame with { Bgra = frame.Bgra.ToArray() });
                if (frames.Count >= FramesPerPhase)
                {
                    sampleReady.TrySetResult(frames.ToArray());
                    sampleReady = null;
                }
            }
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await capture; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { diagnostics.Enqueue("dispose capture task: " + ex.GetType().Name + ": " + ex.Message); }
            stop.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}
