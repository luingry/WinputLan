using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WinputLan.Runtime
{
    // One application per user and interactive session, regardless of elevation or executable path.
    // Cursor recovery helpers never enter this gate. Hold the mutex until all app cleanup completes.
    public sealed class SingleInstanceGate : IDisposable
    {
        public const string HandoverArgument = "--handover-from";
        private readonly Mutex _mutex;
        private readonly EventWaitHandle _activation;
        private RegisteredWaitHandle _activationWait;
        private bool _ownsMutex;

        public SingleInstanceGate(string applicationName = "WinputLan")
        {
            var sid = WindowsIdentity.GetCurrent().User.Value;
            var name = @"Local\" + applicationName + "." + sid;
            // A medium-integrity launcher must be able to signal an elevated owner. Restrict
            // access to this user, and explicitly use the medium mandatory integrity label.
            IntPtr descriptor;
            uint descriptorSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:(A;;GA;;;" + sid + ")S:(ML;;NW;;;ME)", 1, out descriptor, out descriptorSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
                _mutex = new Mutex(false);
                var oldMutexHandle = _mutex.SafeWaitHandle;
                _mutex.SafeWaitHandle = CheckedHandle(CreateMutexEx(ref security, name + ".Instance", 0, 0x00100001));
                oldMutexHandle.Dispose();
                _activation = new EventWaitHandle(false, EventResetMode.AutoReset);
                var oldEventHandle = _activation.SafeWaitHandle;
                _activation.SafeWaitHandle = CheckedHandle(CreateEventEx(ref security, name + ".Activate", 0, 0x00100002));
                oldEventHandle.Dispose();
            }
            catch
            {
                _activation?.Dispose();
                _mutex?.Dispose();
                throw;
            }
            finally { LocalFree(descriptor); }
        }

        // Acquire and dispose on the same thread: Windows mutex ownership is thread-affine.
        public bool TryAcquire()
        {
            if (_ownsMutex) return true;
            try { _ownsMutex = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            return _ownsMutex;
        }

        public void ListenForActivation(Action activate)
        {
            if (!_ownsMutex) throw new InvalidOperationException("Only the application owner can listen for activation.");
            if (activate == null) throw new ArgumentNullException(nameof(activate));
            if (_activationWait != null) throw new InvalidOperationException("Activation is already registered.");
            // Auto-reset event also retains an activation arriving before the UI is ready.
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (state, timedOut) => activate(), null, Timeout.Infinite, false);
        }

        public void NotifyExistingInstance()
        {
            // Let the owner activate its window even though the foreground permission belongs
            // to this new launcher. This works across the normal/elevated boundary as well.
            foreach (var process in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                using (process)
                {
                    try { AllowSetForegroundWindow((uint)process.Id); }
                    catch (InvalidOperationException) { }
                }
            }
            _activation.Set();
        }

        public static void WaitForHandover(string[] args)
        {
            if (Array.IndexOf(args, WinputLan.Core.ElevationPolicy.RelaunchedArgument) < 0) return;
            var index = Array.IndexOf(args, HandoverArgument);
            int parentId;
            if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out parentId) || parentId <= 0 || parentId == Process.GetCurrentProcess().Id) return;
            try
            {
                using (var parent = Process.GetProcessById(parentId)) parent.WaitForExit(15000);
            }
            catch (ArgumentException) { } // Parent already exited.
            catch (Win32Exception) { } // The mutex still decides whether startup is allowed.
        }

        public void Dispose()
        {
            _activationWait?.Unregister(null);
            _activationWait = null;
            if (_ownsMutex) { _mutex.ReleaseMutex(); _ownsMutex = false; }
            _activation.Dispose();
            _mutex.Dispose();
        }

        private static SafeWaitHandle CheckedHandle(SafeWaitHandle handle)
        {
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int InheritHandle;
        }

        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateMutexEx(ref SecurityAttributes security, string name, uint flags, uint access);
        [DllImport("kernel32.dll", EntryPoint = "CreateEventExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateEventEx(ref SecurityAttributes security, string name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }
}
