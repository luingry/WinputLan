using System.Windows;

namespace WinputLan.Runtime
{
    public static class WindowActivation
    {
        // Shared by tray restore and the single-instance activation request. Call on the UI thread.
        public static void Restore(Window window)
        {
            if (window == null) return;
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }
}
