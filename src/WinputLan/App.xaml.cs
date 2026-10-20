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
        // Set when started by the SwissTechKnife module (--hosted); null for the standalone app.
        public static HostedMode Hosted { get; private set; }
        // Settings, device identity and WebView2 profile: %LOCALAPPDATA%\WinputLan, or the module data folder when hosted.
        public static string DataDirectory { get; private set; } = HostedMode.StandaloneDataDirectory;
        private SingleInstanceGate _instance;
        private bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Isolated report UI QA never creates input hooks, a listener, or pairing state.
            if(e.Args.Contains("--report-smoke"))
            {
                base.OnStartup(e);
                var snapshot = BugReportDiagnostics.Capture(WinputConfig.CreateDefault(), new TransactionLogEntry[0], new double[4], false, false, false, "Offline", "Offline", 1, 1);
                var report = new BugReportWindow(snapshot);
                MainWindow = report; report.Show(); return;
            }
            if (e.Args.Length > 0 && string.Equals(e.Args[0], CursorVisibilityGuard.HelperArgument, StringComparison.Ordinal))
            {
                CursorVisibilityGuard.RunHelper(e.Args);
                Shutdown();
                return;
            }
            // Elevated hosted-mode helper (UAC confirmed after an explicit click): no gate, no window.
            string adminData;
            var adminAction = HostedOptions.ParseAdminAction(e.Args, out adminData);
            if (adminAction != null)
            {
                Shutdown(HostedAdmin.Run(adminAction, adminData));
                return;
            }
            var hostedOptions = HostedOptions.Parse(e.Args);
            if (hostedOptions != null) { Hosted = new HostedMode(hostedOptions); DataDirectory = hostedOptions.DataDirectory; }
            var startup = Hosted == null && e.Args.Any(a => string.Equals(a, StartupRegistration.StartupArgument, StringComparison.OrdinalIgnoreCase));
            if (Hosted == null) SingleInstanceGate.WaitForHandover(e.Args);
            try
            {
                _instance = new SingleInstanceGate();
                if (!_instance.TryAcquire())
                {
                    // Same mutex in both modes: standalone and hosted never hook input or bind the port together.
                    // Hosted: the module reads the exit code, waits for the other instance and activates it itself.
                    if (Hosted != null) { Shutdown(HostedOptions.AlreadyRunningExitCode); return; }
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
            if (Hosted != null)
            {
                Hosted.MigrateStandaloneData();
                Hosted.WritePidFile();
                Hosted.WatchForExit(() => Dispatcher.BeginInvoke(new Action(ExitHosted)));
            }
            ConfigStore = new AppConfigStore(DataDirectory);
            Config = ConfigStore.LoadOrCreate();
            // Exit before binding the listener port so the elevated copy can take over.
            // Hosted never relaunches itself: the module starts it elevated through its task, never with a UAC prompt.
            if (Hosted == null && ElevationPolicy.ShouldRelaunchElevated(Config.RunElevated, ProcessElevation.IsCurrentElevated(), e.Args) && ProcessElevation.TryRelaunchElevated(startup))
            {
                Shutdown();
                return;
            }
            var window = new MainWindow(Config, ConfigStore);
            MainWindow = window;
            window.Closed += (sender, args) => _exiting = true;
            // Started with Windows: run in the notification area without showing the window.
            // Hosted: hidden until the module page's "Abrir Winput LAN" (or --show) asks for the window.
            if (Hosted != null) { if (Hosted.Options.ShowOnStart) window.Show(); else window.StartHidden(); }
            else if (startup) window.StartInTray(); else window.Show();
        }

        // The host exited or disabled the module: same cleanup as an explicit exit, then end the process.
        private void ExitHosted() { ExitHosted(0); }

        // Also used with HostedOptions.RestartExitCode after an elevation change, so the module relaunches the app.
        internal void ExitHosted(int exitCode)
        {
            if (_exiting) return;
            // Explicit: closing the main window must not end the process first with exit code 0.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            (MainWindow as MainWindow)?.ExitApplication();
            _exiting = true;
            Shutdown(exitCode);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exiting = true;
            _instance?.Dispose();
            Hosted?.Dispose();
            base.OnExit(e);
        }
    }
}
