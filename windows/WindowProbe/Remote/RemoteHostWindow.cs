using System.Net;
using System.Windows;
using System.Windows.Controls;
using WindowProbe.Models;

namespace WindowProbe.Remote;

internal sealed class RemoteHostWindow : Window
{
    private readonly ComboBox windows = new() { MinWidth = 500, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox address = new() { Text = "127.0.0.1" };
    private readonly TextBox port = new() { Text = "9443" };
    private readonly TextBox credentials = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
    private readonly TextBlock status = new() { Text = "Parado", TextWrapping = TextWrapping.Wrap };
    private readonly Button start = new() { Content = "Autorizar janela e iniciar", Margin = new Thickness(0, 8, 0, 8) };
    private readonly List<RemoteServer> servers = [];
    private readonly ListBox shared = new() { Height = 80 };
    private bool stopping;
    private bool closing;
    private readonly Mutex hostMutex = new(false, @"Global\RemoteDesktop-PairedHost-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
    private bool ownsMutex;

    public RemoteHostWindow()
    {
        Title = "RemoteDesktop — compartilhar janelas"; Width = 720; Height = 660;
        try { ownsMutex = hostMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            Content = new TextBlock { Text = "Já existe um host aberto nesta conta Windows. Use-o para parar ou revogar o pareamento.", Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
            Closed += (_, _) => hostMutex.Dispose();
            return;
        }
        Closed += (_, _) => { hostMutex.ReleaseMutex(); hostMutex.Dispose(); };
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Selecione a janela que o Mac poderá ver e controlar. O foco e o mouse do PC serão compartilhados.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(windows);
        var refresh = new Button { Content = "Atualizar janelas" }; refresh.Click += (_, _) => windows.ItemsSource = WindowEnumerator.GetVisibleWindows(); panel.Children.Add(refresh);
        panel.Children.Add(new TextBlock { Text = "IP local de escuta (use o IP da rede para conectar pelo Mac):" }); panel.Children.Add(address);
        panel.Children.Add(new TextBlock { Text = "Porta:" }); panel.Children.Add(port);
        panel.Children.Add(start); start.Click += Start;
        panel.Children.Add(new TextBlock { Text = "Janelas autorizadas (uma porta por janela):" }); panel.Children.Add(shared);
        var stop = new Button { Content = "Parar compartilhamento" }; stop.Click += async (_, _) => await Stop(); panel.Children.Add(stop);
        var revoke = new Button { Content = "Revogar pareamento" }; revoke.Click += async (_, _) => {
            await Stop();
            try { PairingStore.Revoke(); status.Text = "Pareamento revogado. A próxima autorização terá novas credenciais."; }
            catch (Exception) { status.Text = "Compartilhamento parado, mas não foi possível apagar o pareamento."; }
        }; panel.Children.Add(revoke);
        panel.Children.Add(new TextBlock { Text = "Copie SHA-256 e token para o Mac. O pareamento persiste nesta conta Windows até revogação; cada compartilhamento exige selecionar a janela.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(credentials); panel.Children.Add(status);
        windows.ItemsSource = WindowEnumerator.GetVisibleWindows();
        Closing += async (_, e) => { if (closing) return; e.Cancel = true; closing = true; await Stop(); Close(); };
    }
    private async void Start(object sender, RoutedEventArgs args)
    {
        if (stopping) return;
        if (windows.SelectedItem is not WindowInfo selected || !IPAddress.TryParse(address.Text, out var ip) || !int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
        { status.Text = "Selecione uma janela e informe IP e porta válidos."; return; }
        RemoteServer? server = null;
        try
        {
            var pairing = PairingStore.LoadOrCreate();
            server = new RemoteServer(selected.Handle, ip, number, pairing.Certificate, pairing.Token);
            server.Status = value => Dispatcher.BeginInvoke(() => status.Text = value);
            server.Start();
            servers.Add(server);
            shared.Items.Add($"Porta {server.Port}: {selected.Summary}");
            RemoteInput.Activate(selected.Handle);
            credentials.Text = "Porta desta janela: " + server.Port + "\nSHA-256: " + server.Fingerprint + "\nToken: " + server.Token;
            status.Text = "Aguardando Mac pareado. Use Revogar pareamento para invalidar as credenciais.";
            start.Content = "Autorizar outra janela e porta";
            port.Text = Math.Min(65535, number + 1).ToString();
        }
        catch (Exception) { if (server is not null) { servers.Remove(server); await server.StopAsync(); } server = null; status.Text = "Não foi possível iniciar. Confira IP/porta e pareamento salvo."; }
    }
    private async Task Stop()
    {
        if (stopping) return;
        stopping = true; start.IsEnabled = false;
        try
        {
            var previous = servers.ToArray(); servers.Clear();
            await Task.WhenAll(previous.Select(item => item.StopAsync()));
            shared.Items.Clear();
        }
        finally { stopping = false; }
        credentials.Clear(); start.IsEnabled = true; windows.IsEnabled = true; status.Text = "Compartilhamento parado; pareamento preservado.";
    }
}
