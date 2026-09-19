using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 편집기 개선(2026-09)에서 늘어난 주석 모델 기능.

internal static partial class SelfTest
{
    /// <summary>모자이크·흐림·강조가 사각형 말고 타원·갤러리 도형 모양으로도 된다.</summary>
    private static void TestShapedMasks()
    {
        Section("도형 모양 가리개·강조");

        BitmapSource white = SolidGif(80, 80, Colors.White);

        // 흑백 무늬 위에 타원 모자이크: 타원 밖 모서리는 원본 그대로, 안은 뭉개진다.
        var pattern = new byte[80 * 80 * 4];
        for (int i = 0; i < 80 * 80; i++)
        {
            byte v = (byte)(((i % 80) / 2 + (i / 80) / 2) % 2 == 0 ? 255 : 0);
            pattern[i * 4] = pattern[i * 4 + 1] = pattern[i * 4 + 2] = v;
            pattern[i * 4 + 3] = 255;
        }
        BitmapSource checker = BitmapSource.Create(80, 80, 96, 96, PixelFormats.Bgra32, null, pattern, 320);
        checker.Freeze();

        var ellipseMask = new PixelateAnnotation
        {
            Start = new Point(10, 10), End = new Point(70, 70), Strength = 16, Shape = ToolKind.Ellipse
        };
        BitmapSource masked = AnnotationRenderer.Flatten(checker, new[] { ellipseMask });
        (byte r, _, _) = PixelAtRgb(masked, 12, 12);          // 사각형 안이지만 타원 밖
        (byte c, _, _) = PixelAtRgb(masked, 40, 40);
        Check("타원 밖 모서리는 원본 무늬 그대로(0 또는 255)", r == 0 || r == 255, r.ToString());
        Check("타원 안은 뭉개져 중간값", c > 40 && c < 215, c.ToString());
        Check("타원 밖은 판정에 안 걸린다", !ellipseMask.HitTest(new Point(12, 12)) && ellipseMask.HitTest(new Point(40, 40)));

        var starMask = new PixelateAnnotation
        {
            Start = new Point(0, 0), End = new Point(80, 80), Strength = 8, Shape = ToolKind.Star5, UseBlur = true
        };
        BitmapSource starred = AnnotationRenderer.Flatten(checker, new[] { starMask });
        Check("별 모양 흐림도 예외 없이 그려진다", starred.PixelWidth == 80);

        var spot = new SpotlightAnnotation { Start = new Point(20, 20), End = new Point(60, 60), Shape = ToolKind.Ellipse };
        BitmapSource lit = AnnotationRenderer.Flatten(white, new[] { spot });
        Check("타원 강조: 가운데는 밝다", PixelAtRgb(lit, 40, 40).R > 240);
        Check("타원 강조: 사각형 모서리는 어둡다", PixelAtRgb(lit, 22, 22).R < 160, PixelAtRgb(lit, 22, 22).R.ToString());
        Check("Ellipse 호환 속성이 Shape 를 따른다", spot.Ellipse && !new SpotlightAnnotation { Shape = ToolKind.Diamond }.Ellipse);

        var clone = (PixelateAnnotation)ellipseMask.Clone();
        Check("복제가 모양을 지킨다", clone.Shape == ToolKind.Ellipse);

        // 끌면서 보는 빠른 미리보기도 같은 자리를 덮는다.
        ellipseMask.Fast = true;
        BitmapSource fast = AnnotationRenderer.Flatten(checker, new[] { ellipseMask });
        (byte f, _, _) = PixelAtRgb(fast, 40, 40);
        Check("빠른 미리보기도 가운데를 뭉갠다", f > 40 && f < 215, f.ToString());
    }

