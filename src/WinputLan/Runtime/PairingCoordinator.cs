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
    public sealed class AccessRequest { public string DeviceId { get; set; } public string DisplayName { get; set; } public string RemoteFingerprint { get; set; } public bool Recognized { get; set; } }

    // Certificate/nonce-bound challenge-response. The human code is never sent on the wire.
    // Two request kinds share the same transcript:
    //   offer  - proves knowledge of the target's current access code, and derives a trust key for later;
    //   resume - proves possession of the trust key from an earlier pairing with this exact device and certificate.
    // Both always end with an explicit accept on the target.
    public sealed class PairingCoordinator : IDisposable
    {
        public const string UntrustedReason = "untrusted";
        private readonly PeerTransport _transport; private readonly WinputConfig _config; private readonly X509Certificate2 _certificate; private readonly PairingRole _role;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private AccessRequest _pending; private string _submittedCode; private string _accessCode; private DateTime _accessCodeIssuedUtc; private DateTime _blockedUntilUtc; private int _failedCodeAttempts;
        private byte[] _controllerNonce; private byte[] _targetNonce; private byte[] _transcript; private string _remoteDeviceId; private string _remoteFingerprint; private string _remoteName; private bool _offerSent; private bool _completed; private bool _disposed;
        // Controller resume: stored key plus the identity it belongs to. Target resume: key of the matched controller.
        private byte[] _resumeKey; private string _expectedTargetId; private string _expectedTargetFingerprint; private byte[] _inboundTrustKey;

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
        // Controller: the target no longer recognises this PC (code renewed or identity changed); a new code is required.
        public event Action TrustRejected;
        // Target: returns the trust key for a controller whose device id and TLS fingerprint match a trusted record.
        public Func<string, string, byte[]> TrustLookup { get; set; }
        public string AccessCode { get { return _accessCode; } }
        public AccessRequest PendingRequest { get { return _pending; } }

        public void StartRequest(string accessCode)
        {
            if (_role != PairingRole.Controller) throw new InvalidOperationException("Only a controller may request access.");
            _submittedCode = WinputLan.Core.AccessCode.Normalize(accessCode);
            if (!WinputLan.Core.AccessCode.IsValid(_submittedCode)) throw new InvalidOperationException("Informe o código de acesso de " + WinputLan.Core.AccessCode.Length + " caracteres.");
            _resumeKey = null; _expectedTargetId = null; _expectedTargetFingerprint = null;
            NewNonce(out _controllerNonce); if (_transport.State == PeerConnectionState.Pairing) _ = SendOfferAsync();
        }
        public void StartResume(byte[] trustKey, string targetDeviceId, string targetFingerprint)
        {
            if (_role != PairingRole.Controller) throw new InvalidOperationException("Only a controller may request access.");
            if (trustKey == null || trustKey.Length != TrustKey.Length || string.IsNullOrWhiteSpace(targetDeviceId) || string.IsNullOrWhiteSpace(targetFingerprint)) throw new InvalidOperationException("Esta máquina não tem um vínculo reconhecido.");
            _submittedCode = null; _resumeKey = trustKey; _expectedTargetId = targetDeviceId; _expectedTargetFingerprint = targetFingerprint;
            NewNonce(out _controllerNonce); if (_transport.State == PeerConnectionState.Pairing) _ = SendOfferAsync();
        }
        public void CancelRequest() { if (_role == PairingRole.Controller) { _submittedCode = null; _resumeKey = null; _offerSent = false; _transcript = null; _targetNonce = null; _completed = false; } }
        public void RenewAccessCode() { if (_role != PairingRole.Target) throw new InvalidOperationException("Only a target has an access code."); if (_pending != null) throw new InvalidOperationException("Não renove o código enquanto um pedido está aguardando."); NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); }
        public void RefreshExpiredAccessCode() { if (_role == PairingRole.Target && _pending == null && DateTime.UtcNow - _accessCodeIssuedUtc > TimeSpan.FromMinutes(10)) { NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); } }
        public void AcceptPending()
        {
            if (_role != PairingRole.Target || _pending == null || _transport.State != PeerConnectionState.Pairing || _transcript == null) throw new InvalidOperationException("No active access request is pending.");
            var request = _pending; _pending = null;
            byte[] acceptProof; byte[] trustKey;
            if (request.Recognized) { trustKey = _inboundTrustKey; acceptProof = TrustKey.Prove(trustKey, _transcript, "resume-accept"); }
            else
            {
                // A code is single-use: rotate it so a second request needs the new one.
                var acceptedCode = _accessCode; NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode);
                trustKey = TrustKey.Derive(acceptedCode, _transcript); acceptProof = AccessProof.Create(acceptedCode, _transcript, "accept");
            }
            _transport.SetInputDirection(false, true); _transport.MarkPaired(); _completed = true;
            _ = SendAcceptAsync(acceptProof); PairingCompleted?.Invoke(CreatePin(request.DeviceId, request.RemoteFingerprint, request.DisplayName, trustKey, request.Recognized));
        }
        public void DenyPending() { if (_role != PairingRole.Target || _pending == null) return; var recognized = _pending.Recognized; _pending = null; if (!recognized) { NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); } _ = SendDenyAsync("access denied"); }
        public void Dispose() { if (_disposed) return; _disposed = true; _cts.Cancel(); _transport.StateChanged -= Transport_StateChanged; _transport.FrameReceived -= Transport_FrameReceived; _cts.Dispose(); }

        private bool HasRequest { get { return !string.IsNullOrWhiteSpace(_submittedCode) || _resumeKey != null; } }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            if (state == PeerConnectionState.Pairing && string.Equals(detail, "TLS ativo", StringComparison.Ordinal) && _role == PairingRole.Controller && !_offerSent && HasRequest) _ = SendOfferAsync();
            if ((state == PeerConnectionState.Faulted || state == PeerConnectionState.Offline) && _pending != null) { _pending = null; PendingRequestCancelled?.Invoke(); }
            if ((state == PeerConnectionState.Faulted || state == PeerConnectionState.Offline) && !_completed && (_offerSent || _transcript != null)) PairingFailed?.Invoke(detail);
            if (state == PeerConnectionState.Offline || state == PeerConnectionState.Faulted) { _offerSent = false; _completed = false; _transcript = null; _targetNonce = null; _remoteDeviceId = null; _remoteFingerprint = null; _inboundTrustKey = null; }
        }
        private async Task SendOfferAsync()
        {
            if (_offerSent || !HasRequest || _controllerNonce == null) return; _offerSent = true;
            var kind = _resumeKey != null ? "resume" : "offer";
            try { await _transport.SendAsync(FrameType.PairingOffer, Encoding.UTF8.GetBytes(kind + "|" + _config.DeviceId + "|" + EncodeName(_config.DisplayName) + "|" + Convert.ToBase64String(_controllerNonce)), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void Transport_FrameReceived(Frame frame)
        {
            try
            {
                if (_role == PairingRole.Target && frame.Type == FrameType.PairingOffer) HandleTargetOffer(Encoding.UTF8.GetString(frame.Payload));
                else if (_role == PairingRole.Controller && frame.Type == FrameType.PairingConfirm) HandleControllerConfirm(Encoding.UTF8.GetString(frame.Payload));
                else if (frame.Type == FrameType.Error)
                {
                    if (_role == PairingRole.Controller && string.Equals(Encoding.UTF8.GetString(frame.Payload), UntrustedReason, StringComparison.Ordinal)) { TrustRejected?.Invoke(); Fail("A outra máquina não reconhece mais este PC. Informe o código atual."); }
                    else Fail("Pedido de acesso negado.");
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void HandleTargetOffer(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length > 0 && (parts[0] == "offer" || parts[0] == "resume")) { HandleOffer(parts, parts[0] == "resume"); return; }
            if (parts.Length > 0 && parts[0] == "proof") { HandleRequestProof(parts); return; }
            throw new InvalidDataException("Access request is invalid.");
        }
        private void HandleOffer(string[] parts, bool resume)
        {
            Guid ignored; if (parts.Length != 4 || !Guid.TryParse(parts[1], out ignored) || _pending != null || _transcript != null) throw new InvalidDataException("Access offer is invalid.");
            var nonce = Convert.FromBase64String(parts[3]); if (nonce.Length != 32) throw new InvalidDataException("Access nonce is invalid.");
            var fingerprint = _transport.ObservedRemoteFingerprint; if (string.IsNullOrWhiteSpace(fingerprint)) throw new InvalidDataException("TLS peer fingerprint is unavailable.");
            _inboundTrustKey = null;
            if (resume)
            {
                // Only a controller with the same device id and certificate as a trusted record may skip the code.
                var key = TrustLookup == null ? null : TrustLookup(parts[1], fingerprint);
                if (key == null || key.Length != TrustKey.Length) { _ = SendDenyAsync(UntrustedReason); return; }
                _inboundTrustKey = key;
            }
            _remoteDeviceId = parts[1]; _remoteFingerprint = fingerprint; _pendingName = parts[2]; _controllerNonce = nonce; NewNonce(out _targetNonce);
            _transcript = AccessProof.CanonicalTranscript(_remoteDeviceId, _config.DeviceId, _remoteFingerprint, CertificateManager.Fingerprint(_certificate), _controllerNonce, _targetNonce);
            _ = SendChallengeAsync();
        }
        private void HandleRequestProof(string[] parts)
        {
            if (parts.Length != 2 || _transcript == null || _pending != null) throw new InvalidDataException("Access proof is invalid.");
            var proof = Convert.FromBase64String(parts[1]);
            var recognized = _inboundTrustKey != null;
            if (!recognized && DateTime.UtcNow - _accessCodeIssuedUtc > TimeSpan.FromMinutes(10)) { NewAccessCode(); AccessCodeChanged?.Invoke(_accessCode); RegisterFailedAttempt(); _ = SendDenyAsync("access denied"); return; }
            var valid = recognized ? TrustKey.Verify(_inboundTrustKey, _transcript, "resume-request", proof) : AccessProof.Verify(_accessCode, _transcript, "request", proof);
            if (DateTime.UtcNow < _blockedUntilUtc || !valid) { RegisterFailedAttempt(); _ = SendDenyAsync(recognized ? UntrustedReason : "access denied"); return; }
            var displayName = DecodeName(_pendingName);
            _failedCodeAttempts = 0; _pending = new AccessRequest { DeviceId = _remoteDeviceId, DisplayName = displayName, RemoteFingerprint = _remoteFingerprint, Recognized = recognized }; AccessRequestReceived?.Invoke(_pending);
        }
        private string _pendingName;
        private async Task SendChallengeAsync()
        {
            try { await _transport.SendAsync(FrameType.PairingConfirm, Encoding.UTF8.GetBytes("challenge|" + _config.DeviceId + "|" + Convert.ToBase64String(_targetNonce) + "|" + EncodeName(_config.DisplayName)), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void HandleControllerConfirm(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length > 0 && parts[0] == "challenge")
            {
                Guid ignored; if ((parts.Length != 3 && parts.Length != 4) || !Guid.TryParse(parts[1], out ignored) || _controllerNonce == null) throw new InvalidDataException("Access challenge is invalid.");
                _targetNonce = Convert.FromBase64String(parts[2]); if (_targetNonce.Length != 32) throw new InvalidDataException("Access challenge nonce is invalid.");
                _remoteDeviceId = parts[1]; _remoteFingerprint = _transport.ObservedRemoteFingerprint; if (string.IsNullOrWhiteSpace(_remoteFingerprint)) throw new InvalidDataException("TLS peer fingerprint is unavailable.");
                _remoteName = parts.Length == 4 ? DecodeName(parts[3]) : null;
                // The trust key is only ever used with the exact machine it was created with.
                if (_resumeKey != null && (!string.Equals(_remoteDeviceId, _expectedTargetId, StringComparison.Ordinal) || !string.Equals(_remoteFingerprint, _expectedTargetFingerprint, StringComparison.OrdinalIgnoreCase)))
                {
                    TrustRejected?.Invoke(); Fail("A identidade da outra máquina mudou. Informe o código atual."); return;
                }
                _transcript = AccessProof.CanonicalTranscript(_config.DeviceId, _remoteDeviceId, CertificateManager.Fingerprint(_certificate), _remoteFingerprint, _controllerNonce, _targetNonce);
                _ = SendRequestProofAsync(); return;
            }
            if (parts.Length > 0 && parts[0] == "accept")
            {
                if (parts.Length != 3 || _transcript == null || !HasRequest || !string.Equals(parts[1], _remoteDeviceId, StringComparison.Ordinal)) throw new InvalidDataException("Access approval proof is invalid.");
                var proof = Convert.FromBase64String(parts[2]);
                var resume = _resumeKey != null;
                var valid = resume ? TrustKey.Verify(_resumeKey, _transcript, "resume-accept", proof) : AccessProof.Verify(_submittedCode, _transcript, "accept", proof);
                if (!valid) throw new InvalidDataException("Access approval proof is invalid.");
                var trustKey = resume ? _resumeKey : TrustKey.Derive(_submittedCode, _transcript);
                _transport.SetInputDirection(true, false); _transport.MarkPaired(); _completed = true; PairingCompleted?.Invoke(CreatePin(_remoteDeviceId, _remoteFingerprint, _remoteName, trustKey, resume)); return;
            }
            Fail("Pedido de acesso negado.");
        }
        private async Task SendRequestProofAsync()
        {
            var proof = _resumeKey != null ? TrustKey.Prove(_resumeKey, _transcript, "resume-request") : AccessProof.Create(_submittedCode, _transcript, "request");
            try { await _transport.SendAsync(FrameType.PairingOffer, Encoding.UTF8.GetBytes("proof|" + Convert.ToBase64String(proof)), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private async Task SendAcceptAsync(byte[] acceptProof)
        {
            try { await _transport.SendAsync(FrameType.PairingConfirm, Encoding.UTF8.GetBytes("accept|" + _config.DeviceId + "|" + Convert.ToBase64String(acceptProof)), _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private async Task SendDenyAsync(string reason) { try { await _transport.SendAsync(FrameType.Error, Encoding.UTF8.GetBytes(reason), _cts.Token).ConfigureAwait(false); _transport.Disconnect("access denied"); } catch (Exception ex) { Fail(ex.Message); } }
        private PinRecord CreatePin(string deviceId, string fingerprint, string displayName, byte[] trustKey, bool recognized) { var material = Encoding.UTF8.GetBytes("winput-lan/access-v2|" + _config.DeviceId + "|" + deviceId + "|" + fingerprint); string digest; using (var sha = SHA256.Create()) digest = Convert.ToBase64String(sha.ComputeHash(material)); return new PinRecord { DeviceId = deviceId, CertificateFingerprint = fingerprint, TranscriptDigest = digest, CreatedUtc = DateTime.UtcNow, DisplayName = displayName, TrustKey = trustKey, Recognized = recognized }; }
        private void NewAccessCode() { _accessCode = WinputLan.Core.AccessCode.Generate(); _accessCodeIssuedUtc = DateTime.UtcNow; _failedCodeAttempts = 0; _blockedUntilUtc = DateTime.MinValue; }
        private void RegisterFailedAttempt() { if (++_failedCodeAttempts >= 3) { _failedCodeAttempts = 0; _blockedUntilUtc = DateTime.UtcNow.AddSeconds(30); } }
        private void Fail(string reason) { if (!_completed) PairingFailed?.Invoke(reason); _transport.Disconnect("access request failed"); }
        private static void NewNonce(out byte[] value) { value = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(value); }
        private static string EncodeName(string name) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(name ?? string.Empty)); }
        private static string DecodeName(string encoded)
        {
            string name; try { name = Encoding.UTF8.GetString(Convert.FromBase64String(encoded ?? string.Empty)); } catch { throw new InvalidDataException("Peer name is invalid."); }
            if (name.Length > 64 || name.IndexOfAny(new[] { '\r', '\n', '|' }) >= 0) throw new InvalidDataException("Peer name is invalid.");
            return name;
        }
    }
}
