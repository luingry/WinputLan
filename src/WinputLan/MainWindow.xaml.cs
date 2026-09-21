using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan
{
    public partial class MainWindow : Window
    {
        private readonly WinputConfig _config;
        private readonly AppConfigStore _configStore;
        private readonly InMemoryTransactionLog _transactionLog = new InMemoryTransactionLog();
        private readonly ObservableCollection<string> _logLines = new ObservableCollection<string>();
        private readonly InputEventQueue _inputQueue = new InputEventQueue();
        private readonly SendInputSink _inputSink = new SendInputSink();
        private readonly PeerTransport _transport = new PeerTransport();
        private PeerTransport _listenerTransport;
        private PairingCoordinator _pairingCoordinator;
        private PairingCoordinator _listenerPairingCoordinator;
        private PairingCoordinator _confirmationCoordinator;
        private CancellationTokenSource _listenerCts;
        private Task _listenerTask;
        private readonly SemaphoreSlim _listenerRestartGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _reconnectCts;
        private PinStore _pinStore;
        private InputRouter _inputRouter;
        private PairedInputReceiver _inputReceiver;
        private PairedInputReceiver _listenerInputReceiver;
        private CertificateManager _certificateManager;
        private X509Certificate2 _certificate;
        private GlobalHotkeyService _hotkeys;
        private LowLevelInputCapture _capture;
        private HotkeyBypassDetector _hotkeyBypass;
        private bool _remoteActive;

        public MainWindow(WinputConfig config, AppConfigStore configStore)
        {
            InitializeComponent();
            try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "brand", "winput-lan.ico"))); } catch { }
            _config = config ?? WinputConfig.CreateDefault();
            _configStore = configStore;
            LogList.ItemsSource = _logLines;
            LocalNameText.Text = _config.DisplayName;
            LocalIdText.Text = "id " + _config.DeviceId.Substring(0, Math.Min(12, _config.DeviceId.Length));
            ListenText.Text = "TCP " + _config.ListenPort + " · Private";
            VersionText.Text = "v" + InstalledVersion + " · framework-dependent";
            RemoteAddressBox.Text = string.IsNullOrWhiteSpace(_config.RemoteAddress) ? "127.0.0.1" : _config.RemoteAddress;
            _transactionLog.Add("local", "local", "Session", "ready");
            RefreshLog();
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
                _pairingCoordinator = CreatePairingCoordinator(_transport);
                _listenerTransport = new PeerTransport();
                _listenerPairingCoordinator = CreatePairingCoordinator(_listenerTransport);
                _listenerCts = new CancellationTokenSource();
                _listenerTask = StartListenerAsync(_listenerCts.Token);
                _inputRouter = new InputRouter(_inputQueue, _transport, _inputSink);
                _inputReceiver = new PairedInputReceiver(_transport, _inputSink);
                _listenerInputReceiver = new PairedInputReceiver(_listenerTransport, _inputSink);
                _inputRouter.InputAudited += AuditInput;
                _inputReceiver.InputAudited += AuditInput;
                _listenerInputReceiver.InputAudited += AuditInput;
                _capture = new LowLevelInputCapture(_inputRouter, _hotkeyBypass, action =>
                {
                    if (!_remoteActive) return false;
                    Dispatcher.BeginInvoke(new Action(() => SetInputTarget(action == HotkeyAction.SelectRemote)));
                    return true;
                });
                _capture.Start();
                AddLog("local", "local", "Hooks", "active");
            }
            catch (Exception ex)
            {
                AddLog("local", "local", "Hooks", "unavailable");
                MessageBox.Show("Global input hooks could not start. Winput LAN remains in local-safe mode.\n\n" + ex.Message, "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _capture?.Dispose();
            _inputRouter?.Dispose();
            _inputReceiver?.Dispose();
            _listenerInputReceiver?.Dispose();
            _inputSink.ReleaseAll();
            _pairingCoordinator?.Dispose();
            _listenerPairingCoordinator?.Dispose();
            _listenerCts?.Cancel();
            _reconnectCts?.Cancel();
            _listenerTransport?.Dispose();
            _transport.Dispose();
            _hotkeys?.Dispose();
            try { _configStore?.Save(_config); } catch { }
        }

        private void PairNowButton_Click(object sender, RoutedEventArgs e) { BeginPairing(); }
        private async void ConnectPairButton_Click(object sender, RoutedEventArgs e)
        {
            BeginPairing();
            var host = (RemoteAddressBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host)) { MessageBox.Show("Enter a peer address.", "Pairing", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            _config.RemoteAddress = host;
            try
            {
                _configStore.Save(_config);
                AddLog("local", "remote", "Transport", "connecting");
                var pairingMode = string.IsNullOrWhiteSpace(_config.PinnedFingerprint);
                var port = _config.RemotePort <= 0 ? _config.ListenPort : _config.RemotePort;
                await _transport.ConnectAsync(host, port, _certificate, _config.PinnedFingerprint, pairingMode, CancellationToken.None);
                if (!pairingMode)
                {
                    _reconnectCts?.Cancel();
                    _reconnectCts = new CancellationTokenSource();
                    _ = MonitorReconnectAsync(host, port, _reconnectCts.Token);
                }
            }
            catch (Exception ex)
            {
                AddLog("local", "remote", "Transport", "failed");
                MessageBox.Show("Could not connect to the peer. Check the address, Private firewall rule and pairing state.\n\n" + ex.Message, "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void PairingButton_Click(object sender, RoutedEventArgs e) { BeginPairing(); }
        private void MachinesButton_Click(object sender, RoutedEventArgs e) { SetInputTarget(false); }
        private void ShortcutsButton_Click(object sender, RoutedEventArgs e) { ShowShortcutsEditor(); }
        private async void UpdatesButton_Click(object sender, RoutedEventArgs e) { await CheckUpdatesAsync(); }
        private void ClearLogButton_Click(object sender, RoutedEventArgs e) { _transactionLog.Clear(); RefreshLog(); }
        private void ConfirmPairButton_Click(object sender, RoutedEventArgs e)
        {
            try { _confirmationCoordinator?.ConfirmLocal(_confirmationCoordinator.CurrentCode); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Pairing", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        private void HowPairingButton_Click(object sender, RoutedEventArgs e) { MessageBox.Show("Both PCs create a TLS transcript, display the same six-digit SAS, and require bilateral confirmation. The SAS is never sent. The resulting peer certificate is pinned and protected by Windows DPAPI.", "Pairing", MessageBoxButton.OK, MessageBoxImage.Information); }

        private void BeginPairing()
        {
            ConnectionStateText.Text = "PAIRING";
            PairCodeText.Text = "—— ——";
            PairCodeStateText.Text = "Waiting for a remote offer";
            ConfirmPairButton.IsEnabled = false;
            AddLog("local", "remote", "Pairing", "waiting");
        }

        private PairingCoordinator CreatePairingCoordinator(PeerTransport transport)
        {
            var coordinator = new PairingCoordinator(transport, _config, _certificate);
            coordinator.CodeReady += code => Dispatcher.Invoke(() =>
            {
                _confirmationCoordinator = coordinator;
                PairCodeText.Text = code.Substring(0, 3) + " " + code.Substring(3);
                PairCodeStateText.Text = "Verify this code on the other PC";
                ConfirmPairButton.IsEnabled = true;
                ConnectionStateText.Text = "PAIRING";
                AddLog("remote", "local", "Pairing", "code-ready");
            });
            coordinator.PairingCompleted += record => Dispatcher.Invoke(() => CompletePairing(record, ReferenceEquals(coordinator, _pairingCoordinator)));
            coordinator.PairingFailed += reason => Dispatcher.Invoke(() => AddLog("remote", "local", "Pairing", "failed"));
            return coordinator;
        }

        private void CompletePairing(PinRecord record, bool outbound)
        {
            try
            {
                _pinStore.Save(record);
                _config.PinnedSecret = _pinStore.ExportProtectedBlob();
                _config.PinnedDeviceId = record.DeviceId;
                _config.PinnedFingerprint = record.CertificateFingerprint;
                _configStore.Save(_config);
                PairCodeStateText.Text = "Confirmed bilaterally · certificate pinned";
                ConfirmPairButton.IsEnabled = false;
                RemoteNameText.Text = "Paired peer";
                AddLog("local", "remote", "Pairing", "confirmed");
                if (outbound) _ = RestartListenerAsync();
                if (!string.IsNullOrWhiteSpace(_config.RemoteAddress))
                {
                    _reconnectCts?.Cancel();
                    _reconnectCts = new CancellationTokenSource();
                    _ = MonitorReconnectAsync(_config.RemoteAddress, _config.RemotePort <= 0 ? _config.ListenPort : _config.RemotePort, _reconnectCts.Token);
                }
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
                    await _listenerTransport.ListenOnceAsync(_config.ListenPort, _certificate, _config.PinnedFingerprint, string.IsNullOrWhiteSpace(_config.PinnedFingerprint), listenerToken);
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
            if (remote && (_transport.State != PeerConnectionState.Connected || string.IsNullOrWhiteSpace(_config.PinnedDeviceId)))
            {
                AddLog("local", "remote", "Target", "blocked-unpaired");
                return;
            }
            _remoteActive = remote;
            _inputRouter?.SetRemoteActive(remote);
            RemoteDot.Fill = remote ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");
            ConnectionStateText.Text = remote ? "REMOTE" : "LOCAL";
            TargetStateText.Text = remote ? "Active · inputs route to peer" : "Offline · local input active";
            TargetStateDot.Fill = remote ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");
            AddLog("local", remote ? "remote" : "local", "Target", remote ? "selected" : "restored");
        }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            Dispatcher.Invoke(() =>
            {
                if (state == PeerConnectionState.Connected) { RemoteNameText.Text = "Paired peer"; RemoteAddressText.Text = _config.RemoteAddress ?? "LAN peer"; LatencyText.Text = "measuring"; }
                else if (state == PeerConnectionState.Offline) { LatencyText.Text = "—"; RemoteNameText.Text = "No target"; }
                AddLog("remote", "local", "Transport", state.ToString());
            });
        }

        private void AddLog(string origin, string destination, string type, string status)
        {
            _transactionLog.Add(origin, destination, type, status);
            RefreshLog();
        }

        private void AuditInput(InputKind kind, string status)
        {
            var received = status.StartsWith("received", StringComparison.Ordinal);
            Dispatcher.BeginInvoke(new Action(() => AddLog(received ? "remote" : "local", received ? "local" : "remote", "Input." + kind, status)));
        }

        private void RefreshLog()
        {
            _logLines.Clear();
            foreach (var entry in _transactionLog.Snapshot().Reverse().Select(e => e.ToString())) _logLines.Add(entry);
            LogStatusText.Text = _logLines.Count + " events this session";
        }

        private void ShowShortcutsEditor()
        {
            var local = new System.Windows.Controls.TextBox { Text = _config.LocalHotkey, Margin = new Thickness(0, 5, 0, 10) };
            var remote = new System.Windows.Controls.TextBox { Text = _config.RemoteHotkey, Margin = new Thickness(0, 5, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(local, "Local input shortcut");
            System.Windows.Automation.AutomationProperties.SetName(remote, "Remote input shortcut");
            var save = new System.Windows.Controls.Button { Content = "Save shortcuts", IsDefault = true, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(22), Width = 330 };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "LOCAL INPUT", FontWeight = FontWeights.SemiBold }); panel.Children.Add(local);
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "REMOTE INPUT", FontWeight = FontWeights.SemiBold }); panel.Children.Add(remote); panel.Children.Add(save);
            var dialog = new Window { Title = "Shortcuts", Content = panel, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            save.Click += (s, args) =>
            {
                try
                {
                    var localGesture = HotkeyGesture.Parse(local.Text);
                    var remoteGesture = HotkeyGesture.Parse(remote.Text);
                    if (_hotkeys == null || !_hotkeys.Replace(localGesture, remoteGesture)) throw new InvalidOperationException("Windows could not register one of these global shortcuts.");
                    _config.LocalHotkey = localGesture.ToString(); _config.RemoteHotkey = remoteGesture.ToString();
                    _hotkeyBypass.Reconfigure(localGesture, remoteGesture);
                    _configStore.Save(_config); AddLog("local", "local", "Hotkeys", "saved-reregistered"); dialog.DialogResult = true;
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            dialog.ShowDialog();
        }

        private async Task CheckUpdatesAsync()
        {
            UpdatesButton.IsEnabled = false; UpdatesButton.Content = "Updates · checking…";
            try
            {
                using (var http = new HttpClient())
                {
                    var updater = new GitHubUpdater(http, new WindowsAuthenticodeVerifier(), Process.GetCurrentProcess().MainModule.FileName);
                    var manifest = await updater.ReadManifestAsync(new Uri("https://github.com/luingry/WinputLan/releases/latest/download/update-manifest.json"), CancellationToken.None);
                    string reason;
                    if (!ReleaseManifestValidator.TryValidate(manifest, InstalledVersion, out reason))
                    {
                        UpdatesButton.Content = "Updates · up to date"; AddLog("github", "local", "Update", "no-update");
                        MessageBox.Show("No newer signed update is available.\n\n" + reason, "Updates", MessageBoxButton.OK, MessageBoxImage.Information); return;
                    }
                    UpdatesButton.Content = "Updates · downloading…";
                    var installer = await updater.DownloadAndValidateAsync(manifest, InstalledVersion, Path.Combine(Path.GetTempPath(), "WinputLan", "updates"), CancellationToken.None);
                    UpdatesButton.Content = "Updates · ready"; AddLog("github", "local", "Update", "validated");
                    if (MessageBox.Show("A signed update " + manifest.Version + " is ready. Start its installer now?", "Updates", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { UpdatesButton.Content = "Updates · error"; AddLog("github", "local", "Update", "error"); MessageBox.Show("Update check failed safely. No installer was started.\n\n" + ex.Message, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { UpdatesButton.IsEnabled = true; }
        }

        private static string InstalledVersion { get { return typeof(MainWindow).Assembly.GetName().Version.ToString(3); } }

    }
}
