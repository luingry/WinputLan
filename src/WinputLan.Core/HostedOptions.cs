using System;
using System.IO;

namespace WinputLan.Core
{
    // Command line and shared names for running under the SwissTechKnife host (the winput-lan module).
    // The module (src/WinputLan.Module) duplicates these literals: keep both sides in sync.
    public sealed class HostedOptions
    {
        public const string HostedArgument = "--hosted";
        public const string DataArgument = "--data";
        public const string ParentArgument = "--parent";
        public const string ShowArgument = "--show";
        public const string HiddenArgument = "--hidden";
        // Elevated helper: "--hosted-admin <enable|disable|network> --data <dir>". Never acquires the instance gate.
        public const string AdminArgument = "--hosted-admin";

        // Another Winput LAN (standalone or hosted) owns the single-instance mutex.
        public const int AlreadyRunningExitCode = 3;
        // Elevation changed: the module relaunches at once (elevated through its task when registered).
        public const int RestartExitCode = 4;
        public const int AdminFailedExitCode = 5;

        // Session-local event the module sets to ask the hosted process to save and exit.
        public const string ExitEventName = @"Local\WinputLan.Hosted.Exit";
        // On-demand task (no trigger) that runs the hosted copy elevated without a UAC prompt.
        public const string ElevatedTaskName = "Winput LAN (SwissTechKnife)";
        public const string FirewallRuleName = "Winput LAN (SwissTechKnife, Private TCP)";
        // Stable, user-writable copy of the app inside the module data folder, refreshed by the module on update,
        // so the firewall rule survives module updates. Only ever run non-elevated.
        public const string RuntimeFolder = "runtime";
        // Admin-only copy under Program Files run by the elevated task. It must live where a normal process cannot
        // write or rename it, or the task would be a silent elevation path for anything running as the user.
        public const string SecureFolderName = "Winput LAN (SwissTechKnife)";
        public const string ExecutableName = "WinputLan.Hosted.exe";
        public const string PidFileName = "hosted.pid";

        private HostedOptions(string dataDirectory, int parentId, bool show)
        {
            DataDirectory = dataDirectory;
            ParentId = parentId;
            ShowOnStart = show;
        }

        public string DataDirectory { get; }
        public int ParentId { get; }
        public bool ShowOnStart { get; }

        public static string RuntimeExecutable(string dataDirectory) { return Path.Combine(dataDirectory, RuntimeFolder, ExecutableName); }
        public static string SecureExecutable(string programFiles) { return Path.Combine(programFiles, SecureFolderName, ExecutableName); }

        // Task action arguments; the module fills $(Arg0) with its process id and $(Arg1) with --show or --hidden.
        public static string TaskArguments(string dataDirectory)
        {
            return HostedArgument + " " + DataArgument + " \"" + dataDirectory.TrimEnd('\\', '/') + "\" " + ParentArgument + " $(Arg0) $(Arg1)";
        }

        // Null unless the command line has --hosted and a rooted --data folder. Unknown tokens are ignored
        // (a task run without parameters leaves "$(Arg0)" literals behind).
        public static HostedOptions Parse(string[] args)
        {
            if (args == null) return null;
            var hosted = false; var show = false; var parent = 0; string data = null;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (Is(arg, HostedArgument)) hosted = true;
                else if (Is(arg, ShowArgument)) show = true;
                else if (Is(arg, ParentArgument) && i + 1 < args.Length) { int value; if (int.TryParse(args[++i], out value) && value > 0) parent = value; }
                else if (Is(arg, DataArgument) && i + 1 < args.Length) data = args[++i];
            }
            if (!hosted || !IsRootedPath(data)) return null;
            return new HostedOptions(data.TrimEnd('\\', '/'), parent, show);
        }

        // "--hosted-admin <action> --data <dir>": returns the action (enable, disable, network) or null.
        public static string ParseAdminAction(string[] args, out string dataDirectory)
        {
            dataDirectory = null;
            if (args == null) return null;
            string action = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (Is(args[i], AdminArgument) && i + 1 < args.Length) action = args[++i].ToLowerInvariant();
                else if (Is(args[i], DataArgument) && i + 1 < args.Length) dataDirectory = args[++i];
            }
            if (action != "enable" && action != "disable" && action != "network") return null;
            if (!IsRootedPath(dataDirectory)) { dataDirectory = null; return null; }
            dataDirectory = dataDirectory.TrimEnd('\\', '/');
            return action;
        }

        private static bool Is(string arg, string name) { return string.Equals(arg, name, StringComparison.OrdinalIgnoreCase); }

        private static bool IsRootedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            try { return Path.IsPathRooted(path) && path.Length > 3; } catch (ArgumentException) { return false; }
        }
    }
}
