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
        public sealed class Entry
        {
            public InputEvent Value { get; private set; }
            public long Epoch { get; private set; }
            internal Entry(InputEvent value, long epoch) { Value = value; Epoch = epoch; }
        }
        private readonly object _gate = new object();
        private readonly LinkedList<Entry> _items = new LinkedList<Entry>();
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
            return Enqueue(value, 0);
        }

        public EnqueueResult Enqueue(InputEvent value, long epoch)
        {
            if (value == null) throw new ArgumentNullException("value");
            lock (_gate)
            {
                if (value.Kind == InputKind.MouseDelta && _items.Last != null && _items.Last.Value.Epoch == epoch && _items.Last.Value.Value.Kind == InputKind.MouseDelta)
                {
                    // Relative motion must never be dropped: merge into the unsent tail instead.
                    var tail = _items.Last.Value.Value;
                    _items.Last.Value = new Entry(InputEvent.MouseDelta(tail.X + value.X, tail.Y + value.Y, tail.TimestampUtcTicks), epoch);
                    return EnqueueResult.CoalescedMouseMove;
                }
                if (value.Kind == InputKind.MouseMove)
                {
                    if (_items.Last != null && _items.Last.Value.Epoch == epoch && _items.Last.Value.Value.Kind == InputKind.MouseMove)
                    {
                        _items.Last.Value = new Entry(value, epoch);
                        return EnqueueResult.CoalescedMouseMove;
                    }
                    if (_items.Count >= _capacity) return EnqueueResult.RejectedFull;
                    _items.AddLast(new Entry(value, epoch));
                    _available.Release();
                    return EnqueueResult.Accepted;
                }
                if (_items.Count >= _capacity) return EnqueueResult.RejectedFull;
                _items.AddLast(new Entry(value, epoch));
                _available.Release();
                return EnqueueResult.Accepted;
            }
        }

        // A signal-driven dequeue replaces the former fixed polling delay in the input hot path.
        public async Task<InputEvent> DequeueAsync(CancellationToken cancellationToken)
        {
            return (await DequeueEntryAsync(cancellationToken).ConfigureAwait(false)).Value;
        }

        public async Task<Entry> DequeueEntryAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                Entry value;
                if (TryDequeueEntry(out value)) return value;
            }
        }

        public bool TryDequeue(out InputEvent value)
        {
            Entry entry;
            var found = TryDequeueEntry(out entry);
            value = found ? entry.Value : null;
            return found;
        }

        private bool TryDequeueEntry(out Entry value)
        {
            lock (_gate)
            {
                if (_items.First == null) { value = null; return false; }
                value = _items.First.Value;
                _items.RemoveFirst();
                return true;
            }
        }

        // True while nothing but motion of this epoch waits, so a paced motion may keep waiting without
        // holding back a click, key or wheel behind it.
        public bool OnlyMotionPending(long epoch)
        {
            lock (_gate)
            {
                foreach (var item in _items) if (item.Epoch != epoch || item.Value.Kind != InputKind.MouseDelta) return false;
                return true;
            }
        }

        // Folds motion waiting at the head into a dequeued motion; the result keeps the oldest timestamp.
        public Entry MergeFollowingMotion(Entry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.Value.Kind != InputKind.MouseDelta) return entry;
            lock (_gate)
            {
                var x = entry.Value.X;
                var y = entry.Value.Y;
                var merged = false;
                while (_items.First != null && _items.First.Value.Epoch == entry.Epoch && _items.First.Value.Value.Kind == InputKind.MouseDelta)
                {
                    x += _items.First.Value.Value.X;
                    y += _items.First.Value.Value.Y;
                    _items.RemoveFirst();
                    // The removed item's permit must go with it, or the drain would wake for nothing.
                    _available.Wait(0);
                    merged = true;
                }
                return merged ? new Entry(InputEvent.MouseDelta(x, y, entry.Value.TimestampUtcTicks), entry.Epoch) : entry;
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
