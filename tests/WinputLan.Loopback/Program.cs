using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan.Loopback
{
    internal static class Program
    {
        private static void Main()
        {
            RunAsync().GetAwaiter().GetResult();
        }

        private static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "WinputLan-loopback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var serverConfig = WinputConfig.CreateDefault();
                serverConfig.DeviceId = Guid.NewGuid().ToString("N");
                serverConfig.DisplayName = "loopback-server";
                serverConfig.ListenPort = FindPort();
                var clientConfig = WinputConfig.CreateDefault();
                clientConfig.DeviceId = Guid.NewGuid().ToString("N");
                clientConfig.DisplayName = "loopback-client";
                clientConfig.ListenPort = FindPort();
                var serverCertificate = new CertificateManager(Path.Combine(root, "server"), new DpapiSecretProtector()).LoadOrCreate();
                var clientCertificate = new CertificateManager(Path.Combine(root, "client"), new DpapiSecretProtector()).LoadOrCreate();
                Console.WriteLine("certs server-private=" + serverCertificate.HasPrivateKey + " client-private=" + clientCertificate.HasPrivateKey);
                using (var server = new PeerTransport())
                using (var client = new PeerTransport())
                using (var serverPairing = new PairingCoordinator(server, serverConfig, serverCertificate))
                using (var clientPairing = new PairingCoordinator(client, clientConfig, clientCertificate))
                {
                    var sink = new RecordingSink();
                    using (var receiver = new PairedInputReceiver(server, sink))
                    {
                    receiver.InputAudited += (kind, status) => Console.WriteLine("receiver=" + kind + ":" + status + " state=" + server.State);
                    Console.WriteLine("stage=coordinators");
                    server.StateChanged += (state, detail) => Console.WriteLine("server-state=" + state + " detail=" + detail);
                    client.StateChanged += (state, detail) => Console.WriteLine("client-state=" + state + " detail=" + detail);
                    server.FrameReceived += frame => Console.WriteLine("server-frame=" + frame.Type);
                    client.FrameReceived += frame => Console.WriteLine("client-frame=" + frame.Type);
                    var serverCode = new TaskCompletionSource<string>();
                    var clientCode = new TaskCompletionSource<string>();
                    var serverComplete = new TaskCompletionSource<PinRecord>();
                    var clientComplete = new TaskCompletionSource<PinRecord>();
                    serverPairing.CodeReady += code => serverCode.TrySetResult(code);
                    clientPairing.CodeReady += code => clientCode.TrySetResult(code);
                    serverPairing.PairingFailed += reason => Console.WriteLine("server-pairing-error=" + reason);
                    clientPairing.PairingFailed += reason => Console.WriteLine("client-pairing-error=" + reason);
                    serverPairing.PairingCompleted += record => serverComplete.TrySetResult(record);
                    clientPairing.PairingCompleted += record => clientComplete.TrySetResult(record);
                    var listenTask = server.ListenOnceAsync(serverConfig.ListenPort, serverCertificate, null, true, cancellation.Token);
                    _ = listenTask.ContinueWith(t => Console.WriteLine("listener-error=" + (t.Exception == null ? "unknown" : t.Exception.GetBaseException().Message)), TaskContinuationOptions.OnlyOnFaulted);
                    Console.WriteLine("stage=listener-started port=" + serverConfig.ListenPort);
                    await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("stage=before-connect");
                    var connectTask = client.ConnectAsync("127.0.0.1", serverConfig.ListenPort, clientCertificate, null, true, cancellation.Token);
                    if (await Task.WhenAny(connectTask, Task.Delay(5000, cancellation.Token)).ConfigureAwait(false) != connectTask) throw new TimeoutException("TLS client connect timed out.");
                    await connectTask.ConfigureAwait(false);
                    Console.WriteLine("stage=connected");
                    await client.SendAsync(FrameType.Input, FrameCodec.EncodeInput(InputEvent.Key(InputKind.KeyDown, 0x42, 0x30, 0, DateTime.UtcNow.Ticks)), cancellation.Token).ConfigureAwait(false);
                    await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
                    if (sink.Count != 0) throw new InvalidOperationException("Input was accepted before bilateral pairing.");
                    var codes = await WaitForCodesAsync(serverCode.Task, clientCode.Task, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("stage=codes");
                    if (codes[0] != codes[1]) throw new InvalidOperationException("SAS mismatch across loopback peers.");
                    serverPairing.ConfirmLocal(codes[0]);
                    clientPairing.ConfirmLocal(codes[1]);
                    var records = await WaitForPinsAsync(serverComplete.Task, clientComplete.Task, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("stage=paired");
                    if (records.Any(r => r == null || string.IsNullOrWhiteSpace(r.CertificateFingerprint))) throw new InvalidOperationException("Pin record missing after bilateral confirmation.");
                    await client.SendAsync(FrameType.Input, FrameCodec.EncodeInput(InputEvent.Key(InputKind.KeyDown, 0x41, 0x1E, 0, DateTime.UtcNow.Ticks)), cancellation.Token).ConfigureAwait(false);
                    await sink.WaitForCountAsync(1, cancellation.Token).ConfigureAwait(false);
                    if (sink.Last.Kind != InputKind.KeyDown || sink.Last.VirtualKey != 0x41) throw new InvalidOperationException("Paired receiver did not publish synthetic input.");
                    using (var sender = new InputRouter(new InputEventQueue(), client, new RecordingFailSafeSink()))
                    {
                        sender.SetRemoteActive(true);
                        if (!sender.Publish(InputEvent.Key(InputKind.KeyDown, 0x43, 0x2e, 0, DateTime.UtcNow.Ticks))) throw new InvalidOperationException("Outgoing router did not accept key before local switch.");
                        await sink.WaitForCountAsync(2, cancellation.Token).ConfigureAwait(false);
                        sender.SetRemoteActive(false);
                        await sink.WaitForReleaseAsync(cancellation.Token).ConfigureAwait(false);
                    }
                    var releasesBeforeDisconnect = sink.Releases;
                    await client.SendAsync(FrameType.Input, FrameCodec.EncodeInput(InputEvent.Key(InputKind.KeyDown, 0x44, 0x20, 0, DateTime.UtcNow.Ticks)), cancellation.Token).ConfigureAwait(false);
                    await client.SendAsync(FrameType.Hello, System.Text.Encoding.UTF8.GetBytes(clientConfig.DeviceId + "|" + new string('0', 64) + "|" + Convert.ToBase64String(new byte[32])), cancellation.Token).ConfigureAwait(false);
                    await WaitForOfflineAsync(server, cancellation.Token).ConfigureAwait(false);
                    if (sink.Releases <= releasesBeforeDisconnect) throw new InvalidOperationException("Disconnect did not release remote receiver state.");
                    await listenTask.ConfigureAwait(false);
                    var retryServerCode = new TaskCompletionSource<string>();
                    var retryClientCode = new TaskCompletionSource<string>();
                    serverPairing.CodeReady += code => retryServerCode.TrySetResult(code);
                    clientPairing.CodeReady += code => retryClientCode.TrySetResult(code);
                    var restartListen = server.ListenOnceAsync(serverConfig.ListenPort, serverCertificate, null, true, cancellation.Token);
                    await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
                    await client.ConnectAsync("127.0.0.1", serverConfig.ListenPort, clientCertificate, null, true, cancellation.Token).ConfigureAwait(false);
                    await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("restart-state server=" + server.State + " client=" + client.State);
                    var retryCodes = await WaitForCodesAsync(retryServerCode.Task, retryClientCode.Task, cancellation.Token).ConfigureAwait(false);
                    if (retryCodes[0] == codes[0] || server.State != PeerConnectionState.Pairing || client.State != PeerConnectionState.Pairing) throw new InvalidOperationException("Pairing did not reset with a fresh retry session.");
                    server.Disconnect("restart verified");
                    await restartListen.ConfigureAwait(false);
                    }
                }
                await VerifyHeartbeatFailSafeAsync(serverCertificate, clientCertificate, cancellation.Token).ConfigureAwait(false);
                VerifySignerContinuity();
                Console.WriteLine("LOOPBACK PASS: paired, bilateral SAS confirmed, pin material emitted, synthetic input frame ordered");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static async Task<string[]> WaitForCodesAsync(Task<string> left, Task<string> right, CancellationToken cancellationToken)
        {
            var all = Task.WhenAll(left, right);
            var completed = await Task.WhenAny(all, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (completed != all) throw new TimeoutException("Pairing codes were not received before cancellation.");
            return await all.ConfigureAwait(false);
        }

        private static async Task<PinRecord[]> WaitForPinsAsync(Task<PinRecord> left, Task<PinRecord> right, CancellationToken cancellationToken)
        {
            var all = Task.WhenAll(left, right);
            var completed = await Task.WhenAny(all, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (completed != all) throw new TimeoutException("Pairing completion was not received before cancellation.");
            return await all.ConfigureAwait(false);
        }

        private static int FindPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            try { listener.Start(); return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static async Task WaitForOfflineAsync(PeerTransport transport, CancellationToken cancellationToken)
        {
            while (transport.State != PeerConnectionState.Offline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task VerifyHeartbeatFailSafeAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate, System.Security.Cryptography.X509Certificates.X509Certificate2 clientCertificate, CancellationToken cancellationToken)
        {
            var port = FindPort();
            using (var server = new PeerTransport(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(250), false))
            using (var client = new PeerTransport(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(250)))
            {
                var release = new RecordingFailSafeSink();
                using (var router = new InputRouter(new InputEventQueue(), client, release))
                {
                    var listen = server.ListenOnceAsync(port, serverCertificate, CertificateManager.Fingerprint(clientCertificate), false, cancellationToken);
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    await client.ConnectAsync("127.0.0.1", port, clientCertificate, CertificateManager.Fingerprint(serverCertificate), false, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(75, cancellationToken).ConfigureAwait(false);
                    router.SetRemoteActive(true);
                    if (!router.Publish(InputEvent.Key(InputKind.KeyDown, 0x41, 0x1e, 0, DateTime.UtcNow.Ticks))) throw new InvalidOperationException("Router was not active before heartbeat failure.");
                    await Task.Delay(650, cancellationToken).ConfigureAwait(false);
                    if (client.State != PeerConnectionState.Offline || router.Publish(InputEvent.Key(InputKind.KeyDown, 0x41, 0x1e, 0, DateTime.UtcNow.Ticks)) || release.Releases == 0) throw new InvalidOperationException("Heartbeat timeout did not restore local-safe routing.");
                    server.Disconnect("heartbeat test complete");
                    await listen.ConfigureAwait(false);
                }
            }
        }

        private static void VerifySignerContinuity()
        {
            if (!GitHubUpdater.HasMatchingSigner("ABC", "abc") || GitHubUpdater.HasMatchingSigner("ABC", "DEF") || GitHubUpdater.HasMatchingSigner(null, "ABC")) throw new InvalidOperationException("Updater signer continuity policy is not fail-closed.");
        }

        private sealed class RecordingSink : IFailSafeInputSink
        {
            private readonly TaskCompletionSource<InputEvent> _received = new TaskCompletionSource<InputEvent>();
            private readonly TaskCompletionSource<bool> _released = new TaskCompletionSource<bool>();
            public int Count { get; private set; }
            public int Releases { get; private set; }
            public InputEvent Last { get; private set; }
            public bool Publish(InputEvent value) { Count++; Last = value; _received.TrySetResult(value); return true; }
            public async Task WaitForCountAsync(int count, CancellationToken cancellationToken)
            {
                while (Count < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
            }
            public void ReleaseAll() { Releases++; _released.TrySetResult(true); }
            public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
            {
                var done = await Task.WhenAny(_released.Task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                if (done != _released.Task) throw new TimeoutException("Input receiver did not receive release control.");
            }
        }

        private sealed class RecordingFailSafeSink : IFailSafeInputSink
        {
            public int Releases { get; private set; }
            public bool Publish(InputEvent value) { return true; }
            public void ReleaseAll() { Releases++; }
        }
    }
}
