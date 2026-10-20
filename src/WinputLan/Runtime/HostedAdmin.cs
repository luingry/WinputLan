using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    // Elevated helper for hosted mode ("--hosted-admin <action> --data <dir>"), started with a UAC prompt only
    // after an explicit click (the app's "Permitir controlar apps de administrador" or the module page buttons).
    //   enable : copies this app build to Program Files, registers the on-demand elevated task, adds the firewall rules.
    //   disable: deletes the elevated task (the Program Files copy stays inert without it).
    //   network: (re)creates the Private-profile firewall rules for the hosted copies.
    public static class HostedAdmin
    {
        private const int ErrorCancelled = 1223;

        public static string ProgramFilesDirectory
        {
            get
            {
                // The 64-bit Program Files even if a 32-bit process asks; the module computes the same path.
                var native = Environment.GetEnvironmentVariable("ProgramW6432");
                return string.IsNullOrWhiteSpace(native) ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : native;
            }
        }

        public static string SecureExecutable { get { return HostedOptions.SecureExecutable(ProgramFilesDirectory); } }

        public static int Run(string action, string dataDirectory)
        {
            if (!ProcessElevation.IsCurrentElevated()) return HostedOptions.AdminFailedExitCode;
            try
            {
                switch (action)
                {
                    case "enable":
                        InstallSecureCopy();
                        if (!RegisterTask(dataDirectory)) return HostedOptions.AdminFailedExitCode;
                        EnsureFirewall(dataDirectory);
                        return 0;
                    case "disable":
                        return DeleteTask() ? 0 : HostedOptions.AdminFailedExitCode;
                    case "network":
                        return EnsureFirewall(dataDirectory) ? 0 : HostedOptions.AdminFailedExitCode;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is InvalidOperationException) { }
            return HostedOptions.AdminFailedExitCode;
        }

        // Starts this executable elevated with the given action. Returns null when the user declined the UAC prompt.
        public static int? RunElevated(string action, string dataDirectory)
        {
            try
            {
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    HostedOptions.AdminArgument + " " + action + " " + HostedOptions.DataArgument + " \"" + dataDirectory + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var process = Process.Start(start))
                {
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return null; }
        }

        public static bool DeleteTask() { return RunTool("schtasks.exe", "/Delete /TN \"" + HostedOptions.ElevatedTaskName + "\" /F") == 0; }

        // Copies the folder of the running build (the module package or its runtime copy) to Program Files.
        private static void InstallSecureCopy()
        {
            var source = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            var target = Path.GetDirectoryName(SecureExecutable);
            if (string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyDirectory(source, target);
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        private static bool RegisterTask(string dataDirectory)
        {
            var exe = SecureExecutable;
            var sid = WindowsIdentity.GetCurrent().User.Value;
            // No trigger: the module runs it on demand (RunEx) and passes its pid and the show flag as $(Arg0)/$(Arg1).
            // No time limit (the 72 h default would end the app) and normal priority (the default 7 is below normal).
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>Inicia o Winput LAN do SwissTechKnife com permissão de administrador, sem aviso do UAC.</Description></RegistrationInfo>" +
                "<Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority>" +
                "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command>" +
                "<Arguments>" + SecurityElement.Escape(HostedOptions.TaskArguments(dataDirectory)) + "</Arguments>" +
                "<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
            var file = Path.Combine(Path.GetTempPath(), "winputlan-hosted-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                return RunTool("schtasks.exe", "/Create /TN \"" + HostedOptions.ElevatedTaskName + "\" /XML \"" + file + "\" /F") == 0;
            }
            finally { try { File.Delete(file); } catch (IOException) { } }
        }

        // One rule per hosted copy, all under the same name; also removes the block rules Windows adds when
        // someone dismisses its firewall alert for these programs (a block rule would override the allow rule).
        private static bool EnsureFirewall(string dataDirectory)
        {
            var port = ListenPort(dataDirectory);
            RunTool("netsh.exe", "advfirewall firewall delete rule name=\"" + HostedOptions.FirewallRuleName + "\"");
            var ok = true;
            foreach (var exe in new[] { HostedOptions.RuntimeExecutable(dataDirectory), SecureExecutable })
            {
                RunTool("netsh.exe", "advfirewall firewall delete rule name=all dir=in program=\"" + exe + "\"");
                ok &= RunTool("netsh.exe", FirewallInstaller.BuildNetshArguments(exe, port, HostedOptions.FirewallRuleName)) == 0;
            }
            return ok;
        }

        private static int ListenPort(string dataDirectory)
        {
            var fallback = WinputConfig.CreateDefault().ListenPort;
            try
            {
                if (!File.Exists(Path.Combine(dataDirectory, "config.json"))) return fallback;
                var port = new AppConfigStore(dataDirectory).LoadOrCreate().ListenPort;
                return port > 0 && port <= 65535 ? port : fallback;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) { return fallback; }
        }

        private static int RunTool(string tool, string arguments)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
                    return process.WaitForExit(15000) ? process.ExitCode : -1;
                }
            }
            catch (Win32Exception) { return -1; }
        }
    }
}
