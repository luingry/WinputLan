using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinputLan.Core;
using WinputLan.Runtime;
using Forms = System.Windows.Forms;

namespace WinputLan
{
    public partial class MainWindow : Window
    {
        private readonly WinputConfig _config;
        private readonly AppConfigStore _configStore;
        private readonly InMemoryTransactionLog _transactionLog = new InMemoryTransactionLog();
        private readonly InputEventQueue _inputQueue = new InputEventQueue();
        private readonly SendInputSink _inputSink = new SendInputSink();
        private readonly PeerTransport _transport = new PeerTransport();
        private PeerTransport _listenerTransport;
        private PairingCoordinator _pairingCoordinator;
        private PairingCoordinator _listenerPairingCoordinator;
        private CancellationTokenSource _listenerCts;
        private Task _listenerTask;
        private readonly SemaphoreSlim _listenerRestartGate = new SemaphoreSlim(1, 1);
        private PinStore _pinStore;
        private InputRouter _inputRouter;
        private PairedInputReceiver _listenerInputReceiver;
        private CertificateManager _certificateManager;
        private X509Certificate2 _certificate;
        private GlobalHotkeyService _hotkeys;
        private LowLevelInputCapture _capture;
        private HotkeyBypassDetector _hotkeyBypass;
        private bool _remoteActive;
        private bool _updateBusy;
        private DispatcherTimer _updateTimer;
        private DispatcherTimer _logTimer;
        private DispatcherTimer _uipiTimer;
        private volatile bool _logDirty = true;
        private bool _isElevated;
        private bool _suppressElevationToggle;
        private DateTime _lastBlockedHintUtc = DateTime.MinValue;
        private readonly DpapiSecretProtector _secretProtector = new DpapiSecretProtector();
        private bool _inboundFocused;
        private string _inboundControllerName;
        private string _outboundNote;
        private bool _suppressStartupToggle;
        private Forms.NotifyIcon _trayIcon;
        private readonly BackgroundLifecycle _backgroundLifecycle;
        private readonly InputLatencyWindow _latencyWindow = new InputLatencyWindow();
        private readonly InputAuditPolicy _inputAuditPolicy = new InputAuditPolicy();
        private DispatcherTimer _accessCodeTimer;
        private CancellationTokenSource _outboundRequestCts;

