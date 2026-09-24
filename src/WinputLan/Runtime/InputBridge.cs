using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public interface IInputSink
    {
        bool Publish(InputEvent value);
    }

    public interface IFailSafeInputSink : IInputSink
    {
        void ReleaseAll();
    }

    // Low-level hooks run on a dedicated thread with its own message loop. Windows blocks the whole
    // system input path while a hook runs, so they must never share the WPF dispatcher thread.
    public sealed class LowLevelInputCapture : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonUp = 0x0208;
        private const int WmMouseWheel = 0x020A;
        private const int WmXButtonDown = 0x020B;
        private const int WmXButtonUp = 0x020C;
        private const int WmMouseHWheel = 0x020E;
        private const int WmQuit = 0x0012;
        private const int WmApplyRemote = 0x8001;
        private const uint LlkhfInjected = 0x10;
        private const uint LlmhfInjected = 0x01;
        // A single hook event never legitimately moves this far from the anchor; larger jumps are stale positions.
        private const int MaxDeltaPerEvent = 2000;
        // Larger than any single-event motion in practice, small enough that the nudge off an edge is barely visible.
        private const int AnchorEdgeMargin = 50;
        private readonly IInputSink _sink;
        private readonly IHotkeyChordDetector _hotkeyChordDetector;
        private readonly Func<HotkeyAction, bool> _hotkeyAction;
        private readonly HashSet<ushort> _suppressedHotkeyUps = new HashSet<ushort>();
        private readonly InputRoutingState _routing = new InputRoutingState();
        private NativeMethods.HookProc _keyboardProc;
        private NativeMethods.HookProc _mouseProc;
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private IntPtr _module;
        private Thread _thread;
        private uint _threadId;
        private NativeMethods.POINT _anchor;
        private NativeMethods.POINT _restore;
        private bool _disposed;

        public LowLevelInputCapture(IInputSink sink, IHotkeyChordDetector hotkeyChordDetector = null, Func<HotkeyAction, bool> hotkeyAction = null) { _sink = sink ?? throw new ArgumentNullException("sink"); _hotkeyChordDetector = hotkeyChordDetector; _hotkeyAction = hotkeyAction; }

        public bool Start()
        {
            if (_thread != null) return _keyboardHook != IntPtr.Zero;
            var started = new ManualResetEventSlim(false);
            _thread = new Thread(() => HookThread(started)) { IsBackground = true, Name = "WinputLan input hooks", Priority = ThreadPriority.Highest };
            _thread.Start();
            started.Wait();
            started.Dispose();
            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero) { Stop(); return false; }
            return true;
        }

        // Applied on the hook thread so the cursor anchor and the routing switch are ordered with hook callbacks.
        public void SetRemoteActive(bool active)
        {
            if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, WmApplyRemote, new IntPtr(active ? 1 : 0), IntPtr.Zero);
        }

        public void Stop()
        {
            var thread = _thread;
            if (thread == null) return;
            if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
            _thread = null;
            _threadId = 0;
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }

        private void HookThread(ManualResetEventSlim started)
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            // Hook positions are physical pixels; the anchor and SetCursorPos must use the same space on scaled displays.
            NativeMethods.UsePhysicalPixels();
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;
            _module = NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            _keyboardHook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _keyboardProc, _module, 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(WhMouseLl, _mouseProc, _module, 0);
            started.Set();
            try
            {
                if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero) return;
                NativeMethods.MSG msg;
                while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.Message == WmApplyRemote) { ApplyRemote(msg.WParam != IntPtr.Zero); continue; }
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            finally
            {
                ApplyRemote(false);
                if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _keyboardHook = IntPtr.Zero;
                _mouseHook = IntPtr.Zero;
            }
        }

        private void ApplyRemote(bool active)
        {
            if (active == _routing.RemoteActive) return;
            if (active)
            {
                // Windows calls the most recently installed low-level hook first. Another wheel hook installed
                // after ours (e.g. SmoothMice) would consume the wheel locally, so move ours back to the front.
                RaiseMouseHook();
                // Pin the local cursor where it is: every hook position is then anchor + motion.
                NativeMethods.GetCursorPos(out _restore);
                _anchor = AnchorFor(_restore);
                NativeMethods.SetCursorPos(_anchor.X, _anchor.Y);
                _routing.SetRemoteActive(true);
            }
            else
            {
                _routing.SetRemoteActive(false);
                NativeMethods.SetCursorPos(_restore.X, _restore.Y);
            }
        }

        // Windows clamps hook positions to the screen, so motion towards an edge the cursor touches would be lost.
        // Keep the cursor in place unless it is within AnchorEdgeMargin of its monitor's edge; then nudge it inward.
        private static NativeMethods.POINT AnchorFor(NativeMethods.POINT cursor)
        {
            var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return cursor;
            var bounds = info.Monitor;
            return new NativeMethods.POINT
            {
                X = Math.Max(bounds.Left + AnchorEdgeMargin, Math.Min(bounds.Right - 1 - AnchorEdgeMargin, cursor.X)),
                Y = Math.Max(bounds.Top + AnchorEdgeMargin, Math.Min(bounds.Bottom - 1 - AnchorEdgeMargin, cursor.Y))
            };
        }

        // Runs on the hook thread, which does not pump in between, so no event is seen twice or missed.
        private void RaiseMouseHook()
        {
            var raised = NativeMethods.SetWindowsHookEx(WhMouseLl, _mouseProc, _module, 0);
            if (raised == IntPtr.Zero) return;
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = raised;
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && lParam != IntPtr.Zero)
            {
                var data = (NativeMethods.KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KbdLlHookStruct));
                if ((data.Flags & LlkhfInjected) == 0 && data.ExtraInfo.ToInt64() != SendInputSink.InputTag)
                {
                    var message = wParam.ToInt32();
                    var kind = message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown ? InputKind.KeyDown : message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp ? InputKind.KeyUp : (InputKind?)null;
                    if (kind.HasValue)
                    {
                        var value = InputEvent.Key(kind.Value, (ushort)data.VirtualKey, (ushort)data.ScanCode, data.Flags, DateTime.UtcNow.Ticks);
                        HotkeyAction? action;
                        if (_hotkeyChordDetector != null && _hotkeyChordDetector.TryHandle(value, out action))
                        {
                            if (action.HasValue)
                            {
                                if (_hotkeyAction != null && _hotkeyAction(action.Value)) { _suppressedHotkeyUps.Add(value.VirtualKey); return (IntPtr)1; }
                            }
                            else if (_suppressedHotkeyUps.Remove(value.VirtualKey)) return (IntPtr)1;
                        }
                        var id = InputRoutingState.KeyId(value.VirtualKey);
                        var route = kind.Value == InputKind.KeyDown ? _routing.Press(id) : _routing.Release(id);
                        if (route == InputRoute.Remote && _sink.Publish(value)) return (IntPtr)1;
                    }
                }
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && lParam != IntPtr.Zero)
            {
                var data = (NativeMethods.MouseLlHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MouseLlHookStruct));
                if ((data.Flags & LlmhfInjected) == 0 && data.ExtraInfo.ToInt64() != SendInputSink.InputTag)
                {
                    var message = wParam.ToInt32();
                    var now = DateTime.UtcNow.Ticks;
                    if (message == WmMouseMove)
                    {
                        if (_routing.Continuous() != InputRoute.Remote) return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                        var dx = data.Point.X - _anchor.X;
                        var dy = data.Point.Y - _anchor.Y;
                        if ((dx == 0 && dy == 0) || Math.Abs(dx) > MaxDeltaPerEvent || Math.Abs(dy) > MaxDeltaPerEvent) return (IntPtr)1;
                        if (_sink.Publish(InputEvent.MouseDelta(dx, dy, now))) return (IntPtr)1;
                        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                    }
                    InputEvent value = null;
                    InputRoute route;
                    var xButton = (ushort)(data.MouseData >> 16);
                    if (message == WmLButtonDown || message == WmRButtonDown || message == WmMButtonDown) { value = InputEvent.MouseButton(InputKind.MouseButtonDown, (uint)message, now); route = _routing.Press(InputRoutingState.ButtonId((uint)message)); }
                    else if (message == WmLButtonUp || message == WmRButtonUp || message == WmMButtonUp) { value = InputEvent.MouseButton(InputKind.MouseButtonUp, (uint)message, now); route = _routing.Release(InputRoutingState.ButtonId((uint)message - 1)); }
                    else if (message == WmXButtonDown) { value = InputEvent.MouseButton(InputKind.MouseButtonDown, (uint)message, now); value.MouseData = xButton; route = _routing.Press(InputRoutingState.ButtonId((uint)WmXButtonDown | ((uint)xButton << 12))); }
                    else if (message == WmXButtonUp) { value = InputEvent.MouseButton(InputKind.MouseButtonUp, (uint)message, now); value.MouseData = xButton; route = _routing.Release(InputRoutingState.ButtonId((uint)WmXButtonDown | ((uint)xButton << 12))); }
                    else if (message == WmMouseWheel || message == WmMouseHWheel) { value = InputEvent.MouseWheel(unchecked((short)(data.MouseData >> 16)), now); if (message == WmMouseHWheel) value.Flags = SendInputSink.HorizontalWheelFlag; route = _routing.Continuous(); }
                    else route = InputRoute.Local;
                    if (value != null && route == InputRoute.Remote && _sink.Publish(value)) return (IntPtr)1;
                }
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
    }

    public sealed class SendInputSink : IFailSafeInputSink
    {
        public const long InputTag = 0x57494E505554;
        public const uint HorizontalWheelFlag = 1;
        // After this idle gap the virtual cursor resyncs with the real one, which the local user may have moved.
        private const int CursorResyncMs = 250;
        // Pressed key -> its hook flags, so a fail-safe release keeps the extended-key bit of the original press.
        private readonly Dictionary<ushort, uint> _pressedKeys = new Dictionary<ushort, uint>();
        private readonly HashSet<uint> _pressedButtons = new HashSet<uint>();
        private readonly object _gate = new object();
        private int _cursorX;
        private int _cursorY;
        private int _lastDeltaTick;
        private bool _hasCursor;

        public bool Publish(InputEvent value)
        {
            if (value == null) return false;
            var previousDpi = IntPtr.Zero;
            var input = new NativeMethods.INPUT { Type = NativeMethods.InputKeyboard };
            if (value.Kind == InputKind.KeyDown || value.Kind == InputKind.KeyUp)
            {
                input.Data.Keyboard = new NativeMethods.KEYBDINPUT { Vk = value.VirtualKey, Scan = value.ScanCode, Flags = KeyInjection.SendInputFlags(value.Kind, value.Flags), Time = 0, ExtraInfo = new IntPtr(InputTag) };
            }
            else
            {
                input.Type = NativeMethods.InputMouse;
                var flags = 0U;
                var dx = 0;
                var dy = 0;
                var mouseData = 0U;
                if (value.Kind == InputKind.MouseMove)
                {
                    flags = NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk;
                    dx = PointerCoordinates.ClampNormalized(value.X);
                    dy = PointerCoordinates.ClampNormalized(value.Y);
                }
                else if (value.Kind == InputKind.MouseDelta)
                {
                    // Relative SendInput would apply this machine's pointer acceleration a second time,
                    // so the delta is added to a tracked cursor and injected as an exact absolute position.
                    flags = NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk;
                    // SendInput maps absolute coordinates in the caller's DPI context, so it stays physical until the send.
                    previousDpi = NativeMethods.UsePhysicalPixels();
                    var left = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
                    var top = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
                    var width = NativeMethods.GetSystemMetrics(NativeMethods.SmVirtualScreenWidth);
                    var height = NativeMethods.GetSystemMetrics(NativeMethods.SmVirtualScreenHeight);
                    lock (_gate)
                    {
                        var tick = Environment.TickCount;
                        if (!_hasCursor || unchecked(tick - _lastDeltaTick) > CursorResyncMs)
                        {
                            NativeMethods.POINT current;
                            if (NativeMethods.GetCursorPos(out current)) { _cursorX = current.X; _cursorY = current.Y; _hasCursor = true; }
                        }
                        _lastDeltaTick = tick;
                        _cursorX = Math.Max(left, Math.Min(left + width - 1, _cursorX + value.X));
                        _cursorY = Math.Max(top, Math.Min(top + height - 1, _cursorY + value.Y));
                        dx = PointerCoordinates.ToAbsolute(_cursorX, left, width);
                        dy = PointerCoordinates.ToAbsolute(_cursorY, top, height);
                    }
                }
                else if (value.Kind == InputKind.MouseButtonDown || value.Kind == InputKind.MouseButtonUp)
                {
                    var down = value.Kind == InputKind.MouseButtonDown;
                    flags = MouseButtonFlags(value.Flags, down);
                    if (flags == 0) return false;
                    if (value.Flags == 0x020B || value.Flags == 0x020C) mouseData = value.MouseData;
                }
                else if (value.Kind == InputKind.MouseWheel)
                {
                    flags = value.Flags == HorizontalWheelFlag ? NativeMethods.MouseEventHWheel : NativeMethods.MouseEventWheel;
                    mouseData = unchecked((uint)(short)value.MouseData);
                }
                input.Data.Mouse = new NativeMethods.MOUSEINPUT { Dx = dx, Dy = dy, MouseData = mouseData, Flags = flags, Time = 0, ExtraInfo = new IntPtr(InputTag) };
            }
            uint sent;
            try { sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT))); }
            catch { return false; }
            finally { NativeMethods.RestoreDpi(previousDpi); }
            if (sent != 1) return false;
            lock (_gate)
            {
                if (value.Kind == InputKind.KeyDown) _pressedKeys[value.VirtualKey] = value.Flags;
                else if (value.Kind == InputKind.KeyUp) _pressedKeys.Remove(value.VirtualKey);
                else if (value.Kind == InputKind.MouseButtonDown) _pressedButtons.Add(ButtonKey(value.Flags, value.MouseData));
                else if (value.Kind == InputKind.MouseButtonUp) _pressedButtons.Remove(ButtonKey(value.Flags - 1, value.MouseData));
            }
            return true;
        }

        // Releases only what this sink actually pressed; stray button-ups would end drags or click at random.
        public void ReleaseAll()
        {
            KeyValuePair<ushort, uint>[] keys;
            uint[] buttons;
            lock (_gate) { keys = new List<KeyValuePair<ushort, uint>>(_pressedKeys).ToArray(); buttons = new List<uint>(_pressedButtons).ToArray(); _pressedKeys.Clear(); _pressedButtons.Clear(); _hasCursor = false; }
            foreach (var key in keys) Publish(InputEvent.Key(InputKind.KeyUp, key.Key, 0, key.Value, DateTime.UtcNow.Ticks));
            foreach (var button in buttons)
            {
                var up = InputEvent.MouseButton(InputKind.MouseButtonUp, (button & 0xFFFF) + 1, DateTime.UtcNow.Ticks);
                up.MouseData = (ushort)(button >> 16);
                Publish(up);
            }
        }

        private static uint ButtonKey(uint downMessage, ushort mouseData) { return downMessage == 0x020B ? downMessage | ((uint)mouseData << 16) : downMessage; }

        private static uint MouseButtonFlags(uint message, bool down)
        {
            switch (message)
            {
                case 0x0201: case 0x0202: return down ? NativeMethods.MouseEventLeftDown : NativeMethods.MouseEventLeftUp;
                case 0x0204: case 0x0205: return down ? NativeMethods.MouseEventRightDown : NativeMethods.MouseEventRightUp;
                case 0x0207: case 0x0208: return down ? NativeMethods.MouseEventMiddleDown : NativeMethods.MouseEventMiddleUp;
                case 0x020B: case 0x020C: return down ? NativeMethods.MouseEventXDown : NativeMethods.MouseEventXUp;
                default: return 0;
            }
        }
    }

    internal static class NativeMethods
    {
        internal const int WmKeyDown = 0x0100;
        internal const int WmKeyUp = 0x0101;
        internal const int WmSysKeyDown = 0x0104;
        internal const int WmSysKeyUp = 0x0105;
        internal const uint InputMouse = 0;
        internal const uint InputKeyboard = 1;
        internal const uint MouseEventMove = 0x0001;
        internal const uint MouseEventLeftDown = 0x0002;
        internal const uint MouseEventLeftUp = 0x0004;
        internal const uint MouseEventRightDown = 0x0008;
        internal const uint MouseEventRightUp = 0x0010;
        internal const uint MouseEventMiddleDown = 0x0020;
        internal const uint MouseEventMiddleUp = 0x0040;
        internal const uint MouseEventXDown = 0x0080;
        internal const uint MouseEventXUp = 0x0100;
        internal const uint MouseEventWheel = 0x0800;
        internal const uint MouseEventHWheel = 0x1000;
        internal const uint MouseEventAbsolute = 0x8000;
        internal const uint MouseEventVirtualDesk = 0x4000;
        internal const int SmCxScreen = 0;
        internal const int SmCyScreen = 1;
        internal const int SmXVirtualScreen = 76;
        internal const int SmYVirtualScreen = 77;
        internal const int SmVirtualScreenWidth = 78;
        internal const int SmVirtualScreenHeight = 79;

        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        // Per-thread so WPF's own DPI mode is untouched. Returns the previous context (zero if unsupported).
        internal static IntPtr UsePhysicalPixels()
        {
            try { return SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }

        internal static void RestoreDpi(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(previous); }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

        internal const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MSG { public IntPtr Hwnd; public int Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public POINT Point; }
        [StructLayout(LayoutKind.Sequential)] internal struct KbdLlHookStruct { public uint VirtualKey; public uint ScanCode; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseLlHookStruct { public POINT Point; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    }
}
