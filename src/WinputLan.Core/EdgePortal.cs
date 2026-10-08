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

    // Edge switching: touching the chosen edge of one PC's primary monitor hands control to the other PC,
    // whose cursor appears at the same relative spot of its own chosen edge. Only primary monitors take part.
    // Positions along an edge travel as a fraction 0..MaxFraction, so monitors of any size line up.
    public static class EdgePortal
    {
        public const int MaxFraction = 65535;
        // The arriving cursor lands this far inside the edge, so arriving never counts as touching it.
        public const int SpawnInset = 2;
        public const int PortalPayloadBytes = 4;
        public const int ReachedPayloadBytes = 2;

        public static bool IsValid(ScreenEdge edge) { return edge >= ScreenEdge.Left && edge <= ScreenEdge.Bottom; }

        // Order used by the settings chip: left, right, top, bottom.
        public static ScreenEdge Next(ScreenEdge edge)
        {
            switch (edge)
            {
                case ScreenEdge.Left: return ScreenEdge.Right;
                case ScreenEdge.Right: return ScreenEdge.Top;
                case ScreenEdge.Top: return ScreenEdge.Bottom;
                default: return ScreenEdge.Left;
            }
        }

        // The primary monitor is the one at the virtual-screen origin.
        public static PixelRect Primary(IList<PixelRect> monitors)
        {
            if (monitors != null) foreach (var monitor in monitors) if (!monitor.IsEmpty && monitor.Contains(0, 0)) return monitor;
            return default(PixelRect);
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

        // Controller -> target, after ControlFocus(1): the target's edge and, after an edge switch, where its cursor appears.
        public static byte[] EncodePortal(ScreenEdge edge, int? placeFraction)
        {
            var fraction = placeFraction.HasValue ? Math.Max(0, Math.Min(MaxFraction, placeFraction.Value)) : 0;
            return new[] { (byte)edge, (byte)(placeFraction.HasValue ? 1 : 0), (byte)(fraction & 0xFF), (byte)(fraction >> 8) };
        }

        public static bool TryDecodePortal(byte[] payload, out ScreenEdge edge, out int? placeFraction)
        {
            edge = ScreenEdge.None; placeFraction = null;
            if (payload == null || payload.Length != PortalPayloadBytes || payload[0] > (byte)ScreenEdge.Bottom || payload[1] > 1) return false;
            edge = (ScreenEdge)payload[0];
            if (payload[1] == 1) placeFraction = payload[2] | (payload[3] << 8);
            return true;
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
