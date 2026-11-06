using System;
using System.Collections.Generic;

namespace WinputLan.Core
{
    public enum ScreenEdge : byte
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 3,
        Bottom = 4
    }

    // Edge switching: touching the chosen edge of a monitor of one PC hands control to the other PC, whose cursor
    // appears at the same relative spot of its own chosen edge. Each PC takes part with one monitor, the primary
    // unless the settings chose another. Positions along an edge travel as a fraction 0..MaxFraction, so monitors
    // of any size line up.
    public static class EdgePortal
    {
        public const int MaxFraction = 65535;
        // The arriving cursor lands this far inside the edge, so arriving never counts as touching it.
        public const int SpawnInset = 2;
        public const int PortalPayloadBytes = 4;
        public const int PortalWithMonitorPayloadBytes = 20;
        public const int ReachedPayloadBytes = 2;
        public const int MaxLayoutMonitors = 16;
        private const int LayoutMonitorBytes = 17;
        private const int MaxLayoutCoordinate = 1 << 20;
        private const byte LayoutQueryMarker = 0xFF;

        public static bool IsValid(ScreenEdge edge) { return edge >= ScreenEdge.Left && edge <= ScreenEdge.Bottom; }

        // The primary monitor is the one at the virtual-screen origin.
        public static PixelRect Primary(IList<PixelRect> monitors)
        {
            if (monitors != null) foreach (var monitor in monitors) if (!monitor.IsEmpty && monitor.Contains(0, 0)) return monitor;
            return default(PixelRect);
        }

        // The chosen monitor while it still exists with those bounds; otherwise, or with none chosen, the primary.
        public static PixelRect Resolve(IList<PixelRect> monitors, PixelRect chosen)
        {
            if (!chosen.IsEmpty && monitors != null) foreach (var monitor in monitors) if (monitor.Left == chosen.Left && monitor.Top == chosen.Top && monitor.Right == chosen.Right && monitor.Bottom == chosen.Bottom) return monitor;
            return Primary(monitors);
        }

        // True when (x, y) is on the edge line or beyond it within the edge's span. Beyond covers motion that
        // crosses into a monitor next to that edge; callers also require the previous position on the primary.
        public static bool Touches(ScreenEdge edge, PixelRect primary, int x, int y)
        {
            if (primary.IsEmpty) return false;
            switch (edge)
            {
                case ScreenEdge.Left: return x <= primary.Left && y >= primary.Top && y < primary.Bottom;
                case ScreenEdge.Right: return x >= primary.Right - 1 && y >= primary.Top && y < primary.Bottom;
                case ScreenEdge.Top: return y <= primary.Top && x >= primary.Left && x < primary.Right;
                case ScreenEdge.Bottom: return y >= primary.Bottom - 1 && x >= primary.Left && x < primary.Right;
                default: return false;
            }
        }

        // A clip smaller than the virtual screen (games, window move loops) confines the cursor on purpose.
        public static bool IsConfined(PixelRect clip, PixelRect virtualScreen)
        {
            if (clip.IsEmpty || virtualScreen.IsEmpty) return false;
            return clip.Left > virtualScreen.Left || clip.Top > virtualScreen.Top || clip.Right < virtualScreen.Right || clip.Bottom < virtualScreen.Bottom;
        }

        public static int FractionAt(ScreenEdge edge, PixelRect primary, int x, int y)
        {
            if (primary.IsEmpty) return 0;
            var vertical = edge == ScreenEdge.Left || edge == ScreenEdge.Right;
            var start = vertical ? primary.Top : primary.Left;
            var length = vertical ? primary.Bottom - primary.Top : primary.Right - primary.Left;
            if (length <= 1) return 0;
            var offset = Math.Max(0, Math.Min(length - 1, (vertical ? y : x) - start));
            return (int)((offset * (long)MaxFraction + (length - 1) / 2) / (length - 1));
        }

        // The point on the primary monitor at this fraction of the edge, inset pixels inside it.
        public static void PointAt(ScreenEdge edge, PixelRect primary, int fraction, int inset, out int x, out int y)
        {
            x = primary.Left; y = primary.Top;
            if (primary.IsEmpty) return;
            fraction = Math.Max(0, Math.Min(MaxFraction, fraction));
            var width = primary.Right - primary.Left;
            var height = primary.Bottom - primary.Top;
            var vertical = edge == ScreenEdge.Left || edge == ScreenEdge.Right;
            var length = vertical ? height : width;
            var along = (int)((fraction * (long)(length - 1) + MaxFraction / 2) / MaxFraction);
            if (vertical) y = primary.Top + along; else x = primary.Left + along;
            switch (edge)
            {
                case ScreenEdge.Left: x = primary.Left + Math.Min(inset, width - 1); break;
                case ScreenEdge.Right: x = primary.Right - 1 - Math.Min(inset, width - 1); break;
                case ScreenEdge.Top: y = primary.Top + Math.Min(inset, height - 1); break;
                case ScreenEdge.Bottom: y = primary.Bottom - 1 - Math.Min(inset, height - 1); break;
            }
        }

        public static byte[] EncodePortal(ScreenEdge edge, int? placeFraction) { return EncodePortal(edge, placeFraction, default(PixelRect)); }

        // Controller -> target, after ControlFocus(1): the target's edge and, after an edge switch, where its cursor
        // appears. A monitor other than the primary adds its bounds; peers older than 0.7.0 ignore that longer payload,
        // and only a peer that answered a layout query can have such a monitor chosen.
        public static byte[] EncodePortal(ScreenEdge edge, int? placeFraction, PixelRect monitor)
        {
            var fraction = placeFraction.HasValue ? Math.Max(0, Math.Min(MaxFraction, placeFraction.Value)) : 0;
            var withMonitor = IsValid(edge) && !monitor.IsEmpty;
            var payload = new byte[withMonitor ? PortalWithMonitorPayloadBytes : PortalPayloadBytes];
            payload[0] = (byte)edge;
            payload[1] = (byte)(placeFraction.HasValue ? 1 : 0);
            payload[2] = (byte)(fraction & 0xFF);
            payload[3] = (byte)(fraction >> 8);
            if (withMonitor) WriteRect(payload, 4, monitor);
            return payload;
        }

        public static bool TryDecodePortal(byte[] payload, out ScreenEdge edge, out int? placeFraction)
        {
            PixelRect monitor;
            return TryDecodePortal(payload, out edge, out placeFraction, out monitor);
        }

        public static bool TryDecodePortal(byte[] payload, out ScreenEdge edge, out int? placeFraction, out PixelRect monitor)
        {
            edge = ScreenEdge.None; placeFraction = null; monitor = default(PixelRect);
            if (payload == null || (payload.Length != PortalPayloadBytes && payload.Length != PortalWithMonitorPayloadBytes) || payload[0] > (byte)ScreenEdge.Bottom || payload[1] > 1) return false;
            if (payload.Length == PortalWithMonitorPayloadBytes)
            {
                monitor = ReadRect(payload, 4);
                if (!IsValid((ScreenEdge)payload[0]) || !IsSaneRect(monitor)) { monitor = default(PixelRect); return false; }
            }
            edge = (ScreenEdge)payload[0];
            if (payload[1] == 1) placeFraction = payload[2] | (payload[3] << 8);
            return true;
        }

        // Controller -> target, as an EdgePortal frame: asks for the target's monitors. Peers older than 0.7.0 ignore a
        // payload of this length, so they never answer with a MonitorLayout frame, whose type would close their session.
        public static byte[] EncodeLayoutQuery() { return new[] { LayoutQueryMarker }; }

        public static bool IsLayoutQuery(byte[] payload) { return payload != null && payload.Length == 1 && payload[0] == LayoutQueryMarker; }

        // Target -> controller, MonitorLayout frame: a count, then bounds and flags of each monitor.
        public static byte[] EncodeLayout(IList<EdgeMonitor> monitors)
        {
            var count = Math.Min(monitors == null ? 0 : monitors.Count, MaxLayoutMonitors);
            var payload = new byte[1 + count * LayoutMonitorBytes];
            payload[0] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var offset = 1 + i * LayoutMonitorBytes;
                WriteRect(payload, offset, monitors[i].Bounds);
                payload[offset + 16] = (byte)((monitors[i].Primary ? 1 : 0) | (monitors[i].Internal ? 2 : 0));
            }
            return payload;
        }

        public static bool TryDecodeLayout(byte[] payload, out List<EdgeMonitor> monitors)
        {
            monitors = null;
            if (payload == null || payload.Length < 1 || payload[0] == 0 || payload[0] > MaxLayoutMonitors || payload.Length != 1 + payload[0] * LayoutMonitorBytes) return false;
            var decoded = new List<EdgeMonitor>(payload[0]);
            for (var i = 0; i < payload[0]; i++)
            {
                var offset = 1 + i * LayoutMonitorBytes;
                var rect = ReadRect(payload, offset);
                if (!IsSaneRect(rect) || payload[offset + 16] > 3) return false;
                decoded.Add(new EdgeMonitor { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom, Primary = (payload[offset + 16] & 1) != 0, Internal = (payload[offset + 16] & 2) != 0 });
            }
            monitors = decoded;
            return true;
        }

        private static bool IsSaneRect(PixelRect rect)
        {
            return !rect.IsEmpty && Math.Abs(rect.Left) <= MaxLayoutCoordinate && Math.Abs(rect.Top) <= MaxLayoutCoordinate && Math.Abs(rect.Right) <= MaxLayoutCoordinate && Math.Abs(rect.Bottom) <= MaxLayoutCoordinate;
        }

        private static void WriteRect(byte[] target, int offset, PixelRect rect)
        {
            Array.Copy(BitConverter.GetBytes(rect.Left), 0, target, offset, 4);
            Array.Copy(BitConverter.GetBytes(rect.Top), 0, target, offset + 4, 4);
            Array.Copy(BitConverter.GetBytes(rect.Right), 0, target, offset + 8, 4);
            Array.Copy(BitConverter.GetBytes(rect.Bottom), 0, target, offset + 12, 4);
        }

        private static PixelRect ReadRect(byte[] source, int offset)
        {
            return new PixelRect(BitConverter.ToInt32(source, offset), BitConverter.ToInt32(source, offset + 4), BitConverter.ToInt32(source, offset + 8), BitConverter.ToInt32(source, offset + 12));
        }

        // Target -> controller: its cursor touched its edge at this fraction.
        public static byte[] EncodeReached(int fraction)
        {
            fraction = Math.Max(0, Math.Min(MaxFraction, fraction));
            return new[] { (byte)(fraction & 0xFF), (byte)(fraction >> 8) };
        }

        public static bool TryDecodeReached(byte[] payload, out int fraction)
        {
            fraction = 0;
            if (payload == null || payload.Length != ReachedPayloadBytes) return false;
            fraction = payload[0] | (payload[1] << 8);
            return true;
        }
    }
}
