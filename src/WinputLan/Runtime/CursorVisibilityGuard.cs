using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinputLan.Runtime
{
    // SetSystemCursor changes the interactive desktop, not just our window. A second process
    // reloads the user's cursor scheme if the controller exits before it can restore it.
    internal sealed class CursorVisibilityGuard : IDisposable
    {
        internal static readonly CursorVisibilityGuard Shared = new CursorVisibilityGuard();
        internal const string HelperArgument = "--cursor-guard";
        private const uint SpiSetCursors = 0x0057;
        private static readonly uint[] CursorIds =
        {
            32512, 32513, 32514, 32515, 32516, // arrow, text, wait, cross, up
            32642, 32643, 32644, 32645, 32646, // resize cursors
            32648, 32649, 32650, 32651, 32671, 32672 // no, hand, app starting, help, pin, person
        };

        private EventWaitHandle _ready;
        private EventWaitHandle _done;
        private EventWaitHandle _active;
        private Process _helper;
        private Timer _healthTimer;
        private readonly object _gate = new object();
        private bool _hidden;

        internal bool Arm()
        {
            lock (_gate) return ArmCore();
        }

        private bool ArmCore()
        {
            try
            {
                if (_helper != null && !_helper.HasExited) return true;
                ReleaseHelper();
                var token = Guid.NewGuid().ToString("N");
                _ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "ready"));
                _done = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "done"));
                _active = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token, "active"));
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    HelperArgument + " " + Process.GetCurrentProcess().Id + " " + token)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                _helper = Process.Start(start);
                if (_helper == null || !_ready.WaitOne(3000) || _helper.HasExited) { ReleaseHelper(); return false; }
                return true;
            }
            catch { ReleaseHelper(); return false; }
        }

        internal bool TryHide()
        {
            lock (_gate) return TryHideCore();
        }

        private bool TryHideCore()
        {
            if (_hidden) return true;
            try
            {
                if (!ArmCore()) return false;
                // Keep the recovery signal armed through any partial cursor replacement.
                _hidden = true;
                _active.Set();
                _healthTimer = new Timer(_ => { if (!HelperAlive) Show(); }, null, 500, 500);

                foreach (var id in CursorIds)
                {
                    // SetSystemCursor consumes the handle, so every replacement needs a fresh cursor.
                    var cursor = CreateInvisibleCursor();
                    if (cursor == IntPtr.Zero) { ShowCore(); return false; }
                    if (!SetSystemCursor(cursor, id))
                    {
                        DestroyCursor(cursor);
                        ShowCore();
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                ShowCore();
                return false;
            }
        }

        internal void Show()
        {
            lock (_gate) ShowCore();
        }

        private void ShowCore()
        {
            if (_hidden && !RestoreSystemCursors())
            {
                // Leave the active signal set so the helper also attempts restoration.
                try { _done?.Set(); } catch { }
                return;
            }
            _hidden = false;
            try { _active?.Reset(); } catch { }
            _healthTimer?.Dispose();
            _healthTimer = null;
        }

        internal bool HelperAlive
        {
            get
            {
                lock (_gate)
                {
                    try { return !_hidden || (_helper != null && !_helper.HasExited); }
                    catch { return false; }
                }
            }
        }

        public void Dispose() { lock (_gate) { ShowCore(); ReleaseHelper(); } }

        private void ReleaseHelper()
        {
            try { _done?.Set(); } catch { }
            _ready?.Dispose();
            _done?.Dispose();
            _active?.Dispose();
            _helper?.Dispose();
            _ready = null;
            _done = null;
            _active = null;
            _helper = null;
        }

        internal static void RunHelper(string[] args)
        {
            int parentId;
            Guid token;
            if (args.Length != 3 || !int.TryParse(args[1], out parentId) ||
                !Guid.TryParseExact(args[2], "N", out token)) return;
            try
            {
                using (var parent = Process.GetProcessById(parentId))
                using (var ready = EventWaitHandle.OpenExisting(EventName(args[2], "ready")))
                using (var done = EventWaitHandle.OpenExisting(EventName(args[2], "done")))
                using (var active = EventWaitHandle.OpenExisting(EventName(args[2], "active")))
                {
                    ready.Set();
                    while (!done.WaitOne(0))
                    {
                        if (parent.WaitForExit(200))
                        {
                            // The parent may have died at any point after the ready handshake.
                            if (active.WaitOne(0)) RestoreSystemCursors();
                            return;
                        }
                    }
                    // Backstop a failed restore in the controller before the guard exits.
                    if (active.WaitOne(0)) RestoreSystemCursors();
                }
            }
            catch { RestoreSystemCursors(); }
        }

        private static string EventName(string token, string suffix)
        {
            return @"Local\WinputLan.CursorGuard." + token + "." + suffix;
        }

        private static IntPtr CreateInvisibleCursor()
        {
            const int size = 32;
            var andMask = new byte[size * size / 8];
            var xorMask = new byte[andMask.Length];
            for (var i = 0; i < andMask.Length; i++) andMask[i] = 0xff;
            return CreateCursor(GetModuleHandle(null), 0, 0, size, size, andMask, xorMask);
        }

        internal static bool RestoreSystemCursors()
        {
            return SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateCursor(IntPtr instance, int hotX, int hotY, int width, int height, byte[] andMask, byte[] xorMask);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyCursor(IntPtr cursor);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint parameter, IntPtr value, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