    private static void TestTextWrapAlign()
    {
        Section("글자 줄바꿈·정렬");

        var single = new TextAnnotation { Origin = new Point(0, 0), Text = "가나다라마바사아자차카타파하", FontSize = 20 };
        var wrapped = new TextAnnotation { Origin = new Point(0, 0), Text = "가나다라마바사아자차카타파하", FontSize = 20, MaxWidth = 100 };
        Check("폭을 주면 줄이 바뀌어 높이가 는다", wrapped.Bounds.Height > single.Bounds.Height * 1.5,
              $"{single.Bounds.Height:0.#} -> {wrapped.Bounds.Height:0.#}");
        Check("폭을 준 글자의 너비는 그 폭", Math.Abs(wrapped.Bounds.Width - 100) < 0.01, wrapped.Bounds.Width.ToString("0.#"));

        Check("오른쪽 가장자리 조절점 하나", single.Handles().Count == 1);
        single.DragHandle(0, new Point(120, 0));
        Check("조절점을 끌면 폭이 정해진다", Math.Abs(single.MaxWidth - 120) < 0.01, single.MaxWidth.ToString("0.#"));

        // 가운데 정렬: 짧은 글자가 상자 가운데에 그려진다.
        BitmapSource white = SolidGif(200, 60, Colors.White);
        var centered = new TextAnnotation
        {
            Origin = new Point(0, 4), Text = "AB", FontSize = 24, Color = Colors.Black,
            MaxWidth = 200, Align = TextAlign.Center, OutlineHalo = false
        };
        BitmapSource drawn = AnnotationRenderer.Flatten(white, new[] { centered });
        int firstInk = -1, lastInk = -1;
        for (int x = 0; x < 200; x++)
        {
            bool ink = false;
            for (int y = 4; y < 40 && !ink; y++) if (PixelAtRgb(drawn, x, y).R < 128) ink = true;
            if (ink) { if (firstInk < 0) firstInk = x; lastInk = x; }
        }
        Check("가운데 정렬이면 글자가 상자 가운데에", firstInk > 60 && lastInk < 140, $"{firstInk}~{lastInk}");

        var clone = (TextAnnotation)centered.Clone();
        Check("복제가 정렬·폭을 지킨다", clone.Align == TextAlign.Center && Math.Abs(clone.MaxWidth - 200) < 0.01);
        centered.Scale(2, 2);
        Check("크기 조절이 폭도 따라간다", Math.Abs(centered.MaxWidth - 400) < 0.01);
    }

    private static void TestArrowHeadsAndDashes()
    {
        Section("화살촉·점선 종류");

        BitmapSource white = SolidGif(100, 60, Colors.White);
        ShapeAnnotation Arrow(ArrowHead head) => new()
        {
            Kind = ToolKind.Arrow, Start = new Point(10, 30), End = new Point(80, 30),
            Color = Colors.Red, Thickness = 2, Head = head
        };
        // (70,28): 촉 삼각형 안이면서 몸통(굵기 2)과 열린 촉 선 사이의 빈 곳.
        Check("채운 촉은 삼각형 안이 빨강", PixelAtRgb(AnnotationRenderer.Flatten(white, new[] { Arrow(ArrowHead.Filled) }), 70, 28).G < 120);
        Check("열린 촉은 삼각형 안이 비어 있다", PixelAtRgb(AnnotationRenderer.Flatten(white, new[] { Arrow(ArrowHead.Open) }), 70, 28).G > 200);
        BitmapSource dot = AnnotationRenderer.Flatten(white, new[] { Arrow(ArrowHead.Dot) });
        Check("점 촉은 끝에 동그라미", PixelAtRgb(dot, 80, 30).G < 120 && PixelAtRgb(dot, 70, 28).G > 200);
        Check("복제가 촉 모양을 지킨다", ((ShapeAnnotation)Arrow(ArrowHead.Dot).Clone()).Head == ArrowHead.Dot);

        int Segments(DashPattern pattern)
        {
            var line = new ShapeAnnotation
            {
                Kind = ToolKind.Line, Start = new Point(0, 30), End = new Point(100, 30),
                Color = Colors.Red, Thickness = 4, Dashed = true, DashPattern = pattern
            };
            BitmapSource img = AnnotationRenderer.Flatten(white, new[] { line });
            int runs = 0; bool inRun = false;
            for (int x = 0; x < 100; x++)
            {
                bool ink = PixelAtRgb(img, x, 30).G < 128;
                if (ink && !inRun) runs++;
                inRun = ink;
            }
            return runs;
        }
        int dash = Segments(DashPattern.Dash), dots = Segments(DashPattern.Dot);
        Check("점 무늬는 대시보다 조각이 많다", dots > dash && dash >= 3, $"대시 {dash}, 점 {dots}");
        Check("일점쇄선도 그려진다", Segments(DashPattern.DashDot) >= 3);
    }

    private static void TestHighlighterMultiply()
    {
        Section("형광펜 곱하기 합성");

        var hl = new PathAnnotation { Highlighter = true, Color = Colors.Yellow, Thickness = 4, Blend = BlendMode.Multiply };
        hl.Points.AddRange(new[] { new Point(5, 30), new Point(95, 30) });
        Check("형광펜은 혼합을 받는다", hl.SupportsBlend && !new PathAnnotation().SupportsBlend);

        BitmapSource onWhite = AnnotationRenderer.Flatten(SolidGif(100, 60, Colors.White), new[] { hl });
        (byte r, byte g, byte b) = PixelAtRgb(onWhite, 50, 30);
        Check("흰 바탕 위는 선명한 노랑", r > 230 && g > 200 && b < 110, $"{r},{g},{b}");

        BitmapSource onBlack = AnnotationRenderer.Flatten(SolidGif(100, 60, Colors.Black), new[] { hl });
        (byte r2, byte g2, byte b2) = PixelAtRgb(onBlack, 50, 30);
        Check("검은 글자 위는 검게 남는다(글자를 안 덮는다)", r2 < 60 && g2 < 60 && b2 < 60, $"{r2},{g2},{b2}");
    }

