using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
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
        // Each activation must reach the target as ControlFocus(1) before its first input, because the
        // target discards input while it is not focused. The drain loop waits for this announcement.
        private readonly SemaphoreSlim _focusGate = new SemaphoreSlim(1, 1);
        private long _activationEpoch;
        private long _announcedEpoch;

        public InputRouter(InputEventQueue queue, PeerTransport transport, IInputSink releaseSink)
        {
            _queue = queue ?? throw new ArgumentNullException("queue");
            _transport = transport ?? throw new ArgumentNullException("transport");
            _releaseSink = releaseSink ?? throw new ArgumentNullException("releaseSink");
            _transport.StateChanged += Transport_StateChanged;
            _transport.FrameReceived += Transport_FrameReceived;
            _ = DrainLoopAsync(_cts.Token);
        }

        public event Action<InputKind, string> InputAudited;
        public event Action<TimeSpan> InputLatencyMeasured;

        public bool Publish(InputEvent value)
        {
            if (!_remoteActive || _transport.State != PeerConnectionState.Connected || !_transport.AllowsInputSend) return false;
            var result = _queue.Enqueue(value);
            InputAudited?.Invoke(value.Kind, result == EnqueueResult.RejectedFull ? "dropped-full" : result == EnqueueResult.CoalescedMouseMove ? "coalesced" : "queued");
            return result != EnqueueResult.RejectedFull;
        }

        public void SetRemoteActive(bool active)
        {
            var wasActive = _remoteActive;
            if (active && !wasActive) Interlocked.Increment(ref _activationEpoch);
            _remoteActive = active;
            if (active && !wasActive && _transport.State == PeerConnectionState.Connected) _ = AnnounceFocusAsync(_cts.Token);
            if (!active)
            {
                if (wasActive && _transport.State == PeerConnectionState.Connected) _ = SendUnfocusAndReleaseAsync();
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
            _transport.FrameReceived -= Transport_FrameReceived;
            _queue.Clear();
            ReleaseAll();
            _cts.Dispose();
        }

        private async Task DrainLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var value = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await AnnounceFocusAsync(cancellationToken).ConfigureAwait(false);
                    // Control was handed back while this item waited: it belongs to no active session.
                    if (!_remoteActive) { InputAudited?.Invoke(value.Kind, "dropped-inactive"); continue; }
                    await _transport.SendAsync(FrameType.Input, FrameCodec.EncodeInput(value), cancellationToken).ConfigureAwait(false); InputAudited?.Invoke(value.Kind, "sent");
                }
                catch { FailSafe(); InputAudited?.Invoke(value.Kind, "dropped-disconnected"); }
            }
        }

        private void Transport_FrameReceived(Frame frame)
        {
            if (frame.Type != FrameType.InputAck || frame.Payload == null || frame.Payload.Length != sizeof(long)) return;
            var sentTicks = BitConverter.ToInt64(frame.Payload, 0);
            if (sentTicks <= 0 || sentTicks > DateTime.UtcNow.Ticks) return;
            InputLatencyMeasured?.Invoke(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - sentTicks));
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

        private async Task AnnounceFocusAsync(CancellationToken cancellationToken)
        {
            var epoch = Interlocked.Read(ref _activationEpoch);
            if (!_remoteActive || Interlocked.Read(ref _announcedEpoch) == epoch) return;
            await _focusGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_remoteActive || Interlocked.Read(ref _announcedEpoch) == epoch) return;
                await _transport.SendAsync(FrameType.ControlFocus, new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _announcedEpoch, epoch);
            }
            finally { _focusGate.Release(); }
        }

        private async Task SendUnfocusAndReleaseAsync()
        {
            try { await _transport.SendAsync(FrameType.ControlFocus, new byte[] { 0 }, _cts.Token).ConfigureAwait(false); } catch { }
            await SendReleaseAsync().ConfigureAwait(false);
        }

        private async Task SendReleaseAsync()
        {
            try { await _transport.SendAsync(FrameType.ReleaseAll, new byte[0], _cts.Token).ConfigureAwait(false); InputAudited?.Invoke(InputKind.KeyUp, "sent-release"); }
            catch { InputAudited?.Invoke(InputKind.KeyUp, "dropped-release"); }
        }
    }
}
