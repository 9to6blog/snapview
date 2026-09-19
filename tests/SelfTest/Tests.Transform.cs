using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 회전 · 크기 조절 · 붙여넣기 · 텍스트 서식.

internal static partial class SelfTest
{
    private static void TestRotate()
    {
        Section("90도 회전");

        const double W = 100, H = 40;

        // 시계 방향이면 (x,y) -> (H-y, x). 왼쪽 위 점은 오른쪽 위로 간다.
        var dot = new CounterAnnotation { Center = new Point(0, 0), Number = 1, Radius = 10 };
        dot.Rotate90(true, W, H);
        Check("시계 방향: 좌상단 → 우상단", Close(dot.Center, new Point(H, 0)), dot.Center.ToString());

        var dot2 = new CounterAnnotation { Center = new Point(0, 0), Number = 1, Radius = 10 };
        dot2.Rotate90(false, W, H);
        Check("반시계: 좌상단 → 좌하단", Close(dot2.Center, new Point(0, W)), dot2.Center.ToString());

        // 네 번 돌리면 제자리
        var box = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 5), End = new Point(30, 25)
        };
        double w = W, h = H;
        for (int i = 0; i < 4; i++)
        {
            box.Rotate90(true, w, h);
            (w, h) = (h, w);
        }
        Check("네 번 돌리면 제자리", Close(box.Start, new Point(10, 5)) && Close(box.End, new Point(30, 25)),
              $"{box.Start} {box.End}");

        // 글자는 자리만 옮기고 눕지 않는다.
        var text = new TextAnnotation { Origin = new Point(5, 5), Text = "가나다", FontSize = 20 };
        double before = text.Bounds.Width;
        text.Rotate90(true, W, H);
        Check("글자는 눕지 않는다(폭 유지)", Math.Abs(text.Bounds.Width - before) < 0.01);

        // 자유 곡선의 모든 점이 따라간다.
        var path = new PathAnnotation();
        path.Points.Add(new Point(0, 0));
        path.Points.Add(new Point(10, 20));
        path.Rotate90(true, W, H);
        Check("자유 곡선도 전부 돌아간다",
              Close(path.Points[0], new Point(H, 0)) && Close(path.Points[1], new Point(H - 20, 10)),
              $"{path.Points[0]} {path.Points[1]}");

        // 이미지도 가로세로가 뒤바뀐다.
        BitmapSource img = Solid(30, 10, Colors.Red);
        BitmapSource turned = AnnotationRenderer.Rotate90(img, true);
        Check("이미지의 가로세로가 뒤바뀐다",
              turned.PixelWidth == 10 && turned.PixelHeight == 30,
              $"{turned.PixelWidth}x{turned.PixelHeight}");
    }

    private static void TestResize()
    {
        Section("캔버스 크기 조절");

        BitmapSource img = Solid(100, 50, Colors.Blue);
        BitmapSource half = AnnotationRenderer.Resize(img, 50, 25);
        Check("이미지 크기가 바뀐다", half.PixelWidth == 50 && half.PixelHeight == 25,
              $"{half.PixelWidth}x{half.PixelHeight}");
        Check("0 이하를 넣어도 최소 1", AnnotationRenderer.Resize(img, 0, 0).PixelWidth >= 1);

        // 주석도 같은 비율로 따라간다.
        var box = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 10), End = new Point(50, 30), Thickness = 4
        };
        box.Scale(0.5, 0.5);
        Check("주석 좌표가 절반", Close(box.Start, new Point(5, 5)) && Close(box.End, new Point(25, 15)),
              $"{box.Start} {box.End}");
        Check("굵기도 절반", Math.Abs(box.Thickness - 2) < 0.01, box.Thickness.ToString("0.##"));

        var text = new TextAnnotation { Origin = new Point(20, 20), Text = "글", FontSize = 40 };
        text.Scale(0.5, 0.5);
        Check("글자 크기도 따라 줄어든다", Math.Abs(text.FontSize - 20) < 0.01, text.FontSize.ToString("0.#"));

        var mask = new PixelateAnnotation
        {
            Start = new Point(0, 0), End = new Point(40, 40), Strength = 20
        };
        mask.Scale(0.5, 0.5);
        Check("모자이크 블록도 따라 줄어든다", mask.Strength == 10, mask.Strength.ToString());
        Check("블록이 2 밑으로는 안 내려간다",
              Run(() => { var m = new PixelateAnnotation { Strength = 3 }; m.Scale(0.1, 0.1); return m.Strength; }) >= 2);
    }

    private static void TestTextStyle()
    {
        Section("텍스트 굵게 · 기울임");

        var plain = new TextAnnotation { Origin = new Point(0, 0), Text = "Hello", FontSize = 40, Bold = false };
        var bold = new TextAnnotation { Origin = new Point(0, 0), Text = "Hello", FontSize = 40, Bold = true };
        var italic = new TextAnnotation { Origin = new Point(0, 0), Text = "Hello", FontSize = 40, Bold = false, Italic = true };

        Check("굵게 하면 폭이 넓어진다", bold.Bounds.Width > plain.Bounds.Width,
              $"보통={plain.Bounds.Width:0.#} 굵게={bold.Bounds.Width:0.#}");
        Check("기울임도 그려진다", italic.Bounds.Width > 0);

        var clone = (TextAnnotation)italic.Clone();
        Check("복제가 기울임을 지킨다", clone.Italic && !clone.Bold);
        Check("기본은 굵게", new TextAnnotation().Bold);
    }

    private static void TestPastedImage()
    {
        Section("붙여넣은 그림");

        BitmapSource red = Solid(20, 10, Colors.Red);
        var placed = new ImageAnnotation { Image = red };
        placed.PlaceAt(new Point(5, 5));

        Check("원본 크기로 놓인다",
              Math.Abs(placed.Bounds.Width - 20) < 0.01 && Math.Abs(placed.Bounds.Height - 10) < 0.01,
              $"{placed.Bounds.Width}x{placed.Bounds.Height}");
        Check("조절점 8개로 크기를 바꿀 수 있다", placed.Handles().Count == 8);

        placed.Move(new Vector(3, 4));
        Check("옮길 수 있다", Close(new Point(placed.Bounds.X, placed.Bounds.Y), new Point(8, 9)));

        // 흰 바탕에 얹으면 그 자리가 빨개져야 한다.
        BitmapSource white = Solid(40, 40, Colors.White);
        var stamp = new ImageAnnotation { Image = red };
        stamp.PlaceAt(new Point(10, 10));
        Color at = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { stamp }), 15, 12);
        Check("실제로 그려진다", at.R > 180 && at.G < 90, at.ToString());

        var copy = (ImageAnnotation)stamp.Clone();
        Check("복제가 그림을 지킨다", ReferenceEquals(copy.Image, red));

        // 회전에도 따라간다.
        stamp.Rotate90(true, 40, 40);
        Check("회전에도 따라간다", stamp.Bounds.Width > 0 && stamp.Bounds.Height > 0);
    }

    private static int Run(Func<int> f) => f();

    private static bool Close(Point a, Point b)
        => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
}
