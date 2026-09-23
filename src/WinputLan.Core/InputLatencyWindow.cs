using System;
using System.Collections.Generic;
using System.Linq;

namespace WinputLan.Core
{
    public sealed class InputLatencyWindow
    {
        private readonly object _gate = new object();
        private readonly Queue<double> _values = new Queue<double>();
        private readonly int _capacity;
        private DateTime _lastPublishedUtc = DateTime.MinValue;
        public InputLatencyWindow(int capacity = 60) { _capacity = capacity; }
        public void Record(TimeSpan latency)
        {
            if (latency < TimeSpan.Zero || latency > TimeSpan.FromMinutes(1)) return;
            lock (_gate) { _values.Enqueue(latency.TotalMilliseconds); while (_values.Count > _capacity) _values.Dequeue(); }
        }
        public bool TryGetP50(DateTime utcNow, TimeSpan minimumInterval, out double p50Milliseconds)
        {
            lock (_gate)
            {
                p50Milliseconds = 0;
                if (_values.Count == 0 || utcNow - _lastPublishedUtc < minimumInterval) return false;
                var sorted = _values.OrderBy(value => value).ToArray();
                p50Milliseconds = sorted[sorted.Length / 2]; _lastPublishedUtc = utcNow; return true;
            }
        }
    }
}
