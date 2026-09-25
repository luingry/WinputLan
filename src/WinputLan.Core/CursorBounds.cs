using System;
using System.Collections.Generic;

namespace WinputLan.Core
{
    // Right and Bottom are exclusive, as in a Win32 RECT.
    public struct PixelRect
    {
        public PixelRect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public bool IsEmpty { get { return Right <= Left || Bottom <= Top; } }
        public bool Contains(int x, int y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
    }

    // Windows keeps the real cursor inside the ClipCursor rectangle and on a monitor. A tracked cursor that
    // ignores those limits drifts past them, and later motion back is spent unwinding the phantom offset.
    public static class CursorBounds
    {
        public static void Clamp(ref int x, ref int y, PixelRect clip, IList<PixelRect> monitors)
        {
            if (!clip.IsEmpty) ClampInto(ref x, ref y, clip);
            if (monitors == null || monitors.Count == 0) return;
            foreach (var monitor in monitors) if (monitor.Contains(x, y)) return;
            // Mixed monitor sizes leave gaps in the virtual screen that the cursor cannot enter.
            var bestX = x; var bestY = y; var bestDistance = long.MaxValue;
            foreach (var monitor in monitors)
            {
                if (monitor.IsEmpty) continue;
                int cx = x, cy = y;
                ClampInto(ref cx, ref cy, monitor);
                var distance = (long)(cx - x) * (cx - x) + (long)(cy - y) * (cy - y);
                if (distance < bestDistance) { bestDistance = distance; bestX = cx; bestY = cy; }
            }
            x = bestX; y = bestY;
            if (!clip.IsEmpty) ClampInto(ref x, ref y, clip);
        }

        private static void ClampInto(ref int x, ref int y, PixelRect rect)
        {
            x = Math.Max(rect.Left, Math.Min(rect.Right - 1, x));
            y = Math.Max(rect.Top, Math.Min(rect.Bottom - 1, y));
        }
    }
}
