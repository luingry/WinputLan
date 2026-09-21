using System;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class InputRouter : IInputSink, IDisposable
    {
        private readonly InputEventQueue _queue;
        private readonly PeerTransport _transport;
        private readonly IInputSink _releaseSink;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile bool _remoteActive;
        private bool _disposed;

        public InputRouter(InputEventQueue queue, PeerTransport transport, IInputSink releaseSink)
        {
            _queue = queue ?? throw new ArgumentNullException("queue");
            _transport = transport ?? throw new ArgumentNullException("transport");
            _releaseSink = releaseSink ?? throw new ArgumentNullException("releaseSink");
            _transport.StateChanged += Transport_StateChanged;
            _ = DrainLoopAsync(_cts.Token);
        }

        public event Action<InputKind, string> InputAudited;

        public bool Publish(InputEvent value)
        {
            if (!_remoteActive || _transport.State != PeerConnectionState.Connected) return false;
            var result = _queue.Enqueue(value);
            InputAudited?.Invoke(value.Kind, result == EnqueueResult.RejectedFull ? "dropped-full" : result == EnqueueResult.CoalescedMouseMove ? "coalesced" : "queued");
            return result != EnqueueResult.RejectedFull;
        }

        public void SetRemoteActive(bool active)
        {
            _remoteActive = active;
            if (!active)
            {
                _queue.Clear();
                ReleaseAll();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _remoteActive = false;
            _cts.Cancel();
            _transport.StateChanged -= Transport_StateChanged;
            _queue.Clear();
            ReleaseAll();
            _cts.Dispose();
        }

        private async Task DrainLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                InputEvent value;
                if (!_queue.TryDequeue(out value)) { await Task.Delay(2, cancellationToken).ConfigureAwait(false); continue; }
                try { await _transport.SendAsync(FrameType.Input, FrameCodec.EncodeInput(value), cancellationToken).ConfigureAwait(false); InputAudited?.Invoke(value.Kind, "sent"); }
                catch { FailSafe(); InputAudited?.Invoke(value.Kind, "dropped-disconnected"); }
            }
        }

        private void Transport_StateChanged(PeerConnectionState state, string detail)
        {
            if (state != PeerConnectionState.Connected) FailSafe();
        }

        private void FailSafe()
        {
            _remoteActive = false;
            _queue.Clear();
            ReleaseAll();
        }

        private void ReleaseAll()
        {
            var releasing = _releaseSink as IFailSafeInputSink;
            if (releasing != null) releasing.ReleaseAll();
        }
    }
}
