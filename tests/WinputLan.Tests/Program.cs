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
            Run("pairing transcript, SAS and HKDF", TestPairing);
            Run("access proof rejects certificate substitution", TestAccessProof);
            Run("pin store abstraction", TestPins);
            Run("queue coalescing and order", TestQueue);
            Run("queue coalesce clear leaves no phantom permit", TestQueueSignals);
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
            Assert(AccessCode.IsValid(code) && AccessCode.Format(code).Split(' ').Length == 4, "access code is a grouped 16-character Base32 value");
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
        }

        private static void TestInputRouting()
        {
            var routing = new InputRoutingState();
            uint ctrl = InputRoutingState.KeyId(0xA2), two = InputRoutingState.KeyId((ushort)'2'), a = InputRoutingState.KeyId((ushort)'A');
            // Switch to remote while the chord modifier is physically held: its release must stay local.
            Assert(routing.Press(ctrl) == InputRoute.Local, "modifier pressed before switching is local");
            routing.SetRemoteActive(true);
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
