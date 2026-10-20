using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SwissTechKnife.Sdk;

namespace WinputLan.Module;

public enum HostedAppState
{
    Stopped,
    Starting,
    Running,
    /// <summary>A standalone Winput LAN owns the input hooks and the port; the module waits for it to exit.</summary>
    RunningOutside,
    /// <summary>The app crashed repeatedly; restarts stopped until the user opens it again.</summary>
    Failed,
}

/// <summary>Immutable snapshot for the module page.</summary>
internal sealed class HostedAppStatus
{
    public HostedAppStatus(HostedAppState state, bool elevated, bool adminNeedsConfirm, bool networkBlocked, bool busy)
    {
        State = state;
        Elevated = elevated;
        AdminNeedsConfirm = adminNeedsConfirm;
        NetworkBlocked = networkBlocked;
        Busy = busy;
    }

    public HostedAppState State { get; }
    /// <summary>Started through the elevated task (can control administrator apps).</summary>
    public bool Elevated { get; }
    /// <summary>The elevated task exists but its admin-only copy is from an older version: one UAC confirmation needed.</summary>
    public bool AdminNeedsConfirm { get; }
    /// <summary>No hosted firewall rule covers the running copy: other PCs cannot connect to this one.</summary>
    public bool NetworkBlocked { get; }
    /// <summary>A UAC-confirmed helper is running.</summary>
    public bool Busy { get; }
}

