using System.IO;
using System.Security.Principal;

namespace WinputLan.Module;

/// <summary>
/// Names and codes shared with the app's hosted mode. Must match WinputLan.Core.HostedOptions
/// (not referenced so that this assembly depends on nothing but the SDK).
/// </summary>
internal static class HostedContract
{
    public const string AppFolder = "app";
    public const string ExecutableName = "WinputLan.Hosted.exe";
    public const string RuntimeFolder = "runtime";
    public const string SecureFolderName = "Winput LAN (SwissTechKnife)";
    public const string PidFileName = "hosted.pid";

    public const string ElevatedTaskName = "Winput LAN (SwissTechKnife)";
    public const string FirewallRuleName = "Winput LAN (SwissTechKnife, Private TCP)";

    public const string ExitEventName = @"Local\WinputLan.Hosted.Exit";

    public const int AlreadyRunningExitCode = 3;
    public const int RestartExitCode = 4;

    /// <summary>Process name of the standalone app (installed WinputLan.exe and dev builds).</summary>
    public const string StandaloneProcessName = "WinputLan";

    /// <summary>Activation event of the app's single-instance gate (same for standalone and hosted).</summary>
    public static string ActivateEventName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return @"Local\WinputLan." + identity.User!.Value + ".Activate";
        }
    }

    public static string RuntimeExecutable(string dataDirectory) => Path.Combine(dataDirectory, RuntimeFolder, ExecutableName);

    /// <summary>Admin-only copy run by the elevated task (64-bit Program Files, as the app computes it).</summary>
    public static string SecureExecutable
    {
        get
        {
            var programFiles = System.Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrWhiteSpace(programFiles))
                programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(programFiles, SecureFolderName, ExecutableName);
        }
    }
}
