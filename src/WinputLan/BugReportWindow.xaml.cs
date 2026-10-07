using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan
{
    public partial class BugReportWindow : Window
    {
        [DataContract] private sealed class ChallengeMessage
        {
            [DataMember(Name="type")] public string Type { get; set; }
            [DataMember(Name="token")] public string Token { get; set; }
        }
        private readonly BugReportAttempt _attempt;
        private readonly BugReportClient _client = new BugReportClient();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer _challengeRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _successClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private WebView2 _web;
        private string _token;
        private bool _busy, _ready, _closed, _sent, _preparing;
        public BugReportWindow(BugReport snapshot)
        {
            InitializeComponent();
            _attempt = BugReportAttempt.Create(BugReportJson.NewFromSnapshot(snapshot));
            _challengeRetry.Tick += async (s,e) => { _challengeRetry.Stop(); if (!_busy && !_sent) await PrepareChallengeAsync(); };
            _successClose.Tick += (s,e) => { _successClose.Stop(); if (!_closed) Close(); };
            Populate();
        }
        private void Populate()
        {
            _ready = false;
            TitleBox.Text = _attempt.Report.Title; DescriptionBox.Text = _attempt.Report.Description; StepsBox.Text = _attempt.Report.Steps;
            TitleBox.IsReadOnly = DescriptionBox.IsReadOnly = StepsBox.IsReadOnly = _attempt.Frozen;
            _ready = true;
        }
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TitleBox.Focus();
            var network = await Task.Run(() => BugReportDiagnostics.GetNetworkType()); if (_closed) return;
            if(!_attempt.Frozen) _attempt.Report.Diagnostics.Metadata["networkType"] = network;
            await PrepareChallengeAsync();
        }
        private async Task PrepareChallengeAsync()
        {
            if (_preparing || _closed || _sent) return;
            _preparing = true; _challengeRetry.Stop();
            _token = null; ChallengeHost.Visibility = Visibility.Collapsed; UpdateSend();
            try {
                if (_web == null) {
                    _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(23,28,32) }; ChallengeHost.Children.Add(_web);
                    var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan", "report-webview");
                    var env = await CoreWebView2Environment.CreateAsync(null, profile);
                    if (_closed) return;
                    await _web.EnsureCoreWebView2Async(env); if (_closed) return;
                    var core = _web.CoreWebView2;
                    core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreHostObjectsAllowed = false; core.Settings.IsStatusBarEnabled = false; core.Settings.IsBuiltInErrorPageEnabled = false;
                    core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
                    core.NavigationStarting += (s,e) => { if (!AllowedPage(e.Uri)) e.Cancel = true; };
                    core.NewWindowRequested += (s,e) => e.Handled = true;
                    core.DownloadStarting += (s,e) => e.Cancel = true;
                    core.PermissionRequested += (s,e) => e.State = CoreWebView2PermissionState.Deny;
                    core.NavigationCompleted += (s,e) => { if(!e.IsSuccess) ChallengeUnavailable("Verificação indisponível. Confira a conexão; tentaremos novamente automaticamente."); };
                    core.ProcessFailed += (s,e) => {
                        ChallengeUnavailable("A verificação foi interrompida. Tentaremos novamente automaticamente.");
                        _web?.Dispose(); ChallengeHost.Children.Clear(); _web = null;
                    };
                    core.WebMessageReceived += ChallengeReceived;
                }
                if (!_closed) _web.CoreWebView2.Navigate(new Uri(BugReportClient.ServiceUri, "challenge?id=" + _attempt.Report.Id).AbsoluteUri);
            } catch { if(!_closed) { if(_web != null && _web.CoreWebView2 == null) { _web.Dispose(); ChallengeHost.Children.Clear(); _web = null; } ChallengeUnavailable("Não foi possível abrir a verificação. Confira a conexão e o Microsoft Edge WebView2 Runtime."); } }
            finally { _preparing = false; }
        }
        private void ChallengeUnavailable(string message)
        {
            if (_closed || _sent) return;
            _token = null; ChallengeHost.Visibility = Visibility.Collapsed;
            if (!_busy) SetStatus(message);
            UpdateSend(); _challengeRetry.Start();
        }
        private void SetStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        private bool AllowedPage(string address)
        {
            Uri uri; return Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == BugReportClient.ServiceUri.Host && uri.IsDefaultPort && uri.AbsolutePath == "/challenge" && uri.Query == "?id=" + _attempt.Report.Id;
        }
        private void ChallengeReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_closed || _sent || !AllowedPage(e.Source) || e.WebMessageAsJson.Length > 3000) return;
            try {
                var message = BugReportJson.Deserialize<ChallengeMessage>(System.Text.Encoding.UTF8.GetBytes(e.WebMessageAsJson));
                if (message.Type == "token" && !string.IsNullOrEmpty(message.Token) && message.Token.Length <= 2048) {
                    _token = message.Token; _challengeRetry.Stop(); ChallengeHost.Visibility = Visibility.Collapsed;
                    if (!_busy && !_attempt.Frozen) SetStatus("");
                }
                else if (message.Type == "interactive") {
                    _token = null; _challengeRetry.Stop(); ChallengeHost.Visibility = Visibility.Visible;
                    if (!_busy && !_attempt.Frozen) SetStatus("Conclua a verificação para enviar seu relato.");
                }
                else if (message.Type == "noninteractive") ChallengeHost.Visibility = Visibility.Collapsed;
                else if (message.Type == "expired") { _token = null; ChallengeHost.Visibility = Visibility.Collapsed; }
                else if (message.Type == "error") {
                    ChallengeUnavailable("Verificação indisponível. Tentaremos novamente automaticamente.");
                }
                UpdateSend();
            } catch { _token = null; UpdateSend(); }
        }
        private void Fields_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_ready || _attempt.Frozen) return;
            _attempt.Report.Title = TitleBox.Text; _attempt.Report.Description = DescriptionBox.Text; _attempt.Report.Steps = StepsBox.Text;
            UpdateSend();
        }
        // A frozen retry may confirm an existing durable receipt even while CAPTCHA is unavailable.
        private void UpdateSend() { if(SendButton != null) SendButton.IsEnabled = _ready && !_busy && !_sent && (_token != null || _attempt.Frozen) && BugReportJson.Validate(_attempt.Report) == null; }
        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _sent || (_token == null && !_attempt.Frozen) || BugReportJson.Validate(_attempt.Report) != null) return;
            try { BugReportJson.Serialize(new BugReportSubmission { Report = _attempt.Report, Secret = _attempt.Secret, Token = _token ?? "" }); }
            catch(InvalidOperationException ex) { SetStatus(ex.Message + " Reduza a descrição ou os passos antes de enviar."); return; }
            _attempt.Frozen = true;
            Populate(); _busy = true; _challengeRetry.Stop(); SendButton.Content = "Enviando…"; UpdateSend();
            SetStatus("Enviando relato…");
            try {
                await _client.SendAsync(_attempt, _token, _lifetime.Token);
                if(_closed) return;
                ShowSuccess();
            } catch(OperationCanceledException) { if(!_closed) SetStatus("O envio não foi confirmado a tempo. Tente novamente nesta janela."); }
              catch(Exception ex) { if(!_closed) SetStatus(ex is InvalidOperationException ? ex.Message : "Não foi possível confirmar o envio. Tente novamente nesta janela."); }
            finally {
                _busy = false; if(!_closed && !_sent) { SendButton.Content = "Tentar novamente"; UpdateSend(); await PrepareChallengeAsync(); }
            }
        }
        private void ShowSuccess()
        {
            _sent = true; _token = null; _challengeRetry.Stop();
            ChallengeHost.Visibility = Visibility.Collapsed;
            SetStatus(""); UpdateSend();
            ReportForm.Visibility = Visibility.Collapsed;
            SuccessOverlay.Visibility = Visibility.Visible;
            SuccessOverlay.Focus();
            _successClose.Start();
        }
        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ToggleMaximize(); else DragMove();
        }
        private void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void Maximize_Click(object sender, RoutedEventArgs e) { ToggleMaximize(); }
        private void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _challengeRetry.Stop(); _successClose.Stop();
            _closed = true; _lifetime.Cancel(); _web?.Dispose(); _client.Dispose(); _lifetime.Dispose();
        }
    }
}
