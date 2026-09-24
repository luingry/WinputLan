using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan.Loopback
{
    internal static class StabilityTests
    {
        public static async Task RunAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert)
        {
            await PendingTlsAsync(targetCert, controllerCert).ConfigureAwait(false);
            await CongestedHeartbeatAsync(targetCert, controllerCert).ConfigureAwait(false);
            await FocusAndOverflowAsync(targetCert, controllerCert).ConfigureAwait(false);
            await ReceiverDisconnectAsync(targetCert, controllerCert).ConfigureAwait(false);
            Console.WriteLine("STABILITY PASS: TLS cancellation/timeout/retry, congested heartbeat, stale input rejection, focus recovery, full-queue release");
        }

        private static PeerTransport FastTransport()
        {
            return new PeerTransport(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(800), true, TimeSpan.FromMilliseconds(500));
        }

        private static async Task PendingTlsAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert)
        {
            foreach (var mode in new[] { "cancel", "disconnect", "timeout", "dispose" })
            {
                var port = FreePort();
                using (var target = FastTransport()) using (var cts = new CancellationTokenSource()) using (var peer = new TcpClient())
                {
                    var listen = target.ListenOnceAsync(port, targetCert, null, true, cts.Token);
                    await peer.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                    await Until(() => target.State == PeerConnectionState.Pairing, "listener did not accept peer").ConfigureAwait(false);
                    if (mode == "cancel") cts.Cancel();
                    if (mode == "disconnect") target.Disconnect("test pending TLS");
                    if (mode == "dispose") target.Dispose();
                    await MustEnd(listen, "pending inbound TLS " + mode).ConfigureAwait(false);
                }
                // The exact same port must immediately accept a healthy connection after cleanup.
                using (var target = new PeerTransport()) using (var controller = new PeerTransport())
                {
                    var listen = target.ListenOnceAsync(port, targetCert, null, true, CancellationToken.None);
                    await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, CancellationToken.None).ConfigureAwait(false);
                    controller.Disconnect("retry verified");
                    await MustEnd(listen, "listener retry").ConfigureAwait(false);
                }
            }
            foreach (var mode in new[] { "cancel", "disconnect", "timeout", "dispose" })
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                try
                {
                    using (var controller = FastTransport()) using (var cts = new CancellationTokenSource())
                    {
                        var connect = controller.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, controllerCert, null, true, cts.Token);
                        using (var peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        {
                            if (mode == "cancel") cts.Cancel();
                            if (mode == "disconnect") controller.Disconnect("test pending TLS");
                            if (mode == "dispose") controller.Dispose();
                            await MustEnd(connect, "pending outbound TLS " + mode).ConfigureAwait(false);
                            if (connect.Status == TaskStatus.RanToCompletion) throw new Exception("silent server completed TLS");
                        }
                    }
                }
                finally { listener.Stop(); }
            }
        }

        private static async Task CongestedHeartbeatAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                using (var controller = new PeerTransport(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(800)))
                {
                    var connect = controller.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, controllerCert, null, true, CancellationToken.None);
                    using (var peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                    using (var ssl = new SslStream(peer.GetStream(), false, (s, c, ch, e) => true))
                    {
                        peer.ReceiveBufferSize = 4096;
                        await ssl.AuthenticateAsServerAsync(targetCert, true, SslProtocols.Tls12, false).ConfigureAwait(false);
                        await connect.ConfigureAwait(false);
                        controller.MarkPaired(); controller.SetInputDirection(true, false);
                        var payload = new byte[ProtocolConstants.MaxPayloadBytes];
                        Task blocked = null;
                        for (var i = 0; i < 2048; i++)
                        {
                            var send = controller.SendAsync(FrameType.Input, payload, CancellationToken.None);
                            if (await Task.WhenAny(send, Task.Delay(20)).ConfigureAwait(false) != send) { blocked = send; break; }
                            await send.ConfigureAwait(false);
                        }
                        if (blocked == null) throw new Exception("test failed to congest TLS write");
                        await Until(() => controller.State == PeerConnectionState.Offline, "heartbeat watchdog blocked behind TLS write").ConfigureAwait(false);
                        await MustEnd(blocked, "congested writer cleanup").ConfigureAwait(false);
                    }
                }
            }
            finally { listener.Stop(); }
        }

        private static async Task FocusAndOverflowAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert)
        {
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            {
                var port = FreePort(); var listen = target.ListenOnceAsync(port, targetCert, null, true, CancellationToken.None);
                await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, CancellationToken.None).ConfigureAwait(false);
                await Until(() => target.ObservedRemoteFingerprint != null, "target TLS publication").ConfigureAwait(false);
                target.SetInputDirection(false, true); controller.SetInputDirection(true, false); target.MarkPaired(); controller.MarkPaired();
                var sink = new TrackingSink(); var queue = new InputEventQueue(8);
                using (var receiver = new PairedInputReceiver(target, sink)) using (var router = new InputRouter(queue, controller, new TrackingSink()))
                {
                    var focusGate = Gate(router, "_focusGate");
                    await focusGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        router.SetRemoteActive(true); router.Publish(Key(InputKind.KeyDown, 65));
                        await Until(() => queue.Count == 0, "router didn't dequeue paused input").ConfigureAwait(false);
                        router.SetRemoteActive(false); router.SetRemoteActive(true);
                    }
                    finally { focusGate.Release(); }
                    // A new key proves the new focus is delivered and the drain has processed the old one.
                    router.Publish(Key(InputKind.KeyDown, 66));
                    await Until(() => sink.IsDown(66), "new activation didn't deliver input").ConfigureAwait(false);
                    if (sink.IsDown(65) || sink.DownCount != 1) throw new Exception("stale input crossed activation epoch");
                    var blockedWriter = Gate(controller, "_sendGate");
                    await blockedWriter.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        router.Publish(Key(InputKind.KeyDown, 65));
                        await Until(() => queue.Count == 0, "input didn't reach blocked writer").ConfigureAwait(false);
                        router.SetRemoteActive(false); router.SetRemoteActive(true);
                    }
                    finally { blockedWriter.Release(); }
                    router.Publish(Key(InputKind.KeyDown, 68));
                    await Until(() => sink.IsDown(68), "focus recovery after blocked writer failed").ConfigureAwait(false);
                    if (sink.IsDown(65) || sink.DownCount != 2) throw new Exception("old input passed the write-gate epoch check");
                    router.SetRemoteActive(false);
                    await Until(() => !sink.IsDown(66), "unfocus didn't release keys").ConfigureAwait(false);
                    router.SetRemoteActive(true); router.Publish(Key(InputKind.KeyDown, 67));
                    await Until(() => sink.IsDown(67), "fresh input missing").ConfigureAwait(false);

                    // Hold only the writer: the hook producer must never wait on it, and a full queue
                    // must close TCP and release C even though its KeyUp cannot fit in the queue.
                    var sendGate = Gate(controller, "_sendGate"); await sendGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        router.Publish(InputEvent.MouseWheel(120, DateTime.UtcNow.Ticks));
                        await Until(() => queue.Count == 0, "drain didn't reach write gate").ConfigureAwait(false);
                        for (var i = 0; i < 8; i++) router.Publish(InputEvent.MouseWheel(120, DateTime.UtcNow.Ticks));
                        if (!router.Publish(Key(InputKind.KeyUp, 67))) throw new Exception("overflow leaked KeyUp to local hook");
                        await Until(() => controller.State == PeerConnectionState.Offline && !sink.IsDown(67), "overflow did not disconnect and release remote keys").ConfigureAwait(false);
                    }
                    finally { sendGate.Release(); }
                }
                controller.Disconnect("test complete"); target.Disconnect("test complete");
                await MustEnd(listen, "input listener cleanup").ConfigureAwait(false);
            }
        }

        private static async Task ReceiverDisconnectAsync(X509Certificate2 targetCert, X509Certificate2 controllerCert)
        {
            using (var target = new PeerTransport()) using (var controller = new PeerTransport())
            using (var sink = new PausedSink())
            {
                var port = FreePort(); var listen = target.ListenOnceAsync(port, targetCert, null, true, CancellationToken.None);
                await controller.ConnectAsync("127.0.0.1", port, controllerCert, null, true, CancellationToken.None).ConfigureAwait(false);
                await Until(() => target.ObservedRemoteFingerprint != null, "receiver TLS publication").ConfigureAwait(false);
                target.SetInputDirection(false, true); controller.SetInputDirection(true, false); target.MarkPaired(); controller.MarkPaired();
                using (var receiver = new PairedInputReceiver(target, sink))
                {
                    await controller.SendAsync(FrameType.ControlFocus, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false);
                    await controller.SendAsync(FrameType.Input, FrameCodec.EncodeInput(Key(InputKind.KeyDown, 65)), CancellationToken.None).ConfigureAwait(false);
                    await Until(() => sink.Entered.IsSet, "receiver didn't enter injection").ConfigureAwait(false);
                    var disconnect = Task.Run(() => target.Disconnect("concurrent injection test"));
                    try
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        if (disconnect.IsCompleted) throw new Exception("disconnect released keys before in-flight injection completed");
                    }
                    finally { sink.Resume.Set(); }
                    await MustEnd(disconnect, "serialized receiver cleanup").ConfigureAwait(false);
                    if (sink.Down) throw new Exception("key remained pressed after concurrent disconnect");
                }
                controller.Disconnect("test complete"); await MustEnd(listen, "receiver cleanup").ConfigureAwait(false);
            }
        }



        private sealed class PausedSink : IFailSafeInputSink, IDisposable
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Resume = new ManualResetEventSlim();
            public volatile bool Down;
            public bool Publish(InputEvent value) { Entered.Set(); if (!Resume.Wait(3000)) throw new Exception("paused injection timed out"); Down = true; return true; }
            public void ReleaseAll() { Down = false; }
            public void Dispose() { Resume.Set(); Entered.Dispose(); Resume.Dispose(); }
        }

        private static InputEvent Key(InputKind kind, ushort key) { return InputEvent.Key(kind, key, 0, 0, DateTime.UtcNow.Ticks); }
        private static SemaphoreSlim Gate(object instance, string field) { return (SemaphoreSlim)instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); } }
        private static async Task Until(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds > 3000) throw new Exception(message); await Task.Delay(10).ConfigureAwait(false); }
        }
        private static async Task MustEnd(Task task, string message)
        {
            if (await Task.WhenAny(task, Task.Delay(3000)).ConfigureAwait(false) != task) throw new Exception(message + " did not finish");
            try { await task.ConfigureAwait(false); } catch (IOException) { } catch (OperationCanceledException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
        private sealed class TrackingSink : IFailSafeInputSink
        {
            private readonly object _gate = new object();
            private readonly HashSet<ushort> _down = new HashSet<ushort>();
            private int _downCount;
            public int DownCount { get { lock (_gate) return _downCount; } }
            public bool IsDown(ushort key) { lock (_gate) return _down.Contains(key); }
            public bool Publish(InputEvent value) { lock (_gate) { if (value.Kind == InputKind.KeyDown) { _down.Add(value.VirtualKey); _downCount++; } else if (value.Kind == InputKind.KeyUp) _down.Remove(value.VirtualKey); return true; } }
            public void ReleaseAll() { lock (_gate) _down.Clear(); }
        }
    }
}
