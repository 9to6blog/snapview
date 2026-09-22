using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

namespace SnapView.Editor
{
    /// <summary>
    /// 이미지와 주석을 한 번에 그리는 화면. 내부 좌표는 항상 원본 이미지 픽셀이고,
    /// 보여 줄 때만 <see cref="Scale"/> 을 곱한다. 그래서 아무리 축소해서 편집해도
    /// 결과물은 원본 해상도 그대로 나온다.
    /// </summary>
    public sealed class AnnotationCanvas : FrameworkElement
    {
        private static readonly Color AccentColor = Color.FromRgb(0x4D, 0xA3, 0xFF);

        internal BitmapSource? Source { get; set; }
        internal List<Annotation> Items { get; } = new();

        /// <summary>
        /// 지금 그리는 중이거나, 그려 놓고 아직 Enter 로 확정하지 않은 주석.
        /// 확정 전에는 조절점이 붙어 끌어서 고칠 수 있다.
        /// </summary>
        internal Annotation? Active { get; set; }

        /// <summary>Active 에 조절점을 그릴지. 마우스로 그리는 도중에는 끈다.</summary>
        internal bool ShowActiveHandles { get; set; }

        /// <summary>선택 도구로 고른 기존 주석(대표). 여럿을 골랐으면 그중 하나.</summary>
        internal Annotation? Selected { get; set; }

        /// <summary>
        /// 선택 도구로 고른 전부. Shift+클릭으로 더하고, 빈 곳을 끌어 한꺼번에 담는다.
        /// Selected 가 있으면 항상 이 목록에도 들어 있다(편집기 쪽이 지켜 준다).
        /// </summary>
        internal List<Annotation> SelectedMany { get; } = new();

        /// <summary>마우스가 위에 있는(집을 수 있는) 주석. 살짝 테두리만 띄운다.</summary>
        internal Annotation? Hover { get; set; }

        /// <summary>선택 도구의 올가미 네모(이미지 픽셀). 비어 있으면 안 그린다.</summary>
        internal Rect RubberBand { get; set; } = Rect.Empty;

        /// <summary>끌기 스냅이 잡은 기준선(스마트 가이드). null 이면 안 그린다.</summary>
        internal double? GuideX { get; set; }
        internal double? GuideY { get; set; }
        internal List<EditorGuide> ManualGuides { get; } = new();
        internal bool ShowManualGuides { get; set; }
        internal bool ShowGuideMeasurements { get; set; } = true;

        /// <summary>자동 선택이 고른 영역의 표시(파란 물들임). null 이면 없음.</summary>
        internal BitmapSource? SelectionTint { get; set; }

        /// <summary>영역 선택으로 잡아 놓은 네모(이미지 픽셀). 비어 있으면 안 그린다.</summary>
        internal Rect Region { get; set; } = Rect.Empty;

