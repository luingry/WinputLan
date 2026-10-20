using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
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
            var file = Path.Combine(Path.GetTempPath(), "winputlan-logon-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, BuildElevatedTaskXml(exe, WindowsIdentity.GetCurrent().User.Value), Encoding.Unicode);
                return RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F") == 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            finally { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // Same as "schtasks /SC ONLOGON /IT /RL HIGHEST", minus the defaults that hurt a long-running app:
        // a 72 h execution limit (ended the app after three days), priority 7 (below normal) and not starting
        // or being stopped on battery. InteractiveToken: runs only in this user's session, no stored password.
        public static string BuildElevatedTaskXml(string exe, string userSid)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + userSid + "</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + userSid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority>" +
                "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>" + StartupArgument + "</Arguments></Exec></Actions></Task>";
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
