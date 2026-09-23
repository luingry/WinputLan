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
    public sealed class InputRoutingState
    {
        private readonly object _gate = new object();
        private readonly HashSet<uint> _localDown = new HashSet<uint>();
        private readonly HashSet<uint> _remoteDown = new HashSet<uint>();
        private bool _remoteActive;

        public bool RemoteActive { get { lock (_gate) return _remoteActive; } }

        public void SetRemoteActive(bool active)
        {
            lock (_gate)
            {
                _remoteActive = active;
                // The remote side is reset by ReleaseAll when control returns, so its presses are forgotten here.
                if (!active) _remoteDown.Clear();
            }
        }

        public InputRoute Press(uint id)
        {
            lock (_gate)
            {
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
}
