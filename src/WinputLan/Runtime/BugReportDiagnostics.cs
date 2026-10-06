using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Collections.Generic;
using WinputLan.Core;
using Forms = System.Windows.Forms;

namespace WinputLan.Runtime
{
    public static class BugReportDiagnostics
    {
        public static BugReport Capture(WinputConfig config, IEnumerable<TransactionLogEntry> events, double[] latency,
            bool elevated, bool remoteActive, bool inboundFocused, string outboundState, string inboundState, double dpiX, double dpiY)
        {
            var report = new BugReport();
            var m = report.Diagnostics.Metadata;
            m["appVersion"] = typeof(BugReportDiagnostics).Assembly.GetName().Version.ToString(3);
            m["windowsVersion"] = Environment.OSVersion.Version.ToString();
            m["clrVersion"] = Environment.Version.ToString();
            m["architecture"] = Environment.Is64BitProcess ? "x64" : "x86";
            m["elevated"] = elevated; m["remoteActive"] = remoteActive; m["inboundFocused"] = inboundFocused;
            m["startWithWindows"] = config.StartWithWindows; m["continueInBackground"] = config.ContinueInBackground;
            m["runElevated"] = config.RunElevated; m["autoAcceptKnown"] = config.AutoAcceptKnownConnections;
            m["edgeSwitchEnabled"] = config.EdgeSwitchEnabled;
            m["localEdge"] = config.LocalEdge.ToString(); m["remoteEdge"] = config.RemoteEdge.ToString();
            // Config is never serialized. Only parsed, operational shortcut values are exported.
            try { m["localHotkey"] = HotkeyGesture.Parse(config.LocalHotkey).ToString(); } catch { m["localHotkey"] = "unavailable"; }
            try { m["remoteHotkey"] = HotkeyGesture.Parse(config.RemoteHotkey).ToString(); } catch { m["remoteHotkey"] = "unavailable"; }
            m["outboundState"] = outboundState; m["inboundState"] = inboundState;
            m["dpiScaleX"] = dpiX; m["dpiScaleY"] = dpiY;
            m["latencySamples"] = latency[0]; m["latencyP50Ms"] = latency[1]; m["latencyP95Ms"] = latency[2]; m["latencyMaxMs"] = latency[3];
            try { using (var process = Process.GetCurrentProcess()) { m["uptimeSeconds"] = Math.Max(0, (DateTime.Now - process.StartTime).TotalSeconds); m["processMemoryMb"] = process.WorkingSet64 / 1048576d; } } catch { }
            try { foreach (var screen in Forms.Screen.AllScreens.Take(16)) report.Diagnostics.Monitors.Add(new BugReportMonitor { X = screen.Bounds.X, Y = screen.Bounds.Y, Width = screen.Bounds.Width, Height = screen.Bounds.Height, Primary = screen.Primary }); } catch { }
            report.Diagnostics.Events = BugReportPrivacy.Events(events);
            return report;
        }
        public static string GetNetworkType()
        {
            try {
                var types = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).Select(n => n.NetworkInterfaceType).ToArray();
                return types.Contains(NetworkInterfaceType.Wireless80211) ? "WiFi" : types.Contains(NetworkInterfaceType.Ethernet) ? "Ethernet" : "Other";
            } catch { return "Unknown"; }
        }
    }
}
