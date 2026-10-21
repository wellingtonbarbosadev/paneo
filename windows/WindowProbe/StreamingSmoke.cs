using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WindowProbe.Capture;

namespace WindowProbe;

/// <summary>
/// Smoke runner separado da captura contínua. Não é chamado por App.xaml.cs;
/// a integração no aplicativo fica a cargo de quem for executar o teste.
///
/// Uso previsto: StreamingSmoke.RunAsync(@"C:\pasta-de-teste\streaming-smoke.json").
/// Gera "streaming-smoke.json" e o PNG do último frame em
/// "streaming-smoke.png", ambos ao lado do caminho informado. A pasta deve existir.
///
/// O conteúdo capturado é apenas uma janela sintética deste processo, com texto
/// fixo e um contador. O relatório não inclui títulos de outras janelas, texto de
/// usuário, caminhos absolutos ou conteúdo de tela que não seja o do teste.
/// </summary>
internal static class StreamingSmoke
{
    private const int TargetFrames = 10;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    /// <summary>
    /// Abre uma janela WPF sintética, altera o contador a cada frame, captura
    /// 10 frames contínuos e grava relatório JSON e o último PNG.
    /// </summary>
    /// <param name="reportPath">Caminho do relatório JSON; o PNG usa o mesmo nome.</param>
    /// <returns>0 em sucesso; 1 em falha.</returns>
    internal static async Task<int> RunAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        string jsonPath;
        string pngPath;
        try
        {
            (jsonPath, pngPath) = ResolvePaths(reportPath);
        }
        catch (Exception ex)
        {
            // Sem diretório válido não há onde gravar o relatório.
            Console.Error.WriteLine("StreamingSmoke: " + ex.Message);
            return 1;
        }

        Window? window = null;
        int counter = 0;
        int counterStart = 0;
        int counterEnd = 0;
        int delivered = 0;
        byte[]? lastPng = null;
        var frames = new List<FrameReport>(TargetFrames);
        var streamClock = Stopwatch.StartNew();

        try
        {
            var counterText = new TextBlock
            {
                Text = "0",
                FontSize = 48,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "RemoteDesktop\nSmoke de captura contínua\nContador de teste:",
                Margin = new Thickness(24, 16, 24, 0),
                Foreground = Brushes.Black,
                FontSize = 20,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(counterText);

            window = new Window
            {
                Title = "RemoteDesktop - smoke de captura contínua",
                Width = 640,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White,
                Content = panel
            };

            window.Show();
            await Task.Delay(700, cancellationToken).ConfigureAwait(true);
            window.Activate();

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("A janela sintética não expôs um handle válido.");
            }

            counterStart = counter;
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            streamCts.CancelAfter(OverallTimeout);

            ContinuousCaptureSummary summary = await ContinuousWindowCapture.RunAsync(
                handle,
                (frame, _) =>
                {
                    delivered++;
                    lastPng = frame.Png;
                    frames.Add(new FrameReport(
                        frame.Sequence,
                        frame.Width,
                        frame.Height,
                        frame.Png.Length,
                        frame.EncodeDuration.TotalMilliseconds,
                        frame.Elapsed.TotalMilliseconds));

                    if (delivered >= TargetFrames)
                    {
                        // Encerra o fluxo assim que o alvo é atingido.
                        streamCts.Cancel();
                    }
                    else if (!window.Dispatcher.HasShutdownStarted)
                    {
                        // Atualiza o contador na UI; a próxima composição gera
                        // o próximo frame. É a única interação com a UI aqui.
                        window.Dispatcher.Invoke(() =>
                        {
                            if (window.Dispatcher.HasShutdownStarted)
                            {
                                return;
                            }

                            counter++;
                            if (counter == 4) { window.Width = 800; window.Height = 450; }
                            counterText.Text = counter.ToString(CultureInfo.InvariantCulture);
                        });
                    }

                    return Task.CompletedTask;
                },
                new ContinuousCaptureOptions { FrameTimeout = FrameTimeout },
                null,
                streamCts.Token).ConfigureAwait(true);

            streamClock.Stop();
            counterEnd = counter;

            if (delivered < TargetFrames)
            {
                throw new TimeoutException(
                    $"A captura entregou {delivered} de {TargetFrames} frames antes do " +
                    $"encerramento ou do limite de {OverallTimeout.TotalSeconds:0} segundos.");
            }

            if (frames.Select(f => (f.Width, f.Height)).Distinct().Count() < 2)
                throw new InvalidOperationException("O redimensionamento não apareceu nos frames.");

            if (lastPng is null)
            {
                throw new InvalidOperationException("Nenhum frame PNG foi recebido.");
            }

            if (counterEnd <= counterStart)
            {
                throw new InvalidOperationException("O contador sintético não mudou durante a captura.");
            }

            await File.WriteAllBytesAsync(pngPath, lastPng, cancellationToken).ConfigureAwait(true);

            var report = new
            {
                success = true,
                test = "Janela sintetica WPF com contador; captura continua WGC de 10 frames em PNG na memoria.",
                targetFrames = TargetFrames,
                framesDelivered = delivered,
                counterStart,
                counterEnd,
                counterChanged = counterEnd > counterStart,
                lastPngFile = Path.GetFileName(pngPath),
                lastPngBytes = lastPng.Length,
                durationMs = streamClock.Elapsed.TotalMilliseconds,
                framesCoalesced = summary.FramesCoalesced,
                resizes = summary.Resizes,
                sessionDurationMs = summary.Duration.TotalMilliseconds,
                frameTimeoutMs = FrameTimeout.TotalMilliseconds,
                frames
            };

            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, ReportJson),
                cancellationToken).ConfigureAwait(true);
            return 0;
        }
        catch (Exception ex)
        {
            streamClock.Stop();

            if (window is not null && !window.Dispatcher.HasShutdownStarted)
            {
                counterEnd = counter;
            }

            try
            {
                var failure = new
                {
                    success = false,
                    test = "Janela sintetica WPF com contador; captura continua WGC.",
                    targetFrames = TargetFrames,
                    framesDelivered = delivered,
                    counterStart,
                    counterEnd,
                    error = ex.ToString(),
                    frames
                };

                await File.WriteAllTextAsync(jsonPath,
                    JsonSerializer.Serialize(failure, ReportJson),
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch
            {
                // Preserva o código de falha mesmo se o relatório não puder ser gravado.
            }

            return 1;
        }
        finally
        {
            window?.Close();
        }
    }

    private static (string JsonPath, string PngPath) ResolvePaths(string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("Informe o caminho do relatório JSON.", nameof(reportPath));
        }

        string jsonPath = Path.GetFullPath(reportPath);
        if (!jsonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            jsonPath += ".json";
        }

        string? directory = Path.GetDirectoryName(jsonPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Diretório de destino inexistente: {directory ?? "(vazio)"}");
        }

        return (jsonPath, Path.ChangeExtension(jsonPath, ".png"));
    }

    private sealed record FrameReport(
        long Sequence,
        int Width,
        int Height,
        int Bytes,
        double EncodeMs,
        double ElapsedMs);
}
