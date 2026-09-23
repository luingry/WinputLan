using System;
using System.Linq;

namespace WinputLan.Core
{
    // Windows UIPI drops input injected by a normal process into elevated windows (Task Manager, installers).
    // Running elevated is opt-in; the marker argument stops a relaunch loop if elevation silently fails.
    public static class ElevationPolicy
    {
        public const string RelaunchedArgument = "--elevated-relaunch";

        public static bool ShouldRelaunchElevated(bool runElevated, bool isElevated, string[] args)
        {
            if (!runElevated || isElevated) return false;
            return args == null || !args.Any(a => string.Equals(a, RelaunchedArgument, StringComparison.OrdinalIgnoreCase));
        }
    }
}
