using System;
using System.Collections.Generic;

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
                if (value.Kind == InputKind.MouseMove)
                {
                    if (_items.Last != null && _items.Last.Value.Kind == InputKind.MouseMove)
                    {
                        _items.Last.Value = value;
                        return EnqueueResult.CoalescedMouseMove;
                    }
                    if (_items.Count >= _capacity) return EnqueueResult.RejectedFull;
                    _items.AddLast(value);
                    return EnqueueResult.Accepted;
                }
                if (_items.Count >= _capacity) return EnqueueResult.RejectedFull;
                _items.AddLast(value);
                return EnqueueResult.Accepted;
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
            lock (_gate) _items.Clear();
        }
    }
}
