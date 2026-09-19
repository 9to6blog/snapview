using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 도형 갤러리 · 숨기기(레이어) · 색 변환.

internal static partial class SelfTest
{
    private static void TestShapeGallery()
    {
        Section("도형 갤러리");

        ToolKind[] shapes = ShapeGeometry.All.Where(k => k != ToolKind.Rectangle &&
                                                          k != ToolKind.Ellipse).ToArray();

        var box = new Rect(10, 20, 100, 60);

        foreach (ToolKind kind in shapes)
        {
            Geometry? geo = ShapeGeometry.Build(kind, box);
            if (geo == null)
            {
                Check($"{ShapeGeometry.NameOf(kind)} 도형이 만들어짐", false, "null");
                continue;
            }

            // 도형은 준 사각형 안에 들어와야 한다. 하트는 곡선 제어점이 살짝 밖으로
            // 나가므로 약간의 여유를 준다.
            Rect b = geo.Bounds;
            bool inside = b.X >= box.X - 8 && b.Y >= box.Y - 8 &&
                          b.Right <= box.Right + 8 && b.Bottom <= box.Bottom + 8;

            Check($"{ShapeGeometry.NameOf(kind)} 가 사각형에 맞춰짐", inside && b.Width > 1 && b.Height > 1,
                  $"{b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#}");
        }

        Check("이름이 모두 채워짐", shapes.All(k => !string.IsNullOrWhiteSpace(ShapeGeometry.NameOf(k))));
        Check("리본 기본 도형은 모두 전체 목록에 있다",
              ShapeGeometry.Common.All(k => ShapeGeometry.All.Contains(k)));
        Check("전체 목록에 중복이 없다",
              ShapeGeometry.All.Distinct().Count() == ShapeGeometry.All.Length);
        Check("선·화살표는 사각형 도형이 아님",
              !ShapeGeometry.IsBoxShape(ToolKind.Line) && !ShapeGeometry.IsBoxShape(ToolKind.Arrow));
        Check("갤러리 도형은 모두 사각형 도형", shapes.All(ShapeGeometry.IsBoxShape));
        Check("크기 0 이면 null", ShapeGeometry.Build(ToolKind.Star5, new Rect(0, 0, 0, 0)) == null);

        // 도형은 조절점 8개(사각형과 같은 취급)여야 끌어서 고칠 수 있다.
        foreach (ToolKind kind in shapes)
        {
            var a = new ShapeAnnotation { Kind = kind, Start = new Point(0, 0), End = new Point(50, 40) };
            Check($"{ShapeGeometry.NameOf(kind)} 조절점 8개", a.Handles().Count == 8);
            Check($"{ShapeGeometry.NameOf(kind)} 는 채울 수 있음", a.CanFill);
        }

        var line = new ShapeAnnotation { Kind = ToolKind.Line, Start = new Point(0, 0), End = new Point(9, 9) };
        Check("선은 채울 수 없음", !line.CanFill);

        // 실제로 픽셀이 찍히는지 — 별을 채워 그리면 가운데가 색으로 덮여야 한다.
        BitmapSource white = Solid(40, 40, Colors.White);
        var star = new ShapeAnnotation
        {
            Kind = ToolKind.Star5, Start = new Point(2, 2), End = new Point(38, 38),
            Filled = true, Color = Colors.Red
        };
        Color center = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { star }), 20, 20);
        Check("별 가운데가 실제로 칠해짐", center.R > 180 && center.G < 90, center.ToString());
    }

    private static void TestLayerVisibility()
    {
        Section("레이어 숨기기");

        BitmapSource white = Solid(20, 20, Colors.White);
        var box = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(2, 2), End = new Point(18, 18),
            Filled = true, Color = Colors.Red
        };

        Color shown = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { box }), 10, 10);
        Check("보일 때는 칠해진다", shown.R > 180 && shown.G < 90, shown.ToString());

        box.Visible = false;
        Color hidden = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { box }), 10, 10);
        Check("숨기면 결과물에도 안 나온다", hidden.R > 250 && hidden.G > 250, hidden.ToString());

        Check("기본값은 보임", new ShapeAnnotation().Visible);
    }

    private static void TestColorConversion()
    {
        Section("색 고르기(HSV)");

        Color[] samples =
        {
            Colors.Red, Colors.Lime, Colors.Blue, Colors.White, Colors.Black,
            Color.FromRgb(226, 59, 59), Color.FromRgb(17, 99, 200), Color.FromRgb(128, 128, 128)
        };

        int bad = 0;
        foreach (Color c in samples)
        {
            (double h, double s, double v) = SnapView.Editor.ColorPicker.ToHsv(c);
            Color back = SnapView.Editor.ColorPicker.FromHsv(h, s, v);

            if (Math.Abs(back.R - c.R) > 1 || Math.Abs(back.G - c.G) > 1 || Math.Abs(back.B - c.B) > 1)
            {
                bad++;
                Console.WriteLine($"         (왕복 실패: {c} -> H{h:0.#} S{s:0.##} V{v:0.##} -> {back})");
            }
        }
        Check("RGB → HSV → RGB 왕복", bad == 0, bad + "개 실패");

        Check("빨강의 색상각은 0", Math.Abs(SnapView.Editor.ColorPicker.ToHsv(Colors.Red).H) < 0.01);
        Check("초록의 색상각은 120", Math.Abs(SnapView.Editor.ColorPicker.ToHsv(Colors.Lime).H - 120) < 0.01);
        Check("흰색은 채도 0", SnapView.Editor.ColorPicker.ToHsv(Colors.White).S < 0.001);
        Check("검정은 명도 0", SnapView.Editor.ColorPicker.ToHsv(Colors.Black).V < 0.001);
        Check("색상각이 한 바퀴 돌아도 같다",
              SnapView.Editor.ColorPicker.FromHsv(360, 1, 1) == SnapView.Editor.ColorPicker.FromHsv(0, 1, 1));
        Check("음수 색상각도 안전",
              SnapView.Editor.ColorPicker.FromHsv(-60, 1, 1) == SnapView.Editor.ColorPicker.FromHsv(300, 1, 1));
        Check("16진 표기", SnapView.Editor.ColorPicker.ToHex(Color.FromRgb(0x12, 0xAB, 0xFF)) == "#12ABFF");
    }

    private static void TestEraserHitTest()
    {
        Section("지우개 판정");

        // 지우개는 HitTest 로 지운다. 선 위는 잡히고, 멀리 떨어진 곳은 안 잡혀야 한다.
        var line = new ShapeAnnotation
        {
            Kind = ToolKind.Line, Start = new Point(0, 0), End = new Point(100, 0), Thickness = 3
        };
        Check("선 위를 지나면 잡힌다", line.HitTest(new Point(50, 0)));
        Check("선 바로 옆도 잡힌다", line.HitTest(new Point(50, 4)));
        Check("멀리 떨어지면 안 잡힌다", !line.HitTest(new Point(50, 60)));

        // 겹쳐 있으면 맨 위(나중에 그린 것)부터 지워져야 한다.
        var items = new List<Annotation>
        {
            new ShapeAnnotation { Kind = ToolKind.Rectangle, Start = new Point(0, 0), End = new Point(50, 50) },
            new ShapeAnnotation { Kind = ToolKind.Ellipse, Start = new Point(0, 0), End = new Point(50, 50) }
        };

        Annotation? top = null;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].HitTest(new Point(25, 25))) { top = items[i]; break; }
        }
        Check("겹치면 맨 위 것이 먼저 잡힌다",
              top is ShapeAnnotation { Kind: ToolKind.Ellipse });

        // 지우개는 주석을 지우는 게 아니라 "지운 자리"를 얹는다.
        // 선 하나에 지우개를 대면 닿은 데만 뚫려야 한다 — 선 전체가 사라지면 안 된다.
        BitmapSource white = Solid(60, 20, Colors.White);
        var bar = new ShapeAnnotation
        {
            Kind = ToolKind.Line, Start = new Point(2, 10), End = new Point(58, 10),
            Color = Colors.Red, Thickness = 7
        };

        var mark = new EraseAnnotation { Radius = 6 };
        mark.Add(new Point(30, 10));

        BitmapSource result = AnnotationRenderer.Flatten(white, new List<Annotation> { bar, mark });

        Color hole = PixelAt(result, 30, 10);
        Color leftEnd = PixelAt(result, 8, 10);
        Color rightEnd = PixelAt(result, 52, 10);

        Check("지우개가 닿은 자리는 뚫린다", hole.R > 240 && hole.G > 240, hole.ToString());
        Check("왼쪽 남은 부분은 그대로", leftEnd.R > 180 && leftEnd.G < 90, leftEnd.ToString());
        Check("오른쪽 남은 부분도 그대로", rightEnd.R > 180 && rightEnd.G < 90, rightEnd.ToString());

        // 지우고 나서 그린 것은 멀쩡해야 한다(그림판과 같은 순서 감각).
        var after = new ShapeAnnotation
        {
            Kind = ToolKind.Line, Start = new Point(2, 10), End = new Point(58, 10),
            Color = Colors.Blue, Thickness = 7
        };
        Color later = PixelAt(AnnotationRenderer.Flatten(white,
            new List<Annotation> { bar, mark, after }), 30, 10);
        Check("지운 뒤에 그린 것은 안 지워진다", later.B > 180 && later.R < 90, later.ToString());

        Check("지우개 자국은 눈에 안 보인다(선택도 안 됨)", !mark.HitTest(new Point(30, 10)));

        var copy = (EraseAnnotation)mark.Clone();
        Check("지우개 자국 복제", copy.Points.Count == 1 && Math.Abs(copy.Radius - 6) < 0.01);

        // 번호 도구: 밝은 색을 고르면 숫자가 검게 나와야 보인다.
        BitmapSource bg = Solid(60, 60, Colors.Gray);
        Color onYellow = PixelAt(AnnotationRenderer.Flatten(bg, new Annotation[]
        {
            new CounterAnnotation { Center = new Point(30, 30), Number = 8, Radius = 24, Color = Colors.Yellow }
        }), 30, 30);
        Check("노란 번호의 숫자는 어둡게", onYellow.R < 120 && onYellow.G < 120, onYellow.ToString());

        Color onNavy = PixelAt(AnnotationRenderer.Flatten(bg, new Annotation[]
        {
            new CounterAnnotation { Center = new Point(30, 30), Number = 8, Radius = 24,
                                    Color = Color.FromRgb(20, 30, 90) }
        }), 30, 30);
        Check("어두운 번호의 숫자는 밝게", onNavy.R > 180 && onNavy.G > 180, onNavy.ToString());
    }
}
