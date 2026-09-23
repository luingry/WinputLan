using System;
using System.Threading;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    // The receiver is deliberately bound to one transport: it accepts input only after that transport is paired.
    public sealed class PairedInputReceiver : IDisposable
    {
        private readonly PeerTransport _transport;
        private readonly IInputSink _sink;
        private bool _disposed;
        private int _lastAckTick;
        // Third safeguard: input is injected only while the controller has announced focus on this PC.
        private volatile bool _focused;
        // Motion latency is sampled, not echoed per event: one ACK per mouse sample doubled traffic on the hot path.
        private const int AckIntervalMs = 100;

        public PairedInputReceiver(PeerTransport transport, IInputSink sink)
        {
            _transport = transport ?? throw new ArgumentNullException("transport");
            _sink = sink ?? throw new ArgumentNullException("sink");
            _transport.FrameReceived += Transport_FrameReceived;
            _transport.StateChanged += Transport_StateChanged;
        }

        public event Action<InputKind, string> InputAudited;
        // Whether the controller currently directs its mouse and keyboard at this PC.
        public event Action<bool> FocusChanged;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.FrameReceived -= Transport_FrameReceived;
            _transport.StateChanged -= Transport_StateChanged;
            ReleaseAll();
        }

        private void Transport_FrameReceived(Frame frame)
        {
            if (!_transport.AllowsInputReceive && (frame.Type == FrameType.Input || frame.Type == FrameType.ReleaseAll))
            {
                InputAudited?.Invoke(InputKind.KeyDown, "dropped-direction");
                return;
            }
            if (frame.Type == FrameType.ControlFocus)
            {
                if (_transport.AllowsInputReceive && frame.Payload != null && frame.Payload.Length == 1) { _focused = frame.Payload[0] == 1; FocusChanged?.Invoke(_focused); }
                return;
            }
            if (frame.Type == FrameType.ReleaseAll)
            {
                if (_transport.State == PeerConnectionState.Connected) { ReleaseAll(); InputAudited?.Invoke(InputKind.KeyUp, "received-release"); }
                return;
            }
            if (frame.Type != FrameType.Input) return;
            if (_transport.State != PeerConnectionState.Connected) { InputAudited?.Invoke(InputKind.KeyDown, "dropped-unpaired"); return; }
            if (!_focused) { InputAudited?.Invoke(InputKind.KeyDown, "dropped-unfocused"); return; }
            try
            {
                var input = FrameCodec.DecodeInput(frame.Payload);
                var accepted = _sink.Publish(input);
                InputAudited?.Invoke(input.Kind, accepted ? "received" : "dropped-sink");
                var tick = Environment.TickCount;
                var motion = input.Kind == InputKind.MouseDelta || input.Kind == InputKind.MouseMove;
                if (accepted && (!motion || unchecked(tick - _lastAckTick) >= AckIntervalMs)) { _lastAckTick = tick; _ = SendAckAsync(input.TimestampUtcTicks); }
            }
            catch { InputAudited?.Invoke(InputKind.KeyDown, "dropped-invalid"); }
        }

        private async System.Threading.Tasks.Task SendAckAsync(long timestampUtcTicks)
        {
            try { await _transport.SendAsync(FrameType.InputAck, BitConverter.GetBytes(timestampUtcTicks), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            // Every session starts unfocused; only an explicit ControlFocus(1) opens the gate.
            _focused = false;
            if (state != PeerConnectionState.Connected) { ReleaseAll(); FocusChanged?.Invoke(false); }
        }

        private void ReleaseAll()
        {
            var safe = _sink as IFailSafeInputSink;
            if (safe != null) safe.ReleaseAll();
        }
    }
}
