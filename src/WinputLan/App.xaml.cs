using System;
using System.Linq;
using System.IO;
using System.Windows;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan
{
    public partial class App : Application
    {
        public static WinputConfig Config { get; private set; }
        public static AppConfigStore ConfigStore { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0 && string.Equals(e.Args[0], CursorVisibilityGuard.HelperArgument, StringComparison.Ordinal))
            {
                CursorVisibilityGuard.RunHelper(e.Args);
                Shutdown();
                return;
            }
            base.OnStartup(e);
            var appDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan");
            ConfigStore = new AppConfigStore(appDirectory);
            Config = ConfigStore.LoadOrCreate();
            // Exit before binding the listener port so the elevated copy can take over.
            var startup = e.Args.Any(a => string.Equals(a, StartupRegistration.StartupArgument, StringComparison.OrdinalIgnoreCase));
            if (ElevationPolicy.ShouldRelaunchElevated(Config.RunElevated, ProcessElevation.IsCurrentElevated(), e.Args) && ProcessElevation.TryRelaunchElevated(startup))
            {
                Shutdown();
                return;
            }
            var window = new MainWindow(Config, ConfigStore);
            MainWindow = window;
            // Started with Windows: run in the notification area without showing the window.
            if (startup) window.StartInTray(); else window.Show();
        }
    }
}