        private double _scale = 1;
        internal double Scale
        {
            get => _scale;
            set
            {
                if (Math.Abs(_scale - value) < 1e-9) return;
                _scale = Math.Max(0.01, value);
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        internal double ImageWidth => Source?.PixelWidth ?? 0;
        internal double ImageHeight => Source?.PixelHeight ?? 0;

        /// <summary>화면 좌표 → 이미지 픽셀 좌표.</summary>
        internal Point ToImage(Point p) => new(p.X / _scale, p.Y / _scale);

        /// <summary>화면에서 잰 길이를 이미지 픽셀 길이로. 조절점 판정 반경에 쓴다.</summary>
        internal double ToImageLength(double screenLength) => screenLength / _scale;

        protected override Size MeasureOverride(Size availableSize)
            => new(ImageWidth * _scale, ImageHeight * _scale);

        protected override void OnRender(DrawingContext dc)
        {
            if (Source == null) return;

            dc.PushTransform(new ScaleTransform(_scale, _scale));
            try
            {
                dc.DrawImage(Source, new Rect(0, 0, ImageWidth, ImageHeight));

                // 최종 결과와 같은 경로로 그린다. 여기서 갈라지면 투명도 같은 것이
                // 화면에는 안 보이고 저장할 때만 먹는 상황이 생긴다.
                var scene = new List<Annotation>(Items);
                if (Active != null) scene.Add(Active);
                AnnotationRenderer.Draw(dc, scene, Source);

                if (Region.Width >= 1 && Region.Height >= 1) DrawRegion(dc);

                if (SelectionTint != null)
                    dc.DrawImage(SelectionTint, new Rect(0, 0, ImageWidth, ImageHeight));

                if (Hover != null && !SelectedMany.Contains(Hover) && !ReferenceEquals(Hover, Active))
                    DrawOutline(dc, Hover, faint: true);

                foreach (Annotation s in SelectedMany) DrawOutline(dc, s);
                if (SelectedMany.Count > 1)
                {
                    // 무리 전체 테두리와 조절점 — 모서리를 끌면 함께 키우고 줄인다.
                    Rect u = ArrangeTools.Union(SelectedMany);
                    if (!u.IsEmpty) DrawBoxHandles(dc, u);
                }
                if (Selected != null)
                {
                    if (!SelectedMany.Contains(Selected)) DrawOutline(dc, Selected);

                    // 확정한 주석도 골라서 크기를 다시 바꿀 수 있다 — 조절점을 같이 그린다.
                    // 여럿을 골랐을 때는 안 그린다(어느 것의 조절점인지 헷갈린다).
                    // (글자·번호처럼 조절점이 없는 주석은 Handles() 가 비어 있어 안 그려진다)
                    if (SelectedMany.Count <= 1) DrawHandles(dc, Selected);
                }

                if (RubberBand.Width >= 1 && RubberBand.Height >= 1)
                {
                    var pen = new Pen(new SolidColorBrush(AccentColor), 1 / _scale)
                    {
                        DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
                    };
                    pen.Freeze();
                    dc.DrawRectangle(null, pen, RubberBand);
                }

                if (ShowManualGuides)
                {
                    DrawManualGuides(dc);
                }

                // 스마트 가이드: 스냅이 잡은 기준선을 화면 끝까지 긋는다(분홍).
                if (GuideX.HasValue || GuideY.HasValue)
                {
                    var gp = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4F, 0x9E)), 1 / _scale);
                    gp.Freeze();
                    if (GuideX.HasValue)
                        dc.DrawLine(gp, new Point(GuideX.Value, 0), new Point(GuideX.Value, ImageHeight));
                    if (GuideY.HasValue)
                        dc.DrawLine(gp, new Point(0, GuideY.Value), new Point(ImageWidth, GuideY.Value));
                }
                if (Active != null && ShowActiveHandles)
                {
                    DrawOutline(dc, Active);
                    DrawHandles(dc, Active);
                }
            }
            finally { dc.Pop(); }
        }

