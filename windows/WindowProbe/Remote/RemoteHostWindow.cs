using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly Button copyAddress = new() { Content = "Copiar endereço", Margin = new Thickness(8, 0, 0, 0) };
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
    private readonly ListBox shared = new() { Height = 52 };
    private readonly Button stop = new() { Content = "Encerrar compartilhamentos", Margin = new Thickness(0, 8, 8, 0) };
    private readonly Button revoke = new() { Content = "Revogar pareamento", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Expander options = new() { Header = "Opções e compatibilidade", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox allowHover = new() { Content = "Permitir que o Mac mova o cursor deste PC (hover)", IsChecked = true };
    private readonly CheckBox startWithWindows = new() { Content = "Iniciar e compartilhar automaticamente ao entrar no Windows" };
    private readonly TextBlock status = new() { Text = "Parado", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<RemoteServer> servers = [];
    private readonly List<HostDiscovery> discoveries = [];
    /// <summary>
    /// Mantém a tela acordada enquanto o host está aberto. Sem isso o monitor
    /// entra em stand-by (o timeout é curto) e o WGC para de entregar quadros,
    /// deixando o Mac com a última imagem. Solto ao fechar a janela.
    /// </summary>
    private readonly DisplayWakeLock? hostWake;
    private readonly Mutex hostMutex;

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
        hostMutex = new Mutex(false, @"Global\RemoteDesktop-PairedHost-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        Title = "Paneo — host Windows";
        Width = 760;
        Height = 720;
        MinWidth = 580;
        MinHeight = 640;
        Background = Brush("#EEF3F9");

        try { ownsMutex = hostMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (ownsMutex) hostWake = new DisplayWakeLock();

        if (!ownsMutex)
        {
            Content = Card(
                new TextBlock { Text = "Paneo", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brush("#173B66") },
                new TextBlock
                {
                    Text = "Já existe um host aberto nesta conta Windows. Use-o para encerrar o compartilhamento ou revogar o pareamento.",
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            Closed += (_, _) =>
            {
                hostWake?.Dispose();
                hostMutex.Dispose();
            };
            return;
        }

        Closed += (_, _) =>
        {
            try { hostMutex.ReleaseMutex(); } catch (ApplicationException) { }
            hostMutex.Dispose();
        };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock
        {
            Text = "Paneo",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#173B66")
        });
        header.Children.Add(new TextBlock
        {
            Text = "Compartilhe as janelas deste PC com seu Mac.",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 14,
            Foreground = Brush("#42566F"),
            TextWrapping = TextWrapping.Wrap
        });

        pinAction.Click += SavePin;
        start.Click += StartSharing;
        stop.Click += async (_, _) => await StopAllAsync("Compartilhamento encerrado; pareamento preservado.");
        revoke.Click += RevokePairing;
        copyAddress.Click += CopyDetectedAddress;
        AutomationProperties.SetName(pin, "PIN do pareamento com o Mac");
        AutomationProperties.SetName(pinAction, "Salvar ou trocar PIN do pareamento");
        AutomationProperties.SetName(port, "Porta de conexão do Paneo");
        AutomationProperties.SetName(copyAddress, "Copiar endereço IP local");
        AutomationProperties.SetName(start, "Iniciar compartilhamento do PC com o Mac");
        AutomationProperties.SetName(stop, "Encerrar todos os compartilhamentos ativos");
        AutomationProperties.SetName(revoke, "Revogar o pareamento e exigir novo PIN");
        var refresh = new Button { Content = "Atualizar janelas", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (_, _) => RefreshWindows();

        var addressRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(copyAddress, Dock.Right);
        addressRow.Children.Add(copyAddress);
        addressRow.Children.Add(detectedAddress);
        detectedAddress.FontFamily = new FontFamily("Consolas");
        detectedAddress.FontSize = 16;
        AutomationProperties.SetName(detectedAddress, "Endereço local do PC para conectar o Paneo no Mac");

        var pinCard = Card(
            StepHeading("1", "Defina o PIN"),
            new TextBlock { Text = "O Mac pedirá este PIN para autorizar o pareamento.", Margin = new Thickness(0, 4, 0, 8), Foreground = Brush("#42566F"), TextWrapping = TextWrapping.Wrap },
            pinState,
            pin,
            pinAction);
        var sharingCard = Card(
            StepHeading("2", "Conecte o Mac"),
            new TextBlock { Text = "No Mac, informe este endereço e o PIN acima. O tráfego remoto pode compartilhar mouse e teclado deste PC.", Margin = new Thickness(0, 4, 0, 8), Foreground = Brush("#42566F"), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Endereço deste PC (IP):", Margin = new Thickness(0, 4, 0, 0), Foreground = Brush("#42566F") },
            addressRow,
            start);
        var sessionsCard = Card(
            StepHeading("3", "Sessões e acesso"),
            new TextBlock { Text = "Compartilhamentos ativos neste PC:", Margin = new Thickness(0, 10, 0, 4) },
            shared,
            new WrapPanel { Children = { stop, revoke } });

        var optionsPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        optionsPanel.Children.Add(new TextBlock { Text = "Porta de conexão:", Margin = new Thickness(0, 0, 0, 4) });
        optionsPanel.Children.Add(port);
        allowHover.Margin = new Thickness(0, 10, 0, 0);
        optionsPanel.Children.Add(allowHover);
        startWithWindows.Margin = new Thickness(0, 8, 0, 0);
        optionsPanel.Children.Add(startWithWindows);
        optionsPanel.Children.Add(new TextBlock
        {
            Text = "Usa o PIN salvo nesta conta e inicia após o login. Fechar esta janela encerra o host.",
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = Brush("#42566F"),
            TextWrapping = TextWrapping.Wrap
        });
        optionsPanel.Children.Add(new TextBlock { Text = "Janelas detectadas agora:", Margin = new Thickness(0, 12, 0, 2) });
        optionsPanel.Children.Add(windows);
        optionsPanel.Children.Add(refresh);
        options.Content = optionsPanel;
        var optionsCard = Card(options);
        options.Expanded += (_, _) => AnimateReveal(optionsPanel);

        var statusCard = Card(status);
        statusCard.Margin = new Thickness(0, 2, 0, 0);
        status.Text = "Parado";
        status.FontWeight = FontWeights.Medium;
        status.Foreground = Brush("#173B66");
        status.Margin = new Thickness(0);
        statusCard.Background = Brush("#E5EFFA");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(status, "Estado do host Paneo");

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };
        panel.Children.Add(header);
        panel.Children.Add(pinCard);
        panel.Children.Add(sharingCard);
        panel.Children.Add(sessionsCard);
        panel.Children.Add(optionsCard);
        var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(statusCard, Dock.Bottom);
        root.Children.Add(statusCard);
        root.Children.Add(scroller);
        Content = root;
        if (SystemParameters.ClientAreaAnimation)
        {
            panel.Opacity = 0;
            panel.Loaded += (_, _) => panel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        stop.IsEnabled = false;
        shared.MinHeight = 40;
        shared.MaxHeight = 100;
        shared.Margin = new Thickness(0, 0, 0, 2);
        windows.MinWidth = 0;
        windows.Height = 90;
        pin.MaxWidth = 300;
        pin.FontSize = 15;
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Continue);
        foreach (Button button in new[] { pinAction, start, stop, revoke, copyAddress, refresh })
            StyleButton(button, button == pinAction || button == start);
        allowHover.Checked += (_, _) => ApplyHoverSetting();
        allowHover.Unchecked += (_, _) => ApplyHoverSetting();
        ConfigureSafeClose();

        detectedAddress.Text = DetectLocalAddress();
        RefreshWindows();

        try
        {
            SetState(PairingStore.LoadOrCreate());
            SetStatus(state?.HasPin == true
                ? "Pronto para compartilhar."
                : "Defina um PIN para habilitar o compartilhamento.");
        }
        catch (Exception)
        {
            SetStatus("Não foi possível ler o pareamento salvo. Use Revogar pareamento para recomeçar.");
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
            SetStatus("Não foi possível ler as preferências salvas. Confira a porta e o hover e inicie manualmente para salvá-las novamente.");
        }

        try { SetStartupChecked(HostStartup.IsEnabled()); }
        catch (Exception) { SetStatus("Não foi possível consultar a inicialização com o Windows."); }
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

    }

    private void ConfigureSafeClose() => Closing += async (_, e) =>
    {
        if (closing) return;
        e.Cancel = true;
        closing = true;
        await StopAllAsync();
        hostWake?.Dispose();
        Close();
    };

    private static Border Card(params UIElement[] children)
    {
        var content = new StackPanel();
        foreach (UIElement child in children)
        {
            if (child is Control control && control.Margin == default)
                control.Margin = new Thickness(0, 3, 0, 3);
            content.Children.Add(child);
        }
        return new Border
        {
            Background = Brush("#FAFCFF"),
            BorderBrush = Brush("#D4E0EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };
    }

    private static StackPanel StepHeading(string number, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Background = Brush("#DCEBFC"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 3, 9, 3),
            Child = new TextBlock { Text = number, Foreground = Brush("#165FA8"), FontWeight = FontWeights.Bold }
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(10, 2, 0, 0),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#173B66")
        });
        return row;
    }

    private static SolidColorBrush Brush(string value)
    {
        if (SystemParameters.HighContrast)
        {
            return value switch
            {
                "#2678C5" or "#165FA8" => SystemColors.HighlightBrush,
                "#FFFFFF" => SystemColors.HighlightTextBrush,
                "#EEF3F9" or "#FAFCFF" or "#E5EFFA" or "#DCEBFC" => SystemColors.WindowBrush,
                "#E6EEF8" => SystemColors.ControlBrush,
                "#D4E0EE" => SystemColors.WindowTextBrush,
                _ => SystemColors.WindowTextBrush
            };
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }

    private static void StyleButton(Button button, bool primary)
    {
        string label = button.Content?.ToString() ?? string.Empty;
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(UIElement.OpacityProperty, 0.58) }
        });
        button.Template = template;
        button.Content = new Border
        {
            Name = "ButtonSurface",
            Background = Brush(primary ? "#2678C5" : "#E6EEF8"),
            BorderBrush = Brush("#D4E0EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 7, 14, 7),
            Child = new TextBlock
            {
                Text = label,
                TextAlignment = TextAlignment.Center,
                Foreground = Brush(primary ? "#FFFFFF" : "#173B66"),
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0);
        button.MouseEnter += (_, _) => AnimateButton(button, primary);
        button.MouseLeave += (_, _) => AnimateButton(button, primary);
        button.PreviewMouseLeftButtonDown += (_, _) => AnimateButton(button, primary);
        button.PreviewMouseLeftButtonUp += (_, _) => AnimateButton(button, primary);
        button.GotKeyboardFocus += (_, _) => SetButtonFocus(button, true);
        button.LostKeyboardFocus += (_, _) => SetButtonFocus(button, false);
    }

    private static void SetButtonLabel(Button button, string text)
    {
        if (button.Content is Border { Child: TextBlock label }) label.Text = text;
        else button.Content = text;
    }

    private static void AnimateButton(Button button, bool primary)
    {
        if (button.Content is not Border surface) return;
        bool pressed = button.IsPressed;
        surface.Background = Brush(primary
            ? pressed ? "#165FA8" : "#2678C5"
            : pressed ? "#D4E0EE" : button.IsMouseOver ? "#DCEBFC" : "#E6EEF8");
        if (button.IsMouseOver || pressed || button.IsKeyboardFocused)
            surface.BorderThickness = new Thickness(2);
        else
            surface.BorderThickness = new Thickness(1);
        double opacity = pressed ? 0.82 : button.IsMouseOver ? 0.94 : 1;
        if (!SystemParameters.ClientAreaAnimation)
        {
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            surface.Opacity = 1;
            return;
        }
        surface.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(surface.Opacity, opacity, TimeSpan.FromMilliseconds(140)), HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetButtonFocus(Button button, bool focused)
    {
        if (button.Content is not Border surface) return;
        surface.BorderBrush = focused ? Brush("#2678C5") : Brush("#D4E0EE");
        surface.BorderThickness = new Thickness(focused || button.IsMouseOver || button.IsPressed ? 2 : 1);
    }

    private static void AnimateReveal(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)), HandoffBehavior.SnapshotAndReplace);
    }

    private void SetStatus(string message)
    {
        status.Text = message;
        if (!SystemParameters.ClientAreaAnimation)
        {
            status.BeginAnimation(UIElement.OpacityProperty, null);
            status.Opacity = 1;
            return;
        }
        status.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.62, 1, TimeSpan.FromMilliseconds(140)), HandoffBehavior.SnapshotAndReplace);
    }

    private void CopyDetectedAddress(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(detectedAddress.Text);
            SetStatus("Endereço copiado. Informe-o no Paneo do Mac.");
        }
        catch (Exception)
        {
            SetStatus("Não foi possível copiar o endereço. Digite o IP exibido no Paneo do Mac.");
        }
    }

    private void SetState(HostState next) => state = next;

    private void RefreshWindows() => windows.ItemsSource = WindowEnumerator.GetVisibleWindows();

    private void UpdatePinUi()
    {
        bool hasPin = state?.HasPin == true;
        bool editing = !hasPin || changingPin;
        pin.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SetButtonLabel(pinAction, editing ? "Salvar PIN" : "Trocar PIN");
        pinState.Text = editing
            ? $"Escolha um PIN com pelo menos {HostCredentials.PinMinLength} caracteres. Ele será pedido ao Mac."
            : "PIN definido neste PC.";
        pinAction.IsEnabled = !stopping;
        pinAction.IsDefault = editing;
        start.IsEnabled = hasPin && !stopping;
        start.IsDefault = hasPin && !editing;
        RefreshWindowsEnabled();
    }

    private void RefreshWindowsEnabled()
    {
        windows.IsEnabled = !stopping;
        port.IsEnabled = !stopping;
    }

    private void RefreshSessionActions() => stop.IsEnabled = servers.Count > 0 && !stopping;

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
            SetStatus($"O PIN precisa ter entre {HostCredentials.PinMinLength} e {HostCredentials.PinMaxLength} caracteres.");
            return;
        }

        bool saved = false;
        try
        {
            HostCredentials credentials = state?.Credentials?.WithPin(value) ?? HostCredentials.Create(value);
            SetState(PairingStore.Save(credentials));
            // O PIN novo precisa valer também nos servidores já abertos.
            ApplyCredentialsToServers(credentials);
            changingPin = false;
            pin.Clear();
            SetStatus("PIN definido. Ele será pedido na primeira conexão do Mac.");
            saved = true;
        }
        catch (Exception)
        {
            SetStatus("Não foi possível salvar o PIN.");
        }

        UpdatePinUi();
        if (saved) start.Focus();
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
            SetStatus("Defina um PIN antes de iniciar o compartilhamento.");
            return false;
        }

        if (!int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
        {
            SetStatus("Informe uma porta válida (1 a 65535).");
            return false;
        }

        if (!PersistSettings()) return false;
        if (servers.Any(existing => existing.Port == number))
        {
            SetStatus("O compartilhamento já está ativo nesta porta.");
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
                        window.IconBase64, window.ProcessId, window.Minimized, window.CanResize))
                    .ToArray());

            server.Status = value => Dispatcher.BeginInvoke(() =>
            {
                if (!closing) SetStatus(value);
            });
            server.CredentialsChanged += OnCredentialsChanged;
            server.AllowHover = allowHover.IsChecked == true;
            server.Start();
            servers.Add(server);
            try { discoveries.Add(new HostDiscovery(server.Port)); }
            catch (Exception) { /* A falha de Bonjour não pode impedir o compartilhamento manual por IP. */ }
            shared.Items.Add($"Porta {server.Port}: este PC (janelas escolhidas pelo Mac)");
            RefreshSessionActions();
            SetStatus("Compartilhamento ativo. No Mac, informe o IP e o PIN e escolha a janela.");
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

            SetStatus("Não foi possível iniciar. Confira a porta e tente novamente.");
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
            SetStatus("Informe uma porta válida (1 a 65535) para salvar as preferências.");
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
            SetStatus("Não foi possível salvar as preferências. Confira as permissões desta conta Windows.");
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
            SetStatus("Defina e salve um PIN antes de habilitar a inicialização automática.");
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
            SetStatus(enabled
                ? "Inicialização automática habilitada para o próximo login desta conta Windows."
                : "Inicialização automática desabilitada.");
        }
        catch (Exception)
        {
            SetStartupChecked(!enabled);
            SetStatus("Não foi possível alterar a inicialização com o Windows. Confira as permissões e o local do executável.");
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
            HostDiscovery[] previousDiscoveries = [.. discoveries];
            discoveries.Clear();
            foreach (RemoteServer server in previous)
            {
                server.CredentialsChanged -= OnCredentialsChanged;
            }

            await Task.WhenAll(previous.Select(server => server.StopAsync()));
            try
            {
                await Task.WhenAll(previousDiscoveries.Select(discovery => discovery.StopAsync()))
                    .WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                message += " A remoção do anúncio da rede ainda está pendente.";
            }
            shared.Items.Clear();
        }
        finally
        {
            stopping = false;
            UpdatePinUi();
            RefreshSessionActions();
        }

        SetStatus(message);
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
            SetStatus("Pareamento revogado. Defina um novo PIN para compartilhar.");
        }
        catch (Exception)
        {
            SetStatus("Pareamento revogado, mas não foi possível preparar o novo estado.");
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
