using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinputLan.Core
{
    public enum EnqueueResult
    {
        Accepted,
        CoalescedMouseMove,
        RejectedFull
    }

    public sealed class InputEventQueue
    {
        private readonly object _gate = new object();
        private readonly LinkedList<InputEvent> _items = new LinkedList<InputEvent>();
        private readonly int _capacity;
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);

        public InputEventQueue(int capacity = 256)
        {
            if (capacity < 8) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
        }

        public int Count { get { lock (_gate) return _items.Count; } }
        public int Capacity { get { return _capacity; } }

        public EnqueueResult Enqueue(InputEvent value)
        {
            if (value == null) throw new ArgumentNullException("value");
            lock (_gate)
            {
                if (value.Kind == InputKind.MouseDelta && _items.Last != null && _items.Last.Value.Kind == InputKind.MouseDelta)
                {
                    // Relative motion must never be dropped: merge into the unsent tail instead.
                    var tail = _items.Last.Value;
                    _items.Last.Value = InputEvent.MouseDelta(tail.X + value.X, tail.Y + value.Y, tail.TimestampUtcTicks);
                    return EnqueueResult.CoalescedMouseMove;
                }
                if (value.Kind == InputKind.MouseMove)
                {
                    if (_items.Last != null && _items.Last.Value.Kind == InputKind.MouseMove)
                    {
                        _items.Last.Value = value;
                        return EnqueueResult.CoalescedMouseMove;
                    }
                    if (_items.Count >= _capacity) return EnqueueResult.RejectedFull;
                    _items.AddLast(value);
                    _available.Release();
                    return EnqueueResult.Accepted;
                }
                if (_items.Count >= _capacity) return EnqueueResult.RejectedFull;
                _items.AddLast(value);
                _available.Release();
                return EnqueueResult.Accepted;
            }
        }

        // A signal-driven dequeue replaces the former fixed polling delay in the input hot path.
        public async Task<InputEvent> DequeueAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                InputEvent value;
                if (TryDequeue(out value)) return value;
            }
        }

        public bool TryDequeue(out InputEvent value)
        {
            lock (_gate)
            {
                if (_items.First == null) { value = null; return false; }
                value = _items.First.Value;
                _items.RemoveFirst();
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _items.Clear();
                // Keep semaphore permits aligned with queued items. Holding _gate prevents
                // a concurrent producer from having its fresh signal drained here.
                while (_available.Wait(0)) { }
            }
        }
    }
}
