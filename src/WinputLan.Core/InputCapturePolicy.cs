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

        // Keys and mouse buttons share one id space: mouse buttons use ids above the 0..255 virtual-key range.
        public static uint KeyId(ushort virtualKey) { return virtualKey; }
        public static uint ButtonId(uint buttonMessage) { return 0x10000u | buttonMessage; }
    }

    // Only Ctrl, Shift and Alt follow a switch: alone they do nothing, while any other held key (the chord's
    // terminal key, Win) would type, repeat or open something on the machine that receives it.
    public static class ModifierHandover
    {
        // Unassigned virtual key tapped around a moved Alt so its release cannot open a menu bar. Not 0xFF: some
        // keyboards and hotkey tools use it, and a held Ctrl+Alt+Shift turned the tap into a screenshot shortcut.
        public const ushort MenuMaskKey = 0xE8;

        public static bool IsHandoverKey(uint id)
        {
            return id == 0x10 || id == 0x11 || id == 0x12 || (id >= 0xA0 && id <= 0xA5);
        }

        public static bool IsAlt(uint id) { return id == 0x12 || id == 0xA4 || id == 0xA5; }
        public static bool IsCtrl(uint id) { return id == 0x11 || id == 0xA2 || id == 0xA3; }

        // Alt goes first: pressed first, the keys after it cancel its menu; released first, a held Ctrl keeps it
        // from being a menu key. So the mask tap, itself a chord with the held keys, is only needed without Ctrl.
        public static List<uint> Order(IEnumerable<uint> keys)
        {
            var ordered = new List<uint>();
            foreach (var key in keys) if (IsAlt(key)) ordered.Add(key);
            foreach (var key in keys) if (!IsAlt(key)) ordered.Add(key);
            return ordered;
        }

        public static bool NeedsMenuMask(IList<uint> keys)
        {
            var alt = false;
            foreach (var key in keys) { if (IsCtrl(key)) return false; alt |= IsAlt(key); }
            return alt;
        }
    }
}
