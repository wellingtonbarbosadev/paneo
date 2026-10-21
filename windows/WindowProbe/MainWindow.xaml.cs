using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowProbe.Capture;
using WindowProbe.Interop;
using WindowProbe.Models;

namespace WindowProbe;

public partial class MainWindow : Window
{
    private static readonly TimeSpan CaptureFrameTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnWindowClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppendLog("WindowProbe iniciado. Captura somente local; sem rede, serviços ou injeção de entrada.");
        RefreshWindows();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Já descartado.
        }

        _lifetime.Dispose();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
    }

    private void DestinationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed)
        {
            return;
        }

        WindowInfo? window = WindowList.SelectedItem as WindowInfo;
        string? path = PickDestination(window);
        if (path is not null)
        {
            DestinationPathBox.Text = path;
            AppendLog($"Destino escolhido: {path}");
        }
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed)
        {
            return;
        }

        if (WindowList.SelectedItem is not WindowInfo window)
        {
            MessageBox.Show(
                this,
                "Selecione uma janela na lista antes de capturar.",
                "Nenhuma janela selecionada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Perguntar a cada captura evita sobrescrever silenciosamente o PNG
        // anterior em cliques subsequentes.
        string? path = PickDestination(window);
        if (path is null) return;
        DestinationPathBox.Text = path;

        SetBusy(true);
        AppendLog($"--- Captura: \"{window.Title}\" ({window.HandleText}), pid {window.ProcessId}. ---");

        try
        {
            var service = new WindowCaptureService();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

            CaptureResult result = await service.CaptureAsync(
                window.Handle,
                path,
                CaptureFrameTimeout,
                new DispatcherProgress(Dispatcher, AppendLog),
                linked.Token);

            if (_closed)
            {
                return;
            }

            DestinationPathBox.Text = result.Path;
            StatusText.Text = $"PNG salvo: {result.Path}";
            AppendLog(
                $"SUCESSO: {result.Width}x{result.Height}, {result.Bytes} bytes, " +
                $"{result.Duration.TotalMilliseconds:0} ms.");
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Captura cancelada.";
                AppendLog("Captura cancelada.");
            }
        }
        catch (CaptureException ex)
        {
            ReportCaptureError(ex);
        }
        catch (Exception ex)
        {
            ReportCaptureError(new CaptureException("Erro inesperado na captura.", ex));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshWindows()
    {
        if (_closed || _busy)
        {
            return;
        }

        try
        {
            IntPtr previousHandle = WindowList.SelectedItem is WindowInfo selected
                ? selected.Handle
                : IntPtr.Zero;

            IReadOnlyList<WindowInfo> windows = WindowEnumerator.GetVisibleWindows();
            WindowList.ItemsSource = windows;

            if (previousHandle != IntPtr.Zero)
            {
                WindowInfo? previous = windows.FirstOrDefault(candidate => candidate.Handle == previousHandle);
                if (previous is not null)
                {
                    WindowList.SelectedItem = previous;
                }
            }

            StatusText.Text = $"{windows.Count} janela(s) visível(is) de outros processos.";
            AppendLog($"Enumeração concluída: {windows.Count} janela(s) elegível(is).");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao enumerar janelas.";
            AppendLog("ERRO na enumeração: " + Diagnostics.Describe(ex));

            if (!_closed)
            {
                MessageBox.Show(
                    this,
                    Diagnostics.Describe(ex),
                    "Falha ao enumerar janelas",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private string? PickDestination(WindowInfo? window)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar captura como PNG",
            Filter = "Imagem PNG (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildFileName(window),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static string BuildFileName(WindowInfo? window)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string name = window is null
            ? "janela"
            : new string(window.ProcessName.Where(char.IsLetterOrDigit).ToArray());

        if (name.Length == 0)
        {
            name = "janela";
        }

        if (name.Length > 32)
        {
            name = name[..32];
        }

        return $"captura-{name}-{stamp}.png";
    }

    private void ReportCaptureError(CaptureException exception)
    {
        string details = Diagnostics.Describe(exception);
        string hint = Diagnostics.HintFor(exception);

        AppendLog("ERRO: " + details);
        if (hint.Length > 0)
        {
            AppendLog("Dica: " + hint);
        }

        if (_closed)
        {
            return;
        }

        StatusText.Text = "Falha na captura.";

        string text = hint.Length > 0
            ? details + Environment.NewLine + Environment.NewLine + hint
            : details;

        MessageBox.Show(this, text, "Falha na captura", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        if (_closed)
        {
            return;
        }

        RefreshButton.IsEnabled = !busy;
        CaptureButton.IsEnabled = !busy;
        DestinationButton.IsEnabled = !busy;
        WindowList.IsEnabled = !busy;
        CaptureButton.Content = busy ? "Capturando…" : "Capturar selecionada…";

        if (busy)
        {
            StatusText.Text = "Capturando…";
        }
    }

    private void AppendLog(string message)
    {
        if (_closed)
        {
            return;
        }

        if (LogBox.Text.Length > 200_000)
        {
            LogBox.Clear();
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss.fff}  (log truncado para limitar memória){Environment.NewLine}");
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        if (LogBox.Text.Length > 0)
        {
            LogBox.AppendText(Environment.NewLine);
        }

        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
    }

    /// <summary>
    /// Progresso com post no Dispatcher que tolera o encerramento da janela.
    /// Evita exceção em thread de captura quando a UI já está fechando.
    /// </summary>
    private sealed class DispatcherProgress : IProgress<string>
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _handler;

        public DispatcherProgress(Dispatcher dispatcher, Action<string> handler)
        {
            _dispatcher = dispatcher;
            _handler = handler;
        }

        public void Report(string value)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() =>
                    {
                        try
                        {
                            _handler(value);
                        }
                        catch
                        {
                            // Nunca propagar falha de UI para o pipeline de captura.
                        }
                    }));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher encerrando entre a checagem e o BeginInvoke.
            }
        }
    }
}
