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
        // Edge switching on this side: places the arriving cursor and reports touches of this PC's edge.
        private readonly IEdgePortalSink _portal;
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
            _portal = sink as IEdgePortalSink;
            _injector = new Thread(InjectLoop) { IsBackground = true, Name = "WinputLan input injection", Priority = ThreadPriority.Highest };
            _injector.Start();
            _transport.FrameReceived += Transport_FrameReceived;
            _transport.StateChanged += Transport_StateChanged;
        }

        public event Action<InputKind, string> InputAudited;
        // Whether the controller currently directs its mouse and keyboard at this PC.
        public event Action<bool> FocusChanged;
        // Whether the controller has edge switching on; it then owns the setting for this session.
        public event Action<bool> ControllerEdgeSwitchingChanged;
        // Edge switching events on this PC for the log: "placed" or "touched", with the fraction along the edge.
        public event Action<string, int> EdgeActivity;
        private bool _controllerEdgeSwitching;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.FrameReceived -= Transport_FrameReceived;
            _transport.StateChanged -= Transport_StateChanged;
            lock (_inputGate) { _focused = false; _epoch++; _queue.Complete(); ReleaseAll(); _portal?.SetPortalEdge(ScreenEdge.None); }
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
                int? edgeFraction = null;
                lock (_inputGate) { if (!_disposed && entry.Epoch == _epoch) HandleFrame(entry, out ack, out edgeFraction); }
                // Don't acquire the transport gate while holding the injection gate: state callbacks
                // can originate under the transport gate during connection publication.
                if (edgeFraction.HasValue) _ = SendFrameAsync(FrameType.EdgeReached, EdgePortal.EncodeReached(edgeFraction.Value));
                if (ack.HasValue) _ = SendFrameAsync(FrameType.InputAck, BitConverter.GetBytes(ack.Value));
            }
        }

        private void HandleFrame(InboundFrameQueue.Entry entry, out long? ack, out int? edgeFraction)
        {
            ack = null;
            edgeFraction = null;
            var frame = entry.Frame;
            if (!_transport.AllowsInputReceive && (frame.Type == FrameType.Input || frame.Type == FrameType.ReleaseAll || frame.Type == FrameType.EdgePortal))
            {
                InputAudited?.Invoke(InputKind.KeyDown, "dropped-direction");
                return;
            }
            if (frame.Type == FrameType.ControlFocus)
            {
                if (_transport.AllowsInputReceive && frame.Payload != null && frame.Payload.Length == 1)
                {
                    _focused = frame.Payload[0] == 1;
                    // Each focus starts without an edge; an EdgePortal frame right behind it sets one.
                    _portal?.SetPortalEdge(ScreenEdge.None);
                    if (!_focused) ReleaseAll();
                    FocusChanged?.Invoke(_focused);
                }
                return;
            }
            if (frame.Type == FrameType.EdgePortal)
            {
                ScreenEdge edge;
                int? place;
                if (_transport.State != PeerConnectionState.Connected || !EdgePortal.TryDecodePortal(frame.Payload, out edge, out place)) return;
                // Also sent outside focus, when the session starts or the controller changes the setting.
                SetControllerEdgeSwitching(EdgePortal.IsValid(edge));
                if (_portal == null || !_focused) return;
                _portal.SetPortalEdge(edge);
                if (place.HasValue && EdgePortal.IsValid(edge))
                {
                    var placed = _portal.PlaceAtEdge(edge, place.Value);
                    EdgeActivity?.Invoke(placed ? "placed" : "place-failed", place.Value);
                }
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
            int fraction;
            if (accepted && input.Kind == InputKind.MouseDelta && _portal != null && _portal.TryTakeEdgeTouch(out fraction)) { edgeFraction = fraction; EdgeActivity?.Invoke("touched", fraction); }
        }

        private void SetControllerEdgeSwitching(bool on)
        {
            if (on == _controllerEdgeSwitching) return;
            _controllerEdgeSwitching = on;
            ControllerEdgeSwitchingChanged?.Invoke(on);
        }

        private async System.Threading.Tasks.Task SendFrameAsync(FrameType type, byte[] payload)
        {
            try { await _transport.SendAsync(type, payload, CancellationToken.None).ConfigureAwait(false); }
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
                _portal?.SetPortalEdge(ScreenEdge.None);
                SetControllerEdgeSwitching(false);
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
