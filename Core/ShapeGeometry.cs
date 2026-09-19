using System;
using System.Windows;
using System.Windows.Media;

namespace SnapView.Core
{
    /// <summary>
    /// 도형 하나를 <b>주어진 사각형에 꽉 맞춰</b> 그린다.
    ///
    /// 모든 도형이 Bounds 하나로 정해지기 때문에 조절점·이동·뒤집기는
    /// 사각형류와 똑같이 굴러간다. 새 도형을 넣으려면 여기 한 곳만 손대면 된다.
    /// </summary>
    internal static class ShapeGeometry
    {
        /// <summary>이 도구가 사각형 하나로 정해지는 도형인가(선·화살표는 아니다).</summary>
        internal static bool IsBoxShape(ToolKind kind) => kind switch
        {
            ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.RoundRect
                or ToolKind.Triangle or ToolKind.RightTriangle or ToolKind.Diamond
                or ToolKind.Pentagon or ToolKind.Hexagon or ToolKind.Heptagon or ToolKind.Octagon
                or ToolKind.Star4 or ToolKind.Star5 or ToolKind.Star6 or ToolKind.Star8
                or ToolKind.Callout or ToolKind.CalloutRound
                or ToolKind.Parallelogram or ToolKind.Trapezoid or ToolKind.Chevron
                or ToolKind.BlockArrow or ToolKind.BlockArrowLeft
                or ToolKind.BlockArrowUp or ToolKind.BlockArrowDown
                or ToolKind.Cross or ToolKind.Heart or ToolKind.Lightning
                or ToolKind.Moon or ToolKind.Shield or ToolKind.Cloud => true,
            _ => false
        };

        /// <summary>사람이 읽는 이름. 도구 설명에 쓴다.</summary>
        internal static string NameOf(ToolKind kind) => kind switch
        {
            ToolKind.Rectangle => "사각형",
            ToolKind.Ellipse => "타원",
            ToolKind.Triangle => "삼각형",
            ToolKind.RightTriangle => "직각삼각형",
            ToolKind.Diamond => "마름모",
            ToolKind.Pentagon => "오각형",
            ToolKind.Hexagon => "육각형",
            ToolKind.Heptagon => "칠각형",
            ToolKind.Octagon => "팔각형",
            ToolKind.Star4 => "4각별",
            ToolKind.Star5 => "5각별",
            ToolKind.Star6 => "6각별",
            ToolKind.Star8 => "8각별",
            ToolKind.Callout => "말풍선",
            ToolKind.CalloutRound => "둥근 말풍선",
            ToolKind.RoundRect => "둥근 사각형",
            ToolKind.Parallelogram => "평행사변형",
            ToolKind.Trapezoid => "사다리꼴",
            ToolKind.Chevron => "꺾쇠",
            ToolKind.BlockArrow => "블록 화살표 →",
            ToolKind.BlockArrowLeft => "블록 화살표 ←",
            ToolKind.BlockArrowUp => "블록 화살표 ↑",
            ToolKind.BlockArrowDown => "블록 화살표 ↓",
            ToolKind.Cross => "십자",
            ToolKind.Heart => "하트",
            ToolKind.Lightning => "번개",
            ToolKind.Moon => "달",
            ToolKind.Shield => "방패",
            ToolKind.Cloud => "구름",
            _ => kind.ToString()
        };

        /// <summary>리본에 늘 보이는 도형들. 나머지는 "더보기" 안에 있다.</summary>
        internal static readonly ToolKind[] Common =
        {
            ToolKind.Rectangle, ToolKind.RoundRect, ToolKind.Ellipse, ToolKind.Triangle,
            ToolKind.Diamond, ToolKind.Pentagon, ToolKind.Hexagon, ToolKind.Star5,
            ToolKind.Callout, ToolKind.BlockArrow
        };

        /// <summary>모든 도형. "더보기" 갤러리가 이 차례로 늘어놓는다.</summary>
        internal static readonly ToolKind[] All =
        {
            ToolKind.Rectangle, ToolKind.RoundRect, ToolKind.Ellipse,
            ToolKind.Triangle, ToolKind.RightTriangle, ToolKind.Diamond,
            ToolKind.Parallelogram, ToolKind.Trapezoid, ToolKind.Chevron,
            ToolKind.Pentagon, ToolKind.Hexagon, ToolKind.Heptagon, ToolKind.Octagon,
            ToolKind.Star4, ToolKind.Star5, ToolKind.Star6, ToolKind.Star8,
            ToolKind.Callout, ToolKind.CalloutRound,
            ToolKind.BlockArrow, ToolKind.BlockArrowLeft,
            ToolKind.BlockArrowUp, ToolKind.BlockArrowDown,
            ToolKind.Cross, ToolKind.Heart, ToolKind.Lightning,
            ToolKind.Moon, ToolKind.Shield, ToolKind.Cloud
        };

