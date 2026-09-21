using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class PairingCoordinator : IDisposable
    {
        private readonly PeerTransport _transport;
        private readonly WinputConfig _config;
        private readonly X509Certificate2 _certificate;
        private readonly byte[] _localNonce;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private PairingTranscript _transcript;
        private PairingConfirmation _confirmation;
        private string _remoteDeviceId;
        private string _remoteFingerprint;
        private bool _helloSent;
        private bool _completed;
        private bool _disposed;

        public PairingCoordinator(PeerTransport transport, WinputConfig config, X509Certificate2 certificate)
        {
            _transport = transport ?? throw new ArgumentNullException("transport");
            _config = config ?? throw new ArgumentNullException("config");
            _certificate = certificate ?? throw new ArgumentNullException("certificate");
            _localNonce = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(_localNonce);
            _transport.StateChanged += Transport_StateChanged;
            _transport.FrameReceived += Transport_FrameReceived;
        }

        public event Action<string> CodeReady;
        public event Action<PinRecord> PairingCompleted;
        public event Action<string> PairingFailed;

        public string CurrentCode { get { return _transcript == null ? null : _transcript.SasCode(); } }
        public bool IsComplete { get { return _confirmation != null && _confirmation.IsComplete; } }

        public void ConfirmLocal(string code)
        {
            if (_confirmation == null) throw new InvalidOperationException("No remote pairing offer is available.");
            _confirmation.ConfirmLocal(code);
            _ = SendConfirmationAsync();
            TryComplete();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _transport.StateChanged -= Transport_StateChanged;
            _transport.FrameReceived -= Transport_FrameReceived;
            _cts.Dispose();
        }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            if ((state == PeerConnectionState.Connected || state == PeerConnectionState.Pairing && string.Equals(detail, "TLS ativo", StringComparison.Ordinal)) && !_helloSent) _ = SendHelloAsync();
            if (state == PeerConnectionState.Faulted && !_completed) PairingFailed?.Invoke(detail);
        }

        private async Task SendHelloAsync()
        {
            if (_helloSent) return;
            _helloSent = true;
            var payload = string.Join("|", _config.DeviceId, CertificateManager.Fingerprint(_certificate), Convert.ToBase64String(_localNonce));
            try { await _transport.SendAsync(FrameType.Hello, Encoding.UTF8.GetBytes(payload), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { PairingFailed?.Invoke(ex.Message); }
        }

        private void Transport_FrameReceived(Frame frame)
        {
            try
            {
                if (frame.Type == FrameType.Hello) HandleHello(Encoding.UTF8.GetString(frame.Payload));
                else if (frame.Type == FrameType.PairingConfirm) HandleRemoteConfirmation(Encoding.UTF8.GetString(frame.Payload));
            }
            catch (Exception ex) { PairingFailed?.Invoke(ex.Message); }
        }

        private void HandleHello(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) throw new InvalidDataException("Pairing hello is invalid.");
            var remoteNonce = Convert.FromBase64String(parts[2]);
            _remoteDeviceId = parts[0];
            _remoteFingerprint = parts[1];
            _transcript = new PairingTranscript(_config.DeviceId, _remoteDeviceId, CertificateManager.Fingerprint(_certificate), _remoteFingerprint, _localNonce, remoteNonce);
            _confirmation = new PairingConfirmation(_transcript);
            CodeReady?.Invoke(_transcript.SasCode());
            _ = _transport.SendAsync(FrameType.PairingOffer, Encoding.UTF8.GetBytes(Convert.ToBase64String(_transcript.Digest())), _cts.Token);
        }

        private void HandleRemoteConfirmation(string payload)
        {
            if (_confirmation == null || !string.Equals(payload, Convert.ToBase64String(_transcript.Digest()), StringComparison.Ordinal)) throw new InvalidDataException("Pairing confirmation does not match.");
            _confirmation.ConfirmRemote(payload);
            TryComplete();
        }

        private async Task SendConfirmationAsync()
        {
            var digest = Convert.ToBase64String(_transcript.Digest());
            await _transport.SendAsync(FrameType.PairingConfirm, Encoding.UTF8.GetBytes(digest), _cts.Token).ConfigureAwait(false);
        }

        private void TryComplete()
        {
            if (!IsComplete || _completed) return;
            _completed = true;
            _transport.MarkPaired();
            PairingCompleted?.Invoke(new PinRecord { DeviceId = _remoteDeviceId, CertificateFingerprint = _remoteFingerprint, TranscriptDigest = Convert.ToBase64String(_transcript.Digest()), CreatedUtc = DateTime.UtcNow });
        }
    }
}
