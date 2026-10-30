using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinputLan.Module;

/// <summary>
/// Waits for a process by id with only SYNCHRONIZE and PROCESS_QUERY_LIMITED_INFORMATION, which a normal process
/// is granted on an elevated one too (System.Diagnostics.Process asks for more and fails there).
/// </summary>
internal sealed class ProcessWatch : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    private readonly ProcessHandleWait _handle;
    private RegisteredWaitHandle? _registration;

    private ProcessWatch(int processId, ProcessHandleWait handle)
    {
        ProcessId = processId;
        _handle = handle;
    }

    public int ProcessId { get; }

    public static ProcessWatch? TryOpen(int processId)
    {
        var raw = OpenProcess(Synchronize | QueryLimitedInformation, false, (uint)processId);
        return raw == IntPtr.Zero ? null : new ProcessWatch(processId, new ProcessHandleWait(raw));
    }

    public bool HasExited => _handle.WaitOne(0);

    /// <summary>-1 while running, or once disposed (the exit callback can race a Stop() that disposed the watch).</summary>
    public int ExitCode
    {
        get
        {
            try
            {
                if (!GetExitCodeProcess(_handle.SafeWaitHandle, out var code) || code == StillActive)
                    return -1;
                return unchecked((int)code);
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
        }
    }

    /// <summary>Calls <paramref name="exited"/> once on a pool thread when the process ends.</summary>
    public void OnExit(Action<ProcessWatch> exited) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_handle, (_, _) => exited(this), null, Timeout.Infinite, executeOnlyOnce: true);

    public bool WaitForExit(int milliseconds) => _handle.WaitOne(milliseconds);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _handle.Dispose();
    }

    private sealed class ProcessHandleWait : WaitHandle
    {
        public ProcessHandleWait(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);
}
