using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModCtrl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private readonly HwndSource _source;
        private int _nextId = 100;
        private bool _disposed;

        public GlobalHotkeyService(HwndSource source) { _source = source ?? throw new ArgumentNullException("source"); _source.AddHook(WindowProc); }
        public event Action<HotkeyAction> Invoked;

        public bool Register(HotkeyAction action, HotkeyGesture gesture)
        {
            HotkeyValidator.Validate(gesture);
            var id = _nextId++;
            var modifiers = ToNativeModifiers(gesture.Modifiers);
            if (!NativeMethodsHotkey.RegisterHotKey(_source.Handle, id, modifiers, gesture.VirtualKey)) return false;
            _bindings[id] = action;
            return true;
        }

        public bool Replace(HotkeyGesture local, HotkeyGesture remote)
        {
            HotkeyValidator.Validate(local);
            HotkeyValidator.Validate(remote);
            var localId = _nextId++;
            var remoteId = _nextId++;
            if (!NativeMethodsHotkey.RegisterHotKey(_source.Handle, localId, ToNativeModifiers(local.Modifiers), local.VirtualKey)) return false;
            if (!NativeMethodsHotkey.RegisterHotKey(_source.Handle, remoteId, ToNativeModifiers(remote.Modifiers), remote.VirtualKey))
            {
                NativeMethodsHotkey.UnregisterHotKey(_source.Handle, localId);
                return false;
            }
            foreach (var id in _bindings.Keys) NativeMethodsHotkey.UnregisterHotKey(_source.Handle, id);
            _bindings.Clear();
            _bindings[localId] = HotkeyAction.SelectLocal;
            _bindings[remoteId] = HotkeyAction.SelectRemote;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var id in _bindings.Keys) NativeMethodsHotkey.UnregisterHotKey(_source.Handle, id);
            _bindings.Clear();
            _source.RemoveHook(WindowProc);
        }

        private readonly System.Collections.Generic.Dictionary<int, HotkeyAction> _bindings = new System.Collections.Generic.Dictionary<int, HotkeyAction>();

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey)
            {
                HotkeyAction action;
                if (_bindings.TryGetValue(wParam.ToInt32(), out action)) { handled = true; Invoked?.Invoke(action); }
            }
            return IntPtr.Zero;
        }

        private static uint ToNativeModifiers(HotkeyModifiers modifiers)
        {
            var value = 0U;
            if ((modifiers & HotkeyModifiers.Alt) != 0) value |= ModAlt;
            if ((modifiers & HotkeyModifiers.Ctrl) != 0) value |= ModCtrl;
            if ((modifiers & HotkeyModifiers.Shift) != 0) value |= ModShift;
            if ((modifiers & HotkeyModifiers.Win) != 0) value |= ModWin;
            return value;
        }
    }

    internal static class NativeMethodsHotkey
    {
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
