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
                    var codes = await WaitForCodesAsync(serverCode.Task, clientCode.Task, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("stage=codes");
                    if (codes[0] != codes[1]) throw new InvalidOperationException("SAS mismatch across loopback peers.");
                    serverPairing.ConfirmLocal(codes[0]);
                    clientPairing.ConfirmLocal(codes[1]);
                    var records = await WaitForPinsAsync(serverComplete.Task, clientComplete.Task, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("stage=paired");
                    if (records.Any(r => r == null || string.IsNullOrWhiteSpace(r.CertificateFingerprint))) throw new InvalidOperationException("Pin record missing after bilateral confirmation.");
                    var received = new TaskCompletionSource<InputEvent>();
                    server.FrameReceived += frame => { if (frame.Type == FrameType.Input) received.TrySetResult(FrameCodec.DecodeInput(frame.Payload)); };
                    await client.SendAsync(FrameType.Input, FrameCodec.EncodeInput(InputEvent.Key(InputKind.KeyDown, 0x41, 0x1E, 0, DateTime.UtcNow.Ticks)), cancellation.Token).ConfigureAwait(false);
                    var input = await received.Task.ConfigureAwait(false);
                    if (input.Kind != InputKind.KeyDown || input.VirtualKey != 0x41) throw new InvalidOperationException("Synthetic input frame did not round-trip.");
                    server.Disconnect("loopback complete");
                    await listenTask.ConfigureAwait(false);
                }
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
    }
}
