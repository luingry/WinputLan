using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
        private const uint LlkhfInjected = 0x10;
        private const uint LlmhfInjected = 0x01;
        private readonly IInputSink _sink;
        private NativeMethods.HookProc _keyboardProc;
        private NativeMethods.HookProc _mouseProc;
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private bool _disposed;

        public LowLevelInputCapture(IInputSink sink) { _sink = sink ?? throw new ArgumentNullException("sink"); }

        public bool Start()
        {
            if (_keyboardHook != IntPtr.Zero) return true;
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;
            var module = NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            _keyboardHook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                Stop();
                return false;
            }
            return true;
        }

        public void Stop()
        {
            if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && lParam != IntPtr.Zero)
            {
                var data = (NativeMethods.KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KbdLlHookStruct));
                if ((data.Flags & LlkhfInjected) == 0 && data.ExtraInfo.ToInt64() != SendInputSink.InputTag)
                {
                    var message = wParam.ToInt32();
                    var kind = message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown ? InputKind.KeyDown : message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp ? InputKind.KeyUp : (InputKind?)null;
                    if (kind.HasValue && _sink.Publish(InputEvent.Key(kind.Value, (ushort)data.VirtualKey, (ushort)data.ScanCode, data.Flags, DateTime.UtcNow.Ticks))) return (IntPtr)1;
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
                    InputEvent value = null;
                    if (message == WmMouseMove) value = InputEvent.MouseMove(data.Point.X, data.Point.Y, DateTime.UtcNow.Ticks);
                    else if (message == WmLButtonDown || message == WmRButtonDown || message == WmMButtonDown) value = InputEvent.MouseButton(InputKind.MouseButtonDown, (uint)message, DateTime.UtcNow.Ticks);
                    else if (message == WmLButtonUp || message == WmRButtonUp || message == WmMButtonUp) value = InputEvent.MouseButton(InputKind.MouseButtonUp, (uint)message, DateTime.UtcNow.Ticks);
                    else if (message == WmMouseWheel) value = InputEvent.MouseWheel(unchecked((short)(data.MouseData >> 16)), DateTime.UtcNow.Ticks);
                    if (value != null && _sink.Publish(value)) return (IntPtr)1;
                }
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
    }

    public sealed class SendInputSink : IFailSafeInputSink
    {
        public const long InputTag = 0x57494E505554;
        private readonly HashSet<ushort> _pressedKeys = new HashSet<ushort>();
        private readonly HashSet<uint> _pressedButtons = new HashSet<uint>();
        private readonly object _gate = new object();

        public bool Publish(InputEvent value)
        {
            if (value == null) return false;
            var input = new NativeMethods.INPUT { Type = NativeMethods.InputKeyboard };
            if (value.Kind == InputKind.KeyDown || value.Kind == InputKind.KeyUp)
            {
                input.Data.Keyboard = new NativeMethods.KEYBDINPUT { Vk = value.VirtualKey, Scan = value.ScanCode, Flags = value.Kind == InputKind.KeyUp ? NativeMethods.KeyEventKeyUp : 0, Time = 0, ExtraInfo = new IntPtr(InputTag) };
            }
            else
            {
                input.Type = NativeMethods.InputMouse;
                var flags = 0U;
                var dx = 0;
                var dy = 0;
                if (value.Kind == InputKind.MouseMove)
                {
                    flags = NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk;
                    dx = NormalizeAbsolute(value.X, NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmVirtualScreenWidth));
                    dy = NormalizeAbsolute(value.Y, NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmVirtualScreenHeight));
                }
                if (value.Kind == InputKind.MouseButtonDown) flags |= MouseButtonFlags(value.Flags, true);
                if (value.Kind == InputKind.MouseButtonUp) flags |= MouseButtonFlags(value.Flags, false);
                if (value.Kind == InputKind.MouseWheel) { flags = NativeMethods.MouseEventWheel; }
                input.Data.Mouse = new NativeMethods.MOUSEINPUT { Dx = dx, Dy = dy, MouseData = value.Kind == InputKind.MouseWheel ? unchecked((uint)(short)value.MouseData) : value.MouseData, Flags = flags, Time = 0, ExtraInfo = new IntPtr(InputTag) };
            }
            uint sent;
            try { sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT))); }
            catch { return false; }
            if (sent != 1) return false;
            if (value.Kind == InputKind.KeyDown || value.Kind == InputKind.KeyUp)
            {
                lock (_gate) { if (value.Kind == InputKind.KeyDown) _pressedKeys.Add(value.VirtualKey); else _pressedKeys.Remove(value.VirtualKey); }
            }
            return true;
        }

        public void ReleaseAll()
        {
            ushort[] keys;
            lock (_gate) { keys = new List<ushort>(_pressedKeys).ToArray(); _pressedKeys.Clear(); _pressedButtons.Clear(); }
            foreach (var key in keys) Publish(InputEvent.Key(InputKind.KeyUp, key, 0, 0, DateTime.UtcNow.Ticks));
            Publish(InputEvent.MouseButton(InputKind.MouseButtonUp, 0x0201, DateTime.UtcNow.Ticks));
            Publish(InputEvent.MouseButton(InputKind.MouseButtonUp, 0x0204, DateTime.UtcNow.Ticks));
            Publish(InputEvent.MouseButton(InputKind.MouseButtonUp, 0x0207, DateTime.UtcNow.Ticks));
        }

        private static uint MouseButtonFlags(uint message, bool down)
        {
            switch (message)
            {
                case 0x0201: return down ? NativeMethods.MouseEventLeftDown : NativeMethods.MouseEventLeftUp;
                case 0x0204: return down ? NativeMethods.MouseEventRightDown : NativeMethods.MouseEventRightUp;
                case 0x0207: return down ? NativeMethods.MouseEventMiddleDown : NativeMethods.MouseEventMiddleUp;
                default: return 0;
            }
        }

        private static int NormalizeAbsolute(int coordinate, int origin, int size)
        {
            if (size <= 1) return 0;
            var normalized = (coordinate - origin) * 65535L / (size - 1);
            return (int)Math.Max(0, Math.Min(65535, normalized));
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
        internal const uint KeyEventKeyUp = 0x0002;
        internal const uint MouseEventMove = 0x0001;
        internal const uint MouseEventLeftDown = 0x0002;
        internal const uint MouseEventLeftUp = 0x0004;
        internal const uint MouseEventRightDown = 0x0008;
        internal const uint MouseEventRightUp = 0x0010;
        internal const uint MouseEventMiddleDown = 0x0020;
        internal const uint MouseEventMiddleUp = 0x0040;
        internal const uint MouseEventWheel = 0x0800;
        internal const uint MouseEventAbsolute = 0x8000;
        internal const uint MouseEventVirtualDesk = 0x4000;
        internal const int SmXVirtualScreen = 76;
        internal const int SmYVirtualScreen = 77;
        internal const int SmVirtualScreenWidth = 78;
        internal const int SmVirtualScreenHeight = 79;

        internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct KbdLlHookStruct { public uint VirtualKey; public uint ScanCode; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseLlHookStruct { public POINT Point; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    }
}
