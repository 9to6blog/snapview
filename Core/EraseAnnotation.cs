using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>
    /// 지우개가 지나간 자리.
    ///
    /// 주석을 <b>통째로 지우지 않는다</b>. 선 하나를 지우려고 지우개를 대면 선 전체가
    /// 사라지는 건 지우개가 아니라 삭제다. 그럴 거면 레이어에서 지우는 편이 빠르다.
    ///
    /// 그래서 이 주석은 아무것도 그리지 않고 <b>구멍의 모양만</b> 들고 있는다.
    /// 그리는 쪽(<see cref="AnnotationRenderer"/>)이 이 구멍을 오려 낸 채로
    /// 아래에 있던 주석들을 그린다. 도형이든 글자든 번호든 닿은 부분만 없어진다.
    ///
    /// 자기보다 <b>먼저</b> 그려진 것에만 영향을 준다. 지우고 나서 새로 그린 것은
    /// 멀쩡히 남는다 — 그림판과 같은 순서 감각이다.
    /// </summary>
    internal sealed class EraseAnnotation : Annotation
    {
        private Geometry? _cache;
        private int _cachePoints = -1;
        private double _cacheRadius = -1;

        internal List<Point> Points { get; } = new();

        /// <summary>지우개 반지름(원본 픽셀).</summary>
        internal double Radius { get; set; } = 10;

        /// <summary>
        /// 이 주석<b>만</b> 판다(마스크처럼). null 이면 예전처럼 아래 전부를 판다.
        /// 주석 하나를 골라 두고 지우개를 쓰면 이게 잡힌다.
        /// </summary>
        internal Annotation? Target { get; set; }

        internal override bool SupportsOpacity => false;

        internal void Add(Point p)
        {
            Points.Add(p);
            _cache = null;
        }

        /// <summary>파낼 자리의 모양. 획을 따라 굵은 선을 그은 것과 같다.</summary>
        internal Geometry Area
        {
            get
            {
                if (_cache != null && _cachePoints == Points.Count && _cacheRadius == Radius)
                    return _cache;

                var group = new GeometryGroup { FillRule = FillRule.Nonzero };

                // 점마다 동그라미. 끝이 뭉툭하지 않게 하고, 점 하나짜리도 지워지게 한다.
                foreach (Point p in Points)
                    group.Children.Add(new EllipseGeometry(p, Radius, Radius));

                // 점 사이는 굵은 선으로 이어 준다. 마우스가 빨리 움직이면 점이 띄엄띄엄 온다.
                var pen = new Pen(Brushes.Black, Radius * 2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();

                for (int i = 1; i < Points.Count; i++)
                {
                    var seg = new LineGeometry(Points[i - 1], Points[i]);
                    group.Children.Add(seg.GetWidenedPathGeometry(pen));
                }

                group.Freeze();
                _cache = group;
                _cachePoints = Points.Count;
                _cacheRadius = Radius;
                return group;
            }
        }

        internal override Rect Bounds
        {
            get
            {
                if (Points.Count == 0) return Rect.Empty;

                Rect b = new(Points[0], Points[0]);
                foreach (Point p in Points) b.Union(p);
                b.Inflate(Radius, Radius);
                return b;
            }
        }

        internal override void Move(Vector d)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] += d;
            _cache = null;
        }

        internal override void Flip(bool horizontal, double w, double h)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = MapFlip(Points[i], horizontal, w, h);
            _cache = null;
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
            _cache = null;
        }

        internal override void Scale(double fx, double fy)
        {
            base.Scale(fx, fy);
            Radius = Math.Max(1, Radius * (fx + fy) / 2);
            _cache = null;
        }

        protected override Annotation CloneCore()
        {
            // Target 은 옛 주석을 가리킨 채 복제된다. 목록을 통째로 복제할 때는
            // ArrangeTools.CloneAll 이 새 주석으로 갈아 끼운다.
            var c = new EraseAnnotation { Radius = Radius, Target = Target };
            c.Points.AddRange(Points);
            return c;
        }

        /// <summary>
        /// 이 획이 그 주석을 실제로 스치는가. 아무것도 안 스친 획은 남길 이유가 없다 —
        /// 빈 데를 문질렀는데 레이어만 늘어나면 지우개가 아니라 쓰레기 생산이다.
        /// </summary>
        internal bool Touches(Annotation a)
        {
            if (a is EraseAnnotation || Points.Count == 0) return false;

            Rect b = a.Bounds;
            if (b.IsEmpty) return false;
            b.Inflate(Radius + Math.Max(2, a.Thickness), Radius + Math.Max(2, a.Thickness));

            foreach (Point p in Points)
                if (b.Contains(p)) return true;
            return false;
        }

        /// <summary>지우개 자국 자체는 보이지 않는다. 구멍만 남긴다.</summary>
        internal override void Render(DrawingContext dc, BitmapSource source) { }

        /// <summary>선택 도구로는 못 집는다. 눈에 안 보이는 걸 집으면 헷갈린다.</summary>
        internal override bool HitTest(Point p) => false;
    }
}
