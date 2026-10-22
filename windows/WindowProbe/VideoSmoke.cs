using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowProbe.Capture;
using WindowProbe.Remote;

namespace WindowProbe;

/// <summary>
/// Prova do pipeline de vídeo: captura WGC → pixels BGRA → NV12 → H.264.
/// Usa uma janela sintética com conteúdo mudando e grava um relatório JSON.
/// </summary>
internal static class VideoSmoke
{
    internal static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "video-smoke.json");

        var panel = new StackPanel { Background = System.Windows.Media.Brushes.DarkSlateBlue };
        var counter = new TextBlock { FontSize = 48, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(24) };
        panel.Children.Add(counter);
        var window = new Window
        {
            Title = "RemoteDesktop — prova de vídeo",
            Width = 1920,
            Height = 1080,
            Topmost = true,
            Left = 0,
            Top = 0,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = panel
        };

        int ticks = 0;
        TimeSpan lastRender = TimeSpan.MinValue;
        EventHandler render = (_, e) =>
        {
            if (e is RenderingEventArgs args && args.RenderingTime != lastRender)
            {
                lastRender = args.RenderingTime;
                counter.Text = "Quadro " + ++ticks;
            }
        };

        MfH264Encoder? encoder = null;
        int captured = 0;
        int accessUnits = 0;
        int keyframes = 0;
        long bytes = 0;
        bool annexB = false;
        double encodeMs = 0;
        string? failure = null;
        var watch = Stopwatch.StartNew();

        using var wake = new DisplayWakeLock();
        var pacer = new VideoFramePacer(RemoteServer.VideoFps);
        try
        {
            window.Show();
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Width = 1920 / dpi.DpiScaleX;
            window.Height = 1080 / dpi.DpiScaleY;
            window.Activate();
            CompositionTarget.Rendering += render;
            await Task.Delay(700);

            var options = new ContinuousCaptureOptions
            {
                FrameTimeout = TimeSpan.FromSeconds(5),
                RawPixels = true,
                AcceptFrame = pacer.TryAccept,
                RawFrame = (raw, ct) =>
                {
                    captured++;
                    if (encoder is null)
                    {
                        encoder = new MfH264Encoder(raw.Width, raw.Height, RemoteServer.VideoFps, 12_000_000);
                    }

                    long start = Stopwatch.GetTimestamp();
                    foreach (EncodedAccessUnit unit in encoder.Encode(raw.Bgra, raw.Stride, (long)raw.Elapsed.TotalMilliseconds))
                    {
                        accessUnits++;
                        bytes += unit.Data.Length;
                        if (unit.Keyframe)
                        {
                            keyframes++;
                            if (!annexB) annexB = LooksLikeAnnexB(unit.Data);
                        }
                    }

                    encodeMs += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    return Task.CompletedTask;
                }
            };

            ticks = 0;
            watch.Restart();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await ContinuousWindowCapture.RunAsync(
                    new WindowInteropHelper(window).Handle,
                    (_, _) => Task.CompletedTask,
                    options,
                    null,
                    cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (CaptureException ex) { failure = ex.Message; }

            double seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
            bool success = failure is null && captured >= 10 && accessUnits >= 10 && keyframes >= 1 && annexB;
            string json = JsonSerializer.Serialize(new
            {
                success,
                capturedFrames = captured,
                accessUnits,
                keyframes,
                bytes,
                annexB,
                averageEncodeMs = captured > 0 ? Math.Round(encodeMs / captured, 2) : 0,
                displayWakeRequested = wake.Acquired,
                targetFps = RemoteServer.VideoFps,
                effectiveFps = Math.Round(accessUnits / seconds, 1),
                encoder = encoder?.Name ?? "desconhecido",
                hardware = encoder?.IsHardware ?? false,
                width = encoder?.Width ?? 0,
                height = encoder?.Height ?? 0,
                durationSeconds = Math.Round(seconds, 3),
                sourceRenderTicks = ticks,
                failure
            });

            await File.WriteAllTextAsync(report, json);
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = false, error = ex.GetType().Name, detail = ex.Message }));
            return 1;
        }
        finally
        {
            CompositionTarget.Rendering -= render;
            encoder?.Dispose();
            window.Close();
        }
    }

    /// <summary>Verifica start code H.264 (00 00 00 01 ou 00 00 01) no início da access unit.</summary>
    private static bool LooksLikeAnnexB(byte[] data)
    {
        if (data.Length < 4) return false;
        bool four = data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1;
        bool three = data[0] == 0 && data[1] == 0 && data[2] == 1;
        return four || three;
    }
}