        /// <summary>
        /// 도형을 만든다. 사각형·타원은 전용 그리기가 더 깔끔해서 여기서 안 만든다.
        /// (모르는 도구면 null — 부르는 쪽이 사각형으로 대신 그린다)
        /// </summary>
        internal static Geometry? Build(ToolKind kind, Rect b)
        {
            if (b.Width <= 0 || b.Height <= 0) return null;

            return kind switch
            {
                ToolKind.Triangle => Polygon(new[]
                {
                    new Point(b.X + b.Width / 2, b.Y),
                    new Point(b.Right, b.Bottom),
                    new Point(b.X, b.Bottom)
                }),

                ToolKind.RightTriangle => Polygon(new[]
                {
                    new Point(b.X, b.Y),
                    new Point(b.Right, b.Bottom),
                    new Point(b.X, b.Bottom)
                }),

                ToolKind.Diamond => Polygon(new[]
                {
                    new Point(b.X + b.Width / 2, b.Y),
                    new Point(b.Right, b.Y + b.Height / 2),
                    new Point(b.X + b.Width / 2, b.Bottom),
                    new Point(b.X, b.Y + b.Height / 2)
                }),

                ToolKind.Pentagon => Regular(b, 5),
                ToolKind.Hexagon => Regular(b, 6),
                ToolKind.Heptagon => Regular(b, 7),
                ToolKind.Octagon => Regular(b, 8),

                ToolKind.Star4 => Star(b, 4, 0.38),
                ToolKind.Star5 => Star(b, 5, 0.45),
                ToolKind.Star6 => Star(b, 6, 0.55),
                ToolKind.Star8 => Star(b, 8, 0.62),

                ToolKind.RoundRect => Rounded(b, Math.Min(b.Width, b.Height) * 0.18),
                ToolKind.Callout => Callout(b, round: false),
                ToolKind.CalloutRound => Callout(b, round: true),

                ToolKind.Parallelogram => Polygon(new[]
                {
                    new Point(b.X + b.Width * 0.22, b.Y),
                    new Point(b.Right, b.Y),
                    new Point(b.Right - b.Width * 0.22, b.Bottom),
                    new Point(b.X, b.Bottom)
                }),

                ToolKind.Trapezoid => Polygon(new[]
                {
                    new Point(b.X + b.Width * 0.22, b.Y),
                    new Point(b.Right - b.Width * 0.22, b.Y),
                    new Point(b.Right, b.Bottom),
                    new Point(b.X, b.Bottom)
                }),

                ToolKind.Chevron => Polygon(new[]
                {
                    new Point(b.X, b.Y),
                    new Point(b.Right - b.Width * 0.3, b.Y),
                    new Point(b.Right, b.Y + b.Height / 2),
                    new Point(b.Right - b.Width * 0.3, b.Bottom),
                    new Point(b.X, b.Bottom),
                    new Point(b.X + b.Width * 0.3, b.Y + b.Height / 2)
                }),

                ToolKind.BlockArrow => BlockArrow(b, 0),
                ToolKind.BlockArrowLeft => BlockArrow(b, 180),
                ToolKind.BlockArrowUp => BlockArrow(b, 270),
                ToolKind.BlockArrowDown => BlockArrow(b, 90),

                ToolKind.Cross => Cross(b),
                ToolKind.Heart => Heart(b),
                ToolKind.Lightning => Lightning(b),
                ToolKind.Moon => Moon(b),
                ToolKind.Shield => Shield(b),
                ToolKind.Cloud => Cloud(b),
                _ => null
            };
        }

        // ---------------------------------------------------------------- 만들기

        private static Geometry Polygon(Point[] points)
        {
            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(points[0], isFilled: true, isClosed: true);
                for (int i = 1; i < points.Length; i++) g.LineTo(points[i], true, false);
            }
            geo.Freeze();
            return geo;
        }

