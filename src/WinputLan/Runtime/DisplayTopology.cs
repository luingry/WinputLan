using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    // Reads this PC's monitors in physical pixels and tells panels built into the machine from external ones. Read-only.
    public static class DisplayTopology
    {
        private const uint MonitorInfoPrimary = 1;
        private const uint QueryOnlyActivePaths = 2;
        private const int DeviceInfoGetSourceName = 1;
        private const uint OutputLvds = 6;
        private const uint OutputDisplayPortEmbedded = 11;
        private const uint OutputUdiEmbedded = 13;
        private const uint OutputInternal = 0x80000000;

        public static List<EdgeMonitor> Read()
        {
            var monitors = new List<EdgeMonitor>();
            var previousDpi = NativeMethods.UsePhysicalPixels();
            try
            {
                var internalDevices = InternalDevices();
                NativeMethods.MonitorEnumProc collect = (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT bounds, IntPtr data) =>
                {
                    var info = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                    if (GetMonitorInfoW(monitor, ref info)) monitors.Add(new EdgeMonitor { Left = info.Bounds.Left, Top = info.Bounds.Top, Right = info.Bounds.Right, Bottom = info.Bounds.Bottom, Primary = (info.Flags & MonitorInfoPrimary) != 0, Internal = internalDevices.Contains(info.Device) });
                    return true;
                };
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
                GC.KeepAlive(collect);
            }
            catch { }
            finally { NativeMethods.RestoreDpi(previousDpi); }
            return monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Left).ThenBy(m => m.Top).ToList();
        }

        // GDI device names (\\.\DISPLAY1) of the panels built into the machine; empty when Windows cannot tell.
        private static HashSet<string> InternalDevices()
        {
            var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint pathCount, modeCount;
                if (GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out pathCount, out modeCount) != 0) return devices;
                var paths = new DisplayPath[pathCount];
                var modes = new DisplayMode[modeCount];
                if (QueryDisplayConfig(QueryOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return devices;
                for (var i = 0; i < pathCount; i++)
                {
                    var output = paths[i].TargetOutputTechnology;
                    if (output != OutputLvds && output != OutputDisplayPortEmbedded && output != OutputUdiEmbedded && output != OutputInternal) continue;
                    var name = new SourceDeviceName { Type = DeviceInfoGetSourceName, Size = Marshal.SizeOf(typeof(SourceDeviceName)), AdapterLow = paths[i].SourceAdapterLow, AdapterHigh = paths[i].SourceAdapterHigh, Id = paths[i].SourceId };
                    if (DisplayConfigGetDeviceInfo(ref name) == 0) devices.Add(name.Device);
                }
            }
            catch (EntryPointNotFoundException) { }
            return devices;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths, ref uint modeCount, [Out] DisplayMode[] modes, IntPtr currentTopology);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public NativeMethods.RECT Bounds;
            public NativeMethods.RECT Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        // DISPLAYCONFIG_PATH_INFO: source info (20 bytes), target info (48 bytes), flags.
        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayPath
        {
            public uint SourceAdapterLow; public int SourceAdapterHigh; public uint SourceId; public uint SourceModeIndex; public uint SourceStatus;
            public uint TargetAdapterLow; public int TargetAdapterHigh; public uint TargetId; public uint TargetModeIndex; public uint TargetOutputTechnology;
            public uint TargetRotation; public uint TargetScaling; public uint TargetRefreshNumerator; public uint TargetRefreshDenominator; public uint TargetScanLineOrdering;
            public int TargetAvailable; public uint TargetStatus;
            public uint Flags;
        }

        // DISPLAYCONFIG_MODE_INFO: only its size matters here.
        [StructLayout(LayoutKind.Sequential, Size = 64)] private struct DisplayMode { public uint InfoType; }

        // DISPLAYCONFIG_SOURCE_DEVICE_NAME.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SourceDeviceName
        {
            public int Type; public int Size; public uint AdapterLow; public int AdapterHigh; public uint Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
    }
}
