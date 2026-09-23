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

        public PairedInputReceiver(PeerTransport transport, IInputSink sink)
        {
            _transport = transport ?? throw new ArgumentNullException("transport");
            _sink = sink ?? throw new ArgumentNullException("sink");
            _transport.FrameReceived += Transport_FrameReceived;
            _transport.StateChanged += Transport_StateChanged;
        }

        public event Action<InputKind, string> InputAudited;

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
            if (frame.Type == FrameType.ReleaseAll)
            {
                if (_transport.State == PeerConnectionState.Connected) { ReleaseAll(); InputAudited?.Invoke(InputKind.KeyUp, "received-release"); }
                return;
            }
            if (frame.Type != FrameType.Input) return;
            if (_transport.State != PeerConnectionState.Connected) { InputAudited?.Invoke(InputKind.KeyDown, "dropped-unpaired"); return; }
            try
            {
                var input = FrameCodec.DecodeInput(frame.Payload);
                var accepted = _sink.Publish(input);
                InputAudited?.Invoke(input.Kind, accepted ? "received" : "dropped-sink");
                if (accepted) _ = SendAckAsync(input.TimestampUtcTicks);
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
            if (state != PeerConnectionState.Connected) ReleaseAll();
        }

        private void ReleaseAll()
        {
            var safe = _sink as IFailSafeInputSink;
            if (safe != null) safe.ReleaseAll();
        }
    }
}
