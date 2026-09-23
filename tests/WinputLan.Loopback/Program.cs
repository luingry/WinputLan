using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan.Loopback
{
    internal static class Program
    {
        private static void Main() { RunAsync().GetAwaiter().GetResult(); }
        private static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "WinputLan-loopback-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    var targetCertificate = new CertificateManager(Path.Combine(root, "target"), new DpapiSecretProtector()).LoadOrCreate();
                    var controllerCertificate = new CertificateManager(Path.Combine(root, "controller"), new DpapiSecretProtector()).LoadOrCreate();
                    VerifyBackgroundPreferencePersistence(Path.Combine(root, "config"));
                    await InvalidCodeDoesNotPromptAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    await CertificateSubstitutionProofDoesNotPromptAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    await CancelledRequestDoesNotPromptAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    await SupersededConnectCannotPublishAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    await ValidCodeDeniedAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    var metrics = await ValidCodeAcceptedOneWayAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    var baseline = metrics[0]; var after = metrics[1];
                    Console.WriteLine("LATENCY baseline-poll p50={0:F2} p95={1:F2} max={2:F2}ms", baseline[0], baseline[1], baseline[2]);
                    Console.WriteLine("LATENCY signal-drain p50={0:F2} p95={1:F2} max={2:F2}ms", after[0], after[1], after[2]);
                    if (after[1] > 50) throw new InvalidOperationException("Loopback p95 input latency exceeded 50ms.");
                    // A second accepted session proves that a closed session releases the target for a fresh request.
                    await ValidCodeAcceptedOneWayAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    await VerifyHeartbeatFailSafeAsync(targetCertificate, controllerCertificate, cts.Token).ConfigureAwait(false);
                    Console.WriteLine("LOOPBACK PASS: invalid code hidden, deny recoverable, accept one-way, reconnect and <=50ms local ACK p95");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }
        }

        private static async Task InvalidCodeDoesNotPromptAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var port = FindPort(); var targetConfig = Config("target", port); var controllerConfig = Config("controller", port);
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            using (var targetPairing = new PairingCoordinator(target, targetConfig, targetCert, PairingRole.Target))
            using (var controllerPairing = new PairingCoordinator(controller, controllerConfig, controllerCert, PairingRole.Controller))
            {
                var prompted = false; targetPairing.AccessRequestReceived += _ => prompted = true;
                var listen = target.ListenOnceAsync(port, targetCert, null, true, token); await Task.Delay(50, token).ConfigureAwait(false);
                var wrongCode = targetPairing.AccessCode[0] == 'A' ? "B" + targetPairing.AccessCode.Substring(1) : "A" + targetPairing.AccessCode.Substring(1);
                controllerPairing.StartRequest(wrongCode); await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, token).ConfigureAwait(false);
                await WaitOfflineAsync(target, token).ConfigureAwait(false); await listen.ConfigureAwait(false);
                if (prompted) throw new InvalidOperationException("Invalid code reached target approval UI.");
            }
        }

        private static async Task CancelledRequestDoesNotPromptAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var port = FindPort(); var targetConfig = Config("target", port); var controllerConfig = Config("controller", port);
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            using (var targetPairing = new PairingCoordinator(target, targetConfig, targetCert, PairingRole.Target))
            using (var controllerPairing = new PairingCoordinator(controller, controllerConfig, controllerCert, PairingRole.Controller))
            {
                var prompt = new TaskCompletionSource<bool>(); var cancelled = new TaskCompletionSource<bool>(); targetPairing.AccessRequestReceived += _ => prompt.TrySetResult(true); targetPairing.PendingRequestCancelled += () => cancelled.TrySetResult(true);
                controllerPairing.StartRequest(targetPairing.AccessCode);
                var listen = target.ListenOnceAsync(port, targetCert, null, true, token); await Task.Delay(50, token).ConfigureAwait(false);
                await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, token).ConfigureAwait(false);
                await WaitAsync(prompt.Task, token, "cancelled request did not reach target").ConfigureAwait(false); controllerPairing.CancelRequest(); controller.Disconnect("cancelled request test"); await WaitAsync(cancelled.Task, token, "target was not notified that the pending request was cancelled").ConfigureAwait(false); await listen.ConfigureAwait(false);
                ExpectInvalidOperation(targetPairing.AcceptPending, "Cancelled request could still be accepted.");
                if (targetPairing.PendingRequest != null || controller.AllowsInputSend) throw new InvalidOperationException("Cancelled controller request remained actionable.");
            }
        }

        private static async Task CertificateSubstitutionProofDoesNotPromptAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var port = FindPort(); var targetConfig = Config("target", port); var controllerConfig = Config("controller", port);
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            using (var targetPairing = new PairingCoordinator(target, targetConfig, targetCert, PairingRole.Target))
            {
                var prompted = false; var challenge = new TaskCompletionSource<string>();
                targetPairing.AccessRequestReceived += _ => prompted = true;
                controller.FrameReceived += frame => { if (frame.Type == FrameType.PairingConfirm) challenge.TrySetResult(System.Text.Encoding.UTF8.GetString(frame.Payload)); };
                var listen = target.ListenOnceAsync(port, targetCert, null, true, token); await Task.Delay(50, token).ConfigureAwait(false);
                await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, token).ConfigureAwait(false);
                var controllerNonce = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
                var name = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("controller"));
                await controller.SendAsync(FrameType.PairingOffer, System.Text.Encoding.UTF8.GetBytes("offer|" + controllerConfig.DeviceId + "|" + name + "|" + Convert.ToBase64String(controllerNonce)), token).ConfigureAwait(false);
                var parts = (await WaitResultAsync(challenge.Task, token, "target challenge missing").ConfigureAwait(false)).Split('|');
                if (parts.Length != 3 || parts[0] != "challenge") throw new InvalidOperationException("Target emitted malformed challenge.");
                var targetNonce = Convert.FromBase64String(parts[2]);
                var substitutedTranscript = AccessProof.CanonicalTranscript(controllerConfig.DeviceId, targetConfig.DeviceId, CertificateManager.Fingerprint(controllerCert), "mitm-substituted-target-fingerprint", controllerNonce, targetNonce);
                var proof = AccessProof.Create(targetPairing.AccessCode, substitutedTranscript, "request");
                await controller.SendAsync(FrameType.PairingOffer, System.Text.Encoding.UTF8.GetBytes("proof|" + Convert.ToBase64String(proof)), token).ConfigureAwait(false);
                await WaitOfflineAsync(target, token).ConfigureAwait(false); await listen.ConfigureAwait(false);
                if (prompted) throw new InvalidOperationException("Certificate-substitution proof reached target approval UI.");
            }
        }

        private static async Task SupersededConnectCannotPublishAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var delayedPort = FindPort(); var activePort = FindPort();
            var delayedListener = new TcpListener(IPAddress.Loopback, delayedPort); delayedListener.Start(1);
            var acceptedDelayedClient = new TaskCompletionSource<TcpClient>(); var releaseDelayedTls = new TaskCompletionSource<bool>();
            var delayedServer = Task.Run(async () =>
            {
                using (var client = await delayedListener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    acceptedDelayedClient.TrySetResult(client);
                    await releaseDelayedTls.Task.ConfigureAwait(false);
                    using (var stream = new SslStream(client.GetStream(), false))
                    {
                        try { await stream.AuthenticateAsServerAsync(targetCert, true, SslProtocols.Tls12, false).ConfigureAwait(false); }
                        catch (IOException) { }
                        catch (AuthenticationException) { }
                    }
                }
            });
            try
            {
                using (var controller = new PeerTransport()) using (var activeTarget = new PeerTransport()) using (var cancelA = new CancellationTokenSource())
                {
                    var states = new List<PeerConnectionState>(); controller.StateChanged += (state, detail) => { lock (states) states.Add(state); };
                    var connectA = controller.ConnectAsync("127.0.0.1", delayedPort, controllerCert, null, true, cancelA.Token);
                    await WaitResultAsync(acceptedDelayedClient.Task, token, "delayed A did not reach TCP").ConfigureAwait(false);
                    cancelA.Cancel();
                    var listenB = activeTarget.ListenOnceAsync(activePort, targetCert, null, true, token); await Task.Delay(50, token).ConfigureAwait(false);
                    await controller.ConnectAsync("127.0.0.1", activePort, controllerCert, null, true, token).ConfigureAwait(false);
                    if (controller.State != PeerConnectionState.Pairing || string.IsNullOrWhiteSpace(controller.RemoteEndpoint) || !controller.RemoteEndpoint.EndsWith(":" + activePort, StringComparison.Ordinal)) throw new InvalidOperationException("Superseding B did not become the current pairing transport.");
                    int statesAfterB; lock (states) statesAfterB = states.Count;
                    releaseDelayedTls.TrySetResult(true);
                    try { await connectA.ConfigureAwait(false); throw new InvalidOperationException("Cancelled delayed A completed successfully."); }
                    catch (OperationCanceledException) { }
                    await delayedServer.ConfigureAwait(false);
                    lock (states)
                    {
                        if (states.Count != statesAfterB || states.Skip(statesAfterB).Any(state => state == PeerConnectionState.Pairing || state == PeerConnectionState.Reconnecting || state == PeerConnectionState.Faulted)) throw new InvalidOperationException("Late A published transport state after B became current.");
                    }
                    if (controller.State != PeerConnectionState.Pairing || !controller.RemoteEndpoint.EndsWith(":" + activePort, StringComparison.Ordinal)) throw new InvalidOperationException("Late A replaced B transport state or stream.");
                    controller.Disconnect("supersede test complete"); await listenB.ConfigureAwait(false);
                }
            }
            finally { delayedListener.Stop(); releaseDelayedTls.TrySetResult(true); try { await delayedServer.ConfigureAwait(false); } catch { } }
        }

        private static void VerifyBackgroundPreferencePersistence(string directory)
        {
            var store = new AppConfigStore(directory); var config = WinputConfig.CreateDefault(); config.ContinueInBackground = true; store.Save(config);
            if (!store.LoadOrCreate().ContinueInBackground) throw new InvalidOperationException("Background preference did not persist.");
            var lifecycle = new BackgroundLifecycle(true);
            if (!lifecycle.ShouldHideOnClose || !lifecycle.ShouldHideOnMinimize || !lifecycle.TryBeginCleanup() || lifecycle.TryBeginCleanup()) throw new InvalidOperationException("Background lifecycle initial transition is invalid.");
            lifecycle = new BackgroundLifecycle(true); lifecycle.RequestExplicitExit();
            if (lifecycle.ShouldHideOnClose || lifecycle.ShouldHideOnMinimize || !lifecycle.TryBeginCleanup()) throw new InvalidOperationException("Explicit tray exit did not bypass hide and allow one cleanup.");
        }

        private static async Task ValidCodeDeniedAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var port = FindPort(); var targetConfig = Config("target", port); var controllerConfig = Config("controller", port);
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            using (var targetPairing = new PairingCoordinator(target, targetConfig, targetCert, PairingRole.Target))
            using (var controllerPairing = new PairingCoordinator(controller, controllerConfig, controllerCert, PairingRole.Controller))
            {
                var prompt = new TaskCompletionSource<bool>(); targetPairing.AccessRequestReceived += _ => prompt.TrySetResult(true);
                var listen = target.ListenOnceAsync(port, targetCert, null, true, token); await Task.Delay(50, token).ConfigureAwait(false);
                var consumedCode = targetPairing.AccessCode; controllerPairing.StartRequest(consumedCode); await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, token).ConfigureAwait(false);
                await WaitAsync(prompt.Task, token, "valid code did not reach target").ConfigureAwait(false); targetPairing.DenyPending();
                await WaitOfflineAsync(controller, token).ConfigureAwait(false); await listen.ConfigureAwait(false);
                if (controller.AllowsInputSend || target.AllowsInputReceive || targetPairing.AccessCode == consumedCode) throw new InvalidOperationException("Denied session retained direction or replayable code.");
            }
        }

        private static async Task<double[][]> ValidCodeAcceptedOneWayAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var port = FindPort(); var targetConfig = Config("target", port); var controllerConfig = Config("controller", port);
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            using (var targetPairing = new PairingCoordinator(target, targetConfig, targetCert, PairingRole.Target))
            using (var controllerPairing = new PairingCoordinator(controller, controllerConfig, controllerCert, PairingRole.Controller))
            {
                var sink = new RecordingSink(); var prompted = new TaskCompletionSource<bool>(); var paired = new TaskCompletionSource<bool>();
                targetPairing.AccessRequestReceived += _ => prompted.TrySetResult(true); controllerPairing.PairingCompleted += _ => paired.TrySetResult(true);
                using (var receiver = new PairedInputReceiver(target, sink))
                {
                    var latencies = new List<double>(); controller.FrameReceived += frame =>
                    {
                        if (frame.Type != FrameType.InputAck || frame.Payload.Length != sizeof(long)) return;
                        var ticks = BitConverter.ToInt64(frame.Payload, 0); lock (latencies) latencies.Add(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalMilliseconds);
                    };
                    using (var router = new InputRouter(new InputEventQueue(), controller, new RecordingSink()))
                    {
                        var listen = target.ListenOnceAsync(port, targetCert, null, true, token); await Task.Delay(50, token).ConfigureAwait(false);
                        controllerPairing.StartRequest(targetPairing.AccessCode); await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, token).ConfigureAwait(false);
                        await WaitAsync(prompted.Task, token, "target prompt missing").ConfigureAwait(false); targetPairing.AcceptPending(); await WaitAsync(paired.Task, token, "controller acceptance missing").ConfigureAwait(false);
                        if (!controller.AllowsInputSend || controller.AllowsInputReceive || target.AllowsInputSend || !target.AllowsInputReceive) throw new InvalidOperationException("Session input direction is not strictly one-way.");
                        if (!string.IsNullOrWhiteSpace(targetConfig.PinnedDeviceId) || !string.IsNullOrWhiteSpace(targetConfig.PinnedFingerprint)) throw new InvalidOperationException("Target-side session persisted an outbound peer.");
                        try { await target.SendAsync(FrameType.Input, FrameCodec.EncodeInput(InputEvent.Key(InputKind.KeyDown, 1, 0, 0, DateTime.UtcNow.Ticks)), token).ConfigureAwait(false); throw new InvalidOperationException("Target could send forbidden input."); } catch (InvalidOperationException) { }
                        var baseline = await MeasureLegacyPollingAsync(controller, latencies, token).ConfigureAwait(false);
                        var ackCount = Count(latencies);
                        router.SetRemoteActive(true);
                        for (var i = 0; i < 30; i++) { router.Publish(InputEvent.Key(InputKind.KeyDown, (ushort)(65 + i % 20), 0, 0, DateTime.UtcNow.Ticks)); await WaitUntilAsync(() => Count(latencies) >= ackCount + i + 1, token, "signal input ACK missing").ConfigureAwait(false); }
                        await sink.WaitForCountAsync(60, token).ConfigureAwait(false);
                        router.SetRemoteActive(false); controller.Disconnect("loopback complete"); await listen.ConfigureAwait(false);
                        double[] after; lock (latencies) after = Percentiles(latencies.Skip(ackCount).ToList());
                        return new[] { baseline, after };
                    }
                }
            }
        }

        private static async Task<double[]> MeasureLegacyPollingAsync(PeerTransport controller, List<double> latencies, CancellationToken token)
        {
            var queue = new InputEventQueue(); var start = Count(latencies);
            for (var i = 0; i < 30; i++)
            {
                var waiting = new TaskCompletionSource<bool>(); var pending = LegacyPollDequeueAsync(queue, waiting, token); await WaitAsync(waiting.Task, token, "legacy poll did not wait").ConfigureAwait(false);
                queue.Enqueue(InputEvent.Key(InputKind.KeyDown, (ushort)(65 + i % 20), 0, 0, DateTime.UtcNow.Ticks));
                var value = await pending.ConfigureAwait(false); await controller.SendAsync(FrameType.Input, FrameCodec.EncodeInput(value), token).ConfigureAwait(false);
                await WaitUntilAsync(() => Count(latencies) >= start + i + 1, token, "legacy input ACK missing").ConfigureAwait(false);
            }
            lock (latencies) return Percentiles(latencies.Take(start + 30).Skip(start).ToList());
        }
        private static async Task<InputEvent> LegacyPollDequeueAsync(InputEventQueue queue, TaskCompletionSource<bool> waiting, CancellationToken token) { InputEvent value; while (!queue.TryDequeue(out value)) { waiting.TrySetResult(true); await Task.Delay(2, token).ConfigureAwait(false); } return value; }
        private static int Count(List<double> values) { lock (values) return values.Count; }
        private static double[] Percentiles(List<double> values) { var sorted = values.OrderBy(v => v).ToArray(); return new[] { sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[sorted.Length - 1] }; }
        private static WinputConfig Config(string name, int port) { var config = WinputConfig.CreateDefault(); config.DeviceId = Guid.NewGuid().ToString("N"); config.DisplayName = name; config.ListenPort = port; return config; }
        private static int FindPort() { var listener = new TcpListener(IPAddress.Loopback, 0); try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); } }
        private static async Task WaitAsync(Task task, CancellationToken token, string message) { var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false); if (completed != task) throw new TimeoutException(message); await task.ConfigureAwait(false); }
        private static async Task<T> WaitResultAsync<T>(Task<T> task, CancellationToken token, string message) { await WaitAsync(task, token, message).ConfigureAwait(false); return task.Result; }
        private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token, string message) { while (!predicate()) { token.ThrowIfCancellationRequested(); await Task.Delay(2, token).ConfigureAwait(false); } }
        private static async Task WaitOfflineAsync(PeerTransport transport, CancellationToken token) { await WaitUntilAsync(() => transport.State == PeerConnectionState.Offline, token, "transport did not close").ConfigureAwait(false); }
        private static void ExpectInvalidOperation(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException(message); }

        private static async Task VerifyHeartbeatFailSafeAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert, CancellationToken token)
        {
            var port = FindPort();
            var release = new RecordingFailSafeSink();
            using (var target = new PeerTransport(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(250), false))
            using (var controller = new PeerTransport(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(250)))
            using (var router = new InputRouter(new InputEventQueue(), controller, release))
            {
                var listen = target.ListenOnceAsync(port, targetCert, CertificateManager.Fingerprint(controllerCert), false, token); await Task.Delay(50, token).ConfigureAwait(false);
                await controller.ConnectAsync("127.0.0.1", port, controllerCert, CertificateManager.Fingerprint(targetCert), false, token).ConfigureAwait(false);
                controller.SetInputDirection(true, false); router.SetRemoteActive(true);
                if (!router.Publish(InputEvent.Key(InputKind.KeyDown, 65, 0, 0, DateTime.UtcNow.Ticks))) throw new InvalidOperationException("Router did not activate before heartbeat fail-safe test.");
                await Task.Delay(650, token).ConfigureAwait(false);
                if (controller.State != PeerConnectionState.Offline || router.Publish(InputEvent.Key(InputKind.KeyDown, 65, 0, 0, DateTime.UtcNow.Ticks)) || release.Releases == 0) throw new InvalidOperationException("Heartbeat timeout did not restore local-safe route.");
                target.Disconnect("heartbeat complete"); await listen.ConfigureAwait(false);
            }
        }

        private sealed class RecordingSink : IFailSafeInputSink
        {
            public int Count { get; private set; } public bool Publish(InputEvent value) { Count++; return true; } public void ReleaseAll() { }
            public Task WaitForCountAsync(int count, CancellationToken token) { return WaitUntilAsync(() => Count >= count, token, "target did not receive all input"); }
        }
        private sealed class RecordingFailSafeSink : IFailSafeInputSink
        {
            public int Releases { get; private set; }
            public bool Publish(InputEvent value) { return true; }
            public void ReleaseAll() { Releases++; }
        }
    }
}
