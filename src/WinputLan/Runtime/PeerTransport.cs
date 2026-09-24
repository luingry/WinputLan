using System;
using System.Diagnostics;
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
        private readonly TimeSpan _handshakeTimeout;
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private TcpClient _pendingClient;
        private CancellationTokenSource _pendingCts;
        private TcpClient _client;
        private SslStream _stream;
        private CancellationTokenSource _connectionCts;
        private long _sequence;
        private long _generation;
        private ulong _lastReceivedSequence;
        private long _lastHeartbeatAckTick;
        private bool _disposed;
        private volatile bool _allowsInputSend;
        private volatile bool _allowsInputReceive;

        public PeerTransport() : this(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9), true) { }
        public PeerTransport(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout) : this(heartbeatInterval, heartbeatTimeout, true) { }
        public PeerTransport(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, bool respondToHeartbeats)
            : this(heartbeatInterval, heartbeatTimeout, respondToHeartbeats, TimeSpan.FromSeconds(10)) { }
        public PeerTransport(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, bool respondToHeartbeats, TimeSpan handshakeTimeout)
        {
            if (heartbeatInterval <= TimeSpan.Zero || heartbeatTimeout <= heartbeatInterval) throw new ArgumentOutOfRangeException("heartbeatInterval");
            if (handshakeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("handshakeTimeout");
            _handshakeTimeout = handshakeTimeout;
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
            using (var attempt = RegisterAttempt(client, lease, cancellationToken))
            using (attempt.Token.Register(() => CloseTransport(client)))
            try
            {
                attempt.Token.ThrowIfCancellationRequested();
                await client.ConnectAsync(host, port).ConfigureAwait(false);
                attempt.Token.ThrowIfCancellationRequested();
                var stream = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) => ValidateCertificate(certificate, pinnedFingerprint, pairingMode));
                await stream.AuthenticateAsClientAsync(host, new X509CertificateCollection { localCertificate }, SslProtocols.Tls12, false).ConfigureAwait(false);
                attempt.Token.ThrowIfCancellationRequested();
                long generation;
                if (!TryStartConnection(client, stream, pairingMode, cancellationToken, true, lease, out generation)) throw new OperationCanceledException("Connection attempt was superseded.", cancellationToken);
            }
            catch (Exception) when (attempt.IsCancellationRequested)
            {
                CloseTransport(client);
                CancelConnectionAttempt(lease);
                throw new OperationCanceledException("Connection cancelled or handshake timed out.", attempt.Token);
            }
            catch
            {
                CloseTransport(client);
                FailConnectionAttempt(lease);
                throw;
            }
            finally { ClearAttempt(client); }
        }

        // Occupies the call while its accepted peer is alive, allowing a caller to restart listening safely.
        public async Task ListenOnceAsync(int port, X509Certificate2 localCertificate, string pinnedFingerprint, bool pairingMode, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start(1);
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token))
            using (lifetime.Token.Register(listener.Stop))
            try
            {
                SetState(PeerConnectionState.Connecting, "Aguardando conexão");
                using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var lease = BeginConnectionAttempt(pairingMode);
                    client.NoDelay = true;
                    long generation;
                    SslStream stream;
                    using (var attempt = RegisterAttempt(client, lease, lifetime.Token))
                    using (attempt.Token.Register(() => CloseTransport(client)))
                    {
                        try
                        {
                            stream = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) => ValidateCertificate(certificate, pinnedFingerprint, pairingMode));
                            await stream.AuthenticateAsServerAsync(localCertificate, true, SslProtocols.Tls12, false).ConfigureAwait(false);
                            attempt.Token.ThrowIfCancellationRequested();
                            if (!TryStartConnection(client, stream, pairingMode, lifetime.Token, false, lease, out generation)) throw new OperationCanceledException(lifetime.Token);
                        }
                        catch
                        {
                            FailConnectionAttempt(lease);
                            // A timed-out peer must not stop the outer listener retry loop.
                            if (!lifetime.IsCancellationRequested) throw new IOException("TLS handshake failed, cancelled or timed out.");
                            throw new OperationCanceledException(lifetime.Token);
                        }
                        finally { ClearAttempt(client); }
                    }
                    CancellationToken receiveToken;
                    lock (_gate) { if (generation != _generation || _connectionCts == null) return; receiveToken = _connectionCts.Token; }
                    await ReceiveLoopAsync(stream, generation, receiveToken).ConfigureAwait(false);
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { throw new OperationCanceledException(lifetime.Token); }
            finally { listener.Stop(); }
        }

        private CancellationTokenSource RegisterAttempt(TcpClient client, long lease, CancellationToken token)
        {
            var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeCts.Token);
            attempt.CancelAfter(_handshakeTimeout);
            lock (_gate)
            {
                if (_disposed || lease != _generation) { attempt.Dispose(); CloseTransport(client); throw new OperationCanceledException(token); }
                _pendingClient = client;
                _pendingCts = attempt;
            }
            return attempt;
        }

        private void ClearAttempt(TcpClient client)
        {
            lock (_gate) if (ReferenceEquals(_pendingClient, client)) { _pendingClient = null; _pendingCts = null; }
        }

        public async Task SendAsync(FrameType type, byte[] payload, CancellationToken cancellationToken)
        {
            await SendIfCurrentAsync(type, payload, null, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<bool> SendIfCurrentAsync(FrameType type, byte[] payload, Func<bool> isCurrent, CancellationToken cancellationToken)
        {
            if (type == FrameType.Input && !_allowsInputSend) throw new InvalidOperationException("This session is not permitted to send input.");
            SslStream stream;
            CancellationToken connectionToken;
            long generation;
            lock (_gate) { stream = _stream; generation = _generation; connectionToken = _connectionCts == null ? CancellationToken.None : _connectionCts.Token; }
            if (stream == null) throw new IOException("Peer is not connected.");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionToken))
            {
                await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
                // The sequence is taken inside the send gate so wire order always matches sequence order;
                // numbering first let concurrent senders (heartbeat, input, ACK) reach the wire out of order,
                // which the receiver rejects by closing the connection.
                // SslStream.WriteAsync commits the complete TLS record; FlushAsync added a scheduler hop without improving delivery.
                try
                {
                    lock (_gate) if (generation != _generation || !ReferenceEquals(stream, _stream)) throw new IOException("Connection was superseded.");
                    if (isCurrent != null && !isCurrent()) return false;
                    if (type == FrameType.Input && !_allowsInputSend) throw new IOException("Input direction changed.");
                    var bytes = FrameCodec.Encode(type, unchecked((ulong)Interlocked.Increment(ref _sequence)), payload);
                    await stream.WriteAsync(bytes, 0, bytes.Length, linked.Token).ConfigureAwait(false);
                    return true;
                }
                finally { _sendGate.Release(); }
            }
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
            TcpClient pending;
            CancellationTokenSource pendingCts;
            lock (_gate)
            {
                _generation++;
                client = _client; cts = _connectionCts;
                pending = _pendingClient; pendingCts = _pendingCts; _pendingClient = null; _pendingCts = null;
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null; RemoteEndpoint = null;
                _allowsInputSend = false; _allowsInputReceive = false;
            }
            CancelSafely(pendingCts);
            CloseTransport(pending);
            CancelSafely(cts);
            cts?.Dispose();
            CloseTransport(client);
            SetState(PeerConnectionState.Offline, reason ?? "desconectado");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCts.Cancel();
            Disconnect("encerrado");
            // In-flight writes still release the semaphore during cancellation.
        }

        private long BeginConnectionAttempt(bool pairingMode)
        {
            TcpClient client;
            CancellationTokenSource cts;
            TcpClient pending;
            CancellationTokenSource pendingCts;
            long lease;
            lock (_gate)
            {
                lease = ++_generation;
                client = _client; cts = _connectionCts;
                pending = _pendingClient; pendingCts = _pendingCts; _pendingClient = null; _pendingCts = null;
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null; RemoteEndpoint = null;
                _allowsInputSend = false; _allowsInputReceive = false;
            }
            CancelSafely(pendingCts);
            CloseTransport(pending);
            CancelSafely(cts);
            cts?.Dispose();
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
                // Framework stream reads/writes cannot reliably be cancelled without closing TCP.
                _connectionCts.Token.Register(() => CloseTransport(client));
                _lastReceivedSequence = 0; _lastHeartbeatAckTick = Stopwatch.GetTimestamp();
                ObservedRemoteFingerprint = observed; RemoteEndpoint = client.Client.RemoteEndPoint == null ? null : client.Client.RemoteEndPoint.ToString(); generation = lease == 0 ? ++_generation : lease;
                _allowsInputSend = false; _allowsInputReceive = false;
                token = _connectionCts.Token;
            }
            if (lease == 0) SetState(pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo");
            else if (!SetStateForLease(lease, pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo")) return false;
            var publishedGeneration = generation;
            _ = Task.Run(() => RunHeartbeatAsync(publishedGeneration, token));
            _ = Task.Run(() => WatchHeartbeatAsync(publishedGeneration, token));
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
                    lock (_gate) if (generation != _generation) return;
                    await SendAsync(FrameType.Heartbeat, new byte[0], cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { EndGeneration(generation, "heartbeat send failed", true); }
        }

        private async Task WatchHeartbeatAsync(long generation, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_heartbeatInterval, token).ConfigureAwait(false);
                    long lastAck;
                    lock (_gate) { if (generation != _generation) return; lastAck = _lastHeartbeatAckTick; }
                    if ((Stopwatch.GetTimestamp() - lastAck) / (double)Stopwatch.Frequency > _heartbeatTimeout.TotalSeconds) { EndGeneration(generation, "heartbeat timeout", true); return; }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
                    else if (frame.Type == FrameType.HeartbeatAck) { lock (_gate) if (generation == _generation) _lastHeartbeatAckTick = Stopwatch.GetTimestamp(); }
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
            CancelSafely(cts);
            cts?.Dispose();
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
        private static void CancelSafely(CancellationTokenSource cts) { try { cts?.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
