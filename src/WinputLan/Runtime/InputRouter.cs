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
        private readonly object _stateGate = new object();
        // Focus, release and input share this gate. Epoch checks also run at the transport write gate.
        private readonly SemaphoreSlim _focusGate = new SemaphoreSlim(1, 1);
        private bool _remoteActive;
        private bool _disposed;
        private bool _overflowed;
        private long _activationEpoch;
        private long _announcedEpoch = -1;

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
            EnqueueResult result;
            lock (_stateGate)
            {
                // Suppress the rest of a failed capture until the UI removes its hooks.
                if (_overflowed) return true;
                if (_disposed || !_remoteActive || _transport.State != PeerConnectionState.Connected || !_transport.AllowsInputSend) return false;
                result = _queue.Enqueue(value, _activationEpoch);
                if (result == EnqueueResult.RejectedFull)
                {
                    _overflowed = true;
                    _remoteActive = false;
                    _activationEpoch++;
                    _queue.Clear();
                }
            }
            InputAudited?.Invoke(value.Kind, result == EnqueueResult.RejectedFull ? "dropped-full" : result == EnqueueResult.CoalescedMouseMove ? "coalesced" : "queued");
            // Never wait for networking or WPF from a low-level input hook. Closing the session makes
            // the receiver release every pressed key/button even when its send path is congested.
            if (result == EnqueueResult.RejectedFull) _ = Task.Run(() => _transport.Disconnect("input queue overflow"));
            return true;
        }

        public void SetRemoteActive(bool active)
        {
            long epoch;
            lock (_stateGate)
            {
                if (_disposed || (active && _overflowed) || active == _remoteActive) return;
                _remoteActive = active;
                epoch = ++_activationEpoch;
                if (!active) _queue.Clear();
            }
            _ = ApplyFocusAsync(epoch, active, _cts.Token);
            if (!active) ReleaseAll();
        }

        public void Dispose()
        {
            lock (_stateGate) { if (_disposed) return; _disposed = true; _remoteActive = false; _activationEpoch++; _queue.Clear(); }
            _cts.Cancel();
            _transport.StateChanged -= Transport_StateChanged;
            _transport.FrameReceived -= Transport_FrameReceived;
            ReleaseAll();
            // Async gate holders may still be unwinding; don't dispose their synchronization objects.
        }

        private bool IsCurrent(long epoch, bool active)
        {
            lock (_stateGate) return !_disposed && epoch == _activationEpoch && _remoteActive == active && !_overflowed;
        }

        private async Task DrainLoopAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var entry = await _queue.DequeueEntryAsync(token).ConfigureAwait(false);
                    await _focusGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!IsCurrent(entry.Epoch, true)) { InputAudited?.Invoke(entry.Value.Kind, "dropped-inactive"); continue; }
                        await AnnounceFocusLockedAsync(entry.Epoch, token).ConfigureAwait(false);
                        var sent = await _transport.SendIfCurrentAsync(FrameType.Input, FrameCodec.EncodeInput(entry.Value), () => IsCurrent(entry.Epoch, true), token).ConfigureAwait(false);
                        InputAudited?.Invoke(entry.Value.Kind, sent ? "sent" : "dropped-inactive");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch { if (IsCurrent(entry.Epoch, true)) _transport.Disconnect("input send failed"); }
                    finally { _focusGate.Release(); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private async Task ApplyFocusAsync(long epoch, bool active, CancellationToken token)
        {
            try
            {
                await _focusGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!IsCurrent(epoch, active)) return;
                    if (active) await AnnounceFocusLockedAsync(epoch, token).ConfigureAwait(false);
                    else
                    {
                        await _transport.SendIfCurrentAsync(FrameType.ControlFocus, new byte[] { 0 }, () => IsCurrent(epoch, false), token).ConfigureAwait(false);
                        await _transport.SendIfCurrentAsync(FrameType.ReleaseAll, new byte[0], () => IsCurrent(epoch, false), token).ConfigureAwait(false);
                    }
                }
                finally { _focusGate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { if (IsCurrent(epoch, active)) _transport.Disconnect("focus send failed"); }
        }

        private async Task AnnounceFocusLockedAsync(long epoch, CancellationToken token)
        {
            if (_announcedEpoch == epoch || !IsCurrent(epoch, true)) return;
            // Also reset when a fast off/on supersedes an unfocus that hadn't reached the wire yet.
            await _transport.SendIfCurrentAsync(FrameType.ReleaseAll, new byte[0], () => IsCurrent(epoch, true), token).ConfigureAwait(false);
            if (await _transport.SendIfCurrentAsync(FrameType.ControlFocus, new byte[] { 1 }, () => IsCurrent(epoch, true), token).ConfigureAwait(false)) _announcedEpoch = epoch;
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
            lock (_stateGate)
            {
                if (state == PeerConnectionState.Connected) { _overflowed = false; return; }
                _remoteActive = false;
                _activationEpoch++;
                _queue.Clear();
            }
            ReleaseAll();
        }

        private void ReleaseAll() { (_releaseSink as IFailSafeInputSink)?.ReleaseAll(); }
    }
}
