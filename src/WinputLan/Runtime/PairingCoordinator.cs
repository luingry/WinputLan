using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public enum PairingRole { Controller, Target }
    public sealed class AccessRequest { public string DeviceId { get; set; } public string DisplayName { get; set; } public string RemoteFingerprint { get; set; } }

    // High-entropy, certificate/nonce-bound challenge-response. The human code is never sent on the wire.
    public sealed class PairingCoordinator : IDisposable
    {
        private readonly PeerTransport _transport; private readonly WinputConfig _config; private readonly X509Certificate2 _certificate; private readonly PairingRole _role;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private AccessRequest _pending; private string _submittedCode; private string _accessCode; private DateTime _accessCodeIssuedUtc; private DateTime _blockedUntilUtc; private int _failedCodeAttempts;
        private byte[] _controllerNonce; private byte[] _targetNonce; private byte[] _transcript; private string _remoteDeviceId; private string _remoteFingerprint; private bool _offerSent; private bool _completed; private bool _disposed;

        public PairingCoordinator(PeerTransport transport, WinputConfig config, X509Certificate2 certificate, PairingRole role)
        {
            _transport = transport ?? throw new ArgumentNullException("transport"); _config = config ?? throw new ArgumentNullException("config"); _certificate = certificate ?? throw new ArgumentNullException("certificate"); _role = role;
            if (role == PairingRole.Target) NewAccessCode();
            _transport.StateChanged += Transport_StateChanged; _transport.FrameReceived += Transport_FrameReceived;
        }
        public event Action<string> AccessCodeChanged;
        public event Action<AccessRequest> AccessRequestReceived;
        public event Action PendingRequestCancelled;
        public event Action<PinRecord> PairingCompleted;
        public event Action<string> PairingFailed;
        public string AccessCode { get { return _accessCode; } }
        public AccessRequest PendingRequest { get { return _pending; } }

        public void StartRequest(string accessCode)
        {
            if (_role != PairingRole.Controller) throw new InvalidOperationException("Only a controller may request access.");
            _submittedCode = WinputLan.Core.AccessCode.Normalize(accessCode);
            if (!WinputLan.Core.AccessCode.IsValid(_submittedCode)) throw new InvalidOperationException("Informe o código de acesso de " + WinputLan.Core.AccessCode.Length + " caracteres.");
            NewNonce(out _controllerNonce); if (_transport.State == PeerConnectionState.Pairing) _ = SendOfferAsync();
        }
        public void CancelRequest() { if (_role == PairingRole.Controller) { _submittedCode = null; _offerSent = false; _transcript = null; _targetNonce = null; _completed = false; } }
        public void RenewAccessCode() { if (_role != PairingRole.Target) throw new InvalidOperationException("Only a target has an access code."); if (_pending != null) throw new InvalidOperationException("Não renove o código enquanto um pedido está aguardando."); NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); }
        public void RefreshExpiredAccessCode() { if (_role == PairingRole.Target && _pending == null && DateTime.UtcNow - _accessCodeIssuedUtc > TimeSpan.FromMinutes(10)) { NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); } }
        public void AcceptPending()
        {
            if (_role != PairingRole.Target || _pending == null || _transport.State != PeerConnectionState.Pairing || _transcript == null) throw new InvalidOperationException("No active access request is pending.");
            var request = _pending; var acceptedCode = _accessCode; _pending = null; NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); _transport.SetInputDirection(false, true); _transport.MarkPaired(); _completed = true;
            _ = SendAcceptAsync(acceptedCode); PairingCompleted?.Invoke(CreatePin(request.DeviceId, request.RemoteFingerprint));
        }
        public void DenyPending() { if (_role != PairingRole.Target || _pending == null) return; _pending = null; NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); _ = SendDenyAsync(); }
        public void Dispose() { if (_disposed) return; _disposed = true; _cts.Cancel(); _transport.StateChanged -= Transport_StateChanged; _transport.FrameReceived -= Transport_FrameReceived; _cts.Dispose(); }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            if (state == PeerConnectionState.Pairing && string.Equals(detail, "TLS ativo", StringComparison.Ordinal) && _role == PairingRole.Controller && !_offerSent && !string.IsNullOrWhiteSpace(_submittedCode)) _ = SendOfferAsync();
            if ((state == PeerConnectionState.Faulted || state == PeerConnectionState.Offline) && _pending != null) { _pending = null; PendingRequestCancelled?.Invoke(); }
            if ((state == PeerConnectionState.Faulted || state == PeerConnectionState.Offline) && !_completed && (_offerSent || _transcript != null)) PairingFailed?.Invoke(detail);
            if (state == PeerConnectionState.Offline || state == PeerConnectionState.Faulted) { _offerSent = false; _completed = false; _transcript = null; _targetNonce = null; _remoteDeviceId = null; _remoteFingerprint = null; }
        }
        private async Task SendOfferAsync()
        {
            if (_offerSent || string.IsNullOrWhiteSpace(_submittedCode) || _controllerNonce == null) return; _offerSent = true;
            try { await _transport.SendAsync(FrameType.PairingOffer, Encoding.UTF8.GetBytes("offer|" + _config.DeviceId + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_config.DisplayName ?? string.Empty)) + "|" + Convert.ToBase64String(_controllerNonce)), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void Transport_FrameReceived(Frame frame)
        {
            try
            {
                if (_role == PairingRole.Target && frame.Type == FrameType.PairingOffer) HandleTargetOffer(Encoding.UTF8.GetString(frame.Payload));
                else if (_role == PairingRole.Controller && frame.Type == FrameType.PairingConfirm) HandleControllerConfirm(Encoding.UTF8.GetString(frame.Payload));
                else if (frame.Type == FrameType.Error) Fail("Pedido de acesso negado.");
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void HandleTargetOffer(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length > 0 && parts[0] == "offer") { HandleOffer(parts); return; }
            if (parts.Length > 0 && parts[0] == "proof") { HandleRequestProof(parts); return; }
            throw new InvalidDataException("Access request is invalid.");
        }
        private void HandleOffer(string[] parts)
        {
            Guid ignored; if (parts.Length != 4 || !Guid.TryParse(parts[1], out ignored) || _pending != null || _transcript != null) throw new InvalidDataException("Access offer is invalid.");
            var nonce = Convert.FromBase64String(parts[3]); if (nonce.Length != 32) throw new InvalidDataException("Access nonce is invalid.");
            var fingerprint = _transport.ObservedRemoteFingerprint; if (string.IsNullOrWhiteSpace(fingerprint)) throw new InvalidDataException("TLS peer fingerprint is unavailable.");
            _remoteDeviceId = parts[1]; _remoteFingerprint = fingerprint; _pendingName = parts[2]; _controllerNonce = nonce; NewNonce(out _targetNonce);
            _transcript = AccessProof.CanonicalTranscript(_remoteDeviceId, _config.DeviceId, _remoteFingerprint, CertificateManager.Fingerprint(_certificate), _controllerNonce, _targetNonce);
            _ = SendChallengeAsync();
        }
        private void HandleRequestProof(string[] parts)
        {
            if (parts.Length != 2 || _transcript == null || _pending != null) throw new InvalidDataException("Access proof is invalid.");
            var proof = Convert.FromBase64String(parts[1]);
            if (DateTime.UtcNow - _accessCodeIssuedUtc > TimeSpan.FromMinutes(10)) { NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); RegisterFailedAttempt(); _ = SendDenyAsync(); return; }
            if (DateTime.UtcNow < _blockedUntilUtc || !AccessProof.Verify(_accessCode, _transcript, "request", proof)) { RegisterFailedAttempt(); _ = SendDenyAsync(); return; }
            string displayName; try { displayName = Encoding.UTF8.GetString(Convert.FromBase64String(_pendingName)); } catch { throw new InvalidDataException("Access requester name is invalid."); }
            if (displayName.Length > 64 || displayName.IndexOfAny(new[] { '\r', '\n', '|' }) >= 0) throw new InvalidDataException("Access requester name is invalid.");
            _failedCodeAttempts = 0; _pending = new AccessRequest { DeviceId = _remoteDeviceId, DisplayName = displayName, RemoteFingerprint = _remoteFingerprint }; AccessRequestReceived?.Invoke(_pending);
        }
        private string _pendingName;
        private async Task SendChallengeAsync()
        {
            try { await _transport.SendAsync(FrameType.PairingConfirm, Encoding.UTF8.GetBytes("challenge|" + _config.DeviceId + "|" + Convert.ToBase64String(_targetNonce)), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void HandleControllerConfirm(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length > 0 && parts[0] == "challenge")
            {
                Guid ignored; if (parts.Length != 3 || !Guid.TryParse(parts[1], out ignored) || _controllerNonce == null) throw new InvalidDataException("Access challenge is invalid.");
                _targetNonce = Convert.FromBase64String(parts[2]); if (_targetNonce.Length != 32) throw new InvalidDataException("Access challenge nonce is invalid.");
                _remoteDeviceId = parts[1]; _remoteFingerprint = _transport.ObservedRemoteFingerprint; if (string.IsNullOrWhiteSpace(_remoteFingerprint)) throw new InvalidDataException("TLS peer fingerprint is unavailable.");
                _transcript = AccessProof.CanonicalTranscript(_config.DeviceId, _remoteDeviceId, CertificateManager.Fingerprint(_certificate), _remoteFingerprint, _controllerNonce, _targetNonce);
                _ = SendRequestProofAsync(); return;
            }
            if (parts.Length > 0 && parts[0] == "accept")
            {
                if (parts.Length != 3 || _transcript == null || string.IsNullOrWhiteSpace(_submittedCode) || !string.Equals(parts[1], _remoteDeviceId, StringComparison.Ordinal) || !AccessProof.Verify(_submittedCode, _transcript, "accept", Convert.FromBase64String(parts[2]))) throw new InvalidDataException("Access approval proof is invalid.");
                _transport.SetInputDirection(true, false); _transport.MarkPaired(); _completed = true; PairingCompleted?.Invoke(CreatePin(_remoteDeviceId, _remoteFingerprint)); return;
            }
            Fail("Pedido de acesso negado.");
        }
        private async Task SendRequestProofAsync()
        {
            try { await _transport.SendAsync(FrameType.PairingOffer, Encoding.UTF8.GetBytes("proof|" + Convert.ToBase64String(AccessProof.Create(_submittedCode, _transcript, "request"))), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private async Task SendAcceptAsync(string acceptedCode)
        {
            try { await _transport.SendAsync(FrameType.PairingConfirm, Encoding.UTF8.GetBytes("accept|" + _config.DeviceId + "|" + Convert.ToBase64String(AccessProof.Create(acceptedCode, _transcript, "accept"))), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private async Task SendDenyAsync() { try { await _transport.SendAsync(FrameType.Error, Encoding.UTF8.GetBytes("access denied"), _cts.Token).ConfigureAwait(false); _transport.Disconnect("access denied"); } catch (Exception ex) { Fail(ex.Message); } }
        private PinRecord CreatePin(string deviceId, string fingerprint) { var material = Encoding.UTF8.GetBytes("winput-lan/access-v2|" + _config.DeviceId + "|" + deviceId + "|" + fingerprint); string digest; using (var sha = SHA256.Create()) digest = Convert.ToBase64String(sha.ComputeHash(material)); return new PinRecord { DeviceId = deviceId, CertificateFingerprint = fingerprint, TranscriptDigest = digest, CreatedUtc = DateTime.UtcNow }; }
        private void NewAccessCode() { _accessCode = WinputLan.Core.AccessCode.Generate(); _accessCodeIssuedUtc = DateTime.UtcNow; _failedCodeAttempts = 0; _blockedUntilUtc = DateTime.MinValue; }
        private void RegisterFailedAttempt() { if (++_failedCodeAttempts >= 3) { _failedCodeAttempts = 0; _blockedUntilUtc = DateTime.UtcNow.AddSeconds(30); } }
        private void Fail(string reason) { if (!_completed) PairingFailed?.Invoke(reason); _transport.Disconnect("access request failed"); }
        private static void NewNonce(out byte[] value) { value = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(value); }
    }
}
