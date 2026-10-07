using System.Collections.Generic;

namespace WinputLan.Core
{
    public enum InputRoute
    {
        Local,
        Remote
    }

    // While remote control is active every local input is suppressed and forwarded.
    // A release always follows its press: a key pressed locally before switching is released
    // locally, so modifiers from the switch chord never stay stuck on either machine.
    // Held Ctrl/Shift/Alt are the exception: they move to the machine that takes control, which then owns their release.
    public sealed class InputRoutingState
    {
        private readonly object _gate = new object();
        private readonly HashSet<uint> _localDown = new HashSet<uint>();
        private readonly HashSet<uint> _remoteDown = new HashSet<uint>();
        private bool _remoteActive;

        public bool RemoteActive { get { lock (_gate) return _remoteActive; } }

        // Returns the held modifiers handed to the machine that now has control. The caller presses them there
        // and releases them on the machine that lost control.
        public IList<uint> SetRemoteActive(bool active)
        {
            lock (_gate)
            {
                var handed = new List<uint>();
                if (active == _remoteActive) return handed;
                _remoteActive = active;
                var from = active ? _localDown : _remoteDown;
                var to = active ? _remoteDown : _localDown;
                foreach (var id in from) if (ModifierHandover.IsHandoverKey(id)) handed.Add(id);
                foreach (var id in handed) { from.Remove(id); to.Add(id); }
                // The remote side is reset by ReleaseAll when control returns, so its presses are forgotten here.
                if (!active) _remoteDown.Clear();
                return handed;
            }
        }

        public InputRoute Press(uint id)
        {
            lock (_gate)
            {
                // Auto-repeat belongs to the machine that received the original press.
                if (_localDown.Contains(id)) return InputRoute.Local;
                if (_remoteDown.Contains(id)) return InputRoute.Remote;
                if (_remoteActive) { _remoteDown.Add(id); return InputRoute.Remote; }
                _localDown.Add(id);
                return InputRoute.Local;
            }
        }

        public InputRoute Release(uint id)
        {
            lock (_gate)
            {
                if (_localDown.Remove(id)) return InputRoute.Local;
                if (_remoteDown.Remove(id)) return InputRoute.Remote;
                // A press this state never saw happened before the hook (or was forgotten by ReleaseAll). Windows may
                // hold that key down here, so swallowing its release would leave it stuck; a stray release is harmless.
                return InputRoute.Local;
            }
        }

        public InputRoute Continuous()
        {
            lock (_gate) return _remoteActive ? InputRoute.Remote : InputRoute.Local;
        }

        // A local drag or selection must not be cut off by an edge switch.
        public bool AnyLocalButtonDown
        {
            get { lock (_gate) { foreach (var id in _localDown) if (id >= 0x10000u) return true; return false; } }
        }

        // Keys and mouse buttons share one id space: mouse buttons use ids above the 0..255 virtual-key range.
        public static uint KeyId(ushort virtualKey) { return virtualKey; }
        public static uint ButtonId(uint buttonMessage) { return 0x10000u | buttonMessage; }
    }

    // Only Ctrl, Shift and Alt follow a switch: alone they do nothing, while any other held key (the chord's
    // terminal key, Win) would type, repeat or open something on the machine that receives it.
    // A moved Alt gets no menu-bar guard: releasing the switch chord was verified not to open menus. If it ever
    // does, see "Alt menu guard" in docs/INPUT.md for the design that was removed in 0.3.20.
    // Handing modifiers back to this PC injects their presses from the hook thread. A physical release may already be
    // waiting for that thread; Windows then applies the release first and the injected press after it, leaving the
    // key stuck down. The hook sees both in that order, so a release seen before its injected press cancels the press.
    // Hook thread only.
    public sealed class HandoverReplayGuard
    {
        private readonly HashSet<ushort> _pending = new HashSet<ushort>();
        private readonly HashSet<ushort> _cancelled = new HashSet<ushort>();

        // Called just before the press is injected.
        public void Injecting(ushort virtualKey) { _pending.Add(virtualKey); _cancelled.Remove(virtualKey); }

        // The injection failed, so no press will follow.
        public void NotInjected(ushort virtualKey) { _pending.Remove(virtualKey); _cancelled.Remove(virtualKey); }

        // A physical release of the key reached the hook.
        public void PhysicalRelease(ushort virtualKey) { if (_pending.Contains(virtualKey)) _cancelled.Add(virtualKey); }

        // The injected press reached the hook: true when it must be swallowed because its key was already released.
        public bool ShouldSwallowInjectedPress(ushort virtualKey)
        {
            if (!_pending.Remove(virtualKey)) return false;
            return _cancelled.Remove(virtualKey);
        }
    }

    public static class ModifierHandover
    {
        public static bool IsHandoverKey(uint id)
        {
            return id == 0x10 || id == 0x11 || id == 0x12 || (id >= 0xA0 && id <= 0xA5);
        }
    }
}
