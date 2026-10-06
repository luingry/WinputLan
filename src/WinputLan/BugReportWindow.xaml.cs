using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
        private BugReportDraft _draft;
        private readonly BugReport _snapshot;
        private readonly BugReportDraftStore _store;
        private readonly BugReportClient _client = new BugReportClient();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private WebView2 _web;
        private string _token;
        private bool _busy, _ready, _closed, _sent, _preparing, _restored;
        public BugReportWindow(BugReport snapshot, string draftDirectory = null)
        {
            InitializeComponent(); _snapshot = snapshot;
            _store = new BugReportDraftStore(draftDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan", "reports"));
            _draft = _store.Load(); _restored = _draft != null;
            if(_draft == null) _draft = BugReportDraft.Create(snapshot);
            _saveTimer.Tick += (s,e) => { _saveTimer.Stop(); SaveDraft(); };
            Populate();
        }
        private void Populate()
        {
            _ready = false;
            TitleBox.Text = _draft.Report.Title; DescriptionBox.Text = _draft.Report.Description; StepsBox.Text = _draft.Report.Steps;
            DiagnosticPreview.Text = BugReportJson.Preview(_draft.Report);
            TitleBox.IsReadOnly = DescriptionBox.IsReadOnly = StepsBox.IsReadOnly = _draft.Frozen;
            _ready = true;
        }
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TitleBox.Focus();
            var network = await Task.Run(() => BugReportDiagnostics.GetNetworkType()); if (_closed) return;
            if(!ReferenceEquals(_snapshot,_draft.Report) || !_draft.Frozen) _snapshot.Diagnostics.Metadata["networkType"] = network;
            if(!_restored && !_draft.Frozen) { _draft.Report.Diagnostics.Metadata["networkType"] = network; Populate(); }
            StatusText.Text = _draft.Frozen ? "Rascunho de uma tentativa anterior recuperado. Reenvie para confirmar o resultado." : _restored ? "Rascunho recuperado. O diagnóstico original foi preservado." : "Descreva o problema. O diagnóstico foi capturado ao abrir esta janela.";
            await PrepareChallengeAsync();
        }
        private async Task PrepareChallengeAsync()
        {
            if (_preparing || _closed) return;
            _preparing = true; VerifyButton.IsEnabled = false;
            _token = null; ChallengeHost.Visibility = Visibility.Visible; UpdateSend();
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
                    core.NavigationCompleted += (s,e) => { if(!e.IsSuccess) { _token = null; ChallengeHost.Visibility = Visibility.Collapsed; UpdateSend(); StatusText.Text = "Verificação indisponível. O rascunho fica salvo; tente novamente quando houver conexão."; } };
                    core.ProcessFailed += (s,e) => { _token = null; UpdateSend(); StatusText.Text = "A verificação foi interrompida. Feche e reabra o relato para tentar novamente."; };
                    core.WebMessageReceived += ChallengeReceived;
                }
                if (!_closed) _web.CoreWebView2.Navigate(new Uri(BugReportClient.ServiceUri, "challenge?id=" + _draft.Report.Id).AbsoluteUri);
            } catch { if(!_closed) { if(_web != null && _web.CoreWebView2 == null) { _web.Dispose(); ChallengeHost.Children.Clear(); _web = null; } ChallengeHost.Visibility = Visibility.Collapsed; StatusText.Text = "Não foi possível abrir a verificação. Confira a conexão e o Microsoft Edge WebView2 Runtime. Seu rascunho permanece disponível."; } }
            finally { _preparing = false; if(!_closed) VerifyButton.IsEnabled = !_busy; }
        }
        private bool AllowedPage(string address)
        {
            Uri uri; return Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == BugReportClient.ServiceUri.Host && uri.IsDefaultPort && uri.AbsolutePath == "/challenge" && uri.Query == "?id=" + _draft.Report.Id;
        }
        private void ChallengeReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_closed || !AllowedPage(e.Source) || e.WebMessageAsJson.Length > 3000) return;
            try {
                var message = BugReportJson.Deserialize<ChallengeMessage>(System.Text.Encoding.UTF8.GetBytes(e.WebMessageAsJson));
                if (message.Type == "token" && !string.IsNullOrEmpty(message.Token) && message.Token.Length <= 2048) _token = message.Token;
                else if (message.Type == "expired" || message.Type == "error") _token = null;
                UpdateSend();
            } catch { _token = null; UpdateSend(); }
        }
        private void Fields_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_ready || _draft.Frozen) return;
            _draft.Report.Title = TitleBox.Text; _draft.Report.Description = DescriptionBox.Text; _draft.Report.Steps = StepsBox.Text;
            _saveTimer.Stop(); _saveTimer.Start(); UpdateSend();
        }
        private bool SaveDraft()
        {
            if(_sent) return true;
            try { _store.Save(_draft); return true; }
            catch(InvalidOperationException ex) { StatusText.Text = ex.Message + " Reduza a descrição ou os passos para salvar o rascunho."; return false; }
            catch { StatusText.Text = "Não foi possível salvar o rascunho neste PC. Tente novamente antes de enviar."; return false; }
        }
        // A frozen retry may confirm an existing durable receipt even while CAPTCHA is unavailable.
        private void UpdateSend() { if(SendButton != null) SendButton.IsEnabled = _ready && !_busy && !_sent && (_token != null || _draft.Frozen) && BugReportJson.Validate(_draft.Report) == null; }
        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || (_token == null && !_draft.Frozen) || BugReportJson.Validate(_draft.Report) != null) return;
            try { BugReportJson.Serialize(new BugReportSubmission { Report = _draft.Report, Secret = _draft.Secret, Token = _token ?? "" }); }
            catch(InvalidOperationException ex) { StatusText.Text = ex.Message + " Reduza a descrição ou os passos antes de enviar."; return; }
            _draft.Frozen = true; if(!SaveDraft()) { _draft.Frozen = false; return; }
            Populate(); _busy = true; NewButton.IsEnabled = VerifyButton.IsEnabled = false; SendButton.Content = "Enviando…"; UpdateSend();
            StatusText.Text = "Enviando relato com diagnóstico técnico…";
            try {
                var receipt = await _client.SendAsync(_draft, _token, _lifetime.Token);
                if(_closed) return; _sent = true; try { _store.Clear(); } catch { /* Receipt is already confirmed; a stale draft remains safe to retry. */ }
                StatusText.Text = "Relato recebido. Protocolo: " + receipt + "\nA notificação por e-mail será enviada em segundo plano.";
                ChallengeHost.Visibility = Visibility.Collapsed; VerifyButton.Visibility = Visibility.Collapsed;
            } catch(OperationCanceledException) { if(!_closed) StatusText.Text = "O envio não foi confirmado a tempo. O rascunho foi preservado; tente novamente."; }
              catch(Exception ex) { if(!_closed) StatusText.Text = ex is InvalidOperationException ? ex.Message : "Não foi possível confirmar o envio. O rascunho foi preservado; tente novamente."; }
            finally {
                _busy = false; if(!_closed) { SendButton.Content = _sent ? "Recebido" : "Tentar novamente"; NewButton.IsEnabled = VerifyButton.IsEnabled = true; UpdateSend(); if(!_sent) await PrepareChallengeAsync(); }
            }
        }
        private async void Verify_Click(object sender, RoutedEventArgs e) { if(!_busy) await PrepareChallengeAsync(); }
        private async void New_Click(object sender, RoutedEventArgs e)
        {
            if(_busy) return;
            if(!_sent && (!string.IsNullOrEmpty(_draft.Report.Title) || !string.IsNullOrEmpty(_draft.Report.Description)) && MessageBox.Show(this,"Descartar o rascunho e iniciar um novo relato? Uma tentativa anterior pode já ter sido recebida.","Novo relato",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try { _store.Clear(); } catch { StatusText.Text = "Não foi possível remover o rascunho. Confira a permissão de gravação deste PC."; return; }
            var fresh = BugReportJson.NewFromSnapshot(_snapshot);
            _draft = BugReportDraft.Create(fresh); _sent = false; _restored = false; Populate(); ChallengeHost.Visibility = VerifyButton.Visibility = Visibility.Visible;
            SendButton.Content = "Enviar relato"; StatusText.Text = "Novo relato. O diagnóstico continua sendo o snapshot da abertura."; await PrepareChallengeAsync();
        }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _saveTimer.Stop();
            if (!SaveDraft() && MessageBox.Show(this, "Não foi possível salvar o rascunho. Fechar mesmo assim? Copie o texto antes de fechar se quiser preservá-lo.", "Rascunho não salvo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _closed = true; _lifetime.Cancel(); _web?.Dispose(); _client.Dispose(); _lifetime.Dispose();
        }
    }
}
