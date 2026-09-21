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
        public event Action<Frame> FrameReceived;
        public event Action<PeerConnectionState, string> StateChanged;

        public async Task ConnectAsync(string host, int port, X509Certificate2 localCertificate, string pinnedFingerprint, bool pairingMode, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", "host");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            Disconnect("replacing connection");
            SetState(pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connecting, "Conectando");
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(host, port).ConfigureAwait(false);
                var stream = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) => ValidateCertificate(certificate, pinnedFingerprint, pairingMode));
                await stream.AuthenticateAsClientAsync(host, new X509CertificateCollection { localCertificate }, SslProtocols.Tls12, false).ConfigureAwait(false);
                StartConnection(client, stream, pairingMode, cancellationToken, true);
            }
            catch
            {
                CloseTransport(client);
                SetState(PeerConnectionState.Reconnecting, "Conexão perdida");
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
                var generation = StartConnection(client, stream, pairingMode, cancellationToken, false);
                CancellationToken receiveToken;
                lock (_gate) receiveToken = _connectionCts.Token;
                await ReceiveLoopAsync(stream, generation, receiveToken).ConfigureAwait(false);
            }
            finally { listener.Stop(); }
        }

        public async Task SendAsync(FrameType type, byte[] payload, CancellationToken cancellationToken)
        {
            SslStream stream;
            lock (_gate) stream = _stream;
            if (stream == null) throw new IOException("Peer is not connected.");
            var bytes = FrameCodec.Encode(type, unchecked((ulong)Interlocked.Increment(ref _sequence)), payload);
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
            finally { _sendGate.Release(); }
        }

        public void MarkPaired() { if (State == PeerConnectionState.Pairing) SetState(PeerConnectionState.Connected, "TLS ativo · peer pinned"); }

        public void Disconnect(string reason)
        {
            TcpClient client;
            CancellationTokenSource cts;
            lock (_gate)
            {
                _generation++;
                client = _client; cts = _connectionCts;
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null;
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

        private long StartConnection(TcpClient client, SslStream stream, bool pairingMode, CancellationToken cancellationToken, bool receiveInBackground)
        {
            if (stream.RemoteCertificate == null) throw new AuthenticationException("TLS peer did not present a certificate.");
            var observed = CertificateManager.Fingerprint(new X509Certificate2(stream.RemoteCertificate));
            long generation;
            lock (_gate)
            {
                _client = client; _stream = stream;
                _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _lastReceivedSequence = 0; _lastHeartbeatAckUtc = DateTime.UtcNow;
                ObservedRemoteFingerprint = observed; generation = ++_generation;
            }
            SetState(pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo");
            var token = _connectionCts.Token;
            _ = Task.Run(() => RunHeartbeatAsync(generation, token));
            if (receiveInBackground) _ = Task.Run(() => ReceiveLoopAsync(stream, generation, token));
            return generation;
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
                _client = null; _stream = null; _connectionCts = null; ObservedRemoteFingerprint = null;
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

        private void SetState(PeerConnectionState state, string detail) { State = state; StateDetail = detail; StateChanged?.Invoke(state, detail); }
        private static void CloseTransport(TcpClient client) { try { if (client != null) client.Close(); } catch { } }
    }
}
