using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan.Loopback
{
    // Uses only named synchronization objects. Never starts MainWindow, global hooks or SendInput.
    internal static class SingleInstanceTests
    {
        private static readonly object ResultGate = new object();
        internal static void RunChild(string[] args)
        {
            var mode = args[1]; var name = args[2]; var root = args[3]; var id = args[4];
            if (mode == "ui-hidden" || mode == "ui-minimized") { RunWindowChild(mode, name, root, id); return; }
            if (mode == "handover")
                SingleInstanceGate.WaitForHandover(new[] { ElevationPolicy.RelaunchedArgument, SingleInstanceGate.HandoverArgument, args[5] });
            using (var gate = new SingleInstanceGate(name))
            {
                var owner = gate.TryAcquire();
                if (!owner && mode != "startup") gate.NotifyExistingInstance();
                if (owner)
                    gate.ListenForActivation(() => WriteResult(Path.Combine(root, "activated"), "activated"));
                WriteResult(Path.Combine(root, id), owner ? "OWNER" : "DUPLICATE");
                if (owner && mode != "probe")
                {
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(root, "release")) && timer.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                }
            }
        }

        internal static void Run(bool verifyWindow = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "WinputLan-instance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var name = "WinputLan.Test." + Guid.NewGuid().ToString("N");
            try
            {
                using (var owner = Start("hold", name, root, "owner"))
                {
                    try
                    {
                        ExpectResult(root, "owner", "OWNER");
                        using (var startup = Start("startup", name, root, "startup"))
                        {
                            Finish(startup); ExpectResult(root, "startup", "DUPLICATE");
                            if (File.Exists(Path.Combine(root, "activated"))) throw new InvalidOperationException("Startup restored the existing window.");
                        }
                        using (var second = Start("probe", name, root, "second"))
                        {
                            Finish(second); ExpectResult(root, "second", "DUPLICATE");
                            WaitFile(Path.Combine(root, "activated"));
                        }
                        using (var successor = Start("handover", name, root, "successor", owner.Id.ToString()))
                        {
                            try
                            {
                                // The successor must wait for the parent's exit instead of rejecting its mutex.
                                if (successor.WaitForExit(150)) throw new InvalidOperationException("Handover did not await its parent.");
                                File.WriteAllText(Path.Combine(root, "release"), "release");
                                Finish(owner); Finish(successor); ExpectResult(root, "successor", "OWNER");
                            }
                            finally { Stop(successor); }
                        }
                    }
                    finally { Stop(owner); }
                }
                File.Delete(Path.Combine(root, "release"));
                var racers = Enumerable.Range(0, 8).Select(i => Start("hold", name, root, "race" + i)).ToArray();
                try
                {
                    for (var i = 0; i < racers.Length; i++) WaitFile(Path.Combine(root, "race" + i));
                    if (Enumerable.Range(0, racers.Length).Count(i => File.ReadAllText(Path.Combine(root, "race" + i)) == "OWNER") != 1)
                        throw new InvalidOperationException("Simultaneous starts elected more than one owner.");
                    File.WriteAllText(Path.Combine(root, "release"), "release");
                    foreach (var racer in racers) Finish(racer);
                }
                finally { foreach (var racer in racers) { Stop(racer); racer.Dispose(); } }
                File.Delete(Path.Combine(root, "release"));
                // Retain a handle across the forced exit, so WaitOne must handle a genuinely abandoned mutex.
                using (var crashed = Start("hold", name, root, "crashed"))
                {
                    try
                    {
                        ExpectResult(root, "crashed", "OWNER");
                        using (var recovery = new SingleInstanceGate(name))
                        {
                            if (recovery.TryAcquire()) throw new InvalidOperationException("Acquired the live owner's mutex.");
                            crashed.Kill(); crashed.WaitForExit();
                            if (!recovery.TryAcquire()) throw new InvalidOperationException("Abandoned mutex could not be recovered.");
                        }
                    }
                    finally { Stop(crashed); }
                }
                using (var restart = Start("probe", name, root, "restart"))
                {
                    Finish(restart); ExpectResult(root, "restart", "OWNER");
                }
                using (var early = new SingleInstanceGate(name + ".early"))
                using (var delivered = new ManualResetEvent(false))
                {
                    if (!early.TryAcquire()) throw new InvalidOperationException("Early activation fixture did not own the mutex.");
                    using (var second = Start("probe", name + ".early", root, "early")) Finish(second);
                    early.ListenForActivation(() => delivered.Set());
                    if (!delivered.WaitOne(3000)) throw new InvalidOperationException("Activation before UI initialization was lost.");
                }
                // Foreground-window checks need an interactive desktop; run explicitly on the
                // developer's machine instead of making headless CI depend on foreground focus.
                foreach (var mode in verifyWindow ? new[] { "ui-hidden", "ui-minimized" } : new string[0])
                {
                    File.Delete(Path.Combine(root, "activated"));
                    using (var window = Start(mode, name, root, mode))
                    {
                        try
                        {
                            ExpectResult(root, mode, "OWNER");
                            using (var launcher = Start("probe", name, root, mode + "-launcher"))
                            {
                                Finish(launcher); ExpectResult(root, mode + "-launcher", "DUPLICATE");
                            }
                            WaitFile(Path.Combine(root, "activated")); Finish(window);
                            if (File.ReadAllText(Path.Combine(root, "activated")) != "visible-normal-active")
                                throw new InvalidOperationException("Existing WPF window was not restored and activated.");
                        }
                        finally { Stop(window); }
                    }
                }
                Console.WriteLine("SINGLE INSTANCE PASS: concurrent starts, manual/early activation, quiet startup, parent handover, crash recovery, restart"
                    + (verifyWindow ? ", hidden/minimized WPF restoration" : string.Empty));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void RunWindowChild(string mode, string name, string root, string id)
        {
            using (var gate = new SingleInstanceGate(name))
            {
                if (!gate.TryAcquire()) throw new InvalidOperationException("WPF fixture did not own the mutex.");
                var application = new Application();
                var window = new Window { Title = "Winput LAN — teste de instância", Width = 320, Height = 160,
                    Content = "Validação de restauração da janela", WindowStartupLocation = WindowStartupLocation.CenterScreen };
                gate.ListenForActivation(() => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    WindowActivation.Restore(window);
                    // Let WPF process the native activation before checking the visible state.
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        WriteResult(Path.Combine(root, "activated"),
                            window.IsVisible && window.WindowState == WindowState.Normal && window.IsActive ? "visible-normal-active" : "failed");
                        window.Close();
                    }));
                })));
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                timeout.Tick += (sender, e) => { timeout.Stop(); window.Close(); };
                window.Loaded += (sender, e) =>
                {
                    if (mode == "ui-hidden") window.Hide(); else window.WindowState = WindowState.Minimized;
                    WriteResult(Path.Combine(root, id), "OWNER");
                    timeout.Start();
                };
                application.Run(window);
                timeout.Stop();
            }
        }

        private static void WriteResult(string path, string result)
        {
            // Publish a complete marker: existence must never race a partially written result.
            lock (ResultGate)
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, result);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }

        private static Process Start(string mode, string name, string root, string id, string parentId = "")
        {
            return Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                "--instance-test " + mode + " " + name + " \"" + root + "\" " + id + " " + parentId)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        private static void ExpectResult(string root, string id, string expected)
        {
            var path = Path.Combine(root, id); WaitFile(path);
            if (File.ReadAllText(path) != expected) throw new InvalidOperationException(id + " did not report " + expected);
        }

        private static void WaitFile(string path)
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(path) && timer.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            if (!File.Exists(path)) throw new TimeoutException("Instance test did not create " + Path.GetFileName(path));
        }

        private static void Finish(Process process)
        {
            if (!process.WaitForExit(5000)) throw new TimeoutException("Instance test process did not exit.");
            if (process.ExitCode != 0) throw new InvalidOperationException("Instance test process failed: " + process.ExitCode);
        }

        private static void Stop(Process process)
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
        }
    }
}
