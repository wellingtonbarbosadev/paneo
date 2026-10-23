using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowProbe.Remote;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var window = new RemoteHostWindow();
            var root = (DockPanel)window.Content;
            root.Background = window.Background;
            var statusCard = (Border)root.Children[0];
            Assert(DockPanel.GetDock(statusCard) == Dock.Bottom, "status stays fixed outside the scroll region");
            Assert(window.Title.StartsWith("Paneo", StringComparison.Ordinal), "Paneo branding is visible");

            var options = Find<Expander>(window).Single();
            Assert(!options.IsExpanded, "advanced controls start collapsed");
            Assert(window.MinHeight <= 640, "compact minimum window height");

            var pin = FindByName<PasswordBox>(window, "PIN do pareamento com o Mac");
            var pinAction = FindByName<Button>(window, "Salvar ou trocar PIN do pareamento");
            var start = FindByName<Button>(window, "Iniciar compartilhamento do PC com o Mac");
            var stop = FindByName<Button>(window, "Encerrar todos os compartilhamentos ativos");
            Assert(pin.Visibility == Visibility.Visible && pinAction.IsDefault && !start.IsDefault,
                "unpaired state asks for PIN and Enter submits it first");
            Assert(!stop.IsEnabled, "stop is disabled before a listener starts");

            pin.Password = "x";
            pinAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(FindStatus(window).Text.Contains("PIN precisa ter"), "invalid PIN feedback remains visible");
            Assert(PairingStore.Saves == 0, "invalid PIN does not reach persistence stub");

            pin.Password = "fixture-only-value";
            pinAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(pin.Visibility == Visibility.Collapsed, "saved PIN field collapses");
            Assert(start.IsDefault && start.IsEnabled, "focus flow enables start after PIN");
            Assert(PairingStore.Saves == 1, "valid PIN reaches in-memory persistence stub");

            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(RemoteServer.StartCount == 1 && stop.IsEnabled, "start adds one active listener and enables stop");
            var stopTask = (Task)typeof(RemoteHostWindow).GetMethod("StopAllAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { "Compartilhamento encerrado." })!;
            stopTask.GetAwaiter().GetResult();
            Assert(RemoteServer.StopCount == 1 && !stop.IsEnabled, "stop clears listener and disables itself");
            Assert(HostDiscovery.ListenerStoppedBeforeDiscovery, "discovery cleanup cannot keep the listener open");
            Assert(FindByName<Button>(window, "Revogar o pareamento e exigir novo PIN").IsEnabled,
                "revoke remains separate from stop");

            detectedAddress(window).Text = "192.0.2.10";
            options.IsExpanded = true;
            Layout(root, window.MinWidth, window.MinHeight);
            AssertWithin(root, FindByName<Button>(window, "Iniciar compartilhamento do PC com o Mac"), "start action fits at minimum size");
            AssertWithin(root, FindStatus(window), "status remains inside minimum-size window");
            Layout(root, window.Width, window.Height);
            AssertWithin(root, FindByName<Button>(window, "Iniciar compartilhamento do PC com o Mac"), "start action fits at default size");
            AssertWithin(root, FindStatus(window), "status remains inside default-size window");

            // Static layout/render smoke test only: this harness never shows a real window,
            // so Loaded-based entrance/reveal animations are intentionally settled here.
            SettleAnimations(root);
            var bitmap = Render(root);
            Assert(HasVisiblePixels(bitmap), "WPF window renders non-transparent pixels");
            if (args.Length == 1)
            {
                using var stream = File.Create(args[0]);
                new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
            }

            Console.WriteLine("HostUiTests PASS (UI assertions; no real PIN, storage, registry, mutex, clipboard, or listener used)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"HostUiTests FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static T FindByName<T>(DependencyObject root, string name) where T : DependencyObject =>
        Find<T>(root).Single(item => AutomationProperties.GetName(item) == name);

    private static TextBlock FindStatus(DependencyObject root) => Find<TextBlock>(root)
        .Single(item => AutomationProperties.GetName(item) == "Estado do host Paneo");

    private static TextBlock detectedAddress(Window window) => Find<TextBlock>(window)
        .Single(item => AutomationProperties.GetName(item) == "Endereço local do PC para conectar o Paneo no Mac");

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void AssertWithin(FrameworkElement root, FrameworkElement element, string message)
    {
        var origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
        Assert(origin.X >= 0 && origin.Y >= 0 && origin.X + element.ActualWidth <= root.ActualWidth + 1 &&
            origin.Y + element.ActualHeight <= root.ActualHeight + 1, message);
    }

    private static void SettleAnimations(FrameworkElement root)
    {
        foreach (UIElement element in Find<UIElement>(root))
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private static RenderTargetBitmap Render(FrameworkElement root)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    private static bool HasVisiblePixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
            if (pixels[alpha] != 0) return true;
        return false;
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependency) continue;
            foreach (T match in Find<T>(dependency)) yield return match;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
