using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 편집기에 새로 붙인 것들: 투명도 · 채우기 · 글꼴 · 이미지 필터.

internal static partial class SelfTest
{
    private static void TestAnnotationStyle()
    {
        Section("주석 스타일 (투명도 · 채우기 · 글꼴)");

        // 1) 투명도가 복제에서 살아남는가. 하나라도 빠뜨리면 실행취소 후 값이 튄다.
        var samples = new Annotation[]
        {
            new ShapeAnnotation { Kind = ToolKind.Rectangle, Start = new Point(0, 0), End = new Point(10, 10), Filled = true },
            new PathAnnotation { Highlighter = true, Points = { new Point(0, 0), new Point(5, 5) } },
            new TextAnnotation { Origin = new Point(1, 1), Text = "가나다", FontFamilyName = "Consolas" },
            new PixelateAnnotation { Start = new Point(0, 0), End = new Point(8, 8) },
            new CounterAnnotation { Center = new Point(4, 4), Number = 3, Radius = 12 },
            new CropAnnotation { Start = new Point(0, 0), End = new Point(9, 9) }
        };

        foreach (Annotation a in samples)
        {
            a.Opacity = 0.42;
            Annotation c = a.Clone();
            Check($"{a.GetType().Name} 복제가 투명도를 지킴", Math.Abs(c.Opacity - 0.42) < 1e-9,
                  c.Opacity.ToString("0.###"));
        }

        Check("사각형 복제가 채우기를 지킴",
              ((ShapeAnnotation)samples[0].Clone()).Filled);
        Check("텍스트 복제가 글꼴을 지킴",
              ((TextAnnotation)samples[2].Clone()).FontFamilyName == "Consolas");

        // 2) 가리는 도구는 투명도를 받으면 안 된다 — 비치면 가리는 의미가 없다.
        Check("모자이크·흐림은 투명도 제외", !samples[3].SupportsOpacity);
        Check("자르기 미리보기도 제외", !samples[5].SupportsOpacity);
        Check("도형은 투명도 허용", samples[0].SupportsOpacity);
        Check("텍스트도 투명도 허용", samples[2].SupportsOpacity);

        // 3) 투명도가 실제 픽셀에 반영되는가. 빨간 사각형을 흰 바탕에 반투명으로 얹는다.
        BitmapSource white = Solid(20, 20, Colors.White);

        var opaque = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(2, 2), End = new Point(18, 18),
            Filled = true, Color = Colors.Red, Opacity = 1.0
        };
        var faded = (ShapeAnnotation)opaque.Clone();
        faded.Opacity = 0.3;

