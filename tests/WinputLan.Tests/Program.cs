using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WinputLan.Core;

namespace WinputLan.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Main()
        {
            Run("frame round-trip and bounds", TestFrames);
            Run("wheel delta wire preservation", TestWheel);
            Run("extended keys keep their flag through injection", TestExtendedKeyInjection);
            Run("pairing transcript, SAS and HKDF", TestPairing);
            Run("access proof rejects certificate substitution", TestAccessProof);
            Run("pin store abstraction", TestPins);
            Run("queue coalescing and order", TestQueue);
            Run("queue coalesce clear leaves no phantom permit", TestQueueSignals);
            Run("inbound queue merges waiting motion only", TestInboundQueue);
            Run("paced motion folds only the motion behind it", TestMotionPacingQueue);
            Run("tracked cursor obeys clip and monitor limits", TestCursorBounds);
            Run("LAN IPv4 selection prefers routed Ethernet/Wi-Fi", TestLanAddressSelection);
            Run("rolling latency window is p50 and throttled", TestLatencyWindow);
            Run("input audit filters duplicates and throttles high frequency", TestInputAuditPolicy);
            Run("input routing keeps press/release pairs on one machine", TestInputRouting);
            Run("outbound cancellation cannot update a newer attempt", TestRequestAttemptOwnership);
            Run("hotkey validation", TestHotkeys);
            Run("hotkey hook bypass", TestHotkeyBypass);
            Run("absolute pointer mapping", TestPointerMapping);
            Run("privacy log", TestPrivacyLog);
            Run("configuration corruption validation", TestConfig);
            Run("signed release manifest invariants", TestManifest);
            Run("reconnect backoff bounds", TestBackoff);
            Run("automatic update schedule", TestUpdateSchedule);
            Run("elevated relaunch policy", TestElevationPolicy);
            Run("machine list follows where input goes", TestMachineList);
            Run("trusted peers match identity and certificate", TestTrustedPeers);
            Console.WriteLine("PASS={0} FAIL={1}", _passed, _failed);
            if (_failed != 0) Environment.ExitCode = 1;
        }

        private static void Run(string name, Action action)
        {
            try { action(); _passed++; Console.WriteLine("PASS {0}", name); }
            catch (Exception ex) { _failed++; Console.WriteLine("FAIL {0}: {1}", name, ex.Message); }
        }

        private static void TestFrames()
        {
            var payload = Encoding.UTF8.GetBytes("hello");
            var bytes = FrameCodec.Encode(FrameType.Hello, 42, payload);
            var frame = FrameCodec.Decode(bytes);
            Assert(frame.Type == FrameType.Hello && frame.Sequence == 42 && Encoding.UTF8.GetString(frame.Payload) == "hello", "frame round-trip");
            var input = InputEvent.Key(InputKind.KeyDown, 0x41, 0x1E, 0, DateTime.UtcNow.Ticks);
            Assert(FrameCodec.DecodeInput(FrameCodec.EncodeInput(input)).VirtualKey == 0x41, "input round-trip");
            Expect<Exception>(() => FrameCodec.Decode(new byte[3]));
            using (var stream = new MemoryStream(FrameCodec.Encode(FrameType.Hello, 1, new byte[0]).Concat(FrameCodec.Encode(FrameType.Hello, 1, new byte[0])).ToArray()))
            {
                Assert(FrameCodec.Read(stream).Sequence == 1, "first sequence");
                Assert(FrameCodec.Read(stream).Sequence == 1, "wire sequence retained");
            }
        }

        private static void TestPairing()
        {
            var nonceA = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var nonceB = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
            var first = new PairingTranscript("a", "b", "aa", "bb", nonceA, nonceB);
            var second = new PairingTranscript("b", "a", "bb", "aa", nonceB, nonceA);
            Assert(first.SasCode() == second.SasCode(), "SAS must be symmetric");
            Assert(first.PinCode() == second.PinCode(), "pin must be symmetric");
            var key = HkdfSha256.Derive(first.Digest(), null, "transport", 32);
            Assert(key.Length == 32, "HKDF length");
            var confirm = new PairingConfirmation(first);
            confirm.ConfirmLocal(first.SasCode());
            confirm.ConfirmRemote(Convert.ToBase64String(first.Digest()));
            Assert(confirm.IsComplete, "bilateral confirmation");
        }

        private static void TestAccessProof()
        {
            var code = AccessCode.Generate();
            Assert(AccessCode.IsValid(code) && code.Length == 6 && AccessCode.Format(code).Split(' ').Length == 2, "access code is a grouped 6-character value");
            Assert(!AccessCode.IsValid("ABCDE") && !AccessCode.IsValid("ABCDE0") && !AccessCode.IsValid("ABCDEFG"), "wrong length and look-alike symbols are rejected");
            TestAccessCodeMask();
            var controllerNonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var targetNonce = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
            var directTranscript = AccessProof.CanonicalTranscript("controller", "target", "controller-fingerprint", "target-fingerprint", controllerNonce, targetNonce);
            var proof = AccessProof.Create(code, directTranscript, "request");
            Assert(AccessProof.Verify(code, directTranscript, "request", proof), "matching direct transcript validates");
            var substitutedCertificateTranscript = AccessProof.CanonicalTranscript("controller", "target", "controller-fingerprint", "mitm-target-fingerprint", controllerNonce, targetNonce);
            Assert(!AccessProof.Verify(code, substitutedCertificateTranscript, "request", proof), "certificate substitution rejects request proof before approval");
            Assert(!AccessProof.Verify(code, directTranscript, "accept", proof), "request proof cannot be replayed as acceptance proof");
        }

        private static void TestWheel()
        {
            var wheel = InputEvent.MouseWheel(-120, DateTime.UtcNow.Ticks);
            var decoded = FrameCodec.DecodeInput(FrameCodec.EncodeInput(wheel));
            Assert(decoded.Kind == InputKind.MouseWheel && unchecked((short)decoded.MouseData) == -120, "wheel delta survives the wire record");
        }

        private static void TestExtendedKeyInjection()
        {
            // Home (VK 0x24) from the navigation cluster: LLKHF_EXTENDED set, plus unrelated hook bits (0x20 alt, 0x80 up).
            var home = FrameCodec.DecodeInput(FrameCodec.EncodeInput(InputEvent.Key(InputKind.KeyDown, 0x24, 0x47, 0x01 | 0x20, DateTime.UtcNow.Ticks)));
            Assert(KeyInjection.SendInputFlags(home.Kind, home.Flags) == KeyInjection.SendInputExtendedKey, "extended key down keeps KEYEVENTF_EXTENDEDKEY only");
            Assert(KeyInjection.SendInputFlags(InputKind.KeyUp, 0x01 | 0x80) == (KeyInjection.SendInputExtendedKey | KeyInjection.SendInputKeyUp), "extended key up");
            Assert(KeyInjection.SendInputFlags(InputKind.KeyDown, 0) == 0, "Shift/letters inject without extra flags");
            Assert(KeyInjection.SendInputFlags(InputKind.KeyUp, 0x80) == KeyInjection.SendInputKeyUp, "plain key up");
            Expect<ArgumentException>(() => KeyInjection.SendInputFlags(InputKind.MouseWheel, 0));
        }

        private static void TestPins()
        {
            var protector = new TestProtector();
            var store = new PinStore(protector);
            store.Save(new PinRecord { DeviceId = "peer", CertificateFingerprint = "fingerprint", TranscriptDigest = "digest", CreatedUtc = DateTime.UtcNow });
            var blob = store.ExportProtectedBlob();
            var restored = new PinStore(protector);
            restored.LoadProtectedBlob(blob);
            PinRecord record;
            Assert(restored.TryLoad(out record) && record.DeviceId == "peer", "pin round-trip");
            restored.LoadProtectedBlob(Encoding.UTF8.GetBytes("corrupt"));
            Assert(!restored.TryLoad(out record), "corrupt pin fails safe");
        }

        private static void TestQueue()
        {
            var queue = new InputEventQueue(8);
            queue.Enqueue(InputEvent.Key(InputKind.KeyDown, 65, 0, 0, 1));
            queue.Enqueue(InputEvent.MouseMove(1, 1, 2));
            Assert(queue.Enqueue(InputEvent.MouseMove(2, 2, 3)) == EnqueueResult.CoalescedMouseMove, "only moves coalesce");
            queue.Enqueue(InputEvent.MouseButton(InputKind.MouseButtonDown, 1, 4));
            InputEvent value;
            Assert(queue.TryDequeue(out value) && value.Kind == InputKind.KeyDown, "key order");
            Assert(queue.TryDequeue(out value) && value.Kind == InputKind.MouseMove && value.X == 2, "latest move");
            Assert(queue.TryDequeue(out value) && value.Kind == InputKind.MouseButtonDown, "button order");
            queue.Enqueue(InputEvent.MouseDelta(3, -1, 5));
            Assert(queue.Enqueue(InputEvent.MouseDelta(4, 2, 6)) == EnqueueResult.CoalescedMouseMove, "deltas coalesce");
            queue.Enqueue(InputEvent.MouseButton(InputKind.MouseButtonDown, 0x0201, 7));
            queue.Enqueue(InputEvent.MouseDelta(1, 1, 8));
            Assert(queue.TryDequeue(out value) && value.Kind == InputKind.MouseDelta && value.X == 7 && value.Y == 1 && value.TimestampUtcTicks == 5, "coalesced deltas are summed, never dropped");
            Assert(queue.TryDequeue(out value) && value.Kind == InputKind.MouseButtonDown, "click keeps its place between motions");
            Assert(queue.TryDequeue(out value) && value.X == 1, "motion after click is not merged across it");
            Assert(FrameCodec.DecodeInput(FrameCodec.EncodeInput(InputEvent.MouseDelta(-12, 34, 9))).Y == 34, "delta round-trips on the wire");
        }

        private static void TestQueueSignals()
        {
            var queue = new InputEventQueue(8);
            queue.Enqueue(InputEvent.MouseMove(1, 1, 1));
            queue.Enqueue(InputEvent.MouseMove(2, 2, 2));
            queue.Clear();
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                var wait = queue.DequeueAsync(cancellation.Token);
                System.Threading.Thread.Sleep(20);
                Assert(!wait.IsCompleted, "coalesce plus clear must not leave a dequeue permit");
                cancellation.Cancel();
                try { wait.GetAwaiter().GetResult(); throw new InvalidOperationException("cancelled queue wait completed"); }
                catch (OperationCanceledException) { }
            }
        }

        private static void TestInboundQueue()
        {
            Func<InputEvent, Frame> input = value => new Frame(FrameType.Input, 1, FrameCodec.EncodeInput(value));
            var queue = new InboundFrameQueue();
            Assert(!queue.Enqueue(input(InputEvent.MouseDelta(3, -1, 5)), 0), "first delta is queued");
            Assert(queue.Enqueue(input(InputEvent.MouseDelta(4, 2, 6)), 0), "waiting deltas merge");
            queue.Enqueue(input(InputEvent.MouseButton(InputKind.MouseButtonUp, 0x0202, 7)), 0);
            queue.Enqueue(input(InputEvent.MouseDelta(1, 1, 8)), 0);
            Assert(!queue.Enqueue(input(InputEvent.MouseDelta(1, 1, 9)), 1), "motion never merges across an epoch");
            queue.Enqueue(new Frame(FrameType.ReleaseAll, 2, new byte[0]), 1);
            Assert(!queue.Enqueue(input(InputEvent.MouseDelta(2, 2, 10)), 1), "motion never merges across a control frame");
            queue.Enqueue(new Frame(FrameType.Input, 3, new byte[] { 1 }), 1);
            InboundFrameQueue.Entry entry;
            Assert(queue.TryDequeue(out entry) && entry.Input.Kind == InputKind.MouseDelta && entry.Input.X == 7 && entry.Input.Y == 1 && entry.Input.TimestampUtcTicks == 5, "merged motion is summed and keeps the oldest timestamp");
            Assert(queue.TryDequeue(out entry) && entry.Input.Kind == InputKind.MouseButtonUp, "release keeps its place mid-drag");
            Assert(queue.TryDequeue(out entry) && entry.Input.X == 1 && entry.Epoch == 0, "motion after release is not merged across it");
            Assert(queue.TryDequeue(out entry) && entry.Epoch == 1, "new epoch starts a new entry");
            Assert(queue.TryDequeue(out entry) && entry.Frame.Type == FrameType.ReleaseAll && entry.Input == null, "control frame order");
            Assert(queue.TryDequeue(out entry) && entry.Input.X == 2, "motion after control frame");
            Assert(queue.TryDequeue(out entry) && entry.Frame.Type == FrameType.Input && entry.Input == null, "invalid payload is kept for auditing");
            queue.Enqueue(input(InputEvent.MouseDelta(1, 1, 11)), 1);
            queue.Clear();
            InboundFrameQueue.Entry blocked = null;
            var waiter = System.Threading.Tasks.Task.Run(() => queue.TryDequeue(out blocked));
            Assert(!waiter.Wait(50), "cleared queue blocks the injector");
            queue.Complete();
            Assert(waiter.Wait(1000) && !waiter.Result, "complete releases a blocked injector");
            Assert(!queue.Enqueue(input(InputEvent.MouseDelta(1, 1, 12)), 1) && queue.Count == 0, "completed queue accepts nothing");
        }

        private static void TestMotionPacingQueue()
        {
            var queue = new InputEventQueue(8);
            queue.Enqueue(InputEvent.MouseDelta(1, 1, 1), 3);
            InputEventQueue.Entry entry;
            using (var cancellation = new System.Threading.CancellationTokenSource(1000)) entry = queue.DequeueEntryAsync(cancellation.Token).GetAwaiter().GetResult();
            Assert(queue.OnlyMotionPending(3), "an empty queue lets motion keep pacing");
            queue.Enqueue(InputEvent.MouseDelta(2, 3, 2), 3);
            queue.Enqueue(InputEvent.MouseDelta(4, 5, 3), 3);
            Assert(queue.OnlyMotionPending(3) && !queue.OnlyMotionPending(4), "pending motion of another epoch stops pacing");
            var merged = queue.MergeFollowingMotion(entry);
            Assert(merged.Value.X == 7 && merged.Value.Y == 9 && merged.Value.TimestampUtcTicks == 1 && merged.Epoch == 3 && queue.Count == 0, "waiting motion folds into the paced one");
            queue.Enqueue(InputEvent.MouseButton(InputKind.MouseButtonUp, 0x0202, 4), 3);
            queue.Enqueue(InputEvent.MouseDelta(1, 1, 5), 3);
            Assert(!queue.OnlyMotionPending(3), "a queued click ends pacing");
            Assert(ReferenceEquals(queue.MergeFollowingMotion(merged), merged) && queue.Count == 2, "motion never folds across a click");
            queue.Clear();
            queue.Enqueue(InputEvent.MouseDelta(1, 1, 6), 3);
            queue.MergeFollowingMotion(merged);
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                var wait = queue.DequeueEntryAsync(cancellation.Token);
                System.Threading.Thread.Sleep(20);
                Assert(!wait.IsCompleted, "folding consumes the folded item's permit");
                cancellation.Cancel();
                try { wait.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
        }

        private static void TestCursorBounds()
        {
            var monitors = new[] { new PixelRect(0, 0, 1920, 1080), new PixelRect(1920, 0, 4480, 1440) };
            var none = default(PixelRect);
            int x = 500, y = 500;
            CursorBounds.Clamp(ref x, ref y, none, monitors);
            Assert(x == 500 && y == 500, "a point on a monitor is kept");
            x = 1000; y = 1300;
            CursorBounds.Clamp(ref x, ref y, none, monitors);
            Assert(x == 1000 && y == 1079, "the gap below a shorter monitor snaps to its edge");
            x = 1919; y = 1300;
            CursorBounds.Clamp(ref x, ref y, none, monitors);
            Assert(x == 1920 && y == 1300, "the nearest monitor wins");
            x = 3000; y = 900;
            CursorBounds.Clamp(ref x, ref y, new PixelRect(2000, 100, 2600, 700), monitors);
            Assert(x == 2599 && y == 699, "the clip rectangle holds the cursor like Windows does");
            x = 50; y = 50;
            CursorBounds.Clamp(ref x, ref y, none, new PixelRect[0]);
            Assert(x == 50 && y == 50, "no layout leaves the point alone");
        }

        private static void TestLanAddressSelection()
        {
            var selected = LanAddressSelector.Select(new[]
            {
                new LanAddressCandidate { Address = "169.254.1.1", InterfaceUp = true, HasGateway = true, IsEthernetOrWifi = true },
                new LanAddressCandidate { Address = "10.0.0.7", InterfaceUp = true, HasGateway = false, IsEthernetOrWifi = true },
                new LanAddressCandidate { Address = "192.168.1.9", InterfaceUp = true, HasGateway = true, IsEthernetOrWifi = true },
                new LanAddressCandidate { Address = "127.0.0.1", InterfaceUp = true, HasGateway = true, IsEthernetOrWifi = true }
            });
            Assert(selected == "192.168.1.9", "routed non-APIPA LAN address selected");
        }

        private static void TestLatencyWindow()
        {
            var window = new InputLatencyWindow(5); window.Record(TimeSpan.FromMilliseconds(1)); window.Record(TimeSpan.FromMilliseconds(9)); window.Record(TimeSpan.FromMilliseconds(4));
            double p50; var now = DateTime.UtcNow;
            Assert(window.TryGetP50(now, TimeSpan.FromMilliseconds(250), out p50) && p50 == 4, "rolling p50");
            Assert(!window.TryGetP50(now.AddMilliseconds(100), TimeSpan.FromMilliseconds(250), out p50), "visual updates throttled");
        }

        private static void TestInputAuditPolicy()
        {
            var policy = new InputAuditPolicy(); var now = DateTime.UtcNow;
            Assert(!policy.ShouldEmit(InputKind.MouseMove, "queued", now) && !policy.ShouldEmit(InputKind.MouseMove, "coalesced", now), "queue diagnostics are not UI log events");
            Assert(policy.ShouldEmit(InputKind.KeyDown, "sent", now) && policy.ShouldEmit(InputKind.KeyUp, "received", now), "discrete keys retained");
            Assert(policy.ShouldEmit(InputKind.MouseMove, "sent", now), "first mouse move emitted");
            Assert(!policy.ShouldEmit(InputKind.MouseMove, "sent", now.AddMilliseconds(249)), "mouse move rate limited");
            Assert(policy.ShouldEmit(InputKind.MouseWheel, "received", now.AddMilliseconds(250)), "high frequency update resumes at four hertz");
            foreach (var status in new[] { "sent", "received", "dropped-sink" })
            {
                var deltas = new InputAuditPolicy();
                var emitted = Enumerable.Range(0, 1000).Count(i => deltas.ShouldEmit(InputKind.MouseDelta, status, now.AddMilliseconds(i)));
                Assert(emitted == 4, "relative motion is throttled on every audit path: " + status);
            }
        }

        private static void TestInputRouting()
        {
            var routing = new InputRoutingState();
            uint ctrl = InputRoutingState.KeyId(0xA2), two = InputRoutingState.KeyId((ushort)'2'), a = InputRoutingState.KeyId((ushort)'A');
            // Switch to remote while the chord modifier is physically held: its release must stay local.
            Assert(routing.Press(ctrl) == InputRoute.Local, "modifier pressed before switching is local");
            routing.SetRemoteActive(true);
            Assert(routing.Press(ctrl) == InputRoute.Local && routing.Press(ctrl) == InputRoute.Local, "held-key repeats preserve their original local owner");
            Assert(routing.Release(ctrl) == InputRoute.Local, "local press is released locally, never stuck");
            Assert(routing.Press(a) == InputRoute.Remote && routing.Release(a) == InputRoute.Remote, "keys typed during control go remote");
            Assert(routing.Continuous() == InputRoute.Remote, "motion and wheel go remote while active");
            var left = InputRoutingState.ButtonId(0x0201);
            Assert(routing.Press(left) == InputRoute.Remote, "click during control goes remote");
            // Return chord pressed remotely: after switching back, stray releases are handled locally and harmlessly.
            Assert(routing.Press(ctrl) == InputRoute.Remote, "return chord modifier is forwarded");
            routing.SetRemoteActive(false);
            Assert(routing.Release(ctrl) == InputRoute.Local && routing.Release(left) == InputRoute.Local, "remote presses are forgotten after ReleaseAll");
            Assert(routing.Continuous() == InputRoute.Local && routing.Press(two) == InputRoute.Local, "local machine owns input again");
            Assert(InputRoutingState.ButtonId(0x0201) != InputRoutingState.KeyId(0x01), "button ids never collide with virtual keys");
        }

        private static void TestAccessCodeMask()
        {
            var mask = new AccessCodeMask();
            Assert(mask.Input(0, "a") == 1 && mask[0] == 'A', "typing uppercases and advances");
            Assert(mask.Input(1, "0") == 1 && !mask[1].HasValue, "look-alike symbols are ignored without moving");
            mask.Input(1, "b"); mask.Input(2, "c");
            Assert(mask.Backspace(3) == 2 && !mask[2].HasValue, "backspace on an empty slot clears the previous and moves back");
            Assert(mask.Backspace(2) == 1 && !mask[1].HasValue && mask[0] == 'A', "backspace keeps moving back");
            Assert(mask.Input(1, "k7m-x9p") == 5 && mask.Value == "K7MX9P" && mask.IsComplete, "pasting a full code fills from the first slot and focuses the last");
            Assert(mask.Input(5, "z") == 5 && mask.Value == "K7MX9Z", "typing in the last slot replaces it and stays");
            Assert(mask.Delete(2) == 2 && mask.Value == "K7X9Z" && !mask.IsComplete && mask.FirstEmpty() == 2, "delete clears in place");
            mask.Clear();
            Assert(mask.Value == "" && mask.Backspace(0) == 0, "clear empties every slot");
            Assert(AccessCode.IsValid(AccessCode.Normalize("k7m x9p")), "normalized typed code is valid");
        }

        private static void TestMachineList()
        {
            var controller = new MachineListInput { LocalName = "PC1", TargetName = "PC2", TargetAddress = "10.0.0.2", TargetRecognized = true, Outbound = OutboundSession.Connected, LocalHotkey = "Ctrl+Shift+Alt+1", RemoteHotkey = "Ctrl+Shift+Alt+2" };
            var idle = MachineListState.Build(controller);
            Assert(idle.Local.IsActive && !idle.Other.IsActive && idle.Local.IsController && !idle.Other.IsController, "connected but local: this PC is active and marked as controller");
            Assert(idle.Other.Name == "PC2" && idle.Other.Badge == "Disponível" && idle.Local.Status == "Recebendo entrada", "target shows its real name and is available");
            Assert(idle.Local.Detail == "Mouse e teclado deste PC" && idle.Other.Detail.Contains("Ctrl+Shift+Alt+2"), "shortcut hint only on the machine opposite to the one receiving input");
            controller.OutboundFocused = true;
            var sending = MachineListState.Build(controller);
            Assert(!sending.Local.IsActive && sending.Other.IsActive && sending.Other.Badge == "Ativa" && sending.Local.Status == "Enviando entrada" && sending.Other.Status == "Recebendo entrada", "active state and green follow the machine receiving input");
            Assert(sending.Local.IsController && sending.Local.Detail.Contains("Ctrl+Shift+Alt+1"), "controller keeps its badge and shows how to come back");
            controller.Outbound = OutboundSession.None; controller.OutboundFocused = false;
            var offline = MachineListState.Build(controller);
            Assert(offline.Other.Visible && !offline.Local.IsController && offline.Other.Status == "Clique para conectar" && offline.Other.Subtitle == "Reconhecida", "known target offers approval-only reconnection");
            controller.TargetRecognized = false;
            Assert(MachineListState.Build(controller).Other.Subtitle == "Requer código", "unrecognized target needs a code");
            var target = MachineListState.Build(new MachineListInput { LocalName = "PC2", InboundConnected = true, InboundFocused = true, ControllerName = "PC1" });
            Assert(target.Local.IsActive && !target.Local.IsController && target.Other.IsController && !target.Other.IsActive && target.Other.Name == "PC1", "controlled PC shows the controller with its badge and itself as active");
            var targetIdle = MachineListState.Build(new MachineListInput { LocalName = "PC2", InboundConnected = true, InboundFocused = false, ControllerName = "PC1" });
            Assert(!targetIdle.Local.IsActive && targetIdle.Other.IsActive && targetIdle.Local.Status == "Aguardando controle", "when control returns, the controller row turns active");
            var alone = MachineListState.Build(new MachineListInput { LocalName = "PC1" });
            Assert(alone.Local.IsActive && !alone.Other.Visible && !alone.Local.IsController, "without sessions only this PC is shown");
            var knownController = MachineListState.Build(new MachineListInput { LocalName = "PC2", TargetName = "Máquina vinculada", KnownControllerName = "PC1", KnownControllerAddress = "10.0.0.1" });
            Assert(knownController.Other.Visible && knownController.Other.Name == "PC1" && knownController.Other.Status == "Aguardando conexão" && knownController.Other.Detail == "Máquina já reconhecida" && knownController.Local.IsActive, "a disconnected known controller keeps its name and recognition");
            var recognizedTarget = MachineListState.Build(new MachineListInput { LocalName = "PC1", TargetName = "PC2", TargetRecognized = true, KnownControllerName = "PC3" });
            Assert(recognizedTarget.Other.Name == "PC2" && recognizedTarget.Other.Status == "Clique para conectar", "a recognized target takes precedence over a known controller");
        }

        private static void TestTrustedPeers()
        {
            var code = AccessCode.Generate();
            var transcript = AccessProof.CanonicalTranscript("c", "t", "fc", "ft", new byte[32], Enumerable.Repeat((byte)1, 32).ToArray());
            var key = TrustKey.Derive(code, transcript);
            Assert(key.Length == TrustKey.Length && key.SequenceEqual(TrustKey.Derive(code, transcript)), "trust key is deterministic for the same pairing");
            Assert(!key.SequenceEqual(TrustKey.Derive(code, AccessProof.CanonicalTranscript("c", "t", "fc", "other", new byte[32], Enumerable.Repeat((byte)1, 32).ToArray()))), "trust key is bound to the certificates");
            var proof = TrustKey.Prove(key, transcript, "resume-request");
            Assert(TrustKey.Verify(key, transcript, "resume-request", proof) && !TrustKey.Verify(key, transcript, "resume-accept", proof), "resume proofs are purpose-bound");
            var peers = TrustedPeerList.Upsert(null, new TrustedPeer { DeviceId = "a", Fingerprint = "F1", ProtectedKey = new byte[1], CreatedUtcTicks = 1 });
            peers = TrustedPeerList.Upsert(peers, new TrustedPeer { DeviceId = "a", Fingerprint = "F2", ProtectedKey = new byte[1], CreatedUtcTicks = 2 });
            Assert(peers.Count == 1 && peers[0].Fingerprint == "F2", "re-pairing replaces the old record");
            Assert(TrustedPeerList.Match(peers, "a", "f2") != null && TrustedPeerList.Match(peers, "a", "F1") == null && TrustedPeerList.Match(peers, "b", "F2") == null, "trust requires the same device and certificate");
        }

        private static void TestElevationPolicy()
        {
            Assert(!ElevationPolicy.ShouldRelaunchElevated(false, false, new string[0]), "opt-in only");
            Assert(ElevationPolicy.ShouldRelaunchElevated(true, false, new string[0]), "enabled and not elevated relaunches");
            Assert(!ElevationPolicy.ShouldRelaunchElevated(true, true, new string[0]), "already elevated stays");
            Assert(!ElevationPolicy.ShouldRelaunchElevated(true, false, new[] { ElevationPolicy.RelaunchedArgument }), "a relaunched copy never loops");
        }

        private static void TestUpdateSchedule()
        {
            var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc).Ticks;
            Assert(UpdateSchedule.IsDue(UpdateCheckFrequency.Daily, 0, now), "never checked is due");
            Assert(!UpdateSchedule.IsDue(UpdateCheckFrequency.Daily, now - TimeSpan.FromHours(2).Ticks, now), "daily waits after a recent check");
            Assert(UpdateSchedule.IsDue(UpdateCheckFrequency.Daily, now - TimeSpan.FromHours(21).Ticks, now), "daily is due next day");
            Assert(!UpdateSchedule.IsDue(UpdateCheckFrequency.Weekly, now - TimeSpan.FromDays(3).Ticks, now), "weekly waits a week");
            Assert(UpdateSchedule.IsDue(UpdateCheckFrequency.Weekly, now - TimeSpan.FromDays(8).Ticks, now), "weekly is due after a week");
            Assert(!UpdateSchedule.IsDue(UpdateCheckFrequency.Never, 0, now), "never disables automatic checks");
            Assert(UpdateSchedule.IsDue(UpdateCheckFrequency.Daily, now + TimeSpan.FromDays(1).Ticks, now), "future timestamp from clock skew is due");
            Assert(UpdateSchedule.Next(UpdateSchedule.Next(UpdateSchedule.Next(UpdateCheckFrequency.Daily))) == UpdateCheckFrequency.Daily, "frequency button cycles");
        }

        private static void TestRequestAttemptOwnership()
        {
            using (var requestA = new System.Threading.CancellationTokenSource())
            using (var requestB = new System.Threading.CancellationTokenSource())
            {
            object activeRequest = requestA; var displayedState = "A: connecting";
            requestA.Cancel(); activeRequest = requestB; displayedState = "B: connecting";
            if (RequestAttemptOwnership.IsCurrent(activeRequest, requestA)) displayedState = "A: cancelled";
            Assert(displayedState == "B: connecting", "completion of cancelled A cannot overwrite B UI state");
            Assert(RequestAttemptOwnership.IsCurrent(activeRequest, requestB), "current attempt B retains UI ownership");
            Assert(requestA.IsCancellationRequested && !requestB.IsCancellationRequested, "cancelled A is distinct from active B");
            }
        }

        private static void TestHotkeys()
        {
            var gesture = HotkeyGesture.Parse("Ctrl+Shift+Alt+1");
            Assert(gesture.Modifiers == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Alt), "modifier parse");
            Expect<Exception>(() => HotkeyGesture.Parse("1"));
            Expect<Exception>(() => HotkeyGesture.Parse("Win+1"));
        }

        private static void TestHotkeyBypass()
        {
            var bypass = new HotkeyBypassDetector(HotkeyGesture.Parse("Ctrl+Shift+Alt+1"), HotkeyGesture.Parse("Ctrl+Shift+Alt+2"));
            HotkeyAction? action;
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyDown, 0x11, 0, 0, 1), out action), "ordinary ctrl remains routable");
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyDown, (ushort)'C', 0, 0, 2), out action), "Ctrl+C terminal is not a configured chord");
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyUp, (ushort)'C', 0, 0, 3), out action), "ordinary terminal key-up remains routable");
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyUp, 0x11, 0, 0, 4), out action), "ordinary ctrl key-up remains routable");
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyDown, 0x11, 0, 0, 5), out action), "chord ctrl remains routable before terminal");
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyDown, 0x10, 0, 0, 6), out action), "chord shift remains routable before terminal");
            Assert(!bypass.TryHandle(InputEvent.Key(InputKind.KeyDown, 0x12, 0, 0, 7), out action), "chord alt remains routable before terminal");
            var callbacks = 0;
            Assert(bypass.TryHandle(InputEvent.Key(InputKind.KeyDown, (ushort)'2', 0, 0, 8), out action) && action == HotkeyAction.SelectRemote, "remote chord suppresses only terminal and emits action");
            if (action.HasValue) callbacks++;
            Assert(bypass.TryHandle(InputEvent.Key(InputKind.KeyUp, (ushort)'2', 0, 0, 9), out action) && !action.HasValue, "terminal key-up is suppressed coherently");
            Assert(callbacks == 1, "chord callback emitted exactly once");
        }

        private static void TestPointerMapping()
        {
            Assert(PointerCoordinates.Normalize(100, 0, 200) == 32932, "source pixels normalize into transport space");
            Assert(PointerCoordinates.ClampNormalized(70000) == 65535 && PointerCoordinates.ClampNormalized(-2) == 0, "destination consumes normalized coordinates independently");
            foreach (var size in new[] { 1920, 2560, 3840, 1366 })
                for (var pixel = 0; pixel < size; pixel += 7)
                {
                    var norm = PointerCoordinates.ToAbsolute(pixel - 1920, -1920, size);
                    Assert(norm * (long)size / 65536 == pixel, "absolute mapping lands on the exact pixel");
                }
        }

        private static void TestPrivacyLog()
        {
            var log = new InMemoryTransactionLog(2);
            log.Add("local", "remote", "KeyDown", "sent");
            log.Add("local", "remote", "MouseMove", "coalesced");
            log.Add("local", "remote", "Heartbeat", "ok");
            var entries = log.Snapshot();
            Assert(entries.Count == 2 && entries.All(e => !e.ToString().Contains("payload") && !e.ToString().Contains("typed")), "bounded privacy log");
            log.Add("local", "remote", "Input.KeyDown", "keycode=65 payload=secret");
            Assert(log.Snapshot().Last().Status == "redacted", "input metadata log redacts content-bearing fields");
        }

        private static void TestConfig()
        {
            var config = WinputConfig.CreateDefault();
            Assert(ConfigValidator.Validate(config).Count == 0, "default config");
            config.ListenPort = 80;
            Assert(ConfigValidator.Validate(config).Count > 0, "invalid config rejected");
            config = WinputConfig.CreateDefault();
            config.PinnedDeviceId = "not-a-guid";
            config.PinnedFingerprint = "bad";
            Assert(ConfigValidator.Validate(config).Count > 0, "corrupt pin config rejected");
        }

        private static void TestManifest()
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                var key = rsa.ToXmlString(false);
                var manifest = NewManifest();
                Sign(manifest, rsa);
                string reason;
                Assert(ReleaseManifestSignature.Verify(manifest, key), "valid RSA signature verifies");
                Assert(ReleaseManifestValidator.TryValidate(manifest, "0.1.0", key, out reason), reason);
                foreach (var field in new[] { "Version", "AssetName", "AssetUrl", "Sha256", "NotesUrl" })
                {
                    var tampered = NewManifest(); Sign(tampered, rsa);
                    if (field == "Version") tampered.Version = "0.1.2";
                    if (field == "AssetName") tampered.AssetName = "WinputLan-0.1.2-setup.exe";
                    if (field == "AssetUrl") tampered.AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v0.1.2/WinputLan-0.1.2-setup.exe";
                    if (field == "Sha256") tampered.Sha256 = new string('b', 64);
                    if (field == "NotesUrl") tampered.NotesUrl = "https://github.com/luingry/WinputLan/releases/tag/v0.1.2";
                    Assert(!ReleaseManifestSignature.Verify(tampered, key), field + " tampering rejects signature");
                }
                manifest = NewManifest(); Sign(manifest, rsa); manifest.Signature = Convert.ToBase64String(new byte[256]);
                Assert(!ReleaseManifestSignature.Verify(manifest, key), "signature tampering rejects");
                manifest = NewManifest(); Sign(manifest, rsa); manifest.Algorithm = "RSA-PSS-SHA256";
                Assert(!ReleaseManifestSignature.Verify(manifest, key), "algorithm tampering rejects");
                manifest = NewManifest(); Sign(manifest, rsa); manifest.KeyId = "other-key";
                Assert(!ReleaseManifestSignature.Verify(manifest, key), "key id tampering rejects");
                manifest = NewManifest(); manifest.AssetUrl = "https://attackergithub.com/file.exe"; Sign(manifest, rsa);
                Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", key, out reason), "lookalike GitHub host rejected");
                manifest = NewManifest(); manifest.AssetName = "other.exe"; Sign(manifest, rsa);
                Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", key, out reason), "invalid asset name rejected");
                manifest = NewManifest(); manifest.Sha256 = "no"; Sign(manifest, rsa);
                Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", key, out reason), "invalid hash rejected");
                manifest = NewManifest(); manifest.Version = "0.1.0"; Sign(manifest, rsa);
                Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", key, out reason), "same version rejected");
            }
        }

        private static ReleaseManifest NewManifest()
        {
            return new ReleaseManifest { Version = "0.1.1", AssetName = "WinputLan-0.1.1-setup.exe", AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v0.1.1/WinputLan-0.1.1-setup.exe", Sha256 = new string('a', 64), NotesUrl = "https://github.com/luingry/WinputLan/releases/tag/v0.1.1", Algorithm = ReleaseManifestSignature.AlgorithmName, KeyId = ReleaseManifestSignature.KeyIdentifier };
        }

        private static void Sign(ReleaseManifest manifest, RSACryptoServiceProvider rsa)
        {
            manifest.Signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(ReleaseManifestSignature.CanonicalPayload(manifest)), CryptoConfig.MapNameToOID("SHA256")));
        }

        private static void TestBackoff()
        {
            Assert(ReconnectBackoff.DelayForAttempt(0) < ReconnectBackoff.DelayForAttempt(1), "backoff increases");
            Assert(ReconnectBackoff.DelayForAttempt(100) == TimeSpan.FromSeconds(15), "backoff caps");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private sealed class TestProtector : ISecretProtector
        {
            public byte[] Protect(byte[] plaintext) { var copy = plaintext.ToArray(); Array.Reverse(copy); return copy; }
            public byte[] Unprotect(byte[] protectedData) { var copy = protectedData.ToArray(); Array.Reverse(copy); return copy; }
        }
    }
}