        private void DrawManualGuides(DrawingContext dc)
        {
            var pen = new Pen(Brushes.DeepSkyBlue, 1 / _scale);
            var diagonalPen = new Pen(Brushes.Gold, 1 / _scale) { DashStyle = DashStyles.Dash };
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ImageWidth, ImageHeight)));
            foreach (EditorGuide guide in ManualGuides)
            {
                var line = guide.Line(ImageWidth, ImageHeight);
                dc.DrawLine(guide.Diagonal ? diagonalPen : pen, line.Start, line.End);
                if (!guide.Diagonal) continue;
                dc.DrawEllipse(Brushes.White, diagonalPen, guide.Start, 4 / _scale, 4 / _scale);
                dc.DrawEllipse(Brushes.White, diagonalPen, guide.End, 4 / _scale, 4 / _scale);
                if (ShowGuideMeasurements)
                    DrawGuideLabel(dc, $"{guide.Length:0.#} px · {guide.Angle:0.#}°", guide.Start + (guide.End - guide.Start) * 0.5, Brushes.Gold);
            }
            if (ShowGuideMeasurements && ManualGuides.Exists(g => !g.Diagonal))
                foreach (Rect cell in GuideMeasurements.Cells(ManualGuides, ImageWidth, ImageHeight))
                {
                    if (cell.Width * _scale < 64 || cell.Height * _scale < 24) continue;
                    DrawGuideLabel(dc, $"{cell.Width:0.#} × {cell.Height:0.#} px",
                        new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2), Brushes.DeepSkyBlue);
                }
            dc.Pop();
        }

        private void DrawGuideLabel(DrawingContext dc, string value, Point center, Brush color)
        {
            var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11 / _scale, color,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double pad = 4 / _scale;
            Point origin = new(Math.Clamp(center.X - text.Width / 2, pad, Math.Max(pad, ImageWidth - text.Width - pad)),
                Math.Clamp(center.Y - text.Height / 2, pad, Math.Max(pad, ImageHeight - text.Height - pad)));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 22, 25, 31)), null,
                new Rect(origin.X - pad, origin.Y - pad / 2, text.Width + pad * 2, text.Height + pad), pad, pad);
            dc.DrawText(text, origin);
        }

        /// <summary>고른 영역 — 바깥을 살짝 어둡게 덮고 테두리를 점선으로.</summary>
        private void DrawRegion(DrawingContext dc)
        {
            var shade = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            shade.Freeze();

            var outside = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, ImageWidth, ImageHeight)),
                new RectangleGeometry(Region));
            outside.Freeze();
            dc.DrawGeometry(shade, null, outside);

            var pen = new Pen(new SolidColorBrush(AccentColor), 1 / _scale)
            {
                DashStyle = new DashStyle(new double[] { 5, 3 }, 0)
            };
            pen.Freeze();
            dc.DrawRectangle(null, pen, Region);
        }

        /// <summary>회전해 둔 주석의 표시(점선·조절점)는 같은 각도로 눕혀 그린다.</summary>
        private bool PushRotation(DrawingContext dc, Annotation a)
        {
            if (Math.Abs(a.RotationDeg) < 0.01) return false;
            Point c = a.RotationCenter;
            dc.PushTransform(new RotateTransform(a.RotationDeg, c.X, c.Y));
            return true;
        }

        /// <summary>무리 전체 테두리와 8개 조절점(여럿을 골랐을 때).</summary>
        private void DrawBoxHandles(DrawingContext dc, Rect u)
        {
            var edge = new Pen(new SolidColorBrush(AccentColor), 1 / _scale);
            edge.Freeze();
            dc.DrawRectangle(null, edge, u);

            double size = 9 / _scale;
            foreach (Point p in EditorMath.BoxHandles(u))
                dc.DrawRectangle(Brushes.White, edge, new Rect(p.X - size / 2, p.Y - size / 2, size, size));
        }

        private void DrawOutline(DrawingContext dc, Annotation a, bool faint = false)
        {
            Rect b = a.Bounds;
            if (b.IsEmpty) return;

            if (a is NumberArrowAnnotation) b = NumberArrowEditBounds(a);
            else b.Inflate(Math.Max(4, a.Thickness), Math.Max(4, a.Thickness));

            bool rot = PushRotation(dc, a);

            // 배율이 낮아도 점선이 보이도록 화면 기준 굵기를 역보정한다. 호버는 옅게.
            Color line = faint ? Color.FromArgb(0x80, AccentColor.R, AccentColor.G, AccentColor.B) : AccentColor;
            var pen = new Pen(new SolidColorBrush(line), 1 / _scale)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
            };
            pen.Freeze();
            dc.DrawRectangle(null, pen, b);

            if (rot) dc.Pop();
        }

        /// <summary>회전 손잡이가 놓이는 자리(이미지 좌표, 회전 반영). 편집기 판정도 이걸 쓴다.</summary>
        internal Point RotateKnobPoint(Annotation a)
        {
            var local = new Point(a.RotationCenter.X, a.Bounds.Top - 26 / _scale);
            if (Math.Abs(a.RotationDeg) < 0.01) return local;

            Matrix m = Matrix.Identity;
            m.RotateAt(a.RotationDeg, a.RotationCenter.X, a.RotationCenter.Y);
            return m.Transform(local);
        }

        private Rect NumberArrowEditBounds(Annotation a)
        {
            Rect b = a.Bounds;
            // Separate corner handles from the tip at every zoom level.
            b.Inflate(ToImageLength(20), ToImageLength(20));
            return b;
        }

        internal IReadOnlyList<Point> EditHandles(Annotation a)
        {
            if (a is not NumberArrowAnnotation arrow) return a.Handles();
            Rect b = NumberArrowEditBounds(a);
            return new[] { arrow.Tip, arrow.Center, b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft };
        }

        private void DrawHandles(DrawingContext dc, Annotation a)
        {
            IReadOnlyList<Point> points = EditHandles(a);
            bool rot = PushRotation(dc, a);

            double size = 9 / _scale;          // 화면에서 늘 같은 크기로 보이게
            var edge = new Pen(new SolidColorBrush(AccentColor), 1 / _scale);
            edge.Freeze();

            for (int i = 0; i < points.Count; i++)
            {
                Point p = points[i];
                if (a is NumberArrowAnnotation && i < 2)
                    dc.DrawEllipse(Brushes.White, edge, p, size / 2, size / 2);
                else
                    dc.DrawRectangle(Brushes.White, edge,
                        new Rect(p.X - size / 2, p.Y - size / 2, size, size));
            }

            // 회전 손잡이: 위쪽 가운데에서 떨어진 동그라미. 끌면 도형이 돈다.
            if (a.CanRotate && !a.Bounds.IsEmpty)
            {
                var knob = new Point(a.RotationCenter.X, a.Bounds.Top - 26 / _scale);
                var top = new Point(a.RotationCenter.X, a.Bounds.Top);
                dc.DrawLine(edge, top, new Point(knob.X, knob.Y + 5 / _scale));
                dc.DrawEllipse(Brushes.White, edge, knob, 5 / _scale, 5 / _scale);
            }

            if (rot) dc.Pop();
        }
    }
}
