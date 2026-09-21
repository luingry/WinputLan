using System;
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
        }

        public event Action<InputKind, string> InputAudited;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.FrameReceived -= Transport_FrameReceived;
        }

        private void Transport_FrameReceived(Frame frame)
        {
            if (frame.Type != FrameType.Input) return;
            if (_transport.State != PeerConnectionState.Connected) { InputAudited?.Invoke(InputKind.KeyDown, "dropped-unpaired"); return; }
            try
            {
                var input = FrameCodec.DecodeInput(frame.Payload);
                InputAudited?.Invoke(input.Kind, _sink.Publish(input) ? "received" : "dropped-sink");
            }
            catch { InputAudited?.Invoke(InputKind.KeyDown, "dropped-invalid"); }
        }
    }
}
