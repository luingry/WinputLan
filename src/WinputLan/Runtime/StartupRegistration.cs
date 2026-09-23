using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WinputLan.Runtime
{
    // Normal mode uses the per-user Run key. Elevated mode uses a logon task with highest privileges instead,
    // because a Run entry would start unelevated and then show a UAC prompt at every sign-in.
    public static class StartupRegistration
    {
        public const string StartupArgument = "--startup";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "WinputLan";
        private const string TaskName = "Winput LAN";

        public static void Apply(bool enabled, bool elevatedMode)
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            if (enabled && elevatedMode && TryCreateElevatedTask(exe)) { SetRunValue(null); return; }
            SetRunValue(enabled ? "\"" + exe + "\" " + StartupArgument : null);
            // Removing an elevated task needs elevation; when unelevated this fails harmlessly and the task stays.
            RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
        }

        private static void SetRunValue(string command)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (command == null) key.DeleteValue(RunValueName, false);
                else key.SetValue(RunValueName, command, RegistryValueKind.String);
            }
        }

        private static bool TryCreateElevatedTask(string exe)
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            // /IT: runs only in this user's interactive session, so no stored password is needed.
            return RunSchtasks("/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\" " + StartupArgument + "\" /SC ONLOGON /RU \"" + user + "\" /IT /RL HIGHEST /F") == 0;
        }

        private static int RunSchtasks(string arguments)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
                    return process.WaitForExit(10000) ? process.ExitCode : -1;
                }
            }
            catch { return -1; }
        }
    }
}
