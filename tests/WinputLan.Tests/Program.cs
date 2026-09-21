using System;
using System.IO;
using System.Linq;
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
            Run("pin store abstraction", TestPins);
            Run("queue coalescing and order", TestQueue);
            Run("hotkey validation", TestHotkeys);
            Run("hotkey hook bypass", TestHotkeyBypass);
            Run("absolute pointer mapping", TestPointerMapping);
            Run("privacy log", TestPrivacyLog);
            Run("configuration corruption validation", TestConfig);
            Run("release manifest invariants", TestManifest);
            Run("reconnect backoff bounds", TestBackoff);
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
            string reason;
            var manifest = new ReleaseManifest { Version = "0.1.1", AssetName = "WinputLan-0.1.1-setup.exe", AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v0.1.1/WinputLan-0.1.1-setup.exe", Sha256 = new string('a', 64), AuthenticodeRequired = true };
            Assert(ReleaseManifestValidator.TryValidate(manifest, "0.1.0", out reason), reason);
            manifest.AssetName = "WinputLan-0.1.1-setup.exe";
            Assert(ReleaseManifestValidator.TryValidate(manifest, "0.1.0", out reason), "setup manifest accepted");
            manifest.AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v0.1.1/other-setup.exe";
            Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", out reason), "asset URL/name mismatch rejected");
            manifest.AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v0.1.1/WinputLan-0.1.1-setup.exe";
            manifest.AssetUrl = "http://example.com/file.exe";
            Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", out reason), "non-GitHub URL rejected");
            manifest.AssetUrl = "https://attackergithub.com/file.exe";
            Assert(!ReleaseManifestValidator.TryValidate(manifest, "0.1.0", out reason), "lookalike GitHub host rejected");
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
