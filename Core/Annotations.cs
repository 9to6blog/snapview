using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    internal enum ToolKind
    {
        Select, Arrow, Line, Rectangle, Ellipse, Pen, Highlighter, Text, Mosaic, Blur, Counter, Crop,

        // 도형 갤러리. 전부 사각형 하나로 정해져서 조절점·이동·뒤집기가 사각형과 같다.
        Triangle, RightTriangle, Diamond, Pentagon, Hexagon, Heptagon, Octagon,
        Star4, Star5, Star6, Star8,
        Callout, CalloutRound, RoundRect, Parallelogram, Trapezoid, Chevron,
        BlockArrow, BlockArrowLeft, BlockArrowUp, BlockArrowDown,
        Cross, Heart, Lightning, Moon, Shield, Cloud,

        // 강조 도구
        Spotlight, Magnifier, NumberArrow,

        // 보조 도구
        Picker, Eraser, PixelEraser, RegionSelect, Wand
    }

    /// <summary>
    /// 주석을 바탕 그림과 섞는 방식. WPF 에는 혼합 모드가 없어서 직접 픽셀을 섞는다.
    /// <b>바탕 그림하고만 섞인다</b> — 주석끼리는 안 섞인다(그건 합성 엔진이 통째로 필요하다).
    /// </summary>
    internal enum BlendMode { Normal, Multiply, Screen, Overlay }

    /// <summary>화살촉 모양. 채운 삼각형이 기본이고, 열린 촉과 점 촉을 고를 수 있다.</summary>
    internal enum ArrowHead { Filled, Open, Dot }

    /// <summary>점선 무늬. <see cref="Annotation.Dashed"/> 일 때만 뜻이 있다.</summary>
    internal enum DashPattern { Dash, Dot, DashDot }

    /// <summary>글자 정렬. 폭(<see cref="TextAnnotation.MaxWidth"/>)을 정했을 때 상자 안에서 맞춘다.</summary>
    internal enum TextAlign { Left, Center, Right }

    /// <summary>혼합 모드의 실제 픽셀 계산. 미리보기와 결과가 같은 길을 타도록 한 군데에.</summary>
    internal static class BlendComposite
    {
        internal static string NameOf(BlendMode m) => m switch
        {
            BlendMode.Multiply => "곱하기",
            BlendMode.Screen => "스크린",
            BlendMode.Overlay => "오버레이",
            _ => "보통"
        };

        internal static int Channel(int b, int s, BlendMode m) => m switch
        {
            BlendMode.Multiply => b * s / 255,
            BlendMode.Screen => 255 - (255 - b) * (255 - s) / 255,
            BlendMode.Overlay => b < 128 ? 2 * b * s / 255
                                         : 255 - 2 * (255 - b) * (255 - s) / 255,
            _ => s
        };

        /// <summary>
        /// <paramref name="area"/> 자리에서, 주석을 투명 위에 그린 뒤 바탕과 섞은
        /// 비트맵을 돌려준다. 그릴 자리(정수로 맞춘 영역)는 <paramref name="rect"/> 로.
        /// </summary>
        internal static BitmapSource? Compose(BitmapSource source, Rect area, BlendMode mode,
                                              Action<DrawingContext> drawAnnotation, out Rect rect)
        {
            rect = Rect.Empty;

            int x = Math.Max(0, (int)Math.Floor(area.X));
            int y = Math.Max(0, (int)Math.Floor(area.Y));
            int w = Math.Min(source.PixelWidth - x, (int)Math.Ceiling(area.Right) - x);
            int h = Math.Min(source.PixelHeight - y, (int)Math.Ceiling(area.Bottom) - y);
            if (w < 1 || h < 1) return null;

            // 1) 주석만 투명 바탕에 그린다 (영역 원점 기준으로 옮겨서)
            var visual = new DrawingVisual();
            using (DrawingContext dcc = visual.RenderOpen())
            {
                dcc.PushTransform(new TranslateTransform(-x, -y));
                drawAnnotation(dcc);
                dcc.Pop();
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            int stride = w * 4;
            var over = new byte[stride * h];
            rtb.CopyPixels(over, stride, 0);

            // 2) 그 자리의 바탕 픽셀
            var baseCrop = new FormatConvertedBitmap(
                new CroppedBitmap(source, new Int32Rect(x, y, w, h)),
                PixelFormats.Bgra32, null, 0);
            var basePx = new byte[stride * h];
            baseCrop.CopyPixels(basePx, stride, 0);

            // 3) 섞는다. RTB 는 미리 곱해진(premultiplied) 알파라 되돌려서 계산한다.
            var outPx = new byte[stride * h];
            for (int i = 0; i < outPx.Length; i += 4)
            {
                byte a = over[i + 3];
                if (a == 0)
                {
                    outPx[i] = basePx[i]; outPx[i + 1] = basePx[i + 1];
                    outPx[i + 2] = basePx[i + 2]; outPx[i + 3] = basePx[i + 3];
                    continue;
                }

                for (int c = 0; c < 3; c++)
                {
                    int sc = Math.Min(255, over[i + c] * 255 / a);   // un-premultiply
                    int bc = basePx[i + c];
                    int mixed = Channel(bc, sc, mode);
                    outPx[i + c] = (byte)(bc + (mixed - bc) * a / 255);
                }
                outPx[i + 3] = Math.Max(basePx[i + 3], a);
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, outPx, stride);
            bmp.Freeze();
            rect = new Rect(x, y, w, h);
            return bmp;
        }
    }

    /// <summary>
    /// 주석 하나. 좌표는 전부 <b>원본 이미지 픽셀</b> 기준이다.
    /// 화면에 보이는 배율과 무관하게 원본 해상도 그대로 저장되도록 하기 위한 것.
    /// </summary>
    internal abstract class Annotation
    {
        /// <summary>조절점 순서. 사각형류는 8개를 이 차례로 쓴다.</summary>
        internal static readonly string[] RectHandleNames =
            { "NW", "N", "NE", "E", "SE", "S", "SW", "W" };

        internal Color Color { get; set; } = Colors.Red;
        internal double Thickness { get; set; } = 3;

        /// <summary>0(투명) ~ 1(불투명). 그릴 때 통째로 먹인다.</summary>
        internal double Opacity { get; set; } = 1.0;

        /// <summary>레이어 목록에서 끌 수 있다. 꺼진 것은 화면에도 결과물에도 안 나온다.</summary>
        internal bool Visible { get; set; } = true;

        /// <summary>그림자를 깔지. 캡처 위의 도형·글자가 배경에서 떠 보이게 한다.</summary>
        internal bool Shadow { get; set; }

        /// <summary>선을 점선으로. 도형 테두리·직선·화살표 몸통·펜 획에 먹는다.</summary>
        internal bool Dashed { get; set; }

        /// <summary>점선일 때의 무늬(대시 · 점 · 일점쇄선).</summary>
        internal DashPattern DashPattern { get; set; }

        /// <summary>무늬별 대시 배열. 굵기의 배수라서 굵기가 변해도 보기 좋게 유지된다.</summary>
        internal static double[] DashArray(DashPattern pattern) => pattern switch
        {
            DashPattern.Dot => new[] { 0.1, 2.2 },
            DashPattern.DashDot => new[] { 4, 2.2, 0.1, 2.2 },
            _ => new[] { 3, 2.4 }
        };

        /// <summary>펜에 점선 무늬를 입힌다. 점은 둥근 캡이라야 점으로 보인다.</summary>
        protected void ApplyDash(Pen pen)
        {
            if (!Dashed) return;
            pen.DashStyle = new DashStyle(DashArray(DashPattern), 0);
            pen.DashCap = PenLineCap.Round;
        }

        /// <summary>레이어 목록에 보여 줄 이름. 없으면 종류로 부른다.</summary>
        internal string? Name { get; set; }

        /// <summary>잠그면 캔버스에서 안 집히고, 옮기거나 지울 수 없다(레이어 메뉴로 푼다).</summary>
        internal bool Locked { get; set; }

        /// <summary>바탕 그림과 섞는 방식. <see cref="SupportsBlend"/> 인 주석만 쓴다.</summary>
        internal BlendMode Blend { get; set; } = BlendMode.Normal;

        /// <summary>혼합 모드가 뜻이 있는 주석인가(도형·붙인 그림).</summary>
        internal virtual bool SupportsBlend => false;

        /// <summary>
        /// 투명도를 먹여도 되는 주석인가.
        /// 모자이크·흐림은 <b>안 된다</b> — 반투명하게 만들면 가린 내용이 비쳐 보인다.
        /// 개인정보를 지우려고 쓰는 도구가 슬라이더 하나로 무력해져선 안 된다.
        /// </summary>
        internal virtual bool SupportsOpacity => true;

        /// <summary>그림자가 어울리는 주석인가. 가리개(모자이크)·강조·자르기는 아니다.</summary>
        internal virtual bool SupportsShadow => true;

        /// <summary>
        /// 기본 그림자: 자기 복제본을 검게 물들여 살짝 어긋난 자리에 옅게 그린다.
        /// 모양이 어떻든 스스로의 Render 를 그대로 쓰므로 도형·화살표·펜 전부 통한다.
        /// </summary>
        internal virtual void RenderShadow(DrawingContext dc, BitmapSource source)
        {
            Annotation ghost = Clone();
            ghost.Shadow = false;
            ghost.Color = Color.FromRgb(10, 10, 12);
            if (ghost is ShapeAnnotation shape) shape.FillColor = ghost.Color;
            double off = Math.Max(2.5, Thickness * 0.9);
            ghost.Move(new Vector(off, off));

            dc.PushOpacity(0.35);
            ghost.Render(dc, source);
            dc.Pop();
        }

        /// <summary>도형을 눕혀 놓은 각도(도). 회전 전 Bounds 의 가운데를 축으로 돈다.</summary>
        internal double RotationDeg { get; set; }

        /// <summary>자유 회전이 어울리는 주석인가. 가리개·번호처럼 축이 서야 하는 것은 아니다.</summary>
        internal virtual bool CanRotate => false;

        /// <summary>회전 축 = 회전 전 Bounds 의 가운데.</summary>
        internal Point RotationCenter
        {
            get
            {
                Rect b = Bounds;
                return new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            }
        }

        /// <summary>이미지 좌표를 이 주석의 '눕히기 전' 좌표로 되돌린다. 판정·조절점용.</summary>
        internal Point ToLocal(Point p)
        {
            if (RotationDeg == 0) return p;
            Point c = RotationCenter;
            Matrix m = Matrix.Identity;
            m.RotateAt(-RotationDeg, c.X, c.Y);
            return m.Transform(p);
        }

        /// <summary>회전을 감안한 적중 판정. 편집기는 HitTest 대신 이걸 쓴다.</summary>
        internal bool HitTestRotated(Point p) => HitTest(ToLocal(p));

        internal abstract Rect Bounds { get; }
        internal abstract void Render(DrawingContext dc, BitmapSource source);
        internal abstract void Move(Vector delta);

        /// <summary>복제. 회전·보임 같은 공통 값은 여기서 한 번에 옮긴다.</summary>
        internal Annotation Clone()
        {
            Annotation c = CloneCore();
            c.RotationDeg = RotationDeg;
            c.Visible = Visible;
            c.Dashed = Dashed;
            c.DashPattern = DashPattern;
            c.Name = Name;
            c.Locked = Locked;
            c.GroupId = GroupId;
            c.GroupName = GroupName;
            c.Blend = Blend;
            return c;
        }

        internal string? GroupId { get; set; }
        internal string? GroupName { get; set; }

        /// <summary>타입별 복제 몸통. 자기 필드만 옮기면 된다.</summary>
        protected abstract Annotation CloneCore();

        /// <summary>좌우/상하 뒤집기. 이미지가 뒤집힐 때 주석도 같이 따라가야 한다.</summary>
        internal abstract void Flip(bool horizontal, double imageW, double imageH);

        /// <summary>
        /// 이 주석이 가진 <b>모든 점</b>을 옮긴다.
        /// 회전·크기 조절처럼 좌표만 바뀌는 변형은 전부 이 하나로 처리한다.
        /// (뒤집기는 글자를 거울로 만들면 안 돼서 따로 두었다)
        /// </summary>
        protected abstract void MapPoints(Func<Point, Point> map);

        /// <summary>
        /// 90도 돌린다. <paramref name="imageW"/>·<paramref name="imageH"/> 는 <b>돌리기 전</b> 크기다.
        /// 시계 방향이면 (x,y) → (H−y, x).
        /// </summary>
        internal void Rotate90(bool clockwise, double imageW, double imageH)
            => MapPoints(p => clockwise
                ? new Point(imageH - p.Y, p.X)
                : new Point(p.Y, imageW - p.X));

        /// <summary>이미지 크기가 바뀔 때 주석도 같은 비율로 따라간다.</summary>
        internal virtual void Scale(double fx, double fy)
        {
            MapPoints(p => new Point(p.X * fx, p.Y * fy));
            Thickness = Math.Max(0.5, Thickness * (fx + fy) / 2);
        }

        /// <summary>끌어서 모양을 바꿀 수 있는 점들. 없으면 이동만 된다.</summary>
        internal virtual IReadOnlyList<Point> Handles() => Array.Empty<Point>();

        /// <summary><paramref name="index"/> 번째 조절점을 <paramref name="p"/> 로 끈다.</summary>
        internal virtual void DragHandle(int index, Point p) { }

        internal virtual bool HitTest(Point p)
        {
            Rect b = Bounds;
            b.Inflate(Math.Max(6, Thickness), Math.Max(6, Thickness));
            return b.Contains(p);
        }

        protected Pen MakePen(double? width = null)
        {
            var pen = new Pen(new SolidColorBrush(Color), width ?? Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            ApplyDash(pen);
            pen.Freeze();
            return pen;
        }

        protected static Point MapFlip(Point p, bool horizontal, double w, double h)
            => horizontal ? new Point(w - p.X, p.Y) : new Point(p.X, h - p.Y);

        /// <summary>사각형 8 조절점(NW N NE E SE S SW W).</summary>
        protected static Point[] RectHandles(Rect b)
        {
            double l = b.Left, t = b.Top, r = b.Right, bo = b.Bottom;
            double cx = (l + r) / 2, cy = (t + bo) / 2;
            return new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy),
                new Point(r, bo), new Point(cx, bo), new Point(l, bo), new Point(l, cy)
            };
        }

        /// <summary>사각형 조절점을 끌었을 때의 새 두 꼭짓점.</summary>
        protected static (Point Start, Point End) ResizeRect(Point start, Point end, int index, Point p)
        {
            double l = Math.Min(start.X, end.X), t = Math.Min(start.Y, end.Y);
            double r = Math.Max(start.X, end.X), b = Math.Max(start.Y, end.Y);

            if (index is 0 or 6 or 7) l = p.X;          // NW SW W
            if (index is 2 or 3 or 4) r = p.X;          // NE E SE
            if (index is 0 or 1 or 2) t = p.Y;          // NW N NE
            if (index is 4 or 5 or 6) b = p.Y;          // SE S SW

            return (new Point(Math.Min(l, r), Math.Min(t, b)),
                    new Point(Math.Max(l, r), Math.Max(t, b)));
        }
    }

    /// <summary>두 점으로 정해지는 도형: 선 · 화살표 · 사각형 · 타원.</summary>
    internal sealed class ShapeAnnotation : Annotation
    {
        internal ToolKind Kind { get; set; }
        internal Point Start { get; set; }
        internal Point End { get; set; }
        internal bool Filled { get; set; }
        // null keeps older projects using their original outline color for the fill.
        internal Color? FillColor { get; set; }

        /// <summary>화살표 양 끝에 촉을 단다(구간 표시용). 화살표일 때만 뜻이 있다.</summary>
        internal bool BothArrows { get; set; }

        /// <summary>채우기를 위→아래로 옅어지는 그라데이션으로.</summary>
        internal bool GradientFill { get; set; }

        /// <summary>화살촉 모양. 화살표일 때만 뜻이 있다.</summary>
        internal ArrowHead Head { get; set; }

        private bool IsSegment => Kind is ToolKind.Line or ToolKind.Arrow;

        /// <summary>안쪽이 있어 채울 수 있는 도형인가.</summary>
        internal bool CanFill => !IsSegment;

        /// <summary>선·화살표는 끝점을 끌면 어차피 마음대로 눕는다. 상자 도형만 회전 손잡이.</summary>
        internal override bool CanRotate => !IsSegment;

        internal override Rect Bounds => new(
            Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

        internal override void Move(Vector d)
        {
            Start += d;
            End += d;
        }

        internal override void Flip(bool horizontal, double w, double h)
        {
            Start = MapFlip(Start, horizontal, w, h);
            End = MapFlip(End, horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Start = map(Start);
            End = map(End);
        }

        // 선·화살표는 방향이 중요하므로 양 끝점만, 사각형·타원은 8방향.
        internal override IReadOnlyList<Point> Handles()
            => IsSegment ? new[] { Start, End } : RectHandles(Bounds);

        internal override void DragHandle(int index, Point p)
        {
            if (IsSegment)
            {
                if (index == 0) Start = p; else End = p;
                return;
            }
            (Start, End) = ResizeRect(Start, End, index, p);
        }

        protected override Annotation CloneCore() => new ShapeAnnotation
        {
            Kind = Kind, Start = Start, End = End, Filled = Filled, BothArrows = BothArrows,
            GradientFill = GradientFill, FillColor = FillColor, Head = Head,
            Color = Color, Thickness = Thickness, Opacity = Opacity, Shadow = Shadow
        };

        internal override bool SupportsBlend => true;

        private BitmapSource? _blendCache;
        private string? _blendKey;

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            if (Blend == BlendMode.Normal) { RenderVector(dc); return; }

            // 혼합 모드: 도형을 투명 위에 그린 뒤 바탕 픽셀과 직접 섞는다.
            // 화면과 결과가 같은 길을 타고, 같은 모양이면 다시 계산하지 않는다.
            Rect area = Bounds;
            area.Inflate(Thickness + 2, Thickness + 2);

            string key = $"{Start}|{End}|{Color}|{FillColor}|{Thickness}|{Filled}|{GradientFill}|" +
                         $"{Dashed}|{DashPattern}|{Kind}|{Blend}|{BothArrows}|{Head}|{source.GetHashCode()}";
            if (_blendCache == null || _blendKey != key)
            {
                _blendCache = BlendComposite.Compose(source, area, Blend, RenderVector, out _blendRect);
                _blendKey = key;
            }
            if (_blendCache != null) dc.DrawImage(_blendCache, _blendRect);
        }

        private Rect _blendRect = Rect.Empty;

        private void RenderVector(DrawingContext dc)
        {
            Pen pen = MakePen();
            Brush? fill = null;
            if (Filled)
            {
                Color color = FillColor ?? Color;
                fill = GradientFill
                    ? new LinearGradientBrush(color,
                        Color.FromArgb((byte)(color.A / 6), color.R, color.G, color.B), 90)
                    : new SolidColorBrush(color);
                fill.Freeze();
            }

            switch (Kind)
            {
                case ToolKind.Line:
                    dc.DrawLine(pen, Start, End);
                    break;

                case ToolKind.Rectangle:
                    dc.DrawRectangle(fill, pen, Bounds);
                    break;

                case ToolKind.Ellipse:
                {
                    Rect b = Bounds;
                    dc.DrawEllipse(fill, pen, new Point(b.X + b.Width / 2, b.Y + b.Height / 2),
                                   b.Width / 2, b.Height / 2);
                    break;
                }

                case ToolKind.Arrow:
                    DrawArrow(dc, pen);
                    break;

                default:
                {
                    // 갤러리 도형들. 모르는 것이면 사각형으로 대신 그린다.
                    Geometry? geo = ShapeGeometry.Build(Kind, Bounds);
                    if (geo != null) dc.DrawGeometry(fill, pen, geo);
                    else dc.DrawRectangle(fill, pen, Bounds);
                    break;
                }
            }
        }

        private void DrawArrow(DrawingContext dc, Pen pen)
        {
            var v = End - Start;
            double len = v.Length;
            if (len < 1) return;

            v.Normalize();
            double head = Math.Max(3.2, Thickness * 3.2);
            head = Math.Min(head, len * (BothArrows ? 0.4 : 0.55));

            var brush = new SolidColorBrush(Color);
            brush.Freeze();

            // 촉이 덮을 부분만큼 몸통을 짧게 그려야 선이 촉 밖으로 삐져나오지 않는다.
            // 열린 촉은 몸통이 끝까지 가고, 점 촉은 점 반지름만큼만 비운다.
            double dotR = Math.Max(1.6, Thickness * 1.6);
            double back = Head switch
            {
                ArrowHead.Open => 0,
                ArrowHead.Dot => dotR,
                _ => head * 0.72
            };
            Point shaftStart = BothArrows ? Start + v * back : Start;
            Point shaftEnd = End - v * back;
            dc.DrawLine(pen, shaftStart, shaftEnd);

            DrawHead(dc, brush, pen, End, v, head, Head, dotR);
            if (BothArrows) DrawHead(dc, brush, pen, Start, -v, head, Head, dotR);
        }

        private static void DrawHead(DrawingContext dc, Brush brush, Pen pen, Point tip, Vector dir,
                                     double head, ArrowHead style, double dotRadius)
        {
            if (style == ArrowHead.Dot)
            {
                dc.DrawEllipse(brush, null, tip, dotRadius, dotRadius);
                return;
            }

            var normal = new Vector(-dir.Y, dir.X);
            Point p1 = tip - dir * head + normal * (head * 0.42);
            Point p2 = tip - dir * head - normal * (head * 0.42);

            if (style == ArrowHead.Open)
            {
                // 점선이어도 촉은 실선이라야 촉으로 보인다.
                var solid = new Pen(pen.Brush, pen.Thickness)
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
                };
                solid.Freeze();
                dc.DrawLine(solid, p1, tip);
                dc.DrawLine(solid, p2, tip);
                return;
            }

            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(tip, isFilled: true, isClosed: true);
                g.LineTo(p1, true, false);
                g.LineTo(p2, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(brush, null, geo);
        }

        internal override bool HitTest(Point p)
        {
            if (IsSegment)
            {
                // 선분과의 거리로 판정
                Vector ab = End - Start, ap = p - Start;
                double len2 = ab.LengthSquared;
                double t = len2 <= 0 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / len2, 0, 1);
                Point closest = Start + ab * t;
                return (p - closest).Length <= Math.Max(8, Thickness * 1.5);
            }
            return base.HitTest(p);
        }
    }

    /// <summary>자유 곡선: 펜 · 형광펜.</summary>
    internal sealed class PathAnnotation : Annotation
    {
        internal List<Point> Points { get; } = new();
        internal bool Highlighter { get; set; }

        internal override bool CanRotate => true;

        internal override Rect Bounds
        {
            get
            {
                if (Points.Count == 0) return Rect.Empty;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (Point p in Points)
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
        }

        internal override void Move(Vector d)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] += d;
        }

        internal override void Flip(bool horizontal, double w, double h)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = MapFlip(Points[i], horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
        }

        protected override Annotation CloneCore()
        {
            var c = new PathAnnotation
            {
                Highlighter = Highlighter, Color = Color, Thickness = Thickness,
                Opacity = Opacity, Shadow = Shadow
            };
            c.Points.AddRange(Points);
            return c;
        }

        /// <summary>형광펜은 곱하기 합성이 어울린다 — 흰 종이는 노랗게, 글자는 그대로 검게 남는다.</summary>
        internal override bool SupportsBlend => Highlighter;

        private BitmapSource? _blendCache;
        private string? _blendKey;
        private Rect _blendRect = Rect.Empty;

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            if (!Highlighter || Blend == BlendMode.Normal || Points.Count < 2) { RenderVector(dc); return; }

            Rect area = Bounds;
            area.Inflate(Thickness * 5, Thickness * 5);
            string key = $"{Points.Count}|{Points[0]}|{Points[^1]}|{Color}|{Thickness}|{Blend}|{Dashed}|{DashPattern}|{source.GetHashCode()}";
            if (_blendCache == null || _blendKey != key)
            {
                _blendCache = BlendComposite.Compose(source, area, Blend, RenderVector, out _blendRect);
                _blendKey = key;
            }
            if (_blendCache != null) dc.DrawImage(_blendCache, _blendRect);
        }

        private void RenderVector(DrawingContext dc)
        {
            // 곱하기로 섞을 때는 진하게 그려야 형광펜답다. 보통 모드에서는 반투명으로.
            Color ink = Highlighter
                ? (Blend != BlendMode.Normal ? Color.FromArgb(235, Color.R, Color.G, Color.B) : Fade(Color))
                : Color;

            if (Points.Count < 2)
            {
                if (Points.Count == 1)
                {
                    var b = new SolidColorBrush(ink);
                    b.Freeze();
                    dc.DrawEllipse(b, null, Points[0], Thickness / 2, Thickness / 2);
                }
                return;
            }

            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(Points[0], isFilled: false, isClosed: false);
                for (int i = 1; i < Points.Count; i++) g.LineTo(Points[i], true, true);
            }
            geo.Freeze();

            var pen = new Pen(new SolidColorBrush(ink), Highlighter ? Thickness * 5 : Thickness)
            {
                StartLineCap = Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                EndLineCap = Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            ApplyDash(pen);
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        private static Color Fade(Color c) => Color.FromArgb(90, c.R, c.G, c.B);

        internal override bool HitTest(Point p)
        {
            double tol = Math.Max(8, (Highlighter ? Thickness * 5 : Thickness) / 2 + 4);
            foreach (Point q in Points)
            {
                if (Math.Abs(q.X - p.X) <= tol && Math.Abs(q.Y - p.Y) <= tol) return true;
            }
            return false;
        }
    }

    /// <summary>여러 줄을 쓸 수 있는 글자.</summary>
    internal sealed class TextAnnotation : Annotation
    {
        /// <summary>글꼴이 시스템에 없으면 WPF 가 알아서 대체 글꼴로 그린다.</summary>
        internal const string DefaultFontFamily = "Malgun Gothic";

        internal Point Origin { get; set; }
        internal string Text { get; set; } = "";
        internal double FontSize { get; set; } = 22;
        internal string FontFamilyName { get; set; } = DefaultFontFamily;
        internal bool Bold { get; set; } = true;      // 캡처 위의 글자는 굵어야 읽힌다
        internal bool Italic { get; set; }

        internal override bool CanRotate => true;

        /// <summary>배경이 밝든 어둡든 읽히게 하는 얇은 외곽선. 배경 상자를 켜면 필요 없다.</summary>
        internal bool OutlineHalo { get; set; } = true;

        /// <summary>글자 뒤에 라벨처럼 상자를 깐다. 글자색이 밝으면 어두운 상자, 어두우면 밝은 상자.</summary>
        internal bool Background { get; set; }

        /// <summary>상자 폭. 0 이면 줄바꿈 없이 한 줄(Enter 로만 줄이 바뀐다). 정하면 그 폭에서 자동 줄바꿈.</summary>
        internal double MaxWidth { get; set; }

        /// <summary>상자 안에서의 정렬. 폭을 정했을 때만 눈에 띈다.</summary>
        internal TextAlign Align { get; set; }

        /// <summary>오른쪽 가장자리 조절점 하나: 끌면 폭이 정해지고 그 폭에서 줄이 바뀐다.</summary>
        internal override IReadOnlyList<Point> Handles()
        {
            Rect b = Bounds;
            return new[] { new Point(b.Right, b.Y + b.Height / 2) };
        }

        internal override void DragHandle(int index, Point p)
            => MaxWidth = Math.Clamp(p.X - Origin.X, FontSize, 8000);

        /// <summary>배경 상자 영역(여백 포함). Background 일 때 그림자·히트 판정에 쓴다.</summary>
        private Rect BoxRect(Rect text)
        {
            double pad = FontSize * 0.38;
            return new Rect(text.X - pad, text.Y - pad * 0.55,
                            text.Width + pad * 2, text.Height + pad * 1.1);
        }

        private FormattedText Build()
        {
            var brush = new SolidColorBrush(Color);
            brush.Freeze();

            // FormattedText 는 \n 을 그대로 줄바꿈으로 그린다.
            var ft = new FormattedText(
                string.IsNullOrEmpty(Text) ? " " : Text.Replace("\r\n", "\n"),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(string.IsNullOrWhiteSpace(FontFamilyName)
                                 ? DefaultFontFamily + ", Segoe UI"
                                 : FontFamilyName + ", " + DefaultFontFamily + ", Segoe UI"),
                             Italic ? FontStyles.Italic : FontStyles.Normal,
                             Bold ? FontWeights.Bold : FontWeights.Normal,
                             FontStretches.Normal),
                FontSize, brush, 96)
            {
                LineHeight = FontSize * 1.35
            };
            if (MaxWidth > 0)
            {
                ft.MaxTextWidth = MaxWidth;
                ft.TextAlignment = Align switch
                {
                    TextAlign.Center => TextAlignment.Center,
                    TextAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                };
            }
            return ft;
        }

        internal override Rect Bounds
        {
            get
            {
                FormattedText ft = Build();
                double w = MaxWidth > 0 ? MaxWidth : ft.WidthIncludingTrailingWhitespace;
                return new Rect(Origin.X, Origin.Y, w, ft.Height);
            }
        }

        internal override void Move(Vector d) => Origin += d;

        internal override void Flip(bool horizontal, double w, double h)
        {
            // 글자 자체를 거울로 뒤집으면 읽을 수 없으므로 위치만 옮긴다.
            Rect b = Bounds;
            Origin = horizontal
                ? new Point(w - b.Right, b.Y)
                : new Point(b.X, h - b.Bottom);
        }

        // 글자는 돌리거나 늘려도 <b>똑바로 선 채로</b> 있어야 읽힌다. 자리만 옮긴다.
        protected override void MapPoints(Func<Point, Point> map) => Origin = map(Origin);

        internal override void Scale(double fx, double fy)
        {
            base.Scale(fx, fy);
            FontSize = Math.Max(4, FontSize * (fx + fy) / 2);
            if (MaxWidth > 0) MaxWidth *= fx;
        }

        protected override Annotation CloneCore() => new TextAnnotation
        {
            Origin = Origin, Text = Text, FontSize = FontSize, FontFamilyName = FontFamilyName,
            Bold = Bold, Italic = Italic, OutlineHalo = OutlineHalo, Background = Background,
            MaxWidth = MaxWidth, Align = Align,
            Color = Color, Thickness = Thickness, Opacity = Opacity, Shadow = Shadow
        };

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            if (string.IsNullOrEmpty(Text)) return;

            FormattedText ft = Build();
            var textRect = new Rect(Origin.X, Origin.Y,
                                    MaxWidth > 0 ? MaxWidth : ft.WidthIncludingTrailingWhitespace, ft.Height);

            if (Background)
            {
                double luma = (0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B) / 255.0;
                var bg = new SolidColorBrush(luma > 0.6
                    ? Color.FromArgb(232, 18, 18, 22)
                    : Color.FromArgb(238, 255, 255, 255));
                bg.Freeze();
                double r = FontSize * 0.28;
                dc.DrawRoundedRectangle(bg, null, BoxRect(textRect), r, r);
            }
            else if (OutlineHalo)
            {
                // 배경이 밝든 어둡든 읽히도록 얇은 외곽선을 깐다.
                Geometry outline = ft.BuildGeometry(Origin);
                var halo = new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                                   Math.Max(2, FontSize / 9))
                {
                    LineJoin = PenLineJoin.Round
                };
                halo.Freeze();
                dc.DrawGeometry(null, halo, outline);
            }

            dc.DrawText(ft, Origin);
        }

        /// <summary>배경 상자가 있으면 상자의 그림자만. 글자 복제 그림자는 상자 밖으로 삐친다.</summary>
        internal override void RenderShadow(DrawingContext dc, BitmapSource source)
        {
            if (!Background) { base.RenderShadow(dc, source); return; }

            FormattedText ft = Build();
            Rect box = BoxRect(new Rect(Origin.X, Origin.Y,
                                        ft.WidthIncludingTrailingWhitespace, ft.Height));
            double off = Math.Max(3, FontSize * 0.14);
            box.Offset(off, off);

            var brush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            brush.Freeze();
            double r = FontSize * 0.28;
            dc.DrawRoundedRectangle(brush, null, box, r, r);
        }
    }

    /// <summary>가려야 할 영역: 모자이크 또는 흐림. 원본 픽셀에서 계산해 캐시한다.</summary>
    internal sealed class PixelateAnnotation : Annotation
    {
        private BitmapSource? _cache;
        private BitmapSource? _cacheSource;
        private Rect _cacheRect = Rect.Empty;
        private int _cacheStrength = -1;
        private bool _cacheBlur;

        internal Point Start { get; set; }
        internal Point End { get; set; }
        internal bool UseBlur { get; set; }
        internal int Strength { get; set; } = 12;

        /// <summary>가리개 모양. 사각형이 기본이고 타원·갤러리 도형으로 오려 낼 수 있다.</summary>
        internal ToolKind Shape { get; set; } = ToolKind.Rectangle;

        /// <summary>
        /// 끌어서 크기를 맞추는 동안의 빠른 미리보기. 원본을 1/4 로 줄여 계산하므로
        /// 8K 캡처에서도 끊기지 않는다. 손을 떼면 편집기가 끄고, 그때 정밀하게 다시 계산한다.
        /// </summary>
        internal bool Fast { get; set; }
        private bool _cacheFast;

        internal override Rect Bounds => new(
            Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

        internal override void Move(Vector d)
        {
            Start += d;
            End += d;
            _cache = null;
        }

        internal override void Flip(bool horizontal, double w, double h)
        {
            Start = MapFlip(Start, horizontal, w, h);
            End = MapFlip(End, horizontal, w, h);
            _cache = null;
        }

        internal override IReadOnlyList<Point> Handles() => RectHandles(Bounds);

        internal override void DragHandle(int index, Point p)
        {
            (Start, End) = ResizeRect(Start, End, index, p);
            _cache = null;
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Start = map(Start);
            End = map(End);
            _cache = null;
        }

        internal override void Scale(double fx, double fy)
        {
            base.Scale(fx, fy);
            Strength = Math.Max(2, (int)Math.Round(Strength * (fx + fy) / 2));
            _cache = null;
        }

        /// <summary>반투명하게 만들면 가린 내용이 비친다. 그래서 투명도를 안 받는다.</summary>
        internal override bool SupportsOpacity => false;

        /// <summary>가리개는 배경의 일부처럼 보여야 한다. 그림자로 떠 보이면 이상하다.</summary>
        internal override bool SupportsShadow => false;

        protected override Annotation CloneCore() => new PixelateAnnotation
        {
            Start = Start, End = End, UseBlur = UseBlur, Strength = Strength, Shape = Shape,
            Color = Color, Thickness = Thickness, Opacity = Opacity
        };

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            Rect b = Bounds;
            if (b.Width < 2 || b.Height < 2) return;

            if (_cache == null || _cacheRect != b || _cacheStrength != Strength ||
                _cacheBlur != UseBlur || _cacheFast != Fast || !ReferenceEquals(_cacheSource, source))
            {
                var region = new Int32Rect((int)Math.Round(b.X), (int)Math.Round(b.Y),
                                           Math.Max(1, (int)Math.Round(b.Width)),
                                           Math.Max(1, (int)Math.Round(b.Height)));
                _cache = Fast ? ComputeFast(source, region) : ComputeExact(source, region);
                _cacheRect = b;
                _cacheStrength = Strength;
                _cacheBlur = UseBlur;
                _cacheFast = Fast;
                _cacheSource = source;
            }

            if (_cache == null) return;

            // 사각형이 아니면 도형 윤곽으로 오려 낸다. 조절점·이동은 사각형과 똑같이 움직인다.
            Geometry? clip = Shape == ToolKind.Rectangle ? null : MaskShapes.Outline(Shape, b);
            if (clip != null) dc.PushClip(clip);
            dc.DrawImage(_cache, b);
            if (clip != null) dc.Pop();
        }

        private BitmapSource? ComputeExact(BitmapSource source, Int32Rect region)
            => UseBlur
                ? ImageEffects.Blur(source, region, Math.Max(2, Strength / 2))
                : ImageEffects.Pixelate(source, region, Strength);

        /// <summary>영역만 잘라 1/4 로 줄여서 계산한다. 화면에는 다시 늘려 보이므로 결과와 거의 같다.</summary>
        private BitmapSource? ComputeFast(BitmapSource source, Int32Rect region)
        {
            const int shrink = 4;
            int x = Math.Clamp(region.X, 0, Math.Max(0, source.PixelWidth - 1));
            int y = Math.Clamp(region.Y, 0, Math.Max(0, source.PixelHeight - 1));
            int w = Math.Clamp(region.Width, 1, source.PixelWidth - x);
            int h = Math.Clamp(region.Height, 1, source.PixelHeight - y);
            if (w < shrink * 2 || h < shrink * 2) return ComputeExact(source, region);

            try
            {
                var crop = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
                var small = new TransformedBitmap(crop, new ScaleTransform(1.0 / shrink, 1.0 / shrink));
                small.Freeze();
                var all = new Int32Rect(0, 0, small.PixelWidth, small.PixelHeight);
                int s = Math.Max(2, Strength / shrink);
                return UseBlur
                    ? ImageEffects.Blur(small, all, Math.Max(1, s / 2))
                    : ImageEffects.Pixelate(small, all, s);
            }
            catch { return ComputeExact(source, region); }
        }

        internal override bool HitTest(Point p)
        {
            if (Shape == ToolKind.Rectangle) return base.HitTest(p);
            Rect b = Bounds;
            if (b.Width < 1 || b.Height < 1) return false;
            return MaskShapes.Outline(Shape, b).FillContains(p);
        }
    }

    /// <summary>순서를 표시하는 번호 원.</summary>
    internal sealed class CounterAnnotation : Annotation
    {
        internal Point Center { get; set; }
        internal int Number { get; set; } = 1;
        internal double Radius { get; set; } = 16;

        internal override Rect Bounds =>
            new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

        internal override void Move(Vector d) => Center += d;

        internal override void Flip(bool horizontal, double w, double h)
            => Center = MapFlip(Center, horizontal, w, h);

        protected override void MapPoints(Func<Point, Point> map) => Center = map(Center);

        internal override void Scale(double fx, double fy)
        {
            base.Scale(fx, fy);
            Radius = Math.Max(6, Radius * (fx + fy) / 2);
        }

        protected override Annotation CloneCore() => new CounterAnnotation
        {
            Center = Center, Number = Number, Radius = Radius,
            Color = Color, Thickness = Thickness, Opacity = Opacity, Shadow = Shadow
        };

        /// <summary>
        /// 번호의 그림자는 원판만. 복제해 검게 그리면 대비 규칙 때문에
        /// 숫자가 흰색으로 바뀌어 그림자에 흰 글자가 비쳐 보인다.
        /// </summary>
        internal override void RenderShadow(DrawingContext dc, BitmapSource source)
        {
            var brush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            brush.Freeze();
            double off = Math.Max(2.5, Radius * 0.12);
            dc.DrawEllipse(brush, null, new Point(Center.X + off, Center.Y + off), Radius, Radius);
        }

        /// <summary>
        /// 동그라미 안 숫자와 테두리 색.
        /// 고른 색이 밝으면 검은 글자, 어두우면 흰 글자를 쓴다.
        /// 늘 흰색으로 쓰면 노랑·흰색을 골랐을 때 숫자가 안 보인다.
        /// </summary>
        private Color ContrastColor
        {
            get
            {
                // 사람 눈이 느끼는 밝기(BT.601)
                double luma = (0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B) / 255.0;
                return luma > 0.6 ? Colors.Black : Colors.White;
            }
        }

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            Color ink = ContrastColor;

            var fill = new SolidColorBrush(Color);
            fill.Freeze();
            var inkBrush = new SolidColorBrush(ink);
            inkBrush.Freeze();

            var edge = new Pen(inkBrush, Math.Max(1.5, Radius / 8));
            edge.Freeze();
            dc.DrawEllipse(fill, edge, Center, Radius, Radius);

            var ft = new FormattedText(
                Number.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
                             FontStretches.Normal),
                Radius * 1.25, inkBrush, 96);

            dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
        }

        internal override bool HitTest(Point p) => (p - Center).Length <= Radius + 4;
    }

    /// <summary>
    /// 잘라낼 영역. 다른 주석과 달리 결과물에 그려지는 게 아니라, 확정하면
    /// 이미지 자체를 자르고 사라진다. 조절점·이동 로직을 그대로 쓰려고 주석으로 만들었다.
    /// </summary>
    internal sealed class CropAnnotation : Annotation
    {
        internal Point Start { get; set; }
        internal Point End { get; set; }

        internal override Rect Bounds => new(
            Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

        internal override void Move(Vector d)
        {
            Start += d;
            End += d;
        }

        internal override void Flip(bool horizontal, double w, double h)
        {
            Start = MapFlip(Start, horizontal, w, h);
            End = MapFlip(End, horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Start = map(Start);
            End = map(End);
        }

        internal override IReadOnlyList<Point> Handles() => RectHandles(Bounds);

        internal override void DragHandle(int index, Point p)
            => (Start, End) = ResizeRect(Start, End, index, p);

        protected override Annotation CloneCore() => new CropAnnotation
        {
            Start = Start, End = End,
            Color = Color, Thickness = Thickness, Opacity = Opacity
        };

        // 자르기 미리보기는 "밖을 어둡게" 가 전부다. 투명도를 먹이면 의미가 없다.
        internal override bool SupportsOpacity => false;
        internal override bool SupportsShadow => false;

        /// <summary>잘라낼 영역 밖을 어둡게 덮어 어디가 남는지 보여 준다.</summary>
        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            Rect b = Bounds;
            if (b.Width < 1 || b.Height < 1) return;

            var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
            outside.Children.Add(new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)));
            outside.Children.Add(new RectangleGeometry(b));
            outside.Freeze();

            var dim = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0));
            dim.Freeze();
            dc.DrawGeometry(dim, null, outside);

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)), 1);
            pen.Freeze();
            dc.DrawRectangle(null, pen, b);

            // 삼분할선. 구도를 잡을 때 기준이 된다.
            var thin = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1);
            thin.Freeze();
            for (int i = 1; i <= 2; i++)
            {
                double x = b.X + b.Width * i / 3, y = b.Y + b.Height * i / 3;
                dc.DrawLine(thin, new Point(x, b.Top), new Point(x, b.Bottom));
                dc.DrawLine(thin, new Point(b.Left, y), new Point(b.Right, y));
            }
        }

        /// <summary>이미지 안쪽으로 잘라낸 정수 영역.</summary>
        internal Int32Rect ToRegion(int imageW, int imageH)
        {
            Rect b = Bounds;
            int x = Math.Clamp((int)Math.Round(b.X), 0, Math.Max(0, imageW - 1));
            int y = Math.Clamp((int)Math.Round(b.Y), 0, Math.Max(0, imageH - 1));
            int w = Math.Clamp((int)Math.Round(b.Width), 1, imageW - x);
            int h = Math.Clamp((int)Math.Round(b.Height), 1, imageH - y);
            return new Int32Rect(x, y, w, h);
        }
    }

    /// <summary>
    /// 번호 원 + 가리키는 화살표. 순서를 매기면서 "몇 번이 어디인지" 까지 짚어 준다.
    /// 화살촉이 가리키는 곳, 원이 번호 자리 — 각각 조절점으로 끈다.
    /// </summary>
    internal sealed class NumberArrowAnnotation : Annotation
    {
        internal Point Tip { get; set; }        // 가리키는 곳(화살촉)
        internal Point Center { get; set; }     // 번호 원의 가운데
        internal int Number { get; set; } = 1;
        internal double Radius { get; set; } = 16;

        internal override Rect Bounds
        {
            get
            {
                var b = new Rect(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
                b.Union(Tip);
                return b;
            }
        }

        internal override void Move(Vector d) { Center += d; Tip += d; }

        internal override void Flip(bool horizontal, double w, double h)
        {
            Center = MapFlip(Center, horizontal, w, h);
            Tip = MapFlip(Tip, horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Center = map(Center);
            Tip = map(Tip);
        }

        internal override void Scale(double fx, double fy)
        {
            base.Scale(fx, fy);
            Radius = Math.Max(6, Radius * (fx + fy) / 2);
        }

        internal void EnsureVisibleArrow(Vector fallback)
        {
            Vector offset = Tip - Center;
            double minimum = Radius + Math.Max(28, Thickness * 8);
            if (offset.Length >= minimum) return;
            if (offset.Length < 1) offset = fallback.Length >= 1 ? fallback : new Vector(1, -1);
            offset.Normalize();
            Tip = Center + offset * minimum;
        }

        // 0 = 화살촉, 1 = 번호 원
        internal override IReadOnlyList<Point> Handles() => new[] { Tip, Center };

        internal override void DragHandle(int index, Point p)
        {
            if (index == 0) Tip = p; else Center = p;
        }

        internal override bool HitTest(Point p)
        {
            if ((p - Center).Length <= Radius + 4) return true;

            // 화살표 몸통과의 거리
            Vector ab = Tip - Center, ap = p - Center;
            double len2 = ab.LengthSquared;
            double t = len2 <= 0 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / len2, 0, 1);
            return (p - (Center + ab * t)).Length <= Math.Max(8, Thickness * 1.5);
        }

        protected override Annotation CloneCore() => new NumberArrowAnnotation
        {
            Tip = Tip, Center = Center, Number = Number, Radius = Radius,
            Color = Color, Thickness = Thickness, Opacity = Opacity, Shadow = Shadow
        };

        /// <summary>번호처럼 원판·화살표만 어둡게. 복제 트릭은 숫자 대비색이 뒤집힌다.</summary>
        internal override void RenderShadow(DrawingContext dc, BitmapSource source)
        {
            var brush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            brush.Freeze();
            double off = Math.Max(2.5, Radius * 0.12);
            var d = new Vector(off, off);

            Vector v = Tip - Center;
            if (v.Length > Radius + 2)
            {
                var pen = new Pen(brush, Math.Max(2, Thickness * 1.2));
                pen.Freeze();
                Vector unit = v; unit.Normalize();
                dc.DrawLine(pen, Center + unit * Radius + d, Tip + d);
            }
            dc.DrawEllipse(brush, null, Center + d, Radius, Radius);
        }

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            var fill = new SolidColorBrush(Color);
            fill.Freeze();

            // 화살표: 원 가장자리에서 촉까지. 촉이 원에 파묻히면 안 그린다.
            Vector v = Tip - Center;
            double len = v.Length;
            if (len > Radius + 4)
            {
                Vector unit = v; unit.Normalize();
                Point from = Center + unit * Radius;

                double head = Math.Max(3.2, Thickness * 3.2);
                head = Math.Min(head, (len - Radius) * 0.6);

                var pen = new Pen(fill, Math.Max(2, Thickness))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();
                dc.DrawLine(pen, from, Tip - unit * (head * 0.72));

                var normal = new Vector(-unit.Y, unit.X);
                Point p1 = Tip - unit * head + normal * (head * 0.42);
                Point p2 = Tip - unit * head - normal * (head * 0.42);

                var geo = new StreamGeometry();
                using (StreamGeometryContext g = geo.Open())
                {
                    g.BeginFigure(Tip, isFilled: true, isClosed: true);
                    g.LineTo(p1, true, false);
                    g.LineTo(p2, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);
            }

            // 번호 원: CounterAnnotation 과 같은 대비 규칙(밝은 색이면 검은 숫자).
            double luma = (0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B) / 255.0;
            var ink = new SolidColorBrush(luma > 0.6 ? Colors.Black : Colors.White);
            ink.Freeze();

            var edge = new Pen(ink, Math.Max(1.5, Radius / 8));
            edge.Freeze();
            dc.DrawEllipse(fill, edge, Center, Radius, Radius);

            var ft = new FormattedText(
                Number.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
                             FontStretches.Normal),
                Radius * 1.25, ink, 96);
            dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
        }
    }

    /// <summary>
    /// 강조(스포트라이트): 지정한 영역만 원래 밝기로 남기고 밖을 어둡게 덮는다.
    /// 스크린샷에서 "여기를 보라" 는 가장 확실한 방법이다. 투명도 슬라이더가
    /// 그대로 어둡기 조절이 된다(낮추면 덜 어두워진다).
    /// </summary>
    internal sealed class SpotlightAnnotation : Annotation
    {
        internal Point Start { get; set; }
        internal Point End { get; set; }

        /// <summary>밝힐 영역의 모양. 사각형·타원·갤러리 도형.</summary>
        internal ToolKind Shape { get; set; } = ToolKind.Rectangle;

        /// <summary>옛 이름. 타원인지 여부로만 남겨 둔다(프로젝트 파일 1판 호환).</summary>
        internal bool Ellipse
        {
            get => Shape == ToolKind.Ellipse;
            set => Shape = value ? ToolKind.Ellipse : ToolKind.Rectangle;
        }

        internal override Rect Bounds => new(
            Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

        internal override void Move(Vector d) { Start += d; End += d; }

        internal override void Flip(bool horizontal, double w, double h)
        {
            Start = MapFlip(Start, horizontal, w, h);
            End = MapFlip(End, horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Start = map(Start);
            End = map(End);
        }

        internal override IReadOnlyList<Point> Handles() => RectHandles(Bounds);

        internal override void DragHandle(int index, Point p)
            => (Start, End) = ResizeRect(Start, End, index, p);

        internal override bool SupportsShadow => false;

        protected override Annotation CloneCore() => new SpotlightAnnotation
        {
            Start = Start, End = End, Shape = Shape,
            Color = Color, Thickness = Thickness, Opacity = Opacity
        };

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            Rect b = Bounds;
            if (b.Width < 1 || b.Height < 1) return;

            Geometry hole = Shape == ToolKind.Rectangle
                ? new RectangleGeometry(b, 3, 3)
                : MaskShapes.Outline(Shape, b);

            var outside = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)),
                hole);
            outside.Freeze();

            var dim = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
            dim.Freeze();
            dc.DrawGeometry(dim, null, outside);
        }
    }

    internal enum MagnifierPart { None, Display, Source }

    /// <summary>
    /// 돋보기: 원본의 한 곳을 동그란 창에 확대해 보여 준다. 어디를 확대했는지
    /// 가는 선과 작은 원으로 이어 준다. 세부를 짚어 설명하는 캡처의 단골 도구.
    /// </summary>
    internal sealed class MagnifierAnnotation : Annotation
    {
        internal Point SourceCenter { get; set; }
        internal Point Center { get; set; }

        /// <summary>원본에서 집는 반지름(이미지 픽셀).</summary>
        internal double Radius { get; set; } = 45;
        internal double DisplayRadius { get; set; } = 90;
        internal double Zoom
        {
            get => DisplayRadius / Math.Max(1, Radius);
            set => DisplayRadius = Math.Max(0.1, Radius * value);
        }

        internal override Rect Bounds => new(
            Center.X - DisplayRadius, Center.Y - DisplayRadius,
            DisplayRadius * 2, DisplayRadius * 2);

        /// <summary>옮기면 확대 창만 움직인다. 확대 대상은 그 자리에 있어야 한다.</summary>
        internal override void Move(Vector d) => Center += d;

        internal override void Flip(bool horizontal, double w, double h)
        {
            Center = MapFlip(Center, horizontal, w, h);
            SourceCenter = MapFlip(SourceCenter, horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Center = map(Center);
            SourceCenter = map(SourceCenter);
        }

        internal override void Scale(double fx, double fy)
        {
            base.Scale(fx, fy);
            Radius = Math.Max(8, Radius * (fx + fy) / 2);
            DisplayRadius = Math.Max(8, DisplayRadius * (fx + fy) / 2);
        }

        // 0 = 확대 창 크기, 1 = 원본 중심, 2 = 원본 범위 크기 (서로 독립)
        internal override IReadOnlyList<Point> Handles()
            => new[] { new Point(Center.X + DisplayRadius, Center.Y), SourceCenter,
                       new Point(SourceCenter.X + Radius, SourceCenter.Y) };

        internal override void DragHandle(int index, Point p)
        {
            if (index == 0) DisplayRadius = Math.Max(8, (p - Center).Length);
            else if (index == 1) SourceCenter = p;
            else if (index == 2) Radius = Math.Max(8, (p - SourceCenter).Length);
        }

        internal MagnifierPart PartAt(Point p, double tolerance = 4)
        {
            // The displayed lens is painted above the source circle when they overlap.
            if ((p - Center).Length <= DisplayRadius + tolerance) return MagnifierPart.Display;
            if ((p - SourceCenter).Length <= Radius + tolerance) return MagnifierPart.Source;
            return MagnifierPart.None;
        }

        internal override bool HitTest(Point p) => PartAt(p) != MagnifierPart.None;

        protected override Annotation CloneCore() => new MagnifierAnnotation
        {
            SourceCenter = SourceCenter, Center = Center, Radius = Radius, DisplayRadius = DisplayRadius,
            Color = Color, Thickness = Thickness, Opacity = Opacity, Shadow = Shadow
        };

        /// <summary>복제해 검게 그리면 확대 그림이 그대로 나온다. 원판 그림자만 깐다.</summary>
        internal override void RenderShadow(DrawingContext dc, BitmapSource source)
        {
            var brush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            brush.Freeze();
            double off = Math.Max(3, DisplayRadius * 0.05);
            dc.DrawEllipse(brush, null, new Point(Center.X + off, Center.Y + off),
                           DisplayRadius, DisplayRadius);
        }

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            double r = DisplayRadius;
            if (r < 4) return;

            var edge = new SolidColorBrush(Color);
            edge.Freeze();

            // 어디를 확대했는지: 원본 자리 작은 원 + 확대 창까지 잇는 가는 선
            var thin = new Pen(edge, Math.Max(1.2, Thickness * 0.45));
            thin.Freeze();
            dc.DrawEllipse(null, thin, SourceCenter, Radius, Radius);

            Vector to = Center - SourceCenter;
            if (to.Length > Radius + r)
            {
                Vector unit = to; unit.Normalize();
                dc.DrawLine(thin, SourceCenter + unit * Radius, Center - unit * r);
            }

            // 확대 창: 원 안에만 원본을 Zoom 배로 깔되, 잡은 곳이 창 가운데 오게 민다.
            var clip = new EllipseGeometry(Center, r, r);
            clip.Freeze();
            dc.PushClip(clip);
            var tl = new Point(Center.X - SourceCenter.X * Zoom, Center.Y - SourceCenter.Y * Zoom);
            dc.DrawImage(source, new Rect(tl.X, tl.Y,
                                          source.PixelWidth * Zoom, source.PixelHeight * Zoom));
            dc.Pop();

            var ring = new Pen(edge, Math.Max(2, Thickness));
            ring.Freeze();
            dc.DrawEllipse(null, ring, Center, r, r);
        }
    }

    /// <summary>공유용 꾸밈 설정. 전부 기본값이면 아무것도 안 한다.</summary>
    internal sealed class ExportDecor
    {
        internal int Padding { get; set; }
        internal double CornerRadius { get; set; }
        internal bool Shadow { get; set; }
        internal Color? Background { get; set; }
        internal bool GradientBackground { get; set; }
        internal string? Watermark { get; set; }
        internal double WatermarkOpacity { get; set; } = 0.5;

        internal bool IsPlain => Padding <= 0 && CornerRadius <= 0 && !Shadow &&
                                 Background == null && string.IsNullOrWhiteSpace(Watermark);
    }

    internal static class AnnotationRenderer
    {
        /// <summary>주석들을 이미지 픽셀 좌표계에 그린다.</summary>
        /// <summary>
        /// 주석 하나를 그린다. <b>편집 화면과 최종 결과가 반드시 같은 길을 타야 한다.</b>
        /// 예전에는 화면 쪽이 Render 를 직접 불러서, 투명도가 결과물에만 먹고
        /// 편집 중에는 안 보였다.
        /// </summary>
        internal static void Draw(DrawingContext dc, Annotation a, BitmapSource source)
        {
            if (!a.Visible) return;

            bool fade = a.SupportsOpacity && a.Opacity < 0.999;
            if (fade) dc.PushOpacity(Math.Clamp(a.Opacity, 0, 1));

            // 자유 회전: 그리는 코드는 눕기 전 좌표 그대로 두고, 여기서 통째로 돌린다.
            bool rot = Math.Abs(a.RotationDeg) > 0.01;
            if (rot)
            {
                Point c = a.RotationCenter;
                dc.PushTransform(new RotateTransform(a.RotationDeg, c.X, c.Y));
            }

            if (a.Shadow && a.SupportsShadow) a.RenderShadow(dc, source);
            a.Render(dc, source);

            if (rot) dc.Pop();
            if (fade) dc.Pop();
        }

        /// <summary>
        /// 주석들을 차례로 그리되, <b>뒤에 온 지우개 자국은 구멍으로 뚫어 준다</b>.
        /// 주석마다 "나보다 나중에 지운 자리"를 잘라 낸 클립을 씌우고 그린다.
        /// 이래야 선 하나를 통째로 날리지 않고 닿은 부분만 지울 수 있다.
        /// </summary>
        internal static void Draw(DrawingContext dc, IEnumerable<Annotation> items, BitmapSource source)
        {
            IReadOnlyList<Annotation> list = items as IReadOnlyList<Annotation> ?? new List<Annotation>(items);

            var layers = new DrawingGroup();
            bool hasLayers = false;
            for (int i = 0; i < list.Count; i++)
            {
                Annotation a = list[i];
                if (!a.Visible || a is EraseAnnotation) continue;

                // Sampling tools see the visible layers beneath them. Reading the original
                // here would undo a mosaic when blur or a magnifier is added on top.
                BitmapSource input = hasLayers && (a is PixelateAnnotation or MagnifierAnnotation ||
                    a.SupportsBlend && a.Blend != BlendMode.Normal)
                    ? Composite(source, layers) : source;
                using (DrawingContext layer = layers.Append())
                {
                    Geometry? hole = HolesAfter(list, i, source);
                    if (hole != null) layer.PushClip(hole);
                    Draw(layer, a, input);
                    if (hole != null) layer.Pop();
                }
                hasLayers = true;
            }
            dc.DrawDrawing(layers);
        }

        /// <summary>
        /// <paramref name="index"/> 뒤에 오는 지우개 자국들을 화면에서 잘라 낸 모양.
        /// 지울 게 없으면 null(= 클립 안 씌움).
        /// </summary>
        private static Geometry? HolesAfter(IReadOnlyList<Annotation> list, int index, BitmapSource source)
        {
            GeometryGroup? holes = null;

            for (int j = index + 1; j < list.Count; j++)
            {
                if (list[j] is not EraseAnnotation e || !e.Visible || e.Points.Count == 0) continue;

                // 대상이 정해진 지우개는 그 주석만 판다(마스크처럼).
                if (e.Target != null && !ReferenceEquals(e.Target, list[index])) continue;

                holes ??= new GeometryGroup { FillRule = FillRule.Nonzero };
                holes.Children.Add(e.Area);
            }

            if (holes == null) return null;

            var canvas = new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            var clipped = new CombinedGeometry(GeometryCombineMode.Exclude, canvas, holes);
            clipped.Freeze();
            return clipped;
        }

        private static BitmapSource Composite(BitmapSource source, Drawing layers)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                dc.DrawDrawing(layers);
            }
            var bitmap = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>원본 해상도 그대로 한 장으로 합친다.</summary>
        internal static BitmapSource Flatten(BitmapSource source, IEnumerable<Annotation> items)
        {
            int w = source.PixelWidth, h = source.PixelHeight;

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new Rect(0, 0, w, h));
                Draw(dc, items, source);
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return WithDpiOf(rtb, source);
        }

        /// <summary>
        /// 픽셀은 그대로 두고 DPI 표시만 원본 것으로. RenderTargetBitmap 은 늘 96 이라,
        /// 192DPI 로 찍힌 파일을 편집해 저장하면 물리 크기 정보가 바뀌어 버렸다.
        /// </summary>
        private static BitmapSource WithDpiOf(BitmapSource rendered, BitmapSource source)
        {
            if (Math.Abs(source.DpiX - 96) < 0.01 && Math.Abs(source.DpiY - 96) < 0.01) return rendered;
            if (source.DpiX <= 0 || source.DpiY <= 0) return rendered;

            int w = rendered.PixelWidth, h = rendered.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            rendered.CopyPixels(px, stride, 0);
            var bmp = BitmapSource.Create(w, h, source.DpiX, source.DpiY, rendered.Format, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// 공유용으로 꾸민다: 여백 · 배경 · 그림자 · 둥근 모서리 · 워터마크.
        /// 아무것도 안 켰으면 원본을 그대로 돌려준다.
        /// </summary>
        internal static BitmapSource Decorate(BitmapSource source, ExportDecor d)
        {
            if (d.IsPlain) return source;

            int pad = Math.Max(0, d.Padding);
            int w = source.PixelWidth + pad * 2, h = source.PixelHeight + pad * 2;
            var imgRect = new Rect(pad, pad, source.PixelWidth, source.PixelHeight);
            double r = Math.Max(0, d.CornerRadius);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                if (d.Background.HasValue)
                {
                    Color c = d.Background.Value;
                    Brush bg;
                    if (d.GradientBackground)
                    {
                        Color lighter = Color.FromArgb(c.A, (byte)Math.Min(255, c.R + 60),
                                                       (byte)Math.Min(255, c.G + 60), (byte)Math.Min(255, c.B + 60));
                        bg = new LinearGradientBrush(lighter, c, 45);
                    }
                    else bg = new SolidColorBrush(c);
                    bg.Freeze();
                    dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                }

                if (d.Shadow)
                {
                    // 여러 겹의 반투명 둥근 네모로 부드러운 그림자를 흉내 낸다(효과 객체 없이).
                    for (int i = 6; i >= 1; i--)
                    {
                        var sb = new SolidColorBrush(Color.FromArgb((byte)(10 + (6 - i) * 6), 0, 0, 0));
                        sb.Freeze();
                        Rect sr = imgRect;
                        sr.Inflate(i * 1.5, i * 1.5);
                        sr.Offset(i * 0.8, i * 1.4);
                        dc.DrawRoundedRectangle(sb, null, sr, r + i, r + i);
                    }
                }

                if (r > 0)
                {
                    var clip = new RectangleGeometry(imgRect, r, r);
                    clip.Freeze();
                    dc.PushClip(clip);
                    dc.DrawImage(source, imgRect);
                    dc.Pop();
                }
                else dc.DrawImage(source, imgRect);

                if (!string.IsNullOrWhiteSpace(d.Watermark))
                {
                    double size = Math.Max(12, Math.Min(source.PixelWidth, source.PixelHeight) * 0.05);
                    var ink = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(d.WatermarkOpacity, 0.05, 1)), 30, 30, 34));
                    ink.Freeze();
                    var ft = new FormattedText(d.Watermark.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                        size, ink, 96);
                    var at = new Point(imgRect.Right - ft.Width - size * 0.6, imgRect.Bottom - ft.Height - size * 0.4);
                    var halo = new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * Math.Clamp(d.WatermarkOpacity, 0.05, 1)), 255, 255, 255)),
                                       Math.Max(1.5, size / 10)) { LineJoin = PenLineJoin.Round };
                    halo.Freeze();
                    dc.DrawGeometry(null, halo, ft.BuildGeometry(at));
                    dc.DrawText(ft, at);
                }
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return WithDpiOf(rtb, source);
        }

        /// <summary>이미지를 90도 돌린다.</summary>
        internal static BitmapSource Rotate90(BitmapSource source, bool clockwise)
        {
            var t = new TransformedBitmap(source, new RotateTransform(clockwise ? 90 : -90));
            t.Freeze();
            return t;
        }

        /// <summary>이미지 크기를 바꾼다. 주석은 부르는 쪽에서 같은 비율로 맞춘다.</summary>
        internal static BitmapSource Resize(BitmapSource source, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var t = new TransformedBitmap(source,
                new ScaleTransform((double)width / source.PixelWidth,
                                   (double)height / source.PixelHeight));
            t.Freeze();
            return t;
        }

        /// <summary>
        /// 보간 방식을 골라 크기를 바꾼다. 도트·UI 캡처를 정수 배로 키울 때는
        /// 최근접이어야 픽셀이 안 뭉개지고, 사진은 고품질 보간이 자연스럽다.
        /// </summary>
        internal static BitmapSource Resize(BitmapSource source, int width, int height,
                                            BitmapScalingMode scaling)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            // 확대일 때 RenderTargetBitmap 은 NearestNeighbor 를 무시하고 부드럽게 늘린다
            // (오프스크린 렌더러의 동작). 도트·UI 캡처를 정수 배로 키울 때 픽셀이 뭉개지면
            // 안 되므로 최근접은 픽셀 복사로 직접 한다.
            if (scaling == BitmapScalingMode.NearestNeighbor)
                return ResizeNearest(source, width, height);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
                dc.DrawImage(source, new Rect(0, 0, width, height));

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return WithDpiOf(rtb, source);
        }

        /// <summary>최근접 확대·축소 — 결과 픽셀마다 원본 픽셀 하나를 그대로 가져온다.</summary>
        private static BitmapSource ResizeNearest(BitmapSource source, int width, int height)
        {
            BitmapSource src = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int sw = src.PixelWidth, sh = src.PixelHeight;
            var s = new byte[sw * sh * 4];
            src.CopyPixels(s, sw * 4, 0);

            var d = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int sy = Math.Min(sh - 1, y * sh / height);
                int srow = sy * sw * 4, drow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int sx = Math.Min(sw - 1, x * sw / width);
                    int si = srow + sx * 4, di = drow + x * 4;
                    d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = s[si + 3];
                }
            }

            var bmp = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
                                          PixelFormats.Bgra32, null, d, width * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// 캔버스에 여백을 붙인다 — 그림은 <b>그대로</b> 두고 공간만 늘린다.
        /// 크기 조절(Resize)은 그림을 늘리지만, 이건 주석 달 자리·여백이 필요할 때 쓴다.
        /// <paramref name="fill"/> 이 null 이면 투명(PNG 로 저장하면 뚫린 채로 남는다).
        /// </summary>
        internal static BitmapSource Expand(BitmapSource source, int left, int top,
                                            int right, int bottom, Color? fill)
        {
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Max(0, right); bottom = Math.Max(0, bottom);

            int w = source.PixelWidth + left + right;
            int h = source.PixelHeight + top + bottom;

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                if (fill.HasValue)
                {
                    var brush = new SolidColorBrush(fill.Value);
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
                }
                dc.DrawImage(source, new Rect(left, top, source.PixelWidth, source.PixelHeight));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>이미지를 좌우/상하로 뒤집는다.</summary>
        internal static BitmapSource FlipImage(BitmapSource source, bool horizontal)
        {
            var t = new TransformedBitmap(source,
                horizontal ? new ScaleTransform(-1, 1) : new ScaleTransform(1, -1));
            t.Freeze();
            return t;
        }
    }
}
