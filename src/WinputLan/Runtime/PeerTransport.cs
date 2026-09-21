using System;
using System.Collections.Concurrent;
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
        private TcpClient _client;
        private SslStream _stream;
        private CancellationTokenSource _connectionCts;
        private long _sequence;
        private ulong _lastReceivedSequence;
        private bool _disposed;

        public PeerConnectionState State { get; private set; }
        public string StateDetail { get; private set; }
        public event Action<Frame> FrameReceived;
        public event Action<PeerConnectionState, string> StateChanged;

        public PeerTransport() { SetState(PeerConnectionState.Offline, "Aguardando destino"); }

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
                var clientCertificates = new X509CertificateCollection { localCertificate };
                await stream.AuthenticateAsClientAsync(host, clientCertificates, SslProtocols.Tls12, false).ConfigureAwait(false);
                lock (_gate)
                {
                    _client = client;
                    _stream = stream;
                    _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _lastReceivedSequence = 0;
                }
                SetState(pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo");
                _ = Task.Run(() => RunHeartbeatAsync(_connectionCts.Token));
                _ = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token));
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is AuthenticationException || ex is InvalidDataException || ex is OperationCanceledException)
            {
                SetState(PeerConnectionState.Reconnecting, "Conexão perdida");
                CloseTransport(client);
                throw;
            }
        }

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
                lock (_gate)
                {
                    _client = client;
                    _stream = stream;
                    _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _lastReceivedSequence = 0;
                }
                SetState(pairingMode ? PeerConnectionState.Pairing : PeerConnectionState.Connected, "TLS ativo");
                _ = Task.Run(() => RunHeartbeatAsync(_connectionCts.Token));
                _ = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token));
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

        public async Task RunHeartbeatAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                try { await SendAsync(FrameType.Heartbeat, new byte[0], cancellationToken).ConfigureAwait(false); }
                catch { return; }
            }
        }

        public void MarkPaired()
        {
            if (State == PeerConnectionState.Pairing) SetState(PeerConnectionState.Connected, "TLS ativo · peer pinned");
        }

        public void Disconnect(string reason)
        {
            CancellationTokenSource cts;
            TcpClient client;
            lock (_gate) { cts = _connectionCts; client = _client; _connectionCts = null; _client = null; _stream = null; }
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

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            SslStream stream;
            lock (_gate) stream = _stream;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = FrameCodec.Read(stream);
                    if (frame.Sequence <= _lastReceivedSequence) throw new InvalidDataException("Frame sequence is not strictly increasing.");
                    _lastReceivedSequence = frame.Sequence;
                    if (frame.Type == FrameType.Heartbeat) await SendAsync(FrameType.HeartbeatAck, new byte[0], cancellationToken).ConfigureAwait(false);
                    else FrameReceived?.Invoke(frame);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is AuthenticationException || ex is SocketException || ex is ObjectDisposedException)
            {
                if (!cancellationToken.IsCancellationRequested && !(ex is EndOfStreamException)) SetState(PeerConnectionState.Faulted, ex.Message);
            }
            finally
            {
                Disconnect("peer closed");
            }
        }

        private static bool ValidateCertificate(X509Certificate certificate, string pinnedFingerprint, bool pairingMode)
        {
            if (certificate == null) return false;
            if (pairingMode && string.IsNullOrWhiteSpace(pinnedFingerprint)) return true;
            if (string.IsNullOrWhiteSpace(pinnedFingerprint)) return false;
            using (var cert = new X509Certificate2(certificate)) return string.Equals(CertificateManager.Fingerprint(cert), pinnedFingerprint, StringComparison.OrdinalIgnoreCase);
        }

        private void SetState(PeerConnectionState state, string detail)
        {
            State = state;
            StateDetail = detail;
            StateChanged?.Invoke(state, detail);
        }

        private static void CloseTransport(TcpClient client)
        {
            try { if (client != null) client.Close(); } catch { }
        }
    }
}
