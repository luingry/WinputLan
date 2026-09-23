using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public static class ProcessElevation
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenElevationClass = 20;
        private const int ErrorAccessDenied = 5;
        private const int ErrorCancelled = 1223;

        public static bool IsCurrentElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Starts an elevated copy. Returns false when the user declined the UAC prompt.
        public static bool TryRelaunchElevated(bool startup = false)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, ElevationPolicy.RelaunchedArgument + (startup ? " " + StartupRegistration.StartupArgument : string.Empty)) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return false; }
        }

        // True when the foreground window belongs to an elevated process, which UIPI shields from our SendInput.
        public static bool IsForegroundElevated()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == 0 || processId == (uint)Process.GetCurrentProcess().Id) return false;
            var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return false;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, TokenQuery, out token)) return Marshal.GetLastWin32Error() == ErrorAccessDenied;
                try
                {
                    int elevated, length;
                    return GetTokenInformation(token, TokenElevationClass, out elevated, sizeof(int), out length) && elevated != 0;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
