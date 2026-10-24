using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowProbe.Capture;

namespace WindowProbe.Remote;

/// <summary>Fixture descartável para comandos de janela e menus Win32 sobre TLS v2.</summary>
internal static class WindowCommandsSmoke
{
    private const uint MfString = 0x0000, MfGray = 0x0001, MfPopup = 0x0010, MfChecked = 0x0008, MfSeparator = 0x0800, MfByPosition = 0x0400;
    private const uint WmCommand = 0x0111;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr idOrSubmenu, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr hwnd, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint EnableMenuItem(IntPtr menu, UIntPtr item, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out RECT rect, int size);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    internal static async Task<int> RunAsync(string directory)
    {
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        string sessionFile = Path.Combine(root, "session.json"), reportFile = Path.Combine(root, "host-result.json");
        string completionFile = Path.Combine(root, "client-done");
        string closeDialogReadyFile = Path.Combine(root, "close-dialog-ready");
        var status = new TextBlock { Text = "Pronto", FontSize = 18, Margin = new Thickness(18) };
        var editor = new TextBox { Text = "Janela fixture descartável — teste de menus e comandos.", FontSize = 20, Margin = new Thickness(18), Height = 90 };
        var body = new StackPanel(); body.Children.Add(status); body.Children.Add(editor);
        var window = new Window
        {
            Title = "Paneo Window Commands Fixture",
            Width = 900,
            Height = 620,
            MinWidth = 500,
            MinHeight = 360,
            Content = body,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResize
        };
        int menuCommands = 0, closePrompts = 0, closeCancelled = 0;
        bool allowFinalClose = false;
        IntPtr fixtureMenu = IntPtr.Zero, hwnd = IntPtr.Zero;
        RECT originalRect = default;
        RemoteServer? server = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        long tick = 0;
        timer.Tick += (_, _) => status.Text = $"Fixture descartável • quadro {++tick} • {DateTime.Now:T}";
        window.Closing += (closingSender, e) =>
        {
            if (allowFinalClose) return;
            e.Cancel = true;
            closePrompts++;
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                File.WriteAllText(closeDialogReadyFile, "ready")));
            MessageBoxResult response = MessageBox.Show(window, "Fechar a janela fixture?", "Paneo — confirmar fechamento",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (response == MessageBoxResult.Cancel) closeCancelled++;
        };