/// <summary>
/// Runs the Winput LAN app in its own process (<c>--hosted</c>), so input hooks and networking never share the
/// host's UI thread, GC or crashes. Starts it elevated through the app's on-demand task when the user enabled
/// administrator control (no UAC prompt), otherwise non-elevated. Restarts it after a crash (bounded) and stops it
/// with the module. Thread-safe; <see cref="Changed"/> is raised on arbitrary threads.
/// </summary>
internal sealed class HostedAppSupervisor : IDisposable
{
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);
    private const int MaxCrashesPerWindow = 3;
    private static readonly TimeSpan OutsidePollInterval = TimeSpan.FromSeconds(5);
    private const int ExitTimeoutMs = 5000;
    private const int ElevatedStartTimeoutMs = 15000;

    private readonly object _gate = new();
    private readonly AppDeployment _deployment;
    private readonly string _dataDirectory;
    private readonly IModuleLog _log;
    private readonly Action<string, NotificationKind> _notify;
    private readonly Queue<DateTime> _crashes = new();
    private EventWaitHandle? _exitEvent;
    private ProcessWatch? _watch;
    private Timer? _retry;
    private bool _stopping;
    private bool _disposed;
    private HostedAppState _state = HostedAppState.Stopped;
    private bool _elevated;
    private bool _adminNeedsConfirm;
    private bool _networkBlocked;
    private bool _busy;
    private bool _launching;
    private bool _showPending;

    public HostedAppSupervisor(AppDeployment deployment, string dataDirectory, IModuleLog log, Action<string, NotificationKind> notify)
    {
        _deployment = deployment;
        _dataDirectory = dataDirectory;
        _log = log;
        _notify = notify;
    }

    public event Action? Changed;

    public HostedAppStatus Status
    {
        get { lock (_gate) return new HostedAppStatus(_state, _elevated, _adminNeedsConfirm, _networkBlocked, _busy); }
    }

    /// <summary>Launches the app hidden on a pool thread; returns at once. Never shows a UAC prompt.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping = false;
        }
        ThreadPool.QueueUserWorkItem(_ => Launch(show: false));
    }

    /// <summary>Shows the app's window, starting the app first when it is not running.</summary>
    public void ShowWindow()
    {
        HostedAppState state;
        lock (_gate)
        {
            if (_disposed || _stopping) return;
            state = _state;
        }

        if (state is HostedAppState.Running or HostedAppState.RunningOutside or HostedAppState.Starting)
        {
            // The host is in the foreground (the user clicked); let the app take focus.
            AllowSetForegroundWindow(AsfwAny);
            if (SignalNamedEvent(HostedContract.ActivateEventName))
                return;
        }

        lock (_gate) _crashes.Clear();
        ThreadPool.QueueUserWorkItem(_ => Launch(show: true));
    }

    /// <summary>Asks the app to save and exit; kills it after a timeout when it can. Blocks at most ~6 s.</summary>
    public void Stop()
    {
        ProcessWatch? watch;
        lock (_gate)
        {
            _stopping = true;
            _retry?.Dispose();
            _retry = null;
            watch = _watch;
            _watch = null;
        }

        if (watch is not null)
        {
            try { _exitEvent?.Set(); }
            catch (ObjectDisposedException) { }
            if (!watch.WaitForExit(ExitTimeoutMs))
            {
                _log.Warn("Winput LAN did not exit in time; terminating it.");
                try
                {
                    using var process = Process.GetProcessById(watch.ProcessId);
                    process.Kill();
                    process.WaitForExit(1000);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // An elevated copy cannot be terminated from here; it still exits with the host process.
                    _log.Warn("Could not terminate Winput LAN: " + error.Message);
                }
            }
            watch.Dispose();
        }

        SetState(HostedAppState.Stopped);
    }

    /// <summary>
    /// After the user clicked "Confirmar permissão de administrador": one UAC prompt refreshes the admin-only copy
    /// and the task, then the app restarts elevated. Returns at once.
    /// </summary>
    public void ConfirmAdministrator() => RunHelper("enable", restart: true,
        success: "Permissão de administrador confirmada.",
        failure: "Não foi possível confirmar a permissão de administrador.");

    /// <summary>After the user clicked "Permitir conexões de rede": one UAC prompt adds the firewall rules.</summary>
    public void AllowNetwork() => RunHelper("network", restart: false,
        success: "Conexões de rede liberadas para o Winput LAN.",
        failure: "Não foi possível liberar as conexões de rede.");

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
            _exitEvent?.Dispose();
            _exitEvent = null;
        }
    }

    private void RunHelper(string action, bool restart, string success, string failure)
    {
        lock (_gate)
        {
            if (_disposed || _busy) return;
            _busy = true;
        }
        RaiseChanged();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int? code;
            try { code = _deployment.RunAdminHelper(action); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
            {
                _log.Error("Winput LAN admin helper failed to start.", error);
                code = -1;
            }

            lock (_gate) _busy = false;
            if (code is null)
            {
                _notify("O Windows não confirmou a permissão de administrador.", NotificationKind.Warning);
                RaiseChanged();
                return;
            }
            if (code != 0)
            {
                _log.Warn($"Winput LAN admin helper '{action}' exited with {code}.");
                _notify(failure, NotificationKind.Error);
                RaiseChanged();
                return;
            }

            _notify(success, NotificationKind.Success);
            if (restart)
            {
                Stop();
                lock (_gate)
                {
                    if (_disposed) return;
                    _stopping = false;
                    _crashes.Clear();
                }
                Launch(show: false);
            }
            else
            {
                CheckFirewall();
            }
        });
    }

    /// <summary>One launch at a time: a second request only asks the launch in progress to show the window.</summary>
    private void Launch(bool show)
    {
        lock (_gate)
        {
            if (_launching)
            {
                _showPending |= show;
                return;
            }
            if (_disposed || _stopping || _watch is not null) return;
            _launching = true;
            _showPending = false;
            _retry?.Dispose();
            _retry = null;
        }

        try
        {
            LaunchCore(show);
        }
        finally
        {
            bool showNow;
            lock (_gate)
            {
                _launching = false;
                showNow = _showPending && _watch is not null;
                _showPending = false;
            }
            if (showNow)
            {
                AllowSetForegroundWindow(AsfwAny);
                SignalNamedEvent(HostedContract.ActivateEventName);
            }
        }
    }

    private void LaunchCore(bool show)
    {
        // A standalone Winput LAN already controls input and owns the port; never start a second one.
        if (IsStandaloneRunning())
        {
            if (show)
            {
                AllowSetForegroundWindow(AsfwAny);
                SignalNamedEvent(HostedContract.ActivateEventName);
            }
            EnterRunningOutside();
            return;
        }

        if (!File.Exists(_deployment.PackageExecutable))
        {
            _log.Error("Winput LAN executable not found: " + _deployment.PackageExecutable);
            SetState(HostedAppState.Failed);
            return;
        }

        SetState(HostedAppState.Starting);
        try
        {
            var taskExecutable = ElevatedTask.QueryExecutable();
            var elevated = taskExecutable is not null && _deployment.SecureCopyIsCurrent(taskExecutable);
            lock (_gate) _adminNeedsConfirm = taskExecutable is not null && !elevated;
            if (taskExecutable is not null && !elevated)
                _log.Warn("The elevated Winput LAN copy is out of date; starting without administrator rights until the user confirms.");

            PrepareExitEvent();
            ProcessWatch? watch = null;
            var executable = HostedContract.SecureExecutable;
            if (elevated)
            {
                watch = LaunchElevated(show);
                if (watch is null)
                {
                    _log.Warn("The elevated Winput LAN task did not start the app; starting it without administrator rights.");
                    elevated = false;
                }
            }
            if (watch is null)
            {
                executable = _deployment.PrepareRuntime();
                watch = LaunchNormal(executable, show);
            }
            if (watch is null)
            {
                _log.Error("Winput LAN exited right after starting.");
                SetState(HostedAppState.Failed);
                return;
            }

            lock (_gate)
            {
                if (_stopping || _disposed)
                {
                    // Stopped while starting: ask the new process to exit too.
                    try { _exitEvent?.Set(); } catch (ObjectDisposedException) { }
                    watch.Dispose();
                    return;
                }
                _watch = watch;
                _elevated = elevated;
            }
            watch.OnExit(OnExited);
            _log.Info($"Winput LAN started (pid {watch.ProcessId}{(elevated ? ", elevated" : "")}).");
            SetState(HostedAppState.Running);

            if (show && elevated)
            {
                // Started by the task scheduler: hand it the foreground right explicitly.
                AllowSetForegroundWindow(AsfwAny);
                SignalNamedEvent(HostedContract.ActivateEventName);
            }

            if (!string.Equals(executable, _deployment.PackageExecutable, StringComparison.OrdinalIgnoreCase))
                CheckFirewall(executable);
        }
        catch (Exception error)
        {
            _log.Error("Could not start Winput LAN.", error);
            SetState(HostedAppState.Failed);
        }
    }

    private ProcessWatch? LaunchNormal(string executable, bool show)
    {
        var args = $"--hosted --data \"{_dataDirectory.TrimEnd('\\')}\" --parent {Process.GetCurrentProcess().Id}";
        if (show)
        {
            args += " --show";
            AllowSetForegroundWindow(AsfwAny);
        }
        using var process = Process.Start(new ProcessStartInfo(executable, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        })!;
        // Opened while `process` still holds its handle, so the id cannot be reused in between.
        return ProcessWatch.TryOpen(process.Id);
    }

    /// <summary>Runs the task and waits for the app to publish its pid (the task scheduler, not us, is its parent).</summary>
    private ProcessWatch? LaunchElevated(bool show)
    {
        var pidFile = Path.Combine(_dataDirectory, HostedContract.PidFileName);
        try { File.Delete(pidFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        try
        {
            ElevatedTask.Run(Process.GetCurrentProcess().Id, show);
        }
        catch (Exception error) when (error is System.Reflection.TargetInvocationException or COMException or UnauthorizedAccessException)
        {
            _log.Warn("Could not run the elevated Winput LAN task: " + error.Message);
            return null;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(ElevatedStartTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_stopping || _disposed) return null;
            }
            try
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0)
                    return ProcessWatch.TryOpen(pid);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(100);
        }
        return null;
    }

    private void OnExited(ProcessWatch watch)
    {
        var code = watch.ExitCode;
        bool stopping;
        lock (_gate)
        {
            if (!ReferenceEquals(_watch, watch))
                return; // Stop() owns it.
            _watch = null;
            _elevated = false;
            stopping = _stopping || _disposed;
        }
        watch.Dispose();
        if (stopping)
            return;

        if (code == HostedContract.AlreadyRunningExitCode)
        {
            EnterRunningOutside();
            return;
        }

        if (code == HostedContract.RestartExitCode)
        {
            // The user switched administrator control in the app: relaunch in the new mode and show it again.
            _log.Info("Winput LAN asked to restart (administrator control changed).");
            ThreadPool.QueueUserWorkItem(_ => Launch(show: true));
            return;
        }

        _log.Warn($"Winput LAN exited unexpectedly (code {code}).");
        int recent;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _crashes.Enqueue(now);
            while (_crashes.Count > 0 && now - _crashes.Peek() > CrashWindow)
                _crashes.Dequeue();
            recent = _crashes.Count;
        }

        if (recent >= MaxCrashesPerWindow)
        {
            _log.Error("Winput LAN keeps exiting; automatic restarts stopped.");
            SetState(HostedAppState.Failed);
            return;
        }

        SetState(HostedAppState.Starting);
        ScheduleLaunch(TimeSpan.FromSeconds(recent));
    }

    /// <summary>Polls cheaply (process list, never the mutex) until the standalone app exits, then takes over.</summary>
    private void EnterRunningOutside()
    {
        SetState(HostedAppState.RunningOutside);
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _retry?.Dispose();
            _retry = new Timer(_ =>
            {
                if (!IsStandaloneRunning())
                    Launch(show: false);
            }, null, OutsidePollInterval, OutsidePollInterval);
        }
    }

    private void ScheduleLaunch(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _retry?.Dispose();
            _retry = new Timer(_ => Launch(show: false), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Created (and kept open) by the module before the app starts: an event created by the elevated copy with a
    /// default DACL could not be signalled from here. Reset so a previous Stop() does not end the new process.
    /// </summary>
    private void PrepareExitEvent()
    {
        lock (_gate)
        {
            _exitEvent ??= new EventWaitHandle(false, EventResetMode.ManualReset, HostedContract.ExitEventName);
            _exitEvent.Reset();
        }
    }

    private void CheckFirewall(string? executable = null)
    {
        executable ??= _elevated ? HostedContract.SecureExecutable : _deployment.RuntimeExecutable;
        var blocked = !AppDeployment.FirewallAllows(executable);
        lock (_gate)
        {
            if (_networkBlocked == blocked) return;
            _networkBlocked = blocked;
        }
        if (blocked)
            _log.Warn("No Winput LAN firewall rule covers " + executable + ".");
        RaiseChanged();
    }

    private void SetState(HostedAppState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            _log.Error("State handler failed.", error);
        }
    }

    /// <summary>Installed standalone is WinputLan.exe (also the dev build name). The hosted copy is excluded.</summary>
    private static bool IsStandaloneRunning()
    {
        var processes = Process.GetProcessesByName(HostedContract.StandaloneProcessName);
        foreach (var process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    private static bool SignalNamedEvent(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
