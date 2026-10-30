using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using SwissTechKnife.Sdk;

namespace WinputLan.Module;

/// <summary>
/// Where the hosted app runs from. The package folder changes with every module version, so the module keeps a
/// stable copy in its data folder (<c>runtime\</c>) that the firewall rule can point at, and the elevated task
/// runs an admin-only copy under Program Files that only the app's elevated helper can write.
/// All members do disk or process work: call them from a background thread.
/// </summary>
internal sealed class AppDeployment
{
    private readonly string _dataDirectory;
    private readonly IModuleLog _log;

    public AppDeployment(string installDirectory, string dataDirectory, IModuleLog log)
    {
        PackageExecutable = Path.Combine(installDirectory, HostedContract.AppFolder, HostedContract.ExecutableName);
        _dataDirectory = dataDirectory;
        _log = log;
    }

    public string PackageExecutable { get; }

    public string RuntimeExecutable => HostedContract.RuntimeExecutable(_dataDirectory);

    /// <summary>Refreshes the stable runtime copy from the package when it differs; falls back to the package itself.</summary>
    public string PrepareRuntime()
    {
        var runtime = RuntimeExecutable;
        if (SameBuild(PackageExecutable, runtime))
            return runtime;
        var target = Path.GetDirectoryName(runtime)!;
        try
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            CopyDirectory(Path.GetDirectoryName(PackageExecutable)!, target);
            _log.Info("Winput LAN runtime copy refreshed.");
            return runtime;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Also an antivirus refusing to write the copy (ERROR_VIRUS_INFECTED surfaces as an IOException).
            _log.Warn("Could not refresh the Winput LAN runtime copy; running from the package folder. " + error.Message);
            return PackageExecutable;
        }
    }

    /// <summary>
    /// Executables to try, best first: the stable runtime copy (refreshed from the package), then the package
    /// itself. An antivirus may quarantine either one; whichever survives still starts the app. Empty when both
    /// are gone.
    /// </summary>
    public IReadOnlyList<string> LaunchCandidates()
    {
        var candidates = new List<string>();
        if (File.Exists(PackageExecutable))
        {
            var prepared = PrepareRuntime();
            candidates.Add(prepared);
            if (!string.Equals(prepared, PackageExecutable, StringComparison.OrdinalIgnoreCase))
                candidates.Add(PackageExecutable);
        }
        else if (File.Exists(RuntimeExecutable))
        {
            _log.Warn("Winput LAN executable is missing from the package folder; starting the runtime copy.");
            candidates.Add(RuntimeExecutable);
        }
        return candidates;
    }

    /// <summary>
    /// Presence, size and timestamp of both executables. Changes when a quarantined file is restored (or the
    /// module is reinstalled), which is when a blocked app is worth starting again.
    /// </summary>
    public string Fingerprint() => Describe(PackageExecutable) + "|" + Describe(RuntimeExecutable);

    private static string Describe(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    /// <summary>The admin-only copy the elevated task runs, when it exists and matches this package.</summary>
    public bool SecureCopyIsCurrent(string taskExecutable) =>
        string.Equals(Path.GetFullPath(taskExecutable), Path.GetFullPath(HostedContract.SecureExecutable), StringComparison.OrdinalIgnoreCase)
        && SameBuild(PackageExecutable, HostedContract.SecureExecutable);

    /// <summary>True when the hosted firewall rule allows <paramref name="executable"/> (read-only, no elevation).</summary>
    public static bool FirewallAllows(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe",
                $"advfirewall firewall show rule name=\"{HostedContract.FirewallRuleName}\" verbose")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            return process.ExitCode == 0 && output.IndexOf(executable, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return true; // Unknown: do not nag.
        }
    }

    /// <summary>
    /// Runs the app's elevated helper (<c>--hosted-admin enable|network</c>) from the package. Shows one UAC
    /// prompt; only ever called after the user clicked a button. Returns null when the prompt was declined.
    /// </summary>
    public int? RunAdminHelper(string action)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(PackageExecutable,
                $"--hosted-admin {action} --data \"{_dataDirectory.TrimEnd('\\')}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return null; // ERROR_CANCELLED: the user declined the UAC prompt.
        }
    }

    /// <summary>Same file version, size and timestamp: copies made with File.Copy keep all three.</summary>
    private static bool SameBuild(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length || fa.LastWriteTimeUtc != fb.LastWriteTimeUtc)
                return false;
            return FileVersionInfo.GetVersionInfo(a).FileVersion == FileVersionInfo.GetVersionInfo(b).FileVersion;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