        try
        {
            window.Show();
            window.Activate();
            await Task.Delay(500);
            hwnd = new WindowInteropHelper(window).Handle;
            if (!GetWindowRect(hwnd, out originalRect)) throw new InvalidOperationException("Não foi possível guardar a geometria inicial.");
            fixtureMenu = BuildMenu();
            if (!SetMenu(hwnd, fixtureMenu)) throw new InvalidOperationException("SetMenu falhou.");
            HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr messageHwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == WmCommand)
                {
                    menuCommands++;
                    long commandId = wParam.ToInt64() & 0xffff;
                    if (commandId == 107) _ = EnableMenuItem(fixtureMenu, (UIntPtr)6, MfByPosition | MfGray);
                    status.Text = $"Comando de menu recebido: {commandId}";
                    handled = true;
                }
                return IntPtr.Zero;
            });

            await VerifyAutoFitAsync(window, hwnd, root);

            string pin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var credentials = HostCredentials.Create(pin);
            // A enumeração roda na thread do servidor. Capture os valores WPF
            // antes de criar o provider para não consultar DependencyObjects
            // fora do Dispatcher durante o handshake.
            string fixtureTitle = window.Title;
            int fixtureWidth = (int)window.ActualWidth, fixtureHeight = (int)window.ActualHeight;
            server = new RemoteServer(hwnd, IPAddress.Any, 9446, credentials,
                windowList: () => [new WindowDescriptor(hwnd.ToInt64(), fixtureTitle, "WindowCommandsFixture", fixtureWidth, fixtureHeight)]);
            server.Start();
            await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(new
            {
                host = "127.0.0.1", port = server.Port, fingerprint = server.Fingerprint, pin,
                windowId = hwnd.ToInt64()
            }));
            timer.Start();

            DateTime deadline = DateTime.UtcNow.AddMinutes(8);
            while (DateTime.UtcNow < deadline && !File.Exists(completionFile))
            {
                if (server.LastFailure is not null) break;
                await Task.Delay(200);
            }
            string completion = File.Exists(completionFile) ? await File.ReadAllTextAsync(completionFile) : "missing";
            bool geometryRestored = GetWindowRect(hwnd, out RECT rect) &&
                rect.Left == originalRect.Left && rect.Top == originalRect.Top &&
                rect.Right == originalRect.Right && rect.Bottom == originalRect.Bottom;
            bool success = completion == "passed" && server.FramesSent > 0 && menuCommands > 0 && closePrompts > 0 && closeCancelled > 0 && geometryRestored;
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                success, frames = server.FramesSent, menuCommands, closePrompts, closeCancelled,
                geometryRestored, rect = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom }, failure = server.LastFailure
            }));
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.GetType().Name,
                message = ex.Message
            }));
            return 1;
        }
        finally
        {
            timer.Stop();
            if (server is not null) await server.StopAsync();
            if (File.Exists(sessionFile)) File.Delete(sessionFile);
            if (File.Exists(closeDialogReadyFile)) File.Delete(closeDialogReadyFile);
            if (hwnd != IntPtr.Zero) _ = SetMenu(hwnd, IntPtr.Zero);
            if (fixtureMenu != IntPtr.Zero) _ = DestroyMenu(fixtureMenu);
            allowFinalClose = true;
            window.Close();
        }
    }

    private static async Task VerifyAutoFitAsync(Window window, IntPtr hwnd, string root)
    {
        var evidence = new List<object>();
        double minWidth = window.MinWidth, minHeight = window.MinHeight;
        window.MinWidth = window.MinHeight = 100;
        try
        {
            if (!GetWindowRect(hwnd, out RECT original)) throw new InvalidOperationException("Geometria inicial indisponível.");
            using var commands = new WindowCommands(hwnd);
            foreach (var (width, height) in new[] { (1120, 630), (640, 900), (4096, 2304) })
            {
                var result = commands.Execute(JsonSerializer.SerializeToElement(new { action = "reading", enabled = true, width, height }));
                if (!result.Success) throw new InvalidOperationException(result.Message);
                await Task.Delay(500);
                string image = Path.Combine(root, $"auto-fit-{width}x{height}.png");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                CaptureResult frame = await new WindowCaptureService().CaptureAsync(hwnd, image, TimeSpan.FromSeconds(8), null, timeout.Token);
                _ = GetWindowRect(hwnd, out RECT outer);
                bool hasVisible = DwmGetWindowAttribute(hwnd, 9, out RECT visible, Marshal.SizeOf<RECT>()) == 0;
                bool matchesAspect = Math.Abs(frame.Width - (double)width * frame.Height / height) <= 2;
                evidence.Add(new
                {
                    requested = new[] { width, height }, captured = new[] { frame.Width, frame.Height },
                    outer = new[] { outer.Right - outer.Left, outer.Bottom - outer.Top },
                    visible = hasVisible ? new[] { visible.Right - visible.Left, visible.Bottom - visible.Top } : null,
                    matchesAspect
                });
                await File.WriteAllTextAsync(Path.Combine(root, "auto-fit-measurements.json"),
                    JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
                if (!matchesAspect) throw new InvalidOperationException("A captura não preservou a proporção da área de vídeo.");
                result = commands.Execute(JsonSerializer.SerializeToElement(new { action = "reading", enabled = false }));
                if (!result.Success) throw new InvalidOperationException(result.Message);
                await Task.Delay(350);
                if (!GetWindowRect(hwnd, out RECT restored) || restored.Left != original.Left || restored.Top != original.Top
                    || restored.Right != original.Right || restored.Bottom != original.Bottom)
                    throw new InvalidOperationException("O ajuste automático não restaurou a geometria original.");
            }
        }
        finally { window.MinWidth = minWidth; window.MinHeight = minHeight; }
    }

    private static IntPtr BuildMenu()
    {
        IntPtr root = CreateMenu(), submenu = CreatePopupMenu(), disabledSubmenu = CreatePopupMenu(), dynamicSubmenu = CreatePopupMenu();
        if (root == IntPtr.Zero || submenu == IntPtr.Zero || disabledSubmenu == IntPtr.Zero || dynamicSubmenu == IntPtr.Zero)
            throw new InvalidOperationException("Não foi possível criar o menu Win32 fixture.");
        AppendMenu(submenu, MfString, (UIntPtr)104, "Nested fixture command");
        AppendMenu(submenu, MfString | MfGray, (UIntPtr)105, "Nested disabled command");
        AppendMenu(disabledSubmenu, MfString, (UIntPtr)106, "Child of disabled fixture menu");
        AppendMenu(dynamicSubmenu, MfString, (UIntPtr)108, "Child to disable after snapshot");
        AppendMenu(root, MfString, (UIntPtr)101, "Run fixture command");
        AppendMenu(root, MfString | MfGray, (UIntPtr)102, "Disabled fixture command");
        AppendMenu(root, MfString | MfChecked, (UIntPtr)103, "Checked fixture command");
        AppendMenu(root, MfSeparator, UIntPtr.Zero, null);
        AppendMenu(root, MfPopup, (UIntPtr)submenu, "Nested fixture menu");
        AppendMenu(root, MfPopup | MfGray, (UIntPtr)disabledSubmenu, "Disabled fixture submenu");
        AppendMenu(root, MfPopup, (UIntPtr)dynamicSubmenu, "Dynamic parent fixture submenu");
        AppendMenu(root, MfString, (UIntPtr)107, "Disable dynamic parent");
        return root;
    }
}
