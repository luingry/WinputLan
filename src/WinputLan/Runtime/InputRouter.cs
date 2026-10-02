using System;
using System.Diagnostics;
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
        // Continuous motion leaves at most once per interval, merged. A 1000 Hz mouse otherwise costs a TLS
        // record and a packet per sample, which Wi-Fi turns into jitter. Motion after a pause goes at once.
        public static readonly TimeSpan DefaultMotionInterval = TimeSpan.FromMilliseconds(4);
        private readonly long _motionIntervalTicks;
        private long _lastMotionTimestamp;
        // The drain runs on its own thread at the hook's priority. On the thread pool every event waited for a
        // pool wake-up (SemaphoreSlim queues async waiters there on .NET Framework) and an I/O completion, at
        // normal priority, so a busy controller added jitter between the hook and the wire.
        private readonly Thread _drain;

        public InputRouter(InputEventQueue queue, PeerTransport transport, IInputSink releaseSink) : this(queue, transport, releaseSink, DefaultMotionInterval) { }

        public InputRouter(InputEventQueue queue, PeerTransport transport, IInputSink releaseSink, TimeSpan motionInterval)
        {
            _queue = queue ?? throw new ArgumentNullException("queue");
            _transport = transport ?? throw new ArgumentNullException("transport");
            _releaseSink = releaseSink ?? throw new ArgumentNullException("releaseSink");
            if (motionInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException("motionInterval");
            _motionIntervalTicks = (long)(motionInterval.TotalSeconds * Stopwatch.Frequency);
            _transport.StateChanged += Transport_StateChanged;
            _transport.FrameReceived += Transport_FrameReceived;
            var token = _cts.Token;
            _drain = new Thread(() => DrainLoop(token)) { IsBackground = true, Name = "WinputLan input send", Priority = ThreadPriority.Highest };
            _drain.Start();
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
            // Sends block, so they leave the caller (UI or hotkey thread); epochs make their order irrelevant.
            var token = _cts.Token;
            _ = Task.Run(() => ApplyFocus(epoch, active, token));
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

        private void DrainLoop(CancellationToken token)
        {
            var pacer = _motionIntervalTicks > 0 ? HighResolutionWait.TryCreate() : null;
            try
            {
                while (true)
                {
                    var entry = _queue.DequeueEntry(token);
                    if (entry.Value.Kind == InputKind.MouseDelta) entry = PaceMotion(entry, pacer, token);
                    _focusGate.Wait(token);
                    try
                    {
                        if (!IsCurrent(entry.Epoch, true)) { InputAudited?.Invoke(entry.Value.Kind, "dropped-inactive"); continue; }
                        AnnounceFocusLocked(entry.Epoch, token);
                        var sent = _transport.SendIfCurrent(FrameType.Input, FrameCodec.EncodeInput(entry.Value), () => IsCurrent(entry.Epoch, true), token);
                        InputAudited?.Invoke(entry.Value.Kind, sent ? "sent" : "dropped-inactive");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch { if (IsCurrent(entry.Epoch, true)) _transport.Disconnect("input send failed"); }
                    finally { _focusGate.Release(); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            // A dedicated thread must never let an exception escape: that would end the process.
            catch { }
            finally { pacer?.Dispose(); }
        }

        // Holds a motion until the interval since the previous one has passed, folding in motion that arrives
        // meanwhile. Stops early when a click, key or wheel queues behind it, so those are never delayed.
        private InputEventQueue.Entry PaceMotion(InputEventQueue.Entry entry, HighResolutionWait pacer, CancellationToken token)
        {
            if (pacer != null)
            {
                var due = _lastMotionTimestamp + _motionIntervalTicks;
                var now = Stopwatch.GetTimestamp();
                while (now < due && !token.IsCancellationRequested && _queue.OnlyMotionPending(entry.Epoch) && IsCurrent(entry.Epoch, true))
                {
                    // Short slices keep an arriving click from waiting out the whole interval.
                    var slice = Math.Min(due - now, Stopwatch.Frequency / 1000);
                    if (!pacer.Wait(TimeSpan.FromTicks(slice * TimeSpan.TicksPerSecond / Stopwatch.Frequency))) break;
                    now = Stopwatch.GetTimestamp();
                }
            }
            _lastMotionTimestamp = Stopwatch.GetTimestamp();
            return _queue.MergeFollowingMotion(entry);
        }

        private void ApplyFocus(long epoch, bool active, CancellationToken token)
        {
            try
            {
                _focusGate.Wait(token);
                try
                {
                    if (!IsCurrent(epoch, active)) return;
                    if (active) AnnounceFocusLocked(epoch, token);
                    else
                    {
                        _transport.SendIfCurrent(FrameType.ControlFocus, new byte[] { 0 }, () => IsCurrent(epoch, false), token);
                        _transport.SendIfCurrent(FrameType.ReleaseAll, new byte[0], () => IsCurrent(epoch, false), token);
                    }
                }
                finally { _focusGate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { if (IsCurrent(epoch, active)) _transport.Disconnect("focus send failed"); }
        }

        private void AnnounceFocusLocked(long epoch, CancellationToken token)
        {
            if (_announcedEpoch == epoch || !IsCurrent(epoch, true)) return;
            // Also reset when a fast off/on supersedes an unfocus that hadn't reached the wire yet.
            _transport.SendIfCurrent(FrameType.ReleaseAll, new byte[0], () => IsCurrent(epoch, true), token);
            if (_transport.SendIfCurrent(FrameType.ControlFocus, new byte[] { 1 }, () => IsCurrent(epoch, true), token)) _announcedEpoch = epoch;
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
