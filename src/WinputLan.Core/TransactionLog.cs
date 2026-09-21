using System;
using System.Collections.Generic;
using System.Linq;

namespace WinputLan.Core
{
    public sealed class TransactionLogEntry
    {
        public DateTime TimestampUtc { get; set; }
        public string Origin { get; set; }
        public string Destination { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }

        public override string ToString()
        {
            return string.Format("{0:HH:mm:ss}  {1} -> {2}  {3}  {4}", TimestampUtc.ToLocalTime(), Origin, Destination, Type, Status);
        }
    }

    public sealed class InMemoryTransactionLog
    {
        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly LinkedList<TransactionLogEntry> _entries = new LinkedList<TransactionLogEntry>();

        public InMemoryTransactionLog(int capacity = 500)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
        }

        public void Add(string origin, string destination, string type, string status)
        {
            var entry = new TransactionLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Origin = SafeToken(origin),
                Destination = SafeToken(destination),
                Type = SafeToken(type),
                Status = SafeToken(status)
            };
            lock (_gate)
            {
                _entries.AddLast(entry);
                while (_entries.Count > _capacity) _entries.RemoveFirst();
            }
        }

        public IReadOnlyList<TransactionLogEntry> Snapshot()
        {
            lock (_gate) return _entries.ToList().AsReadOnly();
        }

        public void Clear() { lock (_gate) _entries.Clear(); }

        private static string SafeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            var chars = value.Where(c => !char.IsControl(c) && c != '\r' && c != '\n').ToArray();
            return new string(chars).Trim();
        }
    }
}
