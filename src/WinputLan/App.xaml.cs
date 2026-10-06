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
        private SingleInstanceGate _instance;
        private bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Isolated report UI QA never creates input hooks, a listener, or pairing state.
            if(e.Args.Contains("--report-smoke"))
            {
                base.OnStartup(e);
                var snapshot = BugReportDiagnostics.Capture(WinputConfig.CreateDefault(), new TransactionLogEntry[0], new double[4], false, false, false, "Offline", "Offline", 1, 1);
                var report = new BugReportWindow(snapshot, Path.Combine(Path.GetTempPath(), "WinputLan-report-smoke"));
                MainWindow = report; report.Show(); return;
            }
            if (e.Args.Length > 0 && string.Equals(e.Args[0], CursorVisibilityGuard.HelperArgument, StringComparison.Ordinal))
            {
                CursorVisibilityGuard.RunHelper(e.Args);
                Shutdown();
                return;
            }
            var startup = e.Args.Any(a => string.Equals(a, StartupRegistration.StartupArgument, StringComparison.OrdinalIgnoreCase));
            SingleInstanceGate.WaitForHandover(e.Args);
            try
            {
                _instance = new SingleInstanceGate();
                if (!_instance.TryAcquire())
                {
                    // Login/startup retries stay in the tray; an explicit launch restores the UI.
                    if (!startup) _instance.NotifyExistingInstance();
                    Shutdown();
                    return;
                }
                _instance.ListenForActivation(() =>
                {
                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) WindowActivation.Restore(MainWindow); }));
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Não foi possível verificar a instância do Winput LAN.\n\n" + ex.Message,
                    "Winput LAN", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            base.OnStartup(e);
            var appDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan");
            ConfigStore = new AppConfigStore(appDirectory);
            Config = ConfigStore.LoadOrCreate();
            // Exit before binding the listener port so the elevated copy can take over.
            if (ElevationPolicy.ShouldRelaunchElevated(Config.RunElevated, ProcessElevation.IsCurrentElevated(), e.Args) && ProcessElevation.TryRelaunchElevated(startup))
            {
                Shutdown();
                return;
            }
            var window = new MainWindow(Config, ConfigStore);
            MainWindow = window;
            window.Closed += (sender, args) => _exiting = true;
            // Started with Windows: run in the notification area without showing the window.
            if (startup) window.StartInTray(); else window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exiting = true;
            _instance?.Dispose();
            base.OnExit(e);
        }
    }
}
