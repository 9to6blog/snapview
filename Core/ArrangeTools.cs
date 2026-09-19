using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SnapView.Core
{
    internal enum AlignMode { Left, CenterH, Right, Top, CenterV, Bottom }

    /// <summary>
    /// 여러 주석을 줄 세우는 계산 — 정렬(맞추기)과 배분(균등 간격).
    /// 기준은 <b>골라 둔 것들의 전체 테두리</b>다. 캔버스가 아니라 — 셋을 골라 왼쪽
    /// 정렬하면 셋 중 제일 왼쪽에 맞는 것이 사람이 기대하는 그림이다.
    /// </summary>
    internal static class ArrangeTools
    {
        /// <summary>
        /// 목록을 통째로 복제한다(실행취소 스냅샷용). 대상 지우개의 Target 이
        /// 옛 주석을 가리키지 않도록 <b>복제본으로 갈아 끼운다</b>.
        /// </summary>
        internal static List<Annotation> CloneAll(IReadOnlyList<Annotation> items)
        {
            var map = new Dictionary<Annotation, Annotation>(items.Count);
            var result = new List<Annotation>(items.Count);
            foreach (Annotation a in items)
            {
                Annotation c = a.Clone();
                map[a] = c;
                result.Add(c);
            }

            foreach (Annotation c in result)
            {
                if (c is EraseAnnotation { Target: not null } e)
                    e.Target = map.TryGetValue(e.Target, out Annotation? t) ? t : null;
            }
            return result;
        }

        internal static Rect Union(IReadOnlyList<Annotation> items)
        {
            Rect u = Rect.Empty;
            foreach (Annotation a in items) u.Union(a.Bounds);
            return u;
        }

        internal static void Align(IReadOnlyList<Annotation> items, AlignMode mode)
        {
            if (items.Count < 2) return;
            Rect u = Union(items);

            foreach (Annotation a in items)
            {
                Rect b = a.Bounds;
                double dx = 0, dy = 0;
                switch (mode)
                {
                    case AlignMode.Left: dx = u.Left - b.Left; break;
                    case AlignMode.CenterH: dx = (u.Left + u.Width / 2) - (b.Left + b.Width / 2); break;
                    case AlignMode.Right: dx = u.Right - b.Right; break;
                    case AlignMode.Top: dy = u.Top - b.Top; break;
                    case AlignMode.CenterV: dy = (u.Top + u.Height / 2) - (b.Top + b.Height / 2); break;
                    case AlignMode.Bottom: dy = u.Bottom - b.Bottom; break;
                }
                if (dx != 0 || dy != 0) a.Move(new Vector(dx, dy));
            }
        }

        /// <summary>가운데점 사이 간격을 고르게. 양 끝 것은 그대로 두고 사이만 편다.</summary>
        internal static void Distribute(IReadOnlyList<Annotation> items, bool horizontal)
        {
            if (items.Count < 3) return;

            double Center(Annotation a)
            {
                Rect b = a.Bounds;
                return horizontal ? b.Left + b.Width / 2 : b.Top + b.Height / 2;
            }

            List<Annotation> sorted = items.OrderBy(Center).ToList();
            double first = Center(sorted[0]), last = Center(sorted[^1]);
            double step = (last - first) / (sorted.Count - 1);

            for (int i = 1; i < sorted.Count - 1; i++)
            {
                double want = first + step * i;
                double d = want - Center(sorted[i]);
                if (Math.Abs(d) < 0.001) continue;
                sorted[i].Move(horizontal ? new Vector(d, 0) : new Vector(0, d));
            }
        }

        /// <summary>
        /// 무리 전체를 <paramref name="from"/> 에서 <paramref name="to"/> 로 늘리거나 줄인다.
        /// 서로의 자리·비율은 그대로고 굵기는 안 건드린다(살짝 늘렸다고 선이 굵어지면 이상하다).
        /// </summary>
        internal static void ScaleGroup(IReadOnlyList<Annotation> items, Rect from, Rect to)
        {
            if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0) return;
            double fx = to.Width / from.Width, fy = to.Height / from.Height;
            var shift = new Vector(to.X - from.X * fx, to.Y - from.Y * fy);

            foreach (Annotation a in items)
            {
                if (a.Locked) continue;
                double thickness = a.Thickness;
                a.Scale(fx, fy);
                a.Thickness = thickness;
                a.Move(shift);
            }
        }

    }

    /// <summary>
    /// 끌던 것이 다른 주석·캔버스의 기준선에 가까워지면 착 붙여 주는 계산(스마트 가이드).
    /// 후보 선은 캔버스의 가장자리·가운데와 다른 주석들의 가장자리·가운데.
    /// 끌리는 쪽도 왼끝·가운데·오른끝 셋 다 대 본다.
    /// </summary>
    internal static class SnapGuide
    {
        internal readonly struct Result
        {
            internal Result(Vector adjust, double? guideX, double? guideY)
            { Adjust = adjust; GuideX = guideX; GuideY = guideY; }

            internal Vector Adjust { get; }
            internal double? GuideX { get; }
            internal double? GuideY { get; }
        }

        internal static Result Solve(Rect moving, IEnumerable<Rect> others,
                                     double canvasW, double canvasH, double tolerance)
        {
            var xs = new List<double> { 0, canvasW / 2, canvasW };
            var ys = new List<double> { 0, canvasH / 2, canvasH };
            foreach (Rect o in others)
            {
                if (o.IsEmpty) continue;
                xs.Add(o.Left); xs.Add(o.Left + o.Width / 2); xs.Add(o.Right);
                ys.Add(o.Top); ys.Add(o.Top + o.Height / 2); ys.Add(o.Bottom);
            }

            (double adjust, double? line) BestFor(double[] mine, List<double> lines)
            {
                double bestDist = tolerance, bestAdjust = 0;
                double? bestLine = null;
                foreach (double line in lines)
                foreach (double m in mine)
                {
                    double d = Math.Abs(line - m);
                    if (d < bestDist) { bestDist = d; bestAdjust = line - m; bestLine = line; }
                }
                return (bestAdjust, bestLine);
            }

            (double ax, double? gx) = BestFor(
                new[] { moving.Left, moving.Left + moving.Width / 2, moving.Right }, xs);
            (double ay, double? gy) = BestFor(
                new[] { moving.Top, moving.Top + moving.Height / 2, moving.Bottom }, ys);

            return new Result(new Vector(ax, ay), gx, gy);
        }
    }
}
