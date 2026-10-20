using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    // Running under the SwissTechKnife host (--hosted). The winput-lan module starts this process and owns
    // tray, updates and auto-start, so hosted mode turns those off, keeps its data in the module's data folder
    // and exits with the host or when the module sets the exit event. Input and networking are unchanged.
    public sealed class HostedMode : IDisposable
    {
        private EventWaitHandle _exitEvent;
        private RegisteredWaitHandle _exitRegistration;
        private Process _parent;
        private int _exitRequested;

        public HostedMode(HostedOptions options) { Options = options ?? throw new ArgumentNullException(nameof(options)); }

        public HostedOptions Options { get; }
        public string DataDirectory { get { return Options.DataDirectory; } }

        public static string StandaloneDataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinputLan"); }
        }

        // First hosted run: copies the standalone settings, pairings and device identity once, so known PCs keep
        // recognizing this one. The standalone files are never modified; a copy that fails starts from defaults.
        public void MigrateStandaloneData()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var marker = Path.Combine(DataDirectory, ".migrated");
                if (File.Exists(marker)) return;
                foreach (var name in new[] { "config.json", "identity.pfx.dpapi" })
                {
                    var source = Path.Combine(StandaloneDataDirectory, name);
                    var target = Path.Combine(DataDirectory, name);
                    if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
                }
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // Lets the module find this process even when the elevated task started it (no Process handle there).
        public void WritePidFile()
        {
            try { File.WriteAllText(Path.Combine(DataDirectory, HostedOptions.PidFileName), Process.GetCurrentProcess().Id.ToString()); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // Calls exit once (from any thread) when the module sets the exit event or the host process ends.
        public void WatchForExit(Action exit)
        {
            Action once = () => { if (Interlocked.Exchange(ref _exitRequested, 1) == 0) exit(); };
            try
            {
                _exitEvent = SingleInstanceGate.CreateUserEvent(HostedOptions.ExitEventName, true);
                _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (state, timedOut) => once(), null, Timeout.Infinite, true);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is UnauthorizedAccessException || ex is IOException) { }

            if (Options.ParentId <= 0) return;
            try
            {
                _parent = Process.GetProcessById(Options.ParentId);
                _parent.EnableRaisingEvents = true;
                _parent.Exited += (sender, args) => once();
                if (_parent.HasExited) once();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
                once(); // The host is already gone.
            }
        }

        public void Dispose()
        {
            _exitRegistration?.Unregister(null);
            _exitEvent?.Dispose();
            _parent?.Dispose();
            try
            {
                var pidFile = Path.Combine(DataDirectory, HostedOptions.PidFileName);
                if (File.Exists(pidFile) && File.ReadAllText(pidFile).Trim() == Process.GetCurrentProcess().Id.ToString()) File.Delete(pidFile);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