        /// <summary>정n각형. 꼭짓점 하나가 위를 보도록 −90도에서 시작한다.</summary>
        private static Geometry Regular(Rect b, int sides)
        {
            var pts = new Point[sides];
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            double rx = b.Width / 2, ry = b.Height / 2;

            for (int i = 0; i < sides; i++)
            {
                double a = -Math.PI / 2 + i * 2 * Math.PI / sides;
                pts[i] = new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
            }
            return Polygon(pts);
        }

        /// <summary>바깥·안쪽 꼭짓점을 번갈아 찍는 별.</summary>
        private static Geometry Star(Rect b, int points, double innerRatio)
        {
            var pts = new Point[points * 2];
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            double rx = b.Width / 2, ry = b.Height / 2;

            for (int i = 0; i < points * 2; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / points;
                double f = i % 2 == 0 ? 1.0 : innerRatio;
                pts[i] = new Point(cx + rx * f * Math.Cos(a), cy + ry * f * Math.Sin(a));
            }
            return Polygon(pts);
        }

        private static Geometry Rounded(Rect b, double radius)
        {
            var geo = new RectangleGeometry(b, radius, radius);
            geo.Freeze();
            return geo;
        }

        /// <summary>네모(또는 타원) 몸통 + 왼쪽 아래로 뻗은 꼬리.</summary>
        private static Geometry Callout(Rect b, bool round)
        {
            double bodyH = b.Height * 0.76;
            var body = new Rect(b.X, b.Y, b.Width, bodyH);
            double r = Math.Min(12, Math.Min(body.Width, body.Height) / 4);

            Geometry rounded = round
                ? new EllipseGeometry(body)
                : new RectangleGeometry(body, r, r);

            double tailX = b.X + b.Width * 0.22;
            var tail = Polygon(new[]
            {
                new Point(tailX, body.Bottom - 1),
                new Point(tailX + b.Width * 0.16, body.Bottom - 1),
                new Point(tailX + b.Width * 0.02, b.Bottom)
            });

            var combined = new GeometryGroup { FillRule = FillRule.Nonzero };
            combined.Children.Add(rounded);
            combined.Children.Add(tail);
            combined.Freeze();
            return combined;
        }

        /// <summary>두꺼운 화살표. <paramref name="degrees"/> 만큼 돌려서 방향을 정한다.</summary>
        private static Geometry BlockArrow(Rect b, double degrees)
        {
            Geometry arrow = BlockArrowRight(b);
            if (degrees == 0) return arrow;

            var turned = arrow.Clone();
            turned.Transform = new RotateTransform(degrees, b.X + b.Width / 2, b.Y + b.Height / 2);

            // 돌린 뒤에도 원래 사각형에 맞도록 다시 담는다.
            var flattened = turned.GetFlattenedPathGeometry();
            Rect fb = flattened.Bounds;
            if (fb.Width > 0 && fb.Height > 0)
            {
                var fit = new TransformGroup();
                fit.Children.Add(new TranslateTransform(-fb.X, -fb.Y));
                fit.Children.Add(new ScaleTransform(b.Width / fb.Width, b.Height / fb.Height));
                fit.Children.Add(new TranslateTransform(b.X, b.Y));
                flattened.Transform = fit;
            }
            flattened.Freeze();
            return flattened;
        }

        private static Geometry BlockArrowRight(Rect b)
        {
            double headW = b.Width * 0.4;
            double shaftTop = b.Y + b.Height * 0.28;
            double shaftBottom = b.Bottom - b.Height * 0.28;
            double headLeft = b.Right - headW;

            return Polygon(new[]
            {
                new Point(b.X, shaftTop),
                new Point(headLeft, shaftTop),
                new Point(headLeft, b.Y),
                new Point(b.Right, b.Y + b.Height / 2),
                new Point(headLeft, b.Bottom),
                new Point(headLeft, shaftBottom),
                new Point(b.X, shaftBottom)
            });
        }

        private static Geometry Cross(Rect b)
        {
            double aw = b.Width / 3, ah = b.Height / 3;
            double l = b.X, t = b.Y, r = b.Right, bo = b.Bottom;

            return Polygon(new[]
            {
                new Point(l + aw, t),
                new Point(r - aw, t),
                new Point(r - aw, t + ah),
                new Point(r, t + ah),
                new Point(r, bo - ah),
                new Point(r - aw, bo - ah),
                new Point(r - aw, bo),
                new Point(l + aw, bo),
                new Point(l + aw, bo - ah),
                new Point(l, bo - ah),
                new Point(l, t + ah),
                new Point(l + aw, t + ah)
            });
        }

