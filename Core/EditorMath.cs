using System;
using System.Windows;

namespace SnapView.Core
{
    /// <summary>편집기 배치·그리기 수학. 창 없이 검사할 수 있게 떼어 둔 것.</summary>
    internal static class EditorMath
    {
        /// <summary>
        /// 배율을 바꿔도 커서 아래 그림 점이 그 자리에 있으려면 스크롤을 얼마로 해야 하는가.
        /// <paramref name="cursor"/> 는 뷰포트 기준, <paramref name="offset"/> 은 지금 스크롤.
        /// </summary>
        internal static Vector AnchoredOffset(double oldZoom, double newZoom, Point cursor, Vector offset)
        {
            if (oldZoom <= 0 || newZoom <= 0) return offset;
            double ix = (offset.X + cursor.X) / oldZoom;
            double iy = (offset.Y + cursor.Y) / oldZoom;
            return new Vector(ix * newZoom - cursor.X, iy * newZoom - cursor.Y);
        }

        /// <summary>끌어 그린 사각형을 <paramref name="aspect"/>(가로/세로)에 맞춘다. 긴 쪽을 살린다.</summary>
        internal static Point FitAspect(Point start, Point p, double aspect)
        {
            if (aspect <= 0) return p;
            double dx = p.X - start.X, dy = p.Y - start.Y;
            double w = Math.Abs(dx), h = Math.Abs(dy);
            if (w / aspect >= h) h = w / aspect; else w = h * aspect;
            return new Point(start.X + Math.Sign(dx == 0 ? 1 : dx) * w,
                             start.Y + Math.Sign(dy == 0 ? 1 : dy) * h);
        }

        /// <summary>Alt 를 누르고 그리면 누른 자리가 가운데가 된다.</summary>
        internal static (Point Start, Point End) FromCenter(Point center, Point p)
            => (new Point(2 * center.X - p.X, 2 * center.Y - p.Y), p);

        /// <summary>사각형의 8 조절점(NW N NE E SE S SW W). 여럿을 골랐을 때 무리 전체에 쓴다.</summary>
        internal static Point[] BoxHandles(Rect b)
        {
            double l = b.Left, t = b.Top, r = b.Right, bo = b.Bottom;
            double cx = (l + r) / 2, cy = (t + bo) / 2;
            return new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy),
                new Point(r, bo), new Point(cx, bo), new Point(l, bo), new Point(l, cy)
            };
        }

        /// <summary><paramref name="p"/> 가 어느 조절점 위인지. 없으면 -1.</summary>
        internal static int BoxHandleAt(Rect b, Point p, double tolerance)
        {
            if (b.IsEmpty) return -1;
            Point[] hs = BoxHandles(b);
            for (int i = 0; i < hs.Length; i++)
                if (Math.Abs(p.X - hs[i].X) <= tolerance && Math.Abs(p.Y - hs[i].Y) <= tolerance) return i;
            return -1;
        }

        /// <summary>조절점 <paramref name="index"/> 를 <paramref name="p"/> 로 끌었을 때의 새 사각형.</summary>
        internal static Rect ResizeBox(Rect b, int index, Point p)
        {
            double l = b.Left, t = b.Top, r = b.Right, bo = b.Bottom;
            if (index is 0 or 6 or 7) l = p.X;
            if (index is 2 or 3 or 4) r = p.X;
            if (index is 0 or 1 or 2) t = p.Y;
            if (index is 4 or 5 or 6) bo = p.Y;
            return new Rect(new Point(Math.Min(l, r), Math.Min(t, bo)), new Point(Math.Max(l, r), Math.Max(t, bo)));
        }

        /// <summary>창에 맞추는 배율. 작은 그림은 키우고 큰 그림은 줄인다(예전엔 100% 를 못 넘겼다).</summary>
        internal static double FitZoom(double imageW, double imageH, double availW, double availH, double maxZoom)
        {
            if (imageW <= 0 || imageH <= 0) return 1;
            double z = Math.Min(availW / imageW, availH / imageH);
            return Math.Clamp(z, 0.05, maxZoom);
        }
    }
}
