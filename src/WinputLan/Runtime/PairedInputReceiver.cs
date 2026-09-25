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
        private volatile bool _disposed;
        private readonly object _inputGate = new object();
        // The socket reader only queues frames; this thread injects them. Motion that arrives while an
        // injection is slow merges in the queue instead of piling up in the TCP buffer as lag.
        private readonly InboundFrameQueue _queue = new InboundFrameQueue();
        private readonly Thread _injector;
        // Bumped on every transport state change: frames queued before it are never injected after it.
        private long _epoch;
        private int _lastAckTick;
        // Third safeguard: input is injected only while the controller has announced focus on this PC.
        private volatile bool _focused;
        // Motion latency is sampled, not echoed per event: one ACK per mouse sample doubled traffic on the hot path.
        private const int AckIntervalMs = 100;

        public PairedInputReceiver(PeerTransport transport, IInputSink sink)
        {
            _transport = transport ?? throw new ArgumentNullException("transport");
            _sink = sink ?? throw new ArgumentNullException("sink");
            _injector = new Thread(InjectLoop) { IsBackground = true, Name = "WinputLan input injection", Priority = ThreadPriority.Highest };
            _injector.Start();
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
            lock (_inputGate) { _focused = false; _epoch++; _queue.Complete(); ReleaseAll(); }
        }

        private void Transport_FrameReceived(Frame frame)
        {
            if (!_disposed) _queue.Enqueue(frame, Interlocked.Read(ref _epoch));
        }

        private void InjectLoop()
        {
            InboundFrameQueue.Entry entry;
            while (_queue.TryDequeue(out entry))
            {
                // A disconnect must release inputs after an in-flight injection has finished, never before.
                long? ack = null;
                lock (_inputGate) { if (!_disposed && entry.Epoch == _epoch) HandleFrame(entry, out ack); }
                // Don't acquire the transport gate while holding the injection gate: state callbacks
                // can originate under the transport gate during connection publication.
                if (ack.HasValue) _ = SendAckAsync(ack.Value);
            }
        }

        private void HandleFrame(InboundFrameQueue.Entry entry, out long? ack)
        {
            ack = null;
            var frame = entry.Frame;
            if (!_transport.AllowsInputReceive && (frame.Type == FrameType.Input || frame.Type == FrameType.ReleaseAll))
            {
                InputAudited?.Invoke(InputKind.KeyDown, "dropped-direction");
                return;
            }
            if (frame.Type == FrameType.ControlFocus)
            {
                if (_transport.AllowsInputReceive && frame.Payload != null && frame.Payload.Length == 1) { _focused = frame.Payload[0] == 1; if (!_focused) ReleaseAll(); FocusChanged?.Invoke(_focused); }
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
            var input = entry.Input;
            if (input == null) { InputAudited?.Invoke(InputKind.KeyDown, "dropped-invalid"); return; }
            bool accepted;
            try { accepted = _sink.Publish(input); }
            catch { InputAudited?.Invoke(InputKind.KeyDown, "dropped-invalid"); return; }
            InputAudited?.Invoke(input.Kind, accepted ? "received" : "dropped-sink");
            var tick = Environment.TickCount;
            var motion = input.Kind == InputKind.MouseDelta || input.Kind == InputKind.MouseMove;
            if (accepted && (!motion || unchecked(tick - _lastAckTick) >= AckIntervalMs)) { _lastAckTick = tick; ack = input.TimestampUtcTicks; }
        }

        private async System.Threading.Tasks.Task SendAckAsync(long timestampUtcTicks)
        {
            try { await _transport.SendAsync(FrameType.InputAck, BitConverter.GetBytes(timestampUtcTicks), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            // Every session starts unfocused; only an explicit ControlFocus(1) opens the gate.
            lock (_inputGate)
            {
                _focused = false;
                Interlocked.Increment(ref _epoch);
                _queue.Clear();
                if (state != PeerConnectionState.Connected) { ReleaseAll(); FocusChanged?.Invoke(false); }
            }
        }

        private void ReleaseAll()
        {
            var safe = _sink as IFailSafeInputSink;
            if (safe != null) safe.ReleaseAll();
        }
    }
}