        /// <summary>번개. 위에서 꺾여 내려오는 지그재그.</summary>
        private static Geometry Lightning(Rect b)
        {
            double w = b.Width, h = b.Height;
            return Polygon(new[]
            {
                new Point(b.X + w * 0.55, b.Y),
                new Point(b.X + w * 0.18, b.Y + h * 0.55),
                new Point(b.X + w * 0.45, b.Y + h * 0.55),
                new Point(b.X + w * 0.32, b.Bottom),
                new Point(b.X + w * 0.85, b.Y + h * 0.40),
                new Point(b.X + w * 0.55, b.Y + h * 0.40)
            });
        }

        /// <summary>초승달. 큰 원에서 살짝 옆으로 민 원을 파낸다.</summary>
        private static Geometry Moon(Rect b)
        {
            var outer = new EllipseGeometry(new Point(b.X + b.Width / 2, b.Y + b.Height / 2),
                                            b.Width / 2, b.Height / 2);
            var bite = new EllipseGeometry(new Point(b.X + b.Width * 0.72, b.Y + b.Height * 0.42),
                                           b.Width * 0.42, b.Height * 0.46);

            var moon = new CombinedGeometry(GeometryCombineMode.Exclude, outer, bite);
            moon.Freeze();
            return moon;
        }

        /// <summary>방패. 위는 네모, 아래는 뾰족하게 모인다.</summary>
        private static Geometry Shield(Rect b)
        {
            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(new Point(b.X, b.Y + b.Height * 0.08), isFilled: true, isClosed: true);
                g.LineTo(new Point(b.Right, b.Y + b.Height * 0.08), true, false);
                g.LineTo(new Point(b.Right, b.Y + b.Height * 0.5), true, false);
                g.BezierTo(new Point(b.Right, b.Y + b.Height * 0.78),
                           new Point(b.X + b.Width * 0.72, b.Y + b.Height * 0.94),
                           new Point(b.X + b.Width / 2, b.Bottom), true, false);
                g.BezierTo(new Point(b.X + b.Width * 0.28, b.Y + b.Height * 0.94),
                           new Point(b.X, b.Y + b.Height * 0.78),
                           new Point(b.X, b.Y + b.Height * 0.5), true, false);
            }
            geo.Freeze();
            return geo;
        }

        /// <summary>구름. 크기가 다른 원 넷을 겹친다.</summary>
        private static Geometry Cloud(Rect b)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(new EllipseGeometry(
                new Point(b.X + b.Width * 0.30, b.Y + b.Height * 0.62), b.Width * 0.26, b.Height * 0.30));
            group.Children.Add(new EllipseGeometry(
                new Point(b.X + b.Width * 0.50, b.Y + b.Height * 0.42), b.Width * 0.30, b.Height * 0.36));
            group.Children.Add(new EllipseGeometry(
                new Point(b.X + b.Width * 0.72, b.Y + b.Height * 0.60), b.Width * 0.26, b.Height * 0.30));
            group.Children.Add(new RectangleGeometry(new Rect(
                b.X + b.Width * 0.28, b.Y + b.Height * 0.58, b.Width * 0.46, b.Height * 0.34)));
            group.Freeze();
            return group;
        }

        /// <summary>위쪽 두 개의 둥근 봉우리에서 아래 꼭짓점으로 모이는 하트.</summary>
        private static Geometry Heart(Rect b)
        {
            double cx = b.X + b.Width / 2;
            double topY = b.Y + b.Height * 0.28;
            double tipY = b.Bottom;

            var geo = new StreamGeometry();
            using (StreamGeometryContext g = geo.Open())
            {
                g.BeginFigure(new Point(cx, tipY), isFilled: true, isClosed: true);

                // 왼쪽 반
                g.BezierTo(new Point(b.X - b.Width * 0.06, b.Y + b.Height * 0.62),
                           new Point(b.X + b.Width * 0.03, b.Y),
                           new Point(cx, topY), true, false);

                // 오른쪽 반
                g.BezierTo(new Point(b.Right - b.Width * 0.03, b.Y),
                           new Point(b.Right + b.Width * 0.06, b.Y + b.Height * 0.62),
                           new Point(cx, tipY), true, false);
            }
            geo.Freeze();
            return geo;
        }
    }
}