    private static void TestFlattenDpi()
    {
        Section("합칠 때 DPI 유지");

        var px = new byte[20 * 20 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = 255;
        BitmapSource hi = BitmapSource.Create(20, 20, 192, 192, PixelFormats.Bgra32, null, px, 80);
        hi.Freeze();
        BitmapSource flat = AnnotationRenderer.Flatten(hi, Array.Empty<Annotation>());
        Check("픽셀 크기는 그대로", flat.PixelWidth == 20 && flat.PixelHeight == 20);
        Check("DPI 가 192 로 남는다", Math.Abs(flat.DpiX - 192) < 0.5, flat.DpiX.ToString("0.#"));
    }

    private static void TestExportDecor()
    {
        Section("예쁘게 내보내기");

        BitmapSource white = SolidGif(60, 40, Colors.White);
        var decor = new ExportDecor { Padding = 20, CornerRadius = 12, Shadow = true, Background = Colors.Blue };
        BitmapSource outImg = AnnotationRenderer.Decorate(white, decor);
        Check("여백만큼 커진다", outImg.PixelWidth == 100 && outImg.PixelHeight == 80, $"{outImg.PixelWidth}×{outImg.PixelHeight}");
        Check("여백은 배경색", PixelAtRgb(outImg, 3, 3).B > 200 && PixelAtRgb(outImg, 3, 3).R < 60);
        Check("가운데는 그림", PixelAtRgb(outImg, 50, 40).R > 240);
        Check("둥근 모서리는 배경이 보인다", PixelAtRgb(outImg, 21, 21).B > 150 && PixelAtRgb(outImg, 21, 21).R < 120,
              PixelAtRgb(outImg, 21, 21).ToString());

        var wm = new ExportDecor { Padding = 0, Watermark = "9to6blog", WatermarkOpacity = 1.0 };
        BitmapSource marked = AnnotationRenderer.Decorate(SolidGif(200, 80, Colors.White), wm);
        bool inkFound = false;
        for (int y = 50; y < 80 && !inkFound; y++)
            for (int x = 100; x < 200 && !inkFound; x++)
                if (PixelAtRgb(marked, x, y).R < 200) inkFound = true;
        Check("워터마크가 오른쪽 아래에 찍힌다", inkFound);

        var none = new ExportDecor();
        Check("아무것도 안 켜면 그대로", AnnotationRenderer.Decorate(white, none).PixelWidth == 60);
    }

    private static void TestProjectRoundTripV2()
    {
        Section("프로젝트 파일 — 새 속성 왕복");

        string path = Path.Combine(Path.GetTempPath(), "snapview_v2.snapview");
        try
        {
            var items = new List<Annotation>
            {
                new PixelateAnnotation { Start = new Point(1, 1), End = new Point(20, 20), Shape = ToolKind.Ellipse },
                new SpotlightAnnotation { Start = new Point(1, 1), End = new Point(30, 30), Shape = ToolKind.Heart },
                new TextAnnotation { Origin = new Point(2, 2), Text = "x", MaxWidth = 90, Align = TextAlign.Right },
                new ShapeAnnotation { Kind = ToolKind.Arrow, Start = new Point(0, 0), End = new Point(9, 9), Head = ArrowHead.Open, Dashed = true, DashPattern = DashPattern.DashDot },
                new PathAnnotation { Highlighter = true, Blend = BlendMode.Multiply, Points = { new Point(0, 0), new Point(5, 5) } }
            };
            ProjectFile.Save(path, SolidGif(40, 40, Colors.White), items, 1);
            (_, List<Annotation> back, _) = ProjectFile.Load(path);

            Check("가리개 모양", back[0] is PixelateAnnotation { Shape: ToolKind.Ellipse });
            Check("강조 모양", back[1] is SpotlightAnnotation { Shape: ToolKind.Heart });
            Check("글자 폭·정렬", back[2] is TextAnnotation { Align: TextAlign.Right } t && Math.Abs(t.MaxWidth - 90) < 0.01);
            Check("화살촉·점선 무늬", back[3] is ShapeAnnotation { Head: ArrowHead.Open, DashPattern: DashPattern.DashDot });
            Check("형광펜 혼합", back[4] is PathAnnotation { Blend: BlendMode.Multiply });
        }
        catch (Exception ex) { Check("새 속성 왕복", false, ex.Message); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