        public MainWindow(WinputConfig config, AppConfigStore configStore)
        {
            InitializeComponent();
            try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(BrandIconUri); } catch { }
            _config = config ?? WinputConfig.CreateDefault();
            SizeChanged += (sender, args) => ConfigureMachineRows();
            _backgroundLifecycle = new BackgroundLifecycle(_config.ContinueInBackground);
            _configStore = configStore;
            LocalNameText.Text = _config.DisplayName;
            LocalAddressText.Text = Environment.MachineName + "  |  TCP " + _config.ListenPort;
            LocalIpText.Text = "IP: " + LocalIPv4Address();
            BackgroundModeCheckBox.IsChecked = _config.ContinueInBackground;
            UpdateFrequencyButton.Content = FrequencyLabel(_config.UpdateCheckFrequency);
            _isElevated = ProcessElevation.IsCurrentElevated();
            _suppressElevationToggle = true; RunElevatedCheckBox.IsChecked = _config.RunElevated && _isElevated; _suppressElevationToggle = false;
            _suppressStartupToggle = true; StartWithWindowsCheckBox.IsChecked = _config.StartWithWindows; _suppressStartupToggle = false;
            VersionText.Text = "v" + InstalledVersion;
            RemoteAddressBox.Text = string.IsNullOrWhiteSpace(_config.RemoteAddress) ? "127.0.0.1" : _config.RemoteAddress;
            LocalHotkeyText.Text = ShortcutTail(_config.LocalHotkey);
            RemoteHotkeyText.Text = ShortcutTail(_config.RemoteHotkey);
            RefreshKnownPeer();
            ConfigureMachineRows();
            _transactionLog.Add("local", "local", "Session", _isElevated ? "ready-elevated" : "ready");
            _transport.StateChanged += Transport_StateChanged;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hotkeys = new GlobalHotkeyService(HwndSource.FromHwnd(new WindowInteropHelper(this).Handle));
                _hotkeys.Invoked += action => Dispatcher.Invoke(() => SetInputTarget(action == HotkeyAction.SelectRemote));
                _hotkeys.Register(HotkeyAction.SelectLocal, HotkeyGesture.Parse(_config.LocalHotkey));
                _hotkeys.Register(HotkeyAction.SelectRemote, HotkeyGesture.Parse(_config.RemoteHotkey));
                _hotkeyBypass = new HotkeyBypassDetector(HotkeyGesture.Parse(_config.LocalHotkey), HotkeyGesture.Parse(_config.RemoteHotkey));
                _certificateManager = new CertificateManager(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan"), new DpapiSecretProtector());
                _certificate = _certificateManager.LoadOrCreate();
                _pinStore = new PinStore(new DpapiSecretProtector());
                if (_config.PinnedSecret != null) _pinStore.LoadProtectedBlob(_config.PinnedSecret);
                PinRecord existingPin;
                if (!string.IsNullOrWhiteSpace(_config.PinnedDeviceId) && (!_pinStore.TryLoad(out existingPin) || !string.Equals(existingPin.DeviceId, _config.PinnedDeviceId, StringComparison.Ordinal) || !string.Equals(existingPin.CertificateFingerprint, _config.PinnedFingerprint, StringComparison.OrdinalIgnoreCase)))
                {
                    _config.PinnedDeviceId = null;
                    _config.PinnedFingerprint = null;
                    _config.PinnedSecret = null;
                    _configStore.Save(_config);
                    AddLog("local", "local", "Config", "pin-reset-safe");
                }
                _pairingCoordinator = CreatePairingCoordinator(_transport, PairingRole.Controller);
                _listenerTransport = new PeerTransport();
                _listenerPairingCoordinator = CreatePairingCoordinator(_listenerTransport, PairingRole.Target);
                UpdateAccessCode(_listenerPairingCoordinator.AccessCode);
                _listenerCts = new CancellationTokenSource();
                _listenerTask = StartListenerAsync(_listenerCts.Token);
                _inputRouter = new InputRouter(_inputQueue, _transport, _inputSink);
                _listenerInputReceiver = new PairedInputReceiver(_listenerTransport, _inputSink);
                _inputRouter.InputAudited += AuditInput;
                _listenerInputReceiver.InputAudited += AuditInput;
                _listenerInputReceiver.FocusChanged += focused => Dispatcher.BeginInvoke(new Action(() => { _inboundFocused = focused; RenderMachines(); }));
                _listenerTransport.StateChanged += ListenerTransport_StateChanged;
                _listenerPairingCoordinator.TrustLookup = LookupTrustedController;
                _pairingCoordinator.TrustRejected += () => Dispatcher.BeginInvoke(new Action(OnTrustRejected));
                _inputRouter.InputLatencyMeasured += latency =>
                {
                    _latencyWindow.Record(latency);
                    double p50;
                    if (_latencyWindow.TryGetP50(DateTime.UtcNow, TimeSpan.FromMilliseconds(250), out p50)) Dispatcher.BeginInvoke(new Action(() => { if (_remoteActive) LatencyText.Text = "Latência p50: " + Math.Round(p50) + " ms"; }));
                };
                _accessCodeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _accessCodeTimer.Tick += (s, e) => _listenerPairingCoordinator?.RefreshExpiredAccessCode();
                _accessCodeTimer.Start();
                CreateTrayIcon();
                StartAutomaticUpdateChecks();
                StartBlockedInputWatcher();
                // Keeps the startup entry pointing at this executable and mode after updates or elevation changes.
                if (_config.StartWithWindows) ApplyStartupRegistration(false);
                RenderMachines();
                AddLog("local", "local", "Hooks", "armed-controller-only");
            }
            catch (Exception ex)
            {
                AddLog("local", "local", "Hooks", "unavailable");
                MessageBox.Show("Global input hooks could not start. Winput LAN remains in local-safe mode.\n\n" + ex.Message, "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_backgroundLifecycle.ShouldHideOnClose) { e.Cancel = true; HideToTray(); return; }
            Cleanup();
        }

        private void Cleanup()
        {
            if (!_backgroundLifecycle.TryBeginCleanup()) return;
            _capture?.Dispose();
            _inputRouter?.Dispose();
            _listenerInputReceiver?.Dispose();
            _inputSink.ReleaseAll();
            _pairingCoordinator?.Dispose();
            _listenerPairingCoordinator?.Dispose();
            _listenerCts?.Cancel();
            _outboundRequestCts?.Cancel();
            _accessCodeTimer?.Stop();
            _updateTimer?.Stop();
            _logTimer?.Stop();
            _uipiTimer?.Stop();
            _listenerTransport?.Dispose();
            _transport.Dispose();
            _hotkeys?.Dispose();
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
            try { _configStore?.Save(_config); } catch { }
        }

        private void PairNowButton_Click(object sender, RoutedEventArgs e) { BeginPairing(); }
        private void ClosePairingButton_Click(object sender, RoutedEventArgs e)
        {
            if (TargetApprovalPanel.Visibility == Visibility.Visible) _listenerPairingCoordinator?.DenyPending();
            else CancelOutboundRequest();
            PairingOverlay.Visibility = Visibility.Collapsed; PairNowButton.Focus();
        }
        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else DragMove();
        }
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void CloseButton_Click(object sender, RoutedEventArgs e) { Close(); }
        private void RemoteMachineRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // While another PC controls this one, the second row shows that controller and is informational.
            if (_listenerTransport != null && _listenerTransport.State == PeerConnectionState.Connected && _transport.State != PeerConnectionState.Connected) return;
            if (_transport.State == PeerConnectionState.Connected) { SetInputTarget(true); return; }
            if (_transport.State == PeerConnectionState.Connecting || _transport.State == PeerConnectionState.Pairing) { CancelOutboundRequest("Pedido cancelado."); return; }
            if (HasRecognizedTarget) { _ = ConnectRecognizedAsync(); return; }
            BeginPairing();
            var address = _config.TrustedTarget != null ? _config.TrustedTarget.Address : _config.RemoteAddress;
            if (!string.IsNullOrWhiteSpace(address)) { RemoteAddressBox.Text = address; RemoteCodeBox.FocusFirstEmpty(); }
        }
        private void RemoteCodeBox_Submitted(object sender, EventArgs e) { if (ConnectPairButton.IsEnabled) ConnectPairButton_Click(sender, new RoutedEventArgs()); }
        private async void ConnectPairButton_Click(object sender, RoutedEventArgs e)
        {
            var host = (RemoteAddressBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host)) { MessageBox.Show("Enter a peer address.", "Pairing", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var recognized = _config.TrustedTarget;
            if (RemoteCodeBox.Code.Length == 0 && HasRecognizedTarget && string.Equals(recognized.Address, host, StringComparison.OrdinalIgnoreCase)) { PairingOverlay.Visibility = Visibility.Collapsed; _ = ConnectRecognizedAsync(); return; }
            try { _pairingCoordinator.StartRequest(RemoteCodeBox.Code); }
            catch (Exception ex) { PairCodeStateText.Text = ex.Message; RemoteCodeBox.FocusFirstEmpty(); return; }
            _config.RemoteAddress = host;
            _outboundRequestCts?.Cancel(); var requestCts = new CancellationTokenSource(); _outboundRequestCts = requestCts;
            try
            {
                _configStore.Save(_config);
                AddLog("local", "remote", "Transport", "connecting");
                var port = _config.RemotePort <= 0 ? _config.ListenPort : _config.RemotePort;
                ConnectPairButton.IsEnabled = false; PairCodeStateText.Text = "Enviando pedido…";
                await _transport.ConnectAsync(host, port, _certificate, null, true, requestCts.Token);
                if (!OwnsOutboundRequest(requestCts)) return;
                if (requestCts.IsCancellationRequested) _transport.Disconnect("access request cancelled");
            }
            catch (OperationCanceledException)
            {
                if (!OwnsOutboundRequest(requestCts)) return;
                _transport.Disconnect("access request cancelled");
                PairCodeStateText.Text = "Pedido cancelado. Você pode tentar novamente.";
            }
            catch (Exception ex)
            {
                if (!OwnsOutboundRequest(requestCts)) return;
                if (requestCts.IsCancellationRequested) { _transport.Disconnect("access request cancelled"); PairCodeStateText.Text = "Pedido cancelado. Você pode tentar novamente."; return; }
                AddLog("local", "remote", "Transport", "failed");
                MessageBox.Show("Could not connect to the peer. Check the address, Private firewall rule and pairing state.\n\n" + ex.Message, "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (OwnsOutboundRequest(requestCts)) { ConnectPairButton.IsEnabled = true; _outboundRequestCts = null; }
                requestCts.Dispose();
            }
        }
        private void MachinesButton_Click(object sender, RoutedEventArgs e) { DashboardScroll.ScrollToTop(); SetInputTarget(false); }
        private void ShortcutsButton_Click(object sender, RoutedEventArgs e) { DashboardScroll.ScrollToVerticalOffset(360); ShowShortcutsEditor(); }
        private void LogNavButton_Click(object sender, RoutedEventArgs e) { SetLogPanelVisible(true); LogSection.BringIntoView(); }

        private void LogToggleButton_Click(object sender, RoutedEventArgs e) { SetLogPanelVisible(LogPanel.Visibility != Visibility.Visible); }

        // The list is only materialised while open, and then refreshed at most once per second,
        // so input bursts never rebuild UI rows on the dispatcher.
        private void SetLogPanelVisible(bool visible)
        {
            LogPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ClearLogButton.Visibility = LogPanel.Visibility;
            LogToggleButton.Content = visible ? "Ocultar logs de input" : "Ver logs de input";
            if (!visible) { _logTimer?.Stop(); LogList.ItemsSource = null; return; }
            if (_logTimer == null)
            {
                _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                _logTimer.Tick += (s, e) => { if (_logDirty) RefreshLog(); };
            }
            _logDirty = true; RefreshLog(); _logTimer.Start();
        }

        private void RunElevatedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressElevationToggle) return;
            var enable = RunElevatedCheckBox.IsChecked == true;
            _config.RunElevated = enable;
            try { _configStore.Save(_config); } catch { }
            if (!enable)
            {
                if (_isElevated) MessageBox.Show("O Winput LAN volta a abrir sem privilégios de administrador na próxima vez que for iniciado.", "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Information);
                if (_config.StartWithWindows) ApplyStartupRegistration(false);
                return;
            }
            if (_isElevated) return;
            if (ProcessElevation.TryRelaunchElevated()) { ExitFromTray(); return; }
            // UAC declined: keep the setting off so the next start does not prompt unexpectedly.
            _config.RunElevated = false; try { _configStore.Save(_config); } catch { }
            _suppressElevationToggle = true; RunElevatedCheckBox.IsChecked = false; _suppressElevationToggle = false;
        }

        // UIPI silently discards input injected into elevated windows; tell the person at this PC why control paused.
        private void StartBlockedInputWatcher()
        {
            if (_isElevated) return;
            _uipiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _uipiTimer.Tick += (s, e) =>
            {
                if (_listenerTransport == null || _listenerTransport.State != PeerConnectionState.Connected) return;
                if (ProcessElevation.IsForegroundElevated()) ShowBlockedInputHint("Uma janela de administrador (ex.: Gerenciador de Tarefas) está em foco e o Windows bloqueia o controle remoto nela. Clique fora dela ou ative “Permitir controlar apps de administrador” no Winput LAN deste PC.");
            };
            _uipiTimer.Start();
        }

        private void ShowBlockedInputHint(string message)
        {
            if (DateTime.UtcNow - _lastBlockedHintUtc < TimeSpan.FromSeconds(30)) return;
            _lastBlockedHintUtc = DateTime.UtcNow;
            AddLog("remote", "local", "Input", "blocked-by-windows");
            CreateTrayIcon();
            var wasVisible = _trayIcon.Visible;
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(6000, "Winput LAN: entrada bloqueada", message, Forms.ToolTipIcon.Warning);
            if (!wasVisible)
            {
                var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                hide.Tick += (s, e) => { hide.Stop(); if (IsVisible && _trayIcon != null) _trayIcon.Visible = false; };
                hide.Start();
            }
        }
        private async void UpdatesButton_Click(object sender, RoutedEventArgs e) { await CheckUpdatesAsync(true); }

        private void UpdateFrequencyButton_Click(object sender, RoutedEventArgs e)
        {
            _config.UpdateCheckFrequency = UpdateSchedule.Next(_config.UpdateCheckFrequency);
            UpdateFrequencyButton.Content = FrequencyLabel(_config.UpdateCheckFrequency);
            try { _configStore.Save(_config); } catch { }
        }

        private static string FrequencyLabel(UpdateCheckFrequency frequency)
        {
            return "Atualizações automáticas: " + (frequency == UpdateCheckFrequency.Daily ? "diárias" : frequency == UpdateCheckFrequency.Weekly ? "semanais" : "desligadas");
        }

        // Checks shortly after start and then periodically, so a copy living in the tray for days still updates.
        private void StartAutomaticUpdateChecks()
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(1);
                if (UpdateSchedule.IsDue(_config.UpdateCheckFrequency, _config.LastUpdateCheckUtcTicks, DateTime.UtcNow.Ticks)) await CheckUpdatesAsync(false);
            };
            _updateTimer.Start();
        }
        private void ClearLogButton_Click(object sender, RoutedEventArgs e) { _transactionLog.Clear(); _logDirty = true; RefreshLog(); }
        private void AcceptPairButton_Click(object sender, RoutedEventArgs e) { try { _listenerPairingCoordinator.AcceptPending(); PairingOverlay.Visibility = Visibility.Collapsed; } catch (Exception ex) { MessageBox.Show(ex.Message, "Acesso", MessageBoxButton.OK, MessageBoxImage.Warning); } }
        private void DenyPairButton_Click(object sender, RoutedEventArgs e) { _listenerPairingCoordinator?.DenyPending(); PairingOverlay.Visibility = Visibility.Collapsed; }
        private void RenewAccessCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var count = _config.TrustedControllers == null ? 0 : _config.TrustedControllers.Count;
            if (count > 0 && MessageBox.Show("Renovar o código também faz este PC esquecer " + (count == 1 ? "a máquina reconhecida" : "as " + count + " máquinas reconhecidas") + ". Elas vão precisar do novo código para controlar este PC.\n\nContinuar?", "Renovar código", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { _listenerPairingCoordinator?.RenewAccessCode(); }
            catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "Renovar código", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            _config.TrustedControllers = null;
            try { _configStore.Save(_config); } catch { }
            AddLog("local", "local", "Access", count > 0 ? "code-renewed-trust-cleared" : "code-renewed");
        }

        private void BeginPairing()
        {
            PairingOverlay.Visibility = Visibility.Visible;
            ControllerPairPanel.Visibility = Visibility.Visible; TargetApprovalPanel.Visibility = Visibility.Collapsed;
            PairingTitleText.Text = "Controlar outra máquina"; PairingDescriptionText.Text = "Informe o IP e o código exibidos no PC que você quer controlar.";
            PairCodeStateText.Text = HasRecognizedTarget ? "Para " + TargetDisplayName + " (reconhecida) deixe o código em branco: basta o aceite." : "O PC controlado precisa aceitar o pedido."; RemoteCodeBox.Clear(); RemoteAddressBox.Focus(); AddLog("local", "remote", "Access", "ready");
        }

        private void RefreshKnownPeer() { RenderMachines(); }

        private bool HasRecognizedTarget { get { var t = _config.TrustedTarget; return t != null && t.ProtectedKey != null && !string.IsNullOrWhiteSpace(t.Address) && !string.IsNullOrWhiteSpace(t.Fingerprint); } }
        private string TargetDisplayName { get { var t = _config.TrustedTarget; return t != null && !string.IsNullOrWhiteSpace(t.DisplayName) ? t.DisplayName : "a máquina vinculada"; } }

        // Single place that turns session state into the two machine rows and the shortcut labels.
        private void RenderMachines()
        {
            var target = _config.TrustedTarget;
            var legacyPeer = target == null && !string.IsNullOrWhiteSpace(_config.PinnedDeviceId);
            var state = _transport.State;
            var outbound = state == PeerConnectionState.Connected ? OutboundSession.Connected : state == PeerConnectionState.Pairing ? OutboundSession.AwaitingApproval : state == PeerConnectionState.Connecting ? OutboundSession.Connecting : OutboundSession.None;
            var inbound = _listenerTransport != null && _listenerTransport.State == PeerConnectionState.Connected;
            var model = MachineListState.Build(new MachineListInput
            {
                LocalName = _config.DisplayName, LocalHotkey = _config.LocalHotkey, RemoteHotkey = _config.RemoteHotkey,
                Outbound = outbound, OutboundFocused = _remoteActive,
                TargetName = target != null ? target.DisplayName : legacyPeer ? "Máquina vinculada" : null,
                TargetAddress = target != null ? target.Address : _config.RemoteAddress,
                TargetRecognized = HasRecognizedTarget,
                InboundConnected = inbound, InboundFocused = _inboundFocused,
                ControllerName = _inboundControllerName, ControllerAddress = HostOnly(_listenerTransport == null ? null : _listenerTransport.RemoteEndpoint)
            });
            ApplyRow(model.Local, LocalMachineRow, LocalRowAccent, LocalStatusBadge, LocalDot, LocalBadgeText, LocalControllerBadge, LocalNameText, null, LocalSubtitleText, LocalStateText, LocalDetailText);
            ApplyRow(model.Other, RemoteMachineRow, RemoteRowAccent, RemoteStatusBadge, RemoteDot, RemoteBadgeText, RemoteControllerBadge, RemoteNameText, RemoteAddressText, RemoteStateText, TargetStateText, LatencyText);
            if (model.Other.Visible && !inbound && outbound == OutboundSession.None && !string.IsNullOrWhiteSpace(_outboundNote)) LatencyText.Text = _outboundNote;
            RemoteMachineRow.ToolTip = inbound ? null : outbound == OutboundSession.None ? (HasRecognizedTarget ? "Clique para pedir controle (só precisa do aceite)" : "Clique para vincular com o código") : outbound == OutboundSession.Connected ? "Clique para enviar mouse e teclado" : "Clique para cancelar o pedido";
            NoPeersState.Visibility = model.Other.Visible ? Visibility.Collapsed : Visibility.Visible;
            var hasTarget = target != null || legacyPeer;
            RemoteShortcutTargetText.Text = hasTarget ? (target != null && !string.IsNullOrWhiteSpace(target.DisplayName) ? target.DisplayName : "Máquina vinculada") : "Nenhuma máquina";
            RemoteShortcutBadge.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
            RemoteShortcutStatusText.Text = _remoteActive ? "Ativo" : "Configurado";
            LocalShortcutTargetText.Text = string.IsNullOrWhiteSpace(_config.DisplayName) ? "Este computador" : _config.DisplayName + " (este PC)";
        }

        private void ApplyRow(MachineRowModel row, Grid grid, System.Windows.Shapes.Rectangle accent, Border badge, System.Windows.Shapes.Ellipse dot, TextBlock badgeText, Border controllerBadge, TextBlock name, TextBlock address, TextBlock subtitle, TextBlock status, TextBlock detail)
        {
            grid.Visibility = row.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (!row.Visible) return;
            grid.Background = new SolidColorBrush(row.IsActive ? Color.FromRgb(0x1B, 0x2A, 0x28) : Color.FromRgb(0x18, 0x20, 0x25));
            accent.Visibility = row.IsActive ? Visibility.Visible : Visibility.Collapsed;
            badge.Background = new SolidColorBrush(row.IsActive ? Color.FromRgb(0x23, 0x4B, 0x31) : Color.FromRgb(0x29, 0x34, 0x3D));
            dot.Fill = row.IsActive ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0xAF, 0xC9, 0xE3));
            badgeText.Foreground = row.IsActive ? (Brush)FindResource("AccentStrongBrush") : new SolidColorBrush(Color.FromRgb(0xC7, 0xD8, 0xE8));
            badgeText.Text = row.Badge;
            controllerBadge.Visibility = row.IsController ? Visibility.Visible : Visibility.Collapsed;
            name.Text = row.Name;
            if (address != null) address.Text = row.Address;
            subtitle.Text = row.Subtitle;
            status.Text = row.Status;
            detail.Text = row.Detail;
        }

        private static string HostOnly(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            var colon = endpoint.LastIndexOf(':');
            return (colon > 0 ? endpoint.Substring(0, colon) : endpoint).Trim('[', ']');
        }

        private byte[] LookupTrustedController(string deviceId, string fingerprint)
        {
            var peer = TrustedPeerList.Match(_config.TrustedControllers, deviceId, fingerprint);
            if (peer == null) return null;
            try { return _secretProtector.Unprotect(peer.ProtectedKey); } catch { return null; }
        }

        // Reconnects to the recognised target: it proves the stored trust key and only asks for a click there.
        private async Task ConnectRecognizedAsync()
        {
            var target = _config.TrustedTarget;
            if (!HasRecognizedTarget || _transport.State == PeerConnectionState.Connecting || _transport.State == PeerConnectionState.Pairing || _transport.State == PeerConnectionState.Connected) return;
            byte[] key;
            try { key = _secretProtector.Unprotect(target.ProtectedKey); } catch { OnTrustRejected(); return; }
            try { _pairingCoordinator.StartResume(key, target.DeviceId, target.Fingerprint); } catch (Exception ex) { _outboundNote = ex.Message; RenderMachines(); return; }
            _config.RemoteAddress = target.Address; _outboundNote = null;
            _outboundRequestCts?.Cancel(); var requestCts = new CancellationTokenSource(); _outboundRequestCts = requestCts;
            AddLog("local", "remote", "Access", "resume-requested"); RenderMachines();
            try
            {
                var port = _config.RemotePort <= 0 ? _config.ListenPort : _config.RemotePort;
                await _transport.ConnectAsync(target.Address, port, _certificate, null, true, requestCts.Token);
                if (OwnsOutboundRequest(requestCts) && requestCts.IsCancellationRequested) _transport.Disconnect("access request cancelled");
            }
            catch (Exception ex)
            {
                if (!OwnsOutboundRequest(requestCts)) return;
                _outboundNote = ex is OperationCanceledException ? "Pedido cancelado." : TargetDisplayName + " não respondeu. Verifique se o Winput LAN está aberto nela.";
                AddLog("local", "remote", "Transport", "failed"); RenderMachines();
            }
        }

        private void CancelOutboundRequest(string note)
        {
            _outboundRequestCts?.Cancel(); _pairingCoordinator?.CancelRequest(); _transport.Disconnect("access request cancelled");
            _outboundNote = note; RenderMachines();
        }

        // The target renewed its code or one side's identity changed: forget the key and fall back to the code.
        private void OnTrustRejected()
        {
            if (_config.TrustedTarget != null) { _config.TrustedTarget.ProtectedKey = null; try { _configStore.Save(_config); } catch { } }
            AddLog("remote", "local", "Access", "trust-rejected");
            BeginPairing();
            var address = _config.TrustedTarget != null ? _config.TrustedTarget.Address : _config.RemoteAddress;
            if (!string.IsNullOrWhiteSpace(address)) RemoteAddressBox.Text = address;
            PairCodeStateText.Text = TargetDisplayName + " não reconhece mais este PC. Informe o código atual dela.";
            ConnectPairButton.IsEnabled = true; RemoteCodeBox.FocusFirstEmpty();
            RenderMachines();
        }

        private void ListenerTransport_StateChanged(PeerConnectionState state, string detail)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (state != PeerConnectionState.Connected) _inboundFocused = false;
                RenderMachines();
            }));
        }

        // Called instead of Show() when started with Windows: builds the window handle (hooks, listener, tray) hidden.
        public void StartInTray()
        {
            new WindowInteropHelper(this).EnsureHandle();
            CreateTrayIcon();
            _trayIcon.Visible = true;
        }

        private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressStartupToggle) return;
            _config.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
            try { _configStore.Save(_config); } catch { }
            ApplyStartupRegistration(true);
        }

        private void ApplyStartupRegistration(bool reportErrors)
        {
            var enabled = _config.StartWithWindows; var elevatedMode = _config.RunElevated && _isElevated;
            Task.Run(() =>
            {
                try { StartupRegistration.Apply(enabled, elevatedMode); AddLog("local", "local", "Startup", enabled ? (elevatedMode ? "task" : "run-key") : "off"); }
                catch (Exception ex) { AddLog("local", "local", "Startup", "failed"); if (reportErrors) Dispatcher.BeginInvoke(new Action(() => MessageBox.Show("Não foi possível configurar a inicialização com o Windows.\n\n" + ex.Message, "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Warning))); }
            });
        }

        private void ConfigureMachineRows()
        {
            var compact = ActualWidth > 0 && ActualWidth <= 1100;
            foreach (var row in new[] { LocalMachineRow, RemoteMachineRow })
            {
                if (row.ColumnDefinitions.Count != 6) continue;
                row.ColumnDefinitions[0].Width = new GridLength(compact ? 78 : 98);
                row.ColumnDefinitions[2].Width = new GridLength(compact ? 100 : 132);
                row.ColumnDefinitions[4].Width = new GridLength(compact ? 200 : 255);
                row.ColumnDefinitions[5].Width = new GridLength(compact ? 30 : 42);
            }
            LocalNameText.FontSize = compact ? 18 : 22;
        }

        private static string ShortcutTail(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "—";
            var parts = value.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? value : parts[parts.Length - 1].Trim();
        }

        private PairingCoordinator CreatePairingCoordinator(PeerTransport transport, PairingRole role)
        {
            var coordinator = new PairingCoordinator(transport, _config, _certificate, role);
            coordinator.AccessCodeChanged += code => Dispatcher.BeginInvoke(new Action(() => UpdateAccessCode(code)));
            coordinator.AccessRequestReceived += request => Dispatcher.BeginInvoke(new Action(() => ShowAccessRequest(request)));
            coordinator.PendingRequestCancelled += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(coordinator, _listenerPairingCoordinator) && TargetApprovalPanel.Visibility == Visibility.Visible)
                {
                    PairingOverlay.Visibility = Visibility.Collapsed;
                    PairNowButton.Focus();
                    AddLog("remote", "local", "Access", "request-cancelled");
                }
            }));
            coordinator.PairingCompleted += record => Dispatcher.BeginInvoke(new Action(() => CompletePairing(record, ReferenceEquals(coordinator, _pairingCoordinator))));
            coordinator.PairingFailed += reason => Dispatcher.BeginInvoke(new Action(() =>
            {
                PairCodeStateText.Text = "Pedido encerrado: " + reason;
                if (ReferenceEquals(coordinator, _pairingCoordinator) && PairingOverlay.Visibility != Visibility.Visible) { _outboundNote = reason; RenderMachines(); }
                ConnectPairButton.IsEnabled = true;
                AddLog("remote", "local", "Access", "failed");
            }));
            return coordinator;
        }

        private void CompletePairing(PinRecord record, bool outbound)
        {
            try
            {
                if (outbound)
                {
                    _pinStore.Save(record);
                    _config.PinnedSecret = _pinStore.ExportProtectedBlob();
                    _config.PinnedDeviceId = record.DeviceId;
                    _config.PinnedFingerprint = record.CertificateFingerprint;
                    var previousName = _config.TrustedTarget != null ? _config.TrustedTarget.DisplayName : null;
                    _config.TrustedTarget = new TrustedPeer { DeviceId = record.DeviceId, Fingerprint = record.CertificateFingerprint, DisplayName = string.IsNullOrWhiteSpace(record.DisplayName) ? previousName : record.DisplayName, Address = _config.RemoteAddress, ProtectedKey = record.TrustKey == null ? null : _secretProtector.Protect(record.TrustKey), CreatedUtcTicks = DateTime.UtcNow.Ticks };
                    _outboundNote = null;
                }
                else
                {
                    _inboundControllerName = record.DisplayName; _inboundFocused = false;
                    if (record.TrustKey != null) _config.TrustedControllers = TrustedPeerList.Upsert(_config.TrustedControllers, new TrustedPeer { DeviceId = record.DeviceId, Fingerprint = record.CertificateFingerprint, DisplayName = record.DisplayName, Address = HostOnly(_listenerTransport.RemoteEndpoint), ProtectedKey = _secretProtector.Protect(record.TrustKey), CreatedUtcTicks = DateTime.UtcNow.Ticks });
                }
                _configStore.Save(_config);
                PairCodeStateText.Text = outbound ? "Acesso aceito. Controle pronto." : "Acesso autorizado.";
                PairingOverlay.Visibility = Visibility.Collapsed;
                RenderMachines();
                AddLog("local", "remote", "Access", outbound ? "controller-ready" : "target-ready");
                if (outbound) EnsureControllerCapture();
            }
            catch { AddLog("local", "remote", "Pairing", "pin-save-failed"); }
        }

        private async Task RestartListenerAsync()
        {
            await _listenerRestartGate.WaitAsync().ConfigureAwait(true);
            try
            {
                var oldCts = _listenerCts;
                var oldTask = _listenerTask;
                if (oldCts != null) oldCts.Cancel();
                if (oldTask != null) { try { await oldTask.ConfigureAwait(true); } catch (OperationCanceledException) { } }
                _listenerCts = new CancellationTokenSource();
                _listenerTask = StartListenerAsync(_listenerCts.Token);
            }
            finally { _listenerRestartGate.Release(); }
        }

        private async Task StartListenerAsync(CancellationToken listenerToken)
        {
            while (!listenerToken.IsCancellationRequested)
            {
                try
                {
                    await _listenerTransport.ListenOnceAsync(_config.ListenPort, _certificate, null, true, listenerToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { AddLog("remote", "local", "Listener", "failed"); try { await Task.Delay(500, listenerToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; } }
            }
        }

        private async Task MonitorReconnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (_transport.State == PeerConnectionState.Connected || _transport.State == PeerConnectionState.Pairing || _transport.State == PeerConnectionState.Connecting) { attempt = 0; continue; }
                await Task.Delay(ReconnectBackoff.DelayForAttempt(attempt++), cancellationToken).ConfigureAwait(false);
                try
                {
                    Dispatcher.Invoke(() => AddLog("local", "remote", "Transport", "reconnecting"));
                    await _transport.ConnectAsync(host, port, _certificate, _config.PinnedFingerprint, false, cancellationToken).ConfigureAwait(false);
                    attempt = 0;
                }
                catch (OperationCanceledException) { return; }
                catch { Dispatcher.Invoke(() => AddLog("local", "remote", "Transport", "retry-pending")); }
            }
        }

        private void SetInputTarget(bool remote)
        {
            if (remote && (_transport.State != PeerConnectionState.Connected || !_transport.AllowsInputSend))
            {
                // The shortcut on a disconnected but recognised target starts a reconnection request.
                if (HasRecognizedTarget && _transport.State != PeerConnectionState.Connecting && _transport.State != PeerConnectionState.Pairing) _ = ConnectRecognizedAsync();
                AddLog("local", "remote", "Target", "blocked-unpaired");
                return;
            }
            _remoteActive = remote;
            _inputRouter?.SetRemoteActive(remote);
            _capture?.SetRemoteActive(remote);
            RenderMachines();
            AddLog("local", remote ? "remote" : "local", "Target", remote ? "selected" : "restored");
        }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            Dispatcher.Invoke(() =>
            {
                // A lost session must hand the pinned cursor and keyboard back to this machine immediately.
                if (state != PeerConnectionState.Connected && _remoteActive) SetInputTarget(false);
                if (state == PeerConnectionState.Offline || state == PeerConnectionState.Faulted) { _capture?.Dispose(); _capture = null; }
                RenderMachines();
                AddLog("remote", "local", "Transport", state.ToString());
            });
        }

        private void UpdateAccessCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            LocalAccessCodeText.Text = WinputLan.Core.AccessCode.Format(code);
        }

        private void EnsureControllerCapture()
        {
            if (_capture != null) return;
            // Runs on the hook thread: both chords are claimed here because a suppressed chord never reaches RegisterHotKey.
            _capture = new LowLevelInputCapture(_inputRouter, _hotkeyBypass, action =>
            {
                Dispatcher.BeginInvoke(new Action(() => SetInputTarget(action == HotkeyAction.SelectRemote)));
                return true;
            });
            _capture.Start();
            AddLog("local", "remote", "Hooks", "controller-active");
        }

        private void ShowAccessRequest(AccessRequest request)
        {
            // A machine accepting control must never retain an outbound capture from an older session.
            StopControllerCapture();
            PairingOverlay.Visibility = Visibility.Visible;
            ControllerPairPanel.Visibility = Visibility.Collapsed; TargetApprovalPanel.Visibility = Visibility.Visible;
            PairingTitleText.Text = "Permitir controle?";
            PairingDescriptionText.Text = request.Recognized ? "Máquina reconhecida de um vínculo anterior. Você decide se esta sessão pode começar." : "O código foi validado. Você decide se esta sessão pode começar.";
            // The request must be seen even when the app lives in the notification area.
            if (!IsVisible || WindowState == WindowState.Minimized) RestoreFromTray();
            Activate(); Topmost = true; Topmost = false;
            RequestingMachineText.Text = string.IsNullOrWhiteSpace(request.DisplayName) ? "Máquina solicitante" : request.DisplayName;
            RequestingAddressText.Text = "IP solicitante: " + (_listenerTransport.RemoteEndpoint ?? "rede local");
            AcceptPairButton.Focus(); AddLog("remote", "local", "Access", "approval-requested");
        }

        private void StopControllerCapture()
        {
            _remoteActive = false;
            _inputRouter?.SetRemoteActive(false);
            _capture?.Dispose();
            _capture = null;
        }

        private static string LocalIPv4Address()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses.Select(address => new LanAddressCandidate
                {
                    Address = address.Address.ToString(), InterfaceUp = network.OperationalStatus == OperationalStatus.Up,
                    HasGateway = network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork),
                    IsEthernetOrWifi = network.NetworkInterfaceType == NetworkInterfaceType.Ethernet || network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                }));
                return LanAddressSelector.Select(candidates) ?? "IP LAN não detectado";
            }
            catch { return "IP LAN não detectado"; }
        }

        private void CancelOutboundRequest()
        {
            _outboundRequestCts?.Cancel(); _pairingCoordinator?.CancelRequest(); _transport.Disconnect("access request cancelled");
            ConnectPairButton.IsEnabled = true; PairCodeStateText.Text = "Pedido cancelado. Você pode tentar novamente.";
        }

        private bool OwnsOutboundRequest(CancellationTokenSource requestCts)
        {
            return RequestAttemptOwnership.IsCurrent(_outboundRequestCts, requestCts);
        }

        private void CreateTrayIcon()
        {
            if (_trayIcon != null) return;
            _trayIcon = new Forms.NotifyIcon { Text = "Winput LAN", Icon = LoadTrayIcon(), Visible = false };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Restaurar", null, (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray)));
            menu.Items.Add("Sair", null, (s, e) => Dispatcher.BeginInvoke(new Action(ExitFromTray)));
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
        }

        private void Window_StateChanged(object sender, EventArgs e) { if (_backgroundLifecycle.ShouldHideOnMinimize && WindowState == WindowState.Minimized) HideToTray(); }
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && PairingOverlay.Visibility == Visibility.Visible) { ClosePairingButton_Click(sender, e); e.Handled = true; } }
        private void BackgroundModeCheckBox_Changed(object sender, RoutedEventArgs e) { _config.ContinueInBackground = BackgroundModeCheckBox.IsChecked == true; _backgroundLifecycle.ContinueInBackground = _config.ContinueInBackground; try { _configStore.Save(_config); } catch { } }
        private void HideToTray() { CreateTrayIcon(); Hide(); _trayIcon.Visible = true; _trayIcon.ShowBalloonTip(1000, "Winput LAN", "Continua em execução na área de notificação.", Forms.ToolTipIcon.Info); }
        private void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); if (_trayIcon != null) _trayIcon.Visible = false; }
        private void ExitFromTray() { _backgroundLifecycle.RequestExplicitExit(); if (_trayIcon != null) _trayIcon.Visible = false; Close(); }

        private void AddLog(string origin, string destination, string type, string status)
        {
            // Recording is passive and thread-safe; the UI list reads it only while expanded.
            _transactionLog.Add(origin, destination, type, status);
            _logDirty = true;
        }

        private void AuditInput(InputKind kind, string status)
        {
            if (!_inputAuditPolicy.ShouldEmit(kind, status, DateTime.UtcNow)) return;
            var received = status.StartsWith("received", StringComparison.Ordinal) || status == "dropped-sink";
            AddLog(received ? "remote" : "local", received ? "local" : "remote", "Input." + kind, status);
            // SendInput fails outright while the UAC secure desktop is shown.
            if (status == "dropped-sink") Dispatcher.BeginInvoke(new Action(() => ShowBlockedInputHint("O Windows está mostrando um aviso de segurança (UAC) neste PC. Esse aviso só aceita mouse e teclado físicos; confirme-o localmente para o controle voltar.")));
        }

        private static readonly Uri BrandIconUri = new Uri("pack://application:,,,/WinputLan;component/assets/brand/winput-lan.ico", UriKind.Absolute);

        private static System.Drawing.Icon LoadTrayIcon()
        {
            // Pick the size Windows uses for the notification area at the current DPI instead of scaling a 32 px frame.
            try { using (var stream = Application.GetResourceStream(BrandIconUri).Stream) return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize); }
            catch { return System.Drawing.SystemIcons.Application; }
        }

        private void RefreshLog()
        {
            if (LogPanel.Visibility != Visibility.Visible) return;
            _logDirty = false;
            var entries = _transactionLog.Snapshot().Reverse().ToList();
            LogList.ItemsSource = entries;
            LogStatusText.Text = entries.Count + " eventos nesta sessão";
        }

        private void ShowShortcutsEditor()
        {
            LocalHotkeyBox.Text = _config.LocalHotkey;
            RemoteHotkeyBox.Text = _config.RemoteHotkey;
            HotkeyErrorText.Text = string.Empty;
            HotkeyErrorText.Visibility = Visibility.Collapsed;
            HotkeyOverlay.Visibility = Visibility.Visible;
            LocalHotkeyBox.Focus();
        }

        private void CancelHotkeysButton_Click(object sender, RoutedEventArgs e) { HotkeyOverlay.Visibility = Visibility.Collapsed; }

        private void SaveHotkeysButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var localGesture = HotkeyGesture.Parse(LocalHotkeyBox.Text);
                var remoteGesture = HotkeyGesture.Parse(RemoteHotkeyBox.Text);
                if (_hotkeys == null || !_hotkeys.Replace(localGesture, remoteGesture)) throw new InvalidOperationException("O Windows não conseguiu registrar um dos atalhos globais.");
                _config.LocalHotkey = localGesture.ToString(); _config.RemoteHotkey = remoteGesture.ToString();
                _hotkeyBypass.Reconfigure(localGesture, remoteGesture);
                _configStore.Save(_config);
                LocalHotkeyText.Text = ShortcutTail(_config.LocalHotkey);
                RemoteHotkeyText.Text = ShortcutTail(_config.RemoteHotkey);
                AddLog("local", "local", "Atalhos", "salvos");
                HotkeyOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                HotkeyErrorText.Text = ex.Message;
                HotkeyErrorText.Visibility = Visibility.Visible;
                LocalHotkeyBox.Focus();
            }
        }

        private async Task CheckUpdatesAsync(bool manual)
        {
            if (_updateBusy) return;
            _updateBusy = true; UpdatesButton.IsEnabled = false; UpdatesButtonText.Text = "Verificando…";
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (var http = new HttpClient(handler))
                {
                    var updater = new GitHubUpdater(http);
                    var manifest = await updater.ReadManifestAsync(new Uri("https://github.com/luingry/WinputLan/releases/latest/download/update-manifest.json"), CancellationToken.None);
                    _config.LastUpdateCheckUtcTicks = DateTime.UtcNow.Ticks;
                    try { _configStore.Save(_config); } catch { }
                    string reason;
                    if (!ReleaseManifestValidator.TryValidate(manifest, InstalledVersion, out reason))
                    {
                        AddLog("github", "local", "Update", "no-update");
                        if (manual) MessageBox.Show("Você já está na versão mais recente (" + InstalledVersion + ").", "Atualizações", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    var question = "A versão " + manifest.Version + " do Winput LAN está disponível (instalada: " + InstalledVersion + ").\n\nBaixar e instalar agora? O app fecha durante a instalação e reabre sozinho.";
                    if (MessageBox.Show(question, "Atualização disponível", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) { AddLog("github", "local", "Update", "postponed"); return; }
                    var progress = new Progress<int>(p => UpdatesButtonText.Text = "Baixando " + p + "%");
                    var installer = await updater.DownloadAndValidateAsync(manifest, InstalledVersion, Path.Combine(Path.GetTempPath(), "WinputLan", "updates"), progress, CancellationToken.None);
                    UpdatesButtonText.Text = "Instalando…"; AddLog("github", "local", "Update", "validated");
                    // Silent Inno setup; its [Run] section relaunches the app for the signed-in user when it finishes.
                    Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
                    // Exit for real (not to tray) so the setup can replace the locked executable.
                    ExitFromTray();
                }
            }
            catch (Exception ex)
            {
                AddLog("github", "local", "Update", "error");
                if (manual) MessageBox.Show("Não foi possível atualizar. Nenhum instalador foi executado.\n\n" + ex.Message, "Atualizações", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _updateBusy = false; UpdatesButton.IsEnabled = true; if (UpdatesButtonText.Text != "Instalando…") UpdatesButtonText.Text = "Atualizações"; }
        }

        private static string InstalledVersion { get { return typeof(MainWindow).Assembly.GetName().Version.ToString(3); } }

    }
}
