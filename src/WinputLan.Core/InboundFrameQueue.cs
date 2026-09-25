using System.Collections.Generic;
using System.Threading;

namespace WinputLan.Core
{
    // Frames wait here between the socket and the injector. Consecutive motions merge while they wait,
    // so a busy target (e.g. repainting a dragged window) applies the sum at once instead of replaying
    // a growing backlog one event at a time. Every other frame keeps its place and order.
    public sealed class InboundFrameQueue
    {
        public sealed class Entry
        {
            internal Entry(Frame frame, InputEvent input, long epoch) { Frame = frame; Input = input; Epoch = epoch; }
            public Frame Frame { get; private set; }
            // Decoded input for Input frames; null for other frames or an undecodable payload.
            public InputEvent Input { get; private set; }
            public long Epoch { get; private set; }
        }

        private readonly object _gate = new object();
        private readonly LinkedList<Entry> _items = new LinkedList<Entry>();
        private bool _completed;

        public int Count { get { lock (_gate) return _items.Count; } }

        // Returns true when the frame merged into the waiting tail instead of adding an entry.
        public bool Enqueue(Frame frame, long epoch)
        {
            InputEvent input = null;
            if (frame.Type == FrameType.Input)
            {
                try { input = FrameCodec.DecodeInput(frame.Payload); }
                catch (System.IO.InvalidDataException) { }
            }
            lock (_gate)
            {
                if (_completed) return false;
                var tail = _items.Last == null ? null : _items.Last.Value;
                if (input != null && tail != null && tail.Epoch == epoch && tail.Input != null && tail.Input.Kind == input.Kind)
                {
                    // The oldest timestamp is kept, so the sampled ACK still reports the full wait.
                    if (input.Kind == InputKind.MouseDelta) { _items.Last.Value = new Entry(tail.Frame, InputEvent.MouseDelta(tail.Input.X + input.X, tail.Input.Y + input.Y, tail.Input.TimestampUtcTicks), epoch); return true; }
                    if (input.Kind == InputKind.MouseMove) { _items.Last.Value = new Entry(frame, input, epoch); return true; }
                }
                _items.AddLast(new Entry(frame, input, epoch));
                Monitor.Pulse(_gate);
                return false;
            }
        }

        // Blocks until an entry is available; returns false once the queue is completed.
        public bool TryDequeue(out Entry entry)
        {
            lock (_gate)
            {
                while (_items.First == null && !_completed) Monitor.Wait(_gate);
                if (_items.First == null) { entry = null; return false; }
                entry = _items.First.Value;
                _items.RemoveFirst();
                return true;
            }
        }

        public void Clear() { lock (_gate) _items.Clear(); }

        public void Complete() { lock (_gate) { _completed = true; _items.Clear(); Monitor.PulseAll(_gate); } }
    }
}
