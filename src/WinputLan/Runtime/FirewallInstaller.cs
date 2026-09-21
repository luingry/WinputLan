using System;
using System.Diagnostics;

namespace WinputLan.Runtime
{
    public static class FirewallInstaller
    {
        public const string RuleName = "Winput LAN (Private TCP)";
        public const string RuleDescription = "Winput LAN secure peer input transport; Private profile only.";

        public static string BuildNetshArguments(string programPath, int port)
        {
            if (string.IsNullOrWhiteSpace(programPath)) throw new ArgumentException("Program path is required.", "programPath");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            return string.Format("advfirewall firewall add rule name=\"{0}\" description=\"{1}\" dir=in action=allow enable=yes profile=Private protocol=TCP localport={2} program=\"{3}\"", RuleName, RuleDescription, port, programPath.Replace("\"", string.Empty));
        }

        public static int Install(string programPath, int port)
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = BuildNetshArguments(programPath, port),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
