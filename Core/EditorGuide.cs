using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SnapView.Core
{
    internal sealed class EditorGuide
    {
        internal bool Horizontal { get; set; }
        internal double Position { get; set; }
        internal bool Diagonal { get; set; }
        internal Point Start { get; set; }
        internal Point End { get; set; }
        internal EditorGuide Clone() => (EditorGuide)MemberwiseClone();
        internal double Length => (End - Start).Length;
        internal double Angle => Math.Atan2(End.Y - Start.Y, End.X - Start.X) * 180 / Math.PI;

        internal double DistanceTo(Point p)
        {
            if (!Diagonal) return Math.Abs((Horizontal ? p.Y : p.X) - Position);
            Vector v = End - Start;
            return v.Length < 0.001 ? (p - Start).Length : Math.Abs(Vector.CrossProduct(p - Start, v)) / v.Length;
        }

        internal (Point Start, Point End) Line(double width, double height)
        {
            if (!Diagonal) return Horizontal ? (new Point(0, Position), new Point(width, Position))
                : (new Point(Position, 0), new Point(Position, height));
            Vector v = End - Start;
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            bool Clip(double origin, double direction, double limit)
            {
                if (Math.Abs(direction) < 0.001) return origin >= 0 && origin <= limit;
                double a = -origin / direction, b = (limit - origin) / direction;
                lo = Math.Max(lo, Math.Min(a, b)); hi = Math.Min(hi, Math.Max(a, b));
                return lo <= hi;
            }
            if (v.Length < 0.001 || !Clip(Start.X, v.X, width) || !Clip(Start.Y, v.Y, height)) return (Start, Start);
            return (Start + lo * v, Start + hi * v);
        }
    }

    internal static class GuideMeasurements
    {
        internal static double[] Stops(IEnumerable<EditorGuide> guides, bool horizontal, double extent)
            => guides.Where(g => !g.Diagonal && g.Horizontal == horizontal && double.IsFinite(g.Position) && g.Position > 0 && g.Position < extent)
                .Select(g => g.Position).Append(0).Append(extent).Distinct().OrderBy(v => v).ToArray();

        internal static IEnumerable<Rect> Cells(IEnumerable<EditorGuide> guides, double width, double height)
        {
            double[] xs = Stops(guides, false, width), ys = Stops(guides, true, height);
            for (int y = 0; y + 1 < ys.Length; y++)
                for (int x = 0; x + 1 < xs.Length; x++)
                    yield return new Rect(xs[x], ys[y], xs[x + 1] - xs[x], ys[y + 1] - ys[y]);
        }
    }
}
