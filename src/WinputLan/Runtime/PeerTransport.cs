using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public enum PeerConnectionState { Offline, Connecting, Pairing, Connected, Reconnecting, Faulted }

    public sealed class PeerTransport : IDisposable
    {
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeSpan _heartbeatTimeout;
        private readonly bool _respondToHeartbeats;
        private TcpClient _client;
        private SslStream _stream;
        private CancellationTokenSource _connectionCts;
        private long _sequence;
        private long _generation;
        private ulong _lastReceivedSequence;
        private DateTime _lastHeartbeatAckUtc;
        private bool _disposed;
        private volatile bool _allowsInputSend;
        private volatile bool _allowsInputReceive;

        public PeerTransport() : this(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), true) { }
        public PeerTransport(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout) : this(heartbeatInterval, heartbeatTimeout, true) { }
        public PeerTransport(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, bool respondToHeartbeats)
        {
            if (heartbeatInterval <= TimeSpan.Zero || heartbeatTimeout <= heartbeatInterval) throw new ArgumentOutOfRangeException("heartbeatInterval");
            _heartbeatInterval = heartbeatInterval;
            _heartbeatTimeout = heartbeatTimeout;
            _respondToHeartbeats = respondToHeartbeats;
            SetState(PeerConnectionState.Offline, "Aguardando destino");
        }

        public PeerConnectionState State { get; private set; }
        public string StateDetail { get; private set; }
        public string ObservedRemoteFingerprint { get; private set; }
        public string RemoteEndpoint { get; private set; }
        public bool AllowsInputSend { get { return _allowsInputSend; } }
        public bool AllowsInputReceive { get { return _allowsInputReceive; } }
        public event Action<Frame> FrameReceived;
        public event Action<PeerConnectionState, string> StateChanged;

        public async Task ConnectAsync(string host, int port, X509Certificate2 localCertificate, string pinnedFingerprint, bool pairingMode, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", "host");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            var lease = BeginConnectionAttempt(pairingMode);
            var client = new TcpClient { NoDelay = true };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.ConnectAsync(host, port).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var stream = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) => ValidateCertificate(certificate, pinnedFingerprint, pairingMode));
                await stream.AuthenticateAsClientAsync(host, new X509CertificateCollection { localCertificate }, SslProtocols.Tls12, false).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                long generation;
                if (!TryStartConnection(client, stream, pairingMode, cancellationToken, true, lease, out generation)) throw new OperationCanceledException("Connection attempt was superseded.", cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CloseTransport(client);
                CancelConnectionAttempt(lease);
                throw;
            }
            catch
            {
                CloseTransport(client);
                FailConnectionAttempt(lease);
                throw;
            }
        }

        // Occupies the call while its accepted peer is alive, allowing a caller to restart listening safely.
        public async Task ListenOnceAsync(int port, X509Certificate2 localCertificate, string pinnedFingerprint, bool pairingMode, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start(1);
            try
            {
                SetState(PeerConnectionState.Connecting, "Aguardando conexão");
                var acceptTask = listener.AcceptTcpClientAsync();
                var completed = await Task.WhenAny(acceptTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                if (completed != acceptTask) throw new OperationCanceledException(cancellationToken);
                var client = await acceptTask.ConfigureAwait(false);
                client.NoDelay = true;
                var stream = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) => ValidateCertificate(certificate, pinnedFingerprint, pairingMode));
                await stream.AuthenticateAsServerAsync(localCertificate, true, SslProtocols.Tls12, false).ConfigureAwait(false);
                long generation;
                if (!TryStartConnection(client, stream, pairingMode, cancellationToken, false, 0, out generation)) throw new OperationCanceledException(cancellationToken);
                CancellationToken receiveToken;
                lock (_gate) receiveToken = _connectionCts.Token;
                await ReceiveLoopAsync(stream, generation, receiveToken).ConfigureAwait(false);
            }
            finally { listener.Stop(); }
        }

        public async Task SendAsync(FrameType type, byte[] payload, CancellationToken cancellationToken)
        {
            if (type == FrameType.Input && !_allowsInputSend) throw new InvalidOperationException("This session is not permitted to send input.");
            SslStream stream;
            lock (_gate) stream = _stream;
            if (stream == null) throw new IOException("Peer is not connected.");
            var bytes = FrameCodec.Encode(type, unchecked((ulong)Interlocked.Increment(ref _sequence)), payload);
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            // SslStream.WriteAsync commits the complete TLS record; FlushAsync added a scheduler hop without improving delivery.
            try { await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false); }
            finally { _sendGate.Release(); }
        }

        public void MarkPaired() { if (State == PeerConnectionState.Pairing) SetState(PeerConnectionState.Connected, "TLS ativo · peer pinned"); }

        public void SetInputDirection(bool maySendInput, bool mayReceiveInput)
        {
            _allowsInputSend = maySendInput;
            _allowsInputReceive = mayReceiveInput;
        }

        public void Disconnect(string reason)
        {
            TcpClient client;
            CancellationTokenSource cts;
            lock (_gate)
            {
                _generation++;
                client = _client; cts = _connectionCts;
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null; RemoteEndpoint = null;
                _allowsInputSend = false; _allowsInputReceive = false;
            }
            if (cts != null) cts.Cancel();
            CloseTransport(client);
            SetState(PeerConnectionState.Offline, reason ?? "desconectado");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect("encerrado");
            _sendGate.Dispose();
        }

        private long BeginConnectionAttempt(bool pairingMode)
        {
            TcpClient client;
            CancellationTokenSource cts;
            long lease;
            lock (_gate)
            {
                lease = ++_generation;
                client = _client; cts = _connectionCts;
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null; RemoteEndpoint = null;
                _allowsInputSend = false; _allowsInputReceive = false;
            }
            if (cts != null) cts.Cancel();
            CloseTransport(client);
            SetStateForLease(lease, pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connecting, "Conectando");
            return lease;
        }

        private void CancelConnectionAttempt(long lease)
        {
            lock (_gate)
            {
                if (lease != _generation) return;
                SetStateLocked(PeerConnectionState.Offline, "Conexão cancelada");
                _generation++;
            }
        }

        private void FailConnectionAttempt(long lease)
        {
            lock (_gate)
            {
                if (lease != _generation) return;
                SetStateLocked(PeerConnectionState.Reconnecting, "Conexão perdida");
                _generation++;
            }
        }

        private bool TryStartConnection(TcpClient client, SslStream stream, bool pairingMode, CancellationToken cancellationToken, bool receiveInBackground, long lease, out long generation)
        {
            if (stream.RemoteCertificate == null) throw new AuthenticationException("TLS peer did not present a certificate.");
            var observed = CertificateManager.Fingerprint(new X509Certificate2(stream.RemoteCertificate));
            CancellationToken token;
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested || (lease != 0 && lease != _generation)) { generation = 0; return false; }
                _client = client; _stream = stream;
                _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _lastReceivedSequence = 0; _lastHeartbeatAckUtc = DateTime.UtcNow;
                ObservedRemoteFingerprint = observed; RemoteEndpoint = client.Client.RemoteEndPoint == null ? null : client.Client.RemoteEndPoint.ToString(); generation = lease == 0 ? ++_generation : lease;
                _allowsInputSend = false; _allowsInputReceive = false;
                token = _connectionCts.Token;
            }
            if (lease == 0) SetState(pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo");
            else if (!SetStateForLease(lease, pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo")) return false;
            var publishedGeneration = generation;
            _ = Task.Run(() => RunHeartbeatAsync(publishedGeneration, token));
            if (receiveInBackground) _ = Task.Run(() => ReceiveLoopAsync(stream, publishedGeneration, token));
            return true;
        }

        private async Task RunHeartbeatAsync(long generation, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
                    DateTime lastAck;
                    lock (_gate) { if (generation != _generation) return; lastAck = _lastHeartbeatAckUtc; }
                    if (DateTime.UtcNow - lastAck > _heartbeatTimeout) { EndGeneration(generation, "heartbeat timeout", true); return; }
                    await SendAsync(FrameType.Heartbeat, new byte[0], cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { EndGeneration(generation, "heartbeat send failed", true); }
        }

        private async Task ReceiveLoopAsync(SslStream stream, long generation, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = FrameCodec.Read(stream);
                    lock (_gate)
                    {
                        if (generation != _generation) return;
                        if (frame.Sequence <= _lastReceivedSequence) throw new InvalidDataException("Frame sequence is not strictly increasing.");
                        _lastReceivedSequence = frame.Sequence;
                    }
                    if (frame.Type == FrameType.Heartbeat && _respondToHeartbeats) await SendAsync(FrameType.HeartbeatAck, new byte[0], cancellationToken).ConfigureAwait(false);
                    else if (frame.Type == FrameType.HeartbeatAck) { lock (_gate) if (generation == _generation) _lastHeartbeatAckUtc = DateTime.UtcNow; }
                    else FrameReceived?.Invoke(frame);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is AuthenticationException || ex is SocketException || ex is ObjectDisposedException)
            {
                if (!cancellationToken.IsCancellationRequested) EndGeneration(generation, ex is EndOfStreamException ? "peer closed" : ex.Message, true);
            }
            finally { EndGeneration(generation, "peer closed", false); }
        }

        private void EndGeneration(long generation, string detail, bool faulted)
        {
            TcpClient client = null;
            CancellationTokenSource cts = null;
            lock (_gate)
            {
                if (generation != _generation) return;
                _generation++;
                client = _client; cts = _connectionCts;
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null; RemoteEndpoint = null;
                _allowsInputSend = false; _allowsInputReceive = false;
            }
            if (cts != null) cts.Cancel();
            CloseTransport(client);
            if (faulted) SetState(PeerConnectionState.Faulted, detail);
            SetState(PeerConnectionState.Offline, detail);
        }

        private static bool ValidateCertificate(X509Certificate certificate, string pinnedFingerprint, bool pairingMode)
        {
            if (certificate == null) return false;
            if (pairingMode && string.IsNullOrWhiteSpace(pinnedFingerprint)) return true;
            if (string.IsNullOrWhiteSpace(pinnedFingerprint)) return false;
            using (var cert = new X509Certificate2(certificate)) return string.Equals(CertificateManager.Fingerprint(cert), pinnedFingerprint, StringComparison.OrdinalIgnoreCase);
        }

        private bool SetStateForLease(long lease, PeerConnectionState state, string detail)
        {
            lock (_gate)
            {
                if (lease != _generation || _disposed) return false;
                SetStateLocked(state, detail);
                return true;
            }
        }
        private void SetStateLocked(PeerConnectionState state, string detail) { State = state; StateDetail = detail; StateChanged?.Invoke(state, detail); }
        private void SetState(PeerConnectionState state, string detail) { State = state; StateDetail = detail; StateChanged?.Invoke(state, detail); }
        private static void CloseTransport(TcpClient client) { try { if (client != null) client.Close(); } catch { } }
    }
}
