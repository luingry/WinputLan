using System;

namespace WinputLan.Core
{
    // Keeps diagnostic UI out of the input hot path while preserving discrete input evidence.
    public sealed class InputAuditPolicy
    {
        private DateTime _lastHighFrequencyUtc = DateTime.MinValue;
        public bool ShouldEmit(InputKind kind, string status, DateTime utcNow)
        {
            if (string.Equals(status, "queued", StringComparison.Ordinal) || string.Equals(status, "coalesced", StringComparison.Ordinal)) return false;
            if (kind != InputKind.MouseMove && kind != InputKind.MouseDelta && kind != InputKind.MouseWheel) return true;
            if (utcNow - _lastHighFrequencyUtc < TimeSpan.FromMilliseconds(250)) return false;
            _lastHighFrequencyUtc = utcNow; return true;
        }
    }
}
