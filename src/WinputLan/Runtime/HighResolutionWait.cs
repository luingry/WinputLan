using System;
using Microsoft.Win32.SafeHandles;

namespace WinputLan.Runtime
{
    // Sub-millisecond waits without raising the system timer resolution. Task.Delay and Thread.Sleep follow
    // the ~15.6 ms default tick, far longer than a motion pacing interval.
    internal sealed class HighResolutionWait : IDisposable
    {
        private readonly SafeWaitHandle _timer;

        private HighResolutionWait(SafeWaitHandle timer) { _timer = timer; }

        // Null where the high-resolution timer is unavailable (before Windows 10 1803); callers then don't pace.
        public static HighResolutionWait TryCreate()
        {
            try
            {
                var timer = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, NativeMethods.CreateWaitableTimerHighResolution, NativeMethods.TimerAllAccess);
                if (timer == null || timer.IsInvalid) { timer?.Dispose(); return null; }
                return new HighResolutionWait(timer);
            }
            catch (EntryPointNotFoundException) { return null; }
        }

        public bool Wait(TimeSpan duration)
        {
            // Negative due time is relative, in 100 ns units.
            var due = -Math.Max(1L, duration.Ticks);
            return NativeMethods.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)
                && NativeMethods.WaitForSingleObject(_timer, 1000) == NativeMethods.WaitObject0;
        }

        public void Dispose() { _timer.Dispose(); }
    }
}
