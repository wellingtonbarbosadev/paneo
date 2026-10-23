using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using WindowProbe.Models;

namespace WindowProbe.Remote;

/// <summary>
/// Host de compartilhamento: PIN, IP local e porta. A escolha da janela é do
/// Mac (Fase 3): o host apenas autoriza este computador e publica a lista de
/// janelas visíveis. Não exibe fingerprint nem token.
/// </summary>
internal sealed class RemoteHostWindow : Window
{
    private readonly ComboBox windows = new() { MinWidth = 520, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock detectedAddress = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox port = new() { Text = "9443", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PasswordBox pin = new()
    {
        MaxLength = HostCredentials.PinMaxLength,
        Width = 220,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 4, 0, 4)
    };
    private readonly TextBlock pinState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button pinAction = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button start = new() { Content = "Iniciar compartilhamento", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListBox shared = new() { Height = 80 };
    private readonly CheckBox allowHover = new() { Content = "Permitir que o Mac mova o cursor deste PC (hover)", IsChecked = true };
    private readonly CheckBox startWithWindows = new() { Content = "Iniciar e compartilhar automaticamente ao entrar no Windows" };
    private readonly TextBlock status = new() { Text = "Parado", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<RemoteServer> servers = [];
    /// <summary>
    /// Mantém a tela acordada enquanto o host está aberto. Sem isso o monitor
    /// entra em stand-by (o timeout é curto) e o WGC para de entregar quadros,
    /// deixando o Mac com a última imagem. Solto ao fechar a janela.
    /// </summary>
    private readonly DisplayWakeLock hostWake = new();
    private readonly Mutex hostMutex = new(false, @"Global\RemoteDesktop-PairedHost-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);

    private HostState? state;
    private bool changingPin;
    private bool stopping;
    private bool closing;
    private bool ownsMutex;
    private bool updatingStartup;
    private bool settingsLoaded;

    /// <summary>Serializa as gravações do estado de pareamento.</summary>
    private readonly SemaphoreSlim persistGate = new(1, 1);

    /// <summary>Verdadeiro enquanto a revogação está em curso.</summary>
    private volatile bool revoked;

    public RemoteHostWindow(bool autoStart = false)
    {
        Title = "RemoteDesktop — compartilhar janela";
        Width = 720;
        Height = 680;

        try { ownsMutex = hostMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }

        if (!ownsMutex)
        {
            Content = new TextBlock
            {
                Text = "Já existe um host aberto nesta conta Windows. Use-o para encerrar ou revogar o pareamento.",
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap
            };
            Closed += (_, _) => hostMutex.Dispose();
            return;
        }

        Closed += (_, _) =>
        {
            try { hostMutex.ReleaseMutex(); } catch (ApplicationException) { }
            hostMutex.Dispose();
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        panel.Children.Add(new TextBlock
        {
            Text = "Autorize este PC para o Mac. O Mac escolherá qual janela ver. O mouse e o teclado do PC serão compartilhados.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Janelas visíveis neste PC (o Mac escolhe qual abrir):",
            Margin = new Thickness(0, 8, 0, 4)
        });
        panel.Children.Add(windows);
        var refresh = new Button { Content = "Atualizar janelas", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (_, _) => RefreshWindows();
        panel.Children.Add(refresh);

        panel.Children.Add(new TextBlock
        {
            Text = "IP local detectado (use este endereço no Mac):",
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(detectedAddress);

        panel.Children.Add(new TextBlock { Text = "Porta:" });
        panel.Children.Add(port);

        panel.Children.Add(new TextBlock { Text = "PIN de conexão:", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(pinState);
        panel.Children.Add(pin);
        panel.Children.Add(pinAction);
        pinAction.Click += SavePin;

        panel.Children.Add(start);
        start.Click += StartSharing;

        panel.Children.Add(new TextBlock { Text = "Compartilhamentos ativos nesta sessão:" });
        panel.Children.Add(shared);

        var stop = new Button { Content = "Encerrar todas", Margin = new Thickness(0, 8, 0, 0) };
        stop.Click += async (_, _) => await StopAllAsync("Compartilhamento encerrado; pareamento preservado.");
        panel.Children.Add(stop);

        allowHover.Margin = new Thickness(0, 8, 0, 0);
        allowHover.Checked += (_, _) => ApplyHoverSetting();
        allowHover.Unchecked += (_, _) => ApplyHoverSetting();
        panel.Children.Add(allowHover);

        startWithWindows.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(startWithWindows);
        panel.Children.Add(new TextBlock
        {
            Text = "Usa o PIN salvo nesta conta. Funciona após o login; fechar esta janela encerra o host.",
            TextWrapping = TextWrapping.Wrap
        });

        var revoke = new Button { Content = "Revogar pareamento", Margin = new Thickness(0, 8, 0, 0) };
        revoke.Click += RevokePairing;
        panel.Children.Add(revoke);

        panel.Children.Add(status);

        detectedAddress.Text = DetectLocalAddress();
        RefreshWindows();

        try
        {
            SetState(PairingStore.LoadOrCreate());
            status.Text = state?.HasPin == true
                ? "Pronto para compartilhar."
                : "Defina um PIN para habilitar o compartilhamento.";
        }
        catch (Exception)
        {
            status.Text = "Não foi possível ler o pareamento salvo. Use Revogar pareamento para recomeçar.";
        }

        UpdatePinUi();

        try
        {
            HostSettings settings = HostSettings.Load();
            port.Text = settings.Port.ToString();
            allowHover.IsChecked = settings.AllowHover;
            settingsLoaded = true;
        }
        catch (Exception)
        {
            status.Text = "Não foi possível ler as preferências salvas. Confira a porta e o hover e inicie manualmente para salvá-las novamente.";
        }

        try { SetStartupChecked(HostStartup.IsEnabled()); }
        catch (Exception) { status.Text = "Não foi possível consultar a inicialização com o Windows."; }
        startWithWindows.Checked += ChangeStartup;
        startWithWindows.Unchecked += ChangeStartup;
        port.LostKeyboardFocus += (_, _) => PersistSettings();

        if (autoStart)
        {
            Loaded += (_, _) =>
            {
                if (!settingsLoaded || state is null) return;
                if (StartSharingCore()) WindowState = WindowState.Minimized;
            };
        }

        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            closing = true;
            await StopAllAsync();
            hostWake.Dispose();
            Close();
        };
    }

    private void SetState(HostState next) => state = next;

    private void RefreshWindows() => windows.ItemsSource = WindowEnumerator.GetVisibleWindows();

    private void UpdatePinUi()
    {
        bool hasPin = state?.HasPin == true;
        bool editing = !hasPin || changingPin;
        pin.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        pinAction.Content = editing ? "Salvar PIN" : "Trocar PIN";
        pinState.Text = editing
            ? $"Escolha um PIN com pelo menos {HostCredentials.PinMinLength} caracteres. Ele será pedido ao Mac."
            : "PIN definido neste PC.";
        pinAction.IsEnabled = !stopping;
        start.IsEnabled = hasPin && !stopping;
        RefreshWindowsEnabled();
    }

    private void RefreshWindowsEnabled()
    {
        windows.IsEnabled = !stopping;
        port.IsEnabled = !stopping;
    }

    private void SavePin(object sender, RoutedEventArgs e)
    {
        if (state?.HasPin == true && !changingPin)
        {
            changingPin = true;
            UpdatePinUi();
            pin.Focus();
            return;
        }

        string value = pin.Password;
        if (!HostCredentials.IsAcceptablePin(value))
        {
            status.Text = $"O PIN precisa ter entre {HostCredentials.PinMinLength} e {HostCredentials.PinMaxLength} caracteres.";
            return;
        }

        try
        {
            HostCredentials credentials = state?.Credentials?.WithPin(value) ?? HostCredentials.Create(value);
            SetState(PairingStore.Save(credentials));
            // O PIN novo precisa valer também nos servidores já abertos.
            ApplyCredentialsToServers(credentials);
            changingPin = false;
            pin.Clear();
            status.Text = "PIN definido. Ele será pedido na primeira conexão do Mac.";
        }
        catch (Exception)
        {
            status.Text = "Não foi possível salvar o PIN.";
        }

        UpdatePinUi();
    }

    private void StartSharing(object sender, RoutedEventArgs e)
    {
        StartSharingCore();
    }

    private bool StartSharingCore()
    {
        if (stopping) return false;
        if (state?.Credentials is null)
        {
            status.Text = "Defina um PIN antes de iniciar o compartilhamento.";
            return false;
        }

        if (!int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
        {
            status.Text = "Informe uma porta válida (1 a 65535).";
            return false;
        }

        if (!PersistSettings()) return false;
        if (servers.Any(existing => existing.Port == number))
        {
            status.Text = "O compartilhamento já está ativo nesta porta.";
            return true;
        }

        RemoteServer? server = null;
        try
        {
            server = new RemoteServer(
                IntPtr.Zero,
                IPAddress.Any,
                number,
                state.Credentials,
                state.Certificate,
                () => WindowEnumerator.GetVisibleWindows()
                    .Select(window => new WindowDescriptor(
                        window.Handle.ToInt64(), window.Title, window.ProcessName, window.Width, window.Height,
                        window.IconBase64))
                    .ToArray());

            server.Status = value => Dispatcher.BeginInvoke(() =>
            {
                if (!closing) status.Text = value;
            });
            server.CredentialsChanged += OnCredentialsChanged;
            server.AllowHover = allowHover.IsChecked == true;
            server.Start();
            servers.Add(server);
            shared.Items.Add($"Porta {server.Port}: este PC (janelas escolhidas pelo Mac)");
            status.Text = "Compartilhamento ativo. No Mac, informe o IP e o PIN e escolha a janela.";
            return true;
        }
        catch (Exception)
        {
            if (server is not null)
            {
                server.CredentialsChanged -= OnCredentialsChanged;
                servers.Remove(server);
                _ = server.StopAsync();
            }

            status.Text = "Não foi possível iniciar. Confira a porta e tente novamente.";
            return false;
        }
    }

    private void OnCredentialsChanged(object? sender, HostCredentials updated)
    {
        // Persistir fora da thread da UI; a falha não derruba a sessão em curso.
        // A gravação entra numa fila serial: sem isso, uma gravação disparada
        // antes de uma revogação poderia recriar o arquivo removido e devolver
        /// o pareamento revogado.
        _ = Task.Run(async () =>
        {
            await persistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (revoked) return;
                HostState saved = PairingStore.Save(updated);
                saved.Certificate.Dispose();
            }
            catch (Exception) { }
            finally { persistGate.Release(); }
        });

        Dispatcher.BeginInvoke(() =>
        {
            if (state is not null) state = state with { Credentials = updated };
        });
    }

    /// <summary>
    /// Aplica as credenciais novas aos servidores já abertos. Sem isso o PIN
    /// trocado continuaria válido nos servidores em execução (que guardam um
    /// retrato das credenciais).
    /// </summary>
    private void ApplyCredentialsToServers(HostCredentials credentials)
    {
        foreach (RemoteServer server in servers)
        {
            server.UpdateCredentials(credentials);
        }
    }

    /// <summary>Propaga a opção de hover aos servidores ativos.</summary>
    private void ApplyHoverSetting()
    {
        foreach (RemoteServer server in servers)
        {
            server.AllowHover = allowHover.IsChecked == true;
        }
        if (settingsLoaded) PersistSettings();
    }

    private bool PersistSettings()
    {
        if (!int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
        {
            status.Text = "Informe uma porta válida (1 a 65535) para salvar as preferências.";
            return false;
        }
        try
        {
            new HostSettings(number, allowHover.IsChecked == true).Save();
            settingsLoaded = true;
            return true;
        }
        catch (Exception)
        {
            status.Text = "Não foi possível salvar as preferências. Confira as permissões desta conta Windows.";
            return false;
        }
    }

    private void SetStartupChecked(bool enabled)
    {
        updatingStartup = true;
        startWithWindows.IsChecked = enabled;
        updatingStartup = false;
    }

    private void ChangeStartup(object sender, RoutedEventArgs e)
    {
        if (updatingStartup) return;
        bool enabled = startWithWindows.IsChecked == true;
        if (enabled && state?.HasPin != true)
        {
            SetStartupChecked(false);
            status.Text = "Defina e salve um PIN antes de habilitar a inicialização automática.";
            return;
        }
        if (enabled && !PersistSettings())
        {
            SetStartupChecked(false);
            return;
        }
        try
        {
            HostStartup.SetEnabled(enabled);
            status.Text = enabled
                ? "Inicialização automática habilitada para o próximo login desta conta Windows."
                : "Inicialização automática desabilitada.";
        }
        catch (Exception)
        {
            SetStartupChecked(!enabled);
            status.Text = "Não foi possível alterar a inicialização com o Windows. Confira as permissões e o local do executável.";
        }
    }

    private async Task StopAllAsync(string message = "Compartilhamento encerrado.")
    {
        if (stopping) return;
        stopping = true;
        UpdatePinUi();
        try
        {
            RemoteServer[] previous = [.. servers];
            servers.Clear();
            foreach (RemoteServer server in previous)
            {
                server.CredentialsChanged -= OnCredentialsChanged;
            }

            await Task.WhenAll(previous.Select(server => server.StopAsync()));
            shared.Items.Clear();
        }
        finally
        {
            stopping = false;
            UpdatePinUi();
        }

        status.Text = message;
    }

    private async void RevokePairing(object sender, RoutedEventArgs e)
    {
        await StopAllAsync("Pareamento revogado.");
        try
        {
            // Espera as gravações pendentes e bloqueia novas: uma gravação de
            // pareamento em voo não pode recriar o arquivo revogado.
            revoked = true;
            await persistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                PairingStore.Revoke();
            }
            finally { persistGate.Release(); }

            SetState(PairingStore.LoadOrCreate());
            changingPin = false;
            pin.Clear();
            status.Text = "Pareamento revogado. Defina um novo PIN para compartilhar.";
        }
        catch (Exception)
        {
            status.Text = "Pareamento revogado, mas não foi possível preparar o novo estado.";
        }
        finally
        {
            revoked = false;
        }

        UpdatePinUi();
    }

    /// <summary>Primeiro IPv4 não-loopback de uma interface ativa.</summary>
    private static string DetectLocalAddress()
    {
        try
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (UnicastIPAddressInformation unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;
                    return unicast.Address.ToString();
                }
            }
        }
        catch (NetworkInformationException) { }

        return "127.0.0.1";
    }
}