        Color solid = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { opaque }), 10, 10);
        Color soft = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { faded }), 10, 10);

        Check("불투명하면 빨강 그대로", solid.R > 200 && solid.G < 60, solid.ToString());
        Check("투명도를 낮추면 바탕이 비친다", soft.G > solid.G + 60, $"{solid} -> {soft}");

        // 4) 글꼴을 바꾸면 글자 폭이 달라진다(= 실제로 그 글꼴로 그린다).
        var narrow = new TextAnnotation { Origin = new Point(0, 0), Text = "iiiiiiiiii", FontSize = 40, FontFamilyName = "Consolas" };
        var wide = new TextAnnotation { Origin = new Point(0, 0), Text = "iiiiiiiiii", FontSize = 40, FontFamilyName = "Arial" };
        Check("글꼴이 다르면 글자 폭도 다르다",
              Math.Abs(narrow.Bounds.Width - wide.Bounds.Width) > 1,
              $"Consolas={narrow.Bounds.Width:0.#} Arial={wide.Bounds.Width:0.#}");

        // 5) 없는 글꼴을 넣어도 죽지 않아야 한다.
        var missing = new TextAnnotation { Origin = new Point(0, 0), Text = "가", FontFamilyName = "존재하지않는글꼴XYZ" };
        Check("없는 글꼴이어도 예외 없이 그려짐", missing.Bounds.Width > 0);
    }

    private static void TestImageFilters()
    {
        Section("이미지 필터");

        BitmapSource gray = Solid(16, 16, Color.FromRgb(120, 90, 60));

        foreach (ImageEffects.ImageFilter f in Enum.GetValues<ImageEffects.ImageFilter>())
        {
            BitmapSource? outp = ImageEffects.Apply(gray, f);
            Check($"{ImageEffects.NameOf(f)} 가 결과를 돌려줌",
                  outp != null && outp.PixelWidth == 16 && outp.PixelHeight == 16);
        }

        Color src = PixelAt(gray, 8, 8);

        Color bw = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Grayscale, 1.0)!, 8, 8);
        Check("흑백은 세 채널이 같아진다", bw.R == bw.G && bw.G == bw.B, bw.ToString());

        // 최대 세기에서는 정확히 255-x 여야 한다.
        Color inv = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Invert, 1.0)!, 8, 8);
        Check("반전은 255에서 뺀 값", inv.R == 255 - src.R && inv.B == 255 - src.B, inv.ToString());

        // 강도를 줄이면 원본 쪽으로 돌아와야 한다 — 모든 필터에 강도가 먹는지 본다.
        Color invHalf = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Invert, 0.5)!, 8, 8);
        Check("강도를 낮추면 원본에 가까워진다",
              Math.Abs(invHalf.R - src.R) < Math.Abs(inv.R - src.R),
              $"원본={src.R} 50%={invHalf.R} 100%={inv.R}");

        Color invNone = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Invert, 0.0)!, 8, 8);
        Check("강도 0 이면 원본 그대로", invNone.R == src.R && invNone.G == src.G, invNone.ToString());

        Color bwHalf = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Grayscale, 0.2)!, 8, 8);
        Check("흑백도 강도가 먹는다(살짝이면 색이 남는다)",
              bwHalf.R != bwHalf.B, bwHalf.ToString());

        Color bright = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Brighten)!, 8, 8);
        Color dark = PixelAt(ImageEffects.Apply(gray, ImageEffects.ImageFilter.Darken)!, 8, 8);
        Check("밝게는 밝아진다", bright.R > src.R, $"{src.R} -> {bright.R}");
        Check("어둡게는 어두워진다", dark.R < src.R, $"{src.R} -> {dark.R}");

        // 단색 그림에 노이즈를 걸면 픽셀마다 값이 흩어져야 한다.
        BitmapSource noisy = ImageEffects.Apply(gray, ImageEffects.ImageFilter.Noise)!;
        Check("노이즈는 픽셀마다 값을 흩는다",
              PixelAt(noisy, 3, 3) != PixelAt(noisy, 11, 11),
              $"{PixelAt(noisy, 3, 3)} / {PixelAt(noisy, 11, 11)}");

        // 흰 배경 한가운데 검은 점 — 부드럽게 하면 점이 옅어져야 한다.
        Check("부드럽게 하면 또렷한 점이 옅어진다", SoftenLightensDot());
    }

    private static bool SoftenLightensDot()
    {
        var wb = new WriteableBitmap(9, 9, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[9 * 9 * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255;
        }
        int c = (4 * 9 + 4) * 4;
        px[c] = px[c + 1] = px[c + 2] = 0;
        wb.WritePixels(new Int32Rect(0, 0, 9, 9), px, 9 * 4, 0);
        wb.Freeze();

        BitmapSource? soft = ImageEffects.Apply(wb, ImageEffects.ImageFilter.Soften);
        return soft != null && PixelAt(soft, 4, 4).R > 100;
    }

    /// <summary>
    /// 편집 화면과 최종 결과가 같은 그림을 내놓는가.
    /// 예전에 화면 쪽만 Render 를 직접 불러서 투명도가 화면에 안 보인 적이 있다.
    /// </summary>
    private static void TestPreviewMatchesResult()
    {
        Section("편집 화면 = 최종 결과");

        BitmapSource white = Solid(24, 24, Colors.White);
        var faded = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(3, 3), End = new Point(21, 21),
            Filled = true, Color = Colors.Blue, Opacity = 0.25
        };

        // 결과물 경로
        Color flat = PixelAt(AnnotationRenderer.Flatten(white, new Annotation[] { faded }), 12, 12);

        // 화면 경로 — 캔버스가 쓰는 것과 같은 단일 그리기 진입점
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(white, new Rect(0, 0, 24, 24));
            AnnotationRenderer.Draw(dc, faded, white);
        }
        var rtb = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        Color preview = PixelAt(rtb, 12, 12);

        Check("화면과 결과의 색이 같다", Near(flat, preview), $"결과={flat} 화면={preview}");
        Check("투명도가 실제로 옅게 만든다", preview.R > 120 && preview.B > 200, preview.ToString());
    }

    private static bool Near(Color a, Color b)
        => Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;

    private static BitmapSource Solid(int w, int h, Color color)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = color.B; px[i + 1] = color.G; px[i + 2] = color.R; px[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static Color PixelAt(BitmapSource src, int x, int y)
    {
        BitmapSource s = src.Format == PixelFormats.Bgra32
            ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        s.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return Color.FromArgb(one[3], one[2], one[1], one[0]);
    }
}
