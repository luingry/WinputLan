using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace WinputLan.Core
{
    // One monitor of a PC, in that PC's physical virtual-screen pixels.
    [DataContract]
    public sealed class EdgeMonitor
    {
        [DataMember(Order = 1)] public int Left { get; set; }
        [DataMember(Order = 2)] public int Top { get; set; }
        [DataMember(Order = 3)] public int Right { get; set; }
        [DataMember(Order = 4)] public int Bottom { get; set; }
        [DataMember(Order = 5)] public bool Primary { get; set; }
        // A panel built into the machine, such as a notebook's own screen.
        [DataMember(Order = 6)] public bool Internal { get; set; }

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public PixelRect Bounds { get { return new PixelRect(Left, Top, Right, Bottom); } }
    }

    public sealed class EdgeSnap
    {
        public int X;
        public int Y;
        // The alignment line the placement landed on, for the drag guide.
        public bool GuideVertical;
        public int Guide;
    }

    public sealed class EdgeContact
    {
        public EdgeMonitor First;
        public EdgeMonitor Second;
        public ScreenEdge FirstEdge;
        public ScreenEdge SecondEdge;
        public bool Vertical;
        public int Line;
        // The stretch both monitors share, then each monitor's whole edge, along the line.
        public int Start, End, FirstStart, FirstEnd, SecondStart, SecondEnd;
    }

    // The settings window arranges two groups of monitors, each group keeping the layout Windows gives it.
    public static class EdgeLayout
    {
        // Nearest placement of the moving group that touches the other without overlapping, aligned to the start,
        // center or end of the touched monitor. Null only when a group has no monitors.
        public static EdgeSnap Find(IList<EdgeMonitor> moving, int movingX, int movingY, IList<EdgeMonitor> other, int otherX, int otherY)
        {
            EdgeSnap best = null;
            var bestDistance = long.MaxValue;
            foreach (var d in moving) foreach (var o in other)
            {
                int oLeft = o.Left + otherX, oTop = o.Top + otherY, oRight = o.Right + otherX, oBottom = o.Bottom + otherY;
                for (var side = 0; side < 4; side++) for (var along = 0; along < 3; along++)
                {
                    int x, y, guide;
                    var horizontalNeighbors = side < 2;
                    if (horizontalNeighbors)
                    {
                        x = (side == 0 ? oRight : oLeft - d.Width) - d.Left;
                        y = (along == 0 ? oTop : along == 1 ? oTop + (o.Height - d.Height) / 2 : oBottom - d.Height) - d.Top;
                        guide = along == 0 ? oTop : along == 1 ? oTop + o.Height / 2 : oBottom;
                    }
                    else
                    {
                        y = (side == 2 ? oBottom : oTop - d.Height) - d.Top;
                        x = (along == 0 ? oLeft : along == 1 ? oLeft + (o.Width - d.Width) / 2 : oRight - d.Width) - d.Left;
                        guide = along == 0 ? oLeft : along == 1 ? oLeft + o.Width / 2 : oRight;
                    }
                    if (Overlaps(moving, x, y, other, otherX, otherY)) continue;
                    var distance = (long)(x - movingX) * (x - movingX) + (long)(y - movingY) * (y - movingY);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = new EdgeSnap { X = x, Y = y, GuideVertical = !horizontalNeighbors, Guide = guide };
                }
            }
            return best;
        }

        public static List<EdgeContact> Contacts(IList<EdgeMonitor> first, int firstX, int firstY, IList<EdgeMonitor> second, int secondX, int secondY)
        {
            var contacts = new List<EdgeContact>();
            foreach (var a in first) foreach (var b in second)
            {
                int aLeft = a.Left + firstX, aTop = a.Top + firstY, aRight = a.Right + firstX, aBottom = a.Bottom + firstY;
                int bLeft = b.Left + secondX, bTop = b.Top + secondY, bRight = b.Right + secondX, bBottom = b.Bottom + secondY;
                int top = Math.Max(aTop, bTop), bottom = Math.Min(aBottom, bBottom), left = Math.Max(aLeft, bLeft), right = Math.Min(aRight, bRight);
                if (bottom > top && aRight == bLeft) contacts.Add(Vertical(a, b, ScreenEdge.Right, ScreenEdge.Left, aRight, top, bottom, aTop, aBottom, bTop, bBottom));
                if (bottom > top && aLeft == bRight) contacts.Add(Vertical(a, b, ScreenEdge.Left, ScreenEdge.Right, aLeft, top, bottom, aTop, aBottom, bTop, bBottom));
                if (right > left && aBottom == bTop) contacts.Add(Horizontal(a, b, ScreenEdge.Bottom, ScreenEdge.Top, aBottom, left, right, aLeft, aRight, bLeft, bRight));
                if (right > left && aTop == bBottom) contacts.Add(Horizontal(a, b, ScreenEdge.Top, ScreenEdge.Bottom, aTop, left, right, aLeft, aRight, bLeft, bRight));
            }
            return contacts;
        }

        // Edge switching uses one pair of edges: the longest shared stretch, preferring primary monitors on a tie.
        public static EdgeContact Best(IEnumerable<EdgeContact> contacts)
        {
            return contacts.OrderByDescending(c => c.End - c.Start).ThenByDescending(c => (c.First.Primary ? 1 : 0) + (c.Second.Primary ? 1 : 0)).FirstOrDefault();
        }

        private static bool Overlaps(IList<EdgeMonitor> first, int firstX, int firstY, IList<EdgeMonitor> second, int secondX, int secondY)
        {
            foreach (var a in first) foreach (var b in second)
                if (a.Left + firstX < b.Right + secondX && a.Right + firstX > b.Left + secondX && a.Top + firstY < b.Bottom + secondY && a.Bottom + firstY > b.Top + secondY) return true;
            return false;
        }

        private static EdgeContact Vertical(EdgeMonitor a, EdgeMonitor b, ScreenEdge aEdge, ScreenEdge bEdge, int line, int start, int end, int aStart, int aEnd, int bStart, int bEnd)
        {
            return new EdgeContact { First = a, Second = b, FirstEdge = aEdge, SecondEdge = bEdge, Vertical = true, Line = line, Start = start, End = end, FirstStart = aStart, FirstEnd = aEnd, SecondStart = bStart, SecondEnd = bEnd };
        }

        private static EdgeContact Horizontal(EdgeMonitor a, EdgeMonitor b, ScreenEdge aEdge, ScreenEdge bEdge, int line, int start, int end, int aStart, int aEnd, int bStart, int bEnd)
        {
            return new EdgeContact { First = a, Second = b, FirstEdge = aEdge, SecondEdge = bEdge, Vertical = false, Line = line, Start = start, End = end, FirstStart = aStart, FirstEnd = aEnd, SecondStart = bStart, SecondEnd = bEnd };
        }
    }
}
