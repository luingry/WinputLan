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
                return _remoteActive ? InputRoute.Remote : InputRoute.Local;
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
    public static class ModifierHandover
    {
        public static bool IsHandoverKey(uint id)
        {
            return id == 0x10 || id == 0x11 || id == 0x12 || (id >= 0xA0 && id <= 0xA5);
        }
    }
}
