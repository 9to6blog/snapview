using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 편집기 고급 도구: 그림자 · 텍스트 배경 상자 · 스포트라이트 · 돋보기.
// 전부 합쳐진 결과물의 픽셀을 직접 읽어 확인한다.

internal static partial class SelfTest
{
    private static void TestProAnnotations()
    {
        Section("편집 고급 도구 (그림자 · 강조 · 돋보기 · 텍스트 상자)");

        // ---- 그림자 ----
        var shape = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 10), End = new Point(30, 30),
            Color = Colors.Red, Filled = true, Thickness = 3
        };

        BitmapSource noShadow = AnnotationRenderer.Flatten(SolidGif(60, 60, Colors.White), new[] { shape });
        shape.Shadow = true;
        BitmapSource withShadow = AnnotationRenderer.Flatten(SolidGif(60, 60, Colors.White), new[] { shape });

        // 도형 밖 오른쪽 아래 — 그림자만 닿는 자리
        Check("그림자가 없으면 도형 밖은 흰색", PixelAtRgb(noShadow, 33, 33).R > 240);
        Check("그림자를 켜면 도형 밖 오른쪽 아래가 어두워짐", PixelAtRgb(withShadow, 33, 33).R < 220);
        Check("도형 안은 그림자와 무관하게 빨강", PixelAtRgb(withShadow, 20, 20).R > 180 &&
                                                  PixelAtRgb(withShadow, 20, 20).G < 90);
        Check("복제가 그림자를 지킨다", shape.Clone().Shadow);
        Check("모자이크는 그림자를 안 받는다", !new PixelateAnnotation().SupportsShadow);

        // ---- 텍스트 배경 상자 ----
        var text = new TextAnnotation
        {
            Origin = new Point(30, 20), Text = "가나다 ABC", FontSize = 20,
            Color = Colors.White, Background = true
        };
        BitmapSource boxed = AnnotationRenderer.Flatten(SolidGif(220, 70, Colors.Gray), new[] { text });
        // 글자 왼쪽 여백 — 상자 안, 글자 밖. 흰 글자니 상자는 어두워야 한다.
        Check("텍스트 배경 상자가 그려진다", PixelAtRgb(boxed, 26, 30).R < 80,
              PixelAtRgb(boxed, 26, 30).ToString());
        var tClone = (TextAnnotation)text.Clone();
        Check("복제가 배경 상자·외곽선 설정을 지킨다", tClone.Background && tClone.OutlineHalo);

        // ---- 스포트라이트 ----
        var spot = new SpotlightAnnotation { Start = new Point(20, 20), End = new Point(40, 40) };
        BitmapSource lit = AnnotationRenderer.Flatten(SolidGif(60, 60, Colors.White), new[] { spot });
        Check("강조 영역 안은 원래 밝기", PixelAtRgb(lit, 30, 30).R > 240);
        Check("강조 영역 밖은 어두워짐", PixelAtRgb(lit, 5, 5).R < 160);

        spot.Opacity = 0.4;
        BitmapSource litSoft = AnnotationRenderer.Flatten(SolidGif(60, 60, Colors.White), new[] { spot });
        Check("투명도를 낮추면 덜 어두워짐(강도 조절)",
              PixelAtRgb(litSoft, 5, 5).R > PixelAtRgb(lit, 5, 5).R + 20);

        Check("강조 조절점 8개", spot.Handles().Count == 8);
        spot.Flip(horizontal: true, 60, 60);
        Check("강조도 좌우 뒤집기를 따라간다", Math.Abs(spot.Bounds.X - 20) < 0.01,
              spot.Bounds.ToString());

        // ---- 돋보기 ----
        // 왼쪽 절반 빨강, 오른쪽 절반 파랑. 왼쪽(빨강)을 잡아 오른쪽 위에 띄우면
        // 원래 파랑이던 자리가 확대된 빨강으로 보여야 한다.
        BitmapSource half = HalfAndHalf(80, 80, Colors.Red, Colors.Blue);
        var mag = new MagnifierAnnotation
        {
            SourceCenter = new Point(20, 40), Center = new Point(60, 40),
            Radius = 8, Zoom = 2, Color = Colors.Yellow, Thickness = 2
        };
        BitmapSource magOut = AnnotationRenderer.Flatten(half, new[] { (Annotation)mag });
        var c = PixelAtRgb(magOut, 60, 40);
        Check("돋보기 창 가운데가 잡은 곳(빨강)의 확대", c.R > 180 && c.B < 90, c.ToString());
        var c2 = PixelAtRgb(magOut, 60, 70);
        Check("돋보기 창 밖은 원래 그림(파랑)", c2.B > 180 && c2.R < 90, c2.ToString());

        mag.DragHandle(0, new Point(60 + 30, 40));   // 창 가장자리를 끌어 키우면
        Check("창 크기 조절점: 창만 커지고 원본 범위 유지", mag.DisplayRadius == 30 && mag.Radius == 8);
        mag.DragHandle(1, new Point(25, 30));
        Check("잡는 곳 조절점: 확대 대상이 옮겨진다", mag.SourceCenter == new Point(25, 30));

        var mClone = (MagnifierAnnotation)mag.Clone();
        Check("돋보기 복제가 배율·반지름을 지킨다",
              Math.Abs(mClone.Zoom - mag.Zoom) < 0.01 && Math.Abs(mClone.Radius - mag.Radius) < 0.01);

        mag.Rotate90(clockwise: true, 80, 80);
        Check("돋보기 회전에도 두 점이 함께 돈다",
              mag.Center.X >= 0 && mag.SourceCenter.X >= 0);

        TestNumberArrow();
        TestFreeRotation();
        TestEraseTouches();
        TestArrangeAndSnap();
        TestCanvasExpand();
        TestLineStyles();
        TestProjectFile();
        TestGradientAndLock();
        TestBlendModes();
        TestMagicWand();
        TestTargetedEraser();
    }

    private static void TestBlendModes()
    {
        Section("혼합 모드 (곱하기 · 스크린 · 오버레이)");

        Check("곱하기 계산", BlendComposite.Channel(128, 255, BlendMode.Multiply) == 128 &&
                             BlendComposite.Channel(128, 0, BlendMode.Multiply) == 0);
        Check("스크린 계산", BlendComposite.Channel(128, 0, BlendMode.Screen) == 128 &&
                             BlendComposite.Channel(128, 255, BlendMode.Screen) == 255);

        // 회색(128) 바탕에 빨강을 곱하면 → R 은 128 유지, G·B 는 0
        var rect = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 10), End = new Point(40, 40),
            Color = Colors.Red, Filled = true, Thickness = 1, Blend = BlendMode.Multiply
        };
        BitmapSource gray = SolidGif(60, 60, Color.FromRgb(128, 128, 128));
        BitmapSource outp = AnnotationRenderer.Flatten(gray, new[] { rect });

        var c = PixelAtRgb(outp, 25, 25);
        Check("곱하기: 회색×빨강 = 어두운 빨강", Math.Abs(c.R - 128) <= 6 && c.G <= 6 && c.B <= 6,
              c.ToString());
        var outside = PixelAtRgb(outp, 50, 50);
        Check("도형 밖 바탕은 그대로", Math.Abs(outside.R - 128) <= 2);

        rect.Blend = BlendMode.Screen;
        rect.Color = Colors.Blue;
        BitmapSource outp2 = AnnotationRenderer.Flatten(gray, new[] { rect });
        var s = PixelAtRgb(outp2, 25, 25);
        Check("스크린: 회색+파랑 = 밝은 파랑", s.B >= 250 && Math.Abs(s.R - 128) <= 6, s.ToString());

        Check("복제가 혼합 모드를 지킨다", rect.Clone().Blend == BlendMode.Screen);
        Check("글자는 혼합 대상이 아니다", !new TextAnnotation().SupportsBlend);
        Check("붙인 그림은 혼합 대상", new ImageAnnotation().SupportsBlend);
    }

    private static void TestMagicWand()
    {
        Section("자동 선택 (마술봉)");

        BitmapSource half = HalfAndHalf(40, 30, Colors.Red, Colors.Blue);

        bool[] mask = SelectionTools.FloodSelect(half, 5, 15, tolerance: 20, out int count);
        Check("이어진 같은 색만 담는다", count == 20 * 30, count.ToString());
        Check("경계 너머는 안 담긴다", !mask[15 * 40 + 30]);
        Check("담긴 자리 확인", mask[15 * 40 + 5]);

        BitmapSource erased = SelectionTools.ApplyMask(half, mask, null);
        var conv = new FormatConvertedBitmap(erased, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        conv.CopyPixels(new Int32Rect(5, 15, 1, 1), one, 4, 0);
        Check("고른 데는 투명(배경 제거)", one[3] == 0);
        Check("안 고른 데는 그대로(파랑)", PixelAtRgb(erased, 30, 15).B > 180);

        BitmapSource filled = SelectionTools.ApplyMask(half, mask, Colors.Lime);
        Check("색 채우기", PixelAtRgb(filled, 5, 15).G > 200 && PixelAtRgb(filled, 5, 15).R < 90);

        bool[] mask2 = SelectionTools.FloodSelect(half, 30, 15, 20, out _);
        int total = SelectionTools.Union(mask2, mask);
        Check("Shift 추가: 두 영역이 합쳐진다", total == 40 * 30, total.ToString());

        Check("표시용 물들임이 만들어진다",
              SelectionTools.MaskOverlay(mask, 40, 30).PixelWidth == 40);
    }

    private static void TestTargetedEraser()
    {
        Section("대상 지우개 (마스크처럼 한 주석만)");

        var bottom = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 10), End = new Point(50, 50),
            Color = Colors.Red, Filled = true, Thickness = 1
        };
        var top = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 10), End = new Point(50, 50),
            Color = Colors.Blue, Filled = true, Thickness = 1
        };
        var erase = new EraseAnnotation { Radius = 8, Target = top };
        erase.Add(new Point(30, 30));

        BitmapSource outp = AnnotationRenderer.Flatten(
            SolidGif(60, 60, Colors.White), new Annotation[] { bottom, top, erase });

        var hole = PixelAtRgb(outp, 30, 30);
        Check("대상(위 파랑)만 파여 아래 빨강이 보인다", hole.R > 180 && hole.B < 90, hole.ToString());
        var rest = PixelAtRgb(outp, 15, 15);
        Check("판 자리 밖은 위 파랑 그대로", rest.B > 180 && rest.R < 90);

        erase.Target = null;
        BitmapSource outp2 = AnnotationRenderer.Flatten(
            SolidGif(60, 60, Colors.White), new Annotation[] { bottom, top, erase });
        var through = PixelAtRgb(outp2, 30, 30);
        Check("대상이 없으면 예전처럼 전부 뚫려 바탕이 보인다", through.R > 240 && through.G > 240);

        // 실행취소 스냅샷 복제 시 대상이 복제본으로 갈아 끼워지는지
        erase.Target = top;
        List<Annotation> clones = ArrangeTools.CloneAll(new Annotation[] { bottom, top, erase });
        var clonedErase = (EraseAnnotation)clones[2];
        Check("목록 복제가 대상을 복제본으로 잇는다",
              ReferenceEquals(clonedErase.Target, clones[1]) &&
              !ReferenceEquals(clonedErase.Target, top));

        // 프로젝트 왕복에서도 대상·혼합이 살아 온다
        string path = Path.Combine(Path.GetTempPath(), "snapview_target_test.snapview");
        try
        {
            bottom.Blend = BlendMode.Multiply;
            ProjectFile.Save(path, SolidGif(60, 60, Colors.White),
                             new Annotation[] { bottom, top, erase }, 1);
            (_, List<Annotation> back, _) = ProjectFile.Load(path);
            Check("혼합 모드가 프로젝트에 남는다",
                  ((ShapeAnnotation)back[0]).Blend == BlendMode.Multiply);
            Check("대상 지우개가 프로젝트에서 다시 이어진다",
                  back[2] is EraseAnnotation le && ReferenceEquals(le.Target, back[1]));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>프로젝트(.snapview) 왕복 — 모든 주석 종류가 살아 돌아와야 한다.</summary>
    private static void TestProjectFile()
    {
        Section("프로젝트 저장 · 열기 (.snapview)");

        string path = Path.Combine(Path.GetTempPath(), "snapview_project_test.snapview");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            BitmapSource baseImg = HalfAndHalf(40, 30, Colors.Teal, Colors.Orange);

            var items = new List<Annotation>
            {
                new ShapeAnnotation
                {
                    Kind = ToolKind.Star5, Start = new Point(1, 2), End = new Point(20, 21),
                    Filled = true, GradientFill = true, Dashed = true, Shadow = true,
                    Color = Colors.Gold, RotationDeg = 30, Name = "별", Locked = true
                },
                new PathAnnotation { Highlighter = true, Points = { new Point(1, 1), new Point(9, 9) } },
                new TextAnnotation
                {
                    Origin = new Point(3, 4), Text = "안녕 ABC", FontSize = 18,
                    Background = true, OutlineHalo = false, Bold = false, Italic = true
                },
                new PixelateAnnotation { Start = new Point(2, 2), End = new Point(12, 12), UseBlur = true, Strength = 7 },
                new CounterAnnotation { Center = new Point(15, 15), Radius = 9, Number = 4 },
                new NumberArrowAnnotation { Center = new Point(20, 8), Tip = new Point(5, 25), Radius = 8, Number = 5 },
                new SpotlightAnnotation { Start = new Point(4, 4), End = new Point(30, 20), Ellipse = true, Opacity = 0.5 },
                new MagnifierAnnotation { Center = new Point(28, 10), SourceCenter = new Point(6, 20), Radius = 5, Zoom = 3 },
                new ImageAnnotation { Image = SolidGif(8, 6, Colors.Lime), Start = new Point(9, 9), End = new Point(17, 15) },
            };
            var erase = new EraseAnnotation { Radius = 4 };
            erase.Add(new Point(3, 3));
            items.Add(erase);

            ProjectFile.Save(path, baseImg, items, counter: 6);
            Check("프로젝트 파일이 생겼다", File.Exists(path) && new FileInfo(path).Length > 500);

            (BitmapSource img, List<Annotation> back, int counter) = ProjectFile.Load(path);

            Check("번호 이어쓰기 값 유지", counter == 6);
            Check("주석 개수 그대로", back.Count == items.Count, back.Count.ToString());
            Check("바탕 그림 크기 유지", img.PixelWidth == 40 && img.PixelHeight == 30);
            Check("바탕 그림 픽셀 유지(왼쪽 청록)", PixelAtRgb(img, 5, 15).G > 100 &&
                                                    PixelAtRgb(img, 5, 15).R < 90);

            var star = (ShapeAnnotation)back[0];
            Check("도형: 종류·채움·그라데·점선·그림자 유지",
                  star.Kind == ToolKind.Star5 && star.Filled && star.GradientFill &&
                  star.Dashed && star.Shadow);
            Check("도형: 회전·이름·잠금 유지",
                  Math.Abs(star.RotationDeg - 30) < 0.01 && star.Name == "별" && star.Locked);

            Check("펜: 형광펜·점 유지", back[1] is PathAnnotation pp && pp.Highlighter && pp.Points.Count == 2);
            var txt = (TextAnnotation)back[2];
            Check("글자: 내용·크기·바탕·기울임 유지",
                  txt.Text == "안녕 ABC" && Math.Abs(txt.FontSize - 18) < 0.01 &&
                  txt.Background && !txt.OutlineHalo && txt.Italic && !txt.Bold);
            Check("가리개: 흐림·세기 유지", back[3] is PixelateAnnotation px && px.UseBlur && px.Strength == 7);
            Check("번호 유지", back[4] is CounterAnnotation cc && cc.Number == 4 && Math.Abs(cc.Radius - 9) < 0.01);
            Check("번호+화살표 유지", back[5] is NumberArrowAnnotation nn && nn.Number == 5 &&
                                      nn.Tip == new Point(5, 25));
            Check("강조: 타원·어둡기 유지", back[6] is SpotlightAnnotation ss && ss.Ellipse &&
                                            Math.Abs(ss.Opacity - 0.5) < 0.01);
            Check("돋보기: 배율 유지", back[7] is MagnifierAnnotation mm && Math.Abs(mm.Zoom - 3) < 0.01);

            var imga = (ImageAnnotation)back[8];
            Check("붙인 그림까지 살아 온다", imga.Image != null && imga.Image.PixelWidth == 8 &&
                                             PixelAtRgb(imga.Image!, 4, 3).G > 180);
            Check("지운 자리도 유지", back[9] is EraseAnnotation ee && ee.Points.Count == 1 &&
                                      Math.Abs(ee.Radius - 4) < 0.01);

            // 왕복한 것들이 그대로 그려지는지 최종 확인(예외 없이 + 크기 유지)
            BitmapSource flat = AnnotationRenderer.Flatten(img, back);
            Check("연 프로젝트가 그대로 그려진다", flat.PixelWidth == 40 && flat.PixelHeight == 30);

            bool threw = false;
            try { ProjectFile.Load(Path.Combine(Path.GetTempPath(), "없는파일.snapview")); }
            catch { threw = true; }
            Check("없는 파일은 예외로 알린다", threw);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void TestGradientAndLock()
    {
        Section("그라데이션 채우기 · 잠금");

        var g = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(5, 5), End = new Point(45, 55),
            Color = Colors.Blue, Filled = true, GradientFill = true, Thickness = 1
        };
        BitmapSource outp = AnnotationRenderer.Flatten(SolidGif(50, 60, Colors.White), new[] { g });

        var top = PixelAtRgb(outp, 25, 9);
        var bottom = PixelAtRgb(outp, 25, 51);
        Check("위쪽은 진한 파랑", top.B > 200 && top.R < 90, top.ToString());
        Check("아래로 갈수록 옅어진다(흰 바탕이 비침)", bottom.R > top.R + 80,
              $"top R={top.R}, bottom R={bottom.R}");
        Check("복제가 그라데이션을 지킨다", ((ShapeAnnotation)g.Clone()).GradientFill);

        var locked = new CounterAnnotation { Center = new Point(10, 10), Locked = true, Name = "고정 번호" };
        var lc = locked.Clone();
        Check("복제가 잠금·이름을 지킨다", lc.Locked && lc.Name == "고정 번호");
    }

    private static void TestArrangeAndSnap()
    {
        Section("정렬 · 균등 간격 · 스냅");

        ShapeAnnotation Box(double x, double y, double w, double h) => new()
        {
            Kind = ToolKind.Rectangle, Start = new Point(x, y), End = new Point(x + w, y + h)
        };

        var a = Box(10, 10, 20, 10);
        var b = Box(40, 30, 10, 20);
        var c = Box(70, 55, 30, 10);
        var list = new Annotation[] { a, b, c };

        ArrangeTools.Align(list, AlignMode.Left);
        Check("왼쪽 맞춤: 전부 제일 왼쪽에", a.Bounds.Left == 10 && b.Bounds.Left == 10 && c.Bounds.Left == 10);

        ArrangeTools.Align(list, AlignMode.CenterV);
        double cy = a.Bounds.Top + a.Bounds.Height / 2;
        Check("세로 가운데 맞춤: 가운데가 같다",
              Math.Abs(b.Bounds.Top + b.Bounds.Height / 2 - cy) < 0.01 &&
              Math.Abs(c.Bounds.Top + c.Bounds.Height / 2 - cy) < 0.01);

        var d1 = Box(0, 0, 10, 10);    // 가운데 5
        var d2 = Box(10, 0, 10, 10);   // 가운데 15 → 50 으로 가야
        var d3 = Box(90, 0, 10, 10);   // 가운데 95
        ArrangeTools.Distribute(new Annotation[] { d1, d2, d3 }, horizontal: true);
        Check("가로 간격 고르게: 가운데 것이 절반 지점으로",
              Math.Abs(d2.Bounds.Left + 5 - 50) < 0.01, (d2.Bounds.Left + 5).ToString("0.0"));
        Check("양 끝은 안 움직인다", d1.Bounds.Left == 0 && d3.Bounds.Left == 90);

        // 스냅: 캔버스 가운데(100)에 4px 차이로 다가가면 붙는다
        SnapGuide.Result r1 = SnapGuide.Solve(new Rect(86, 40, 20, 10), Array.Empty<Rect>(),
                                              200, 100, tolerance: 6);
        Check("캔버스 가운데에 스냅", Math.Abs(r1.Adjust.X - 4) < 0.01 && r1.GuideX == 100,
              r1.Adjust.ToString());

        SnapGuide.Result r2 = SnapGuide.Solve(new Rect(52, 40, 20, 10),
                                              new[] { new Rect(10, 10, 40, 10) }, 200, 100, 6);
        Check("다른 주석의 오른끝(50)에 왼끝이 스냅", Math.Abs(r2.Adjust.X - (-2)) < 0.01 && r2.GuideX == 50);

        SnapGuide.Result r3 = SnapGuide.Solve(new Rect(60, 62, 10, 10), Array.Empty<Rect>(),
                                              201, 301, 6);
        Check("멀면 안 붙는다", r3.Adjust.X == 0 && r3.GuideX == null && r3.GuideY == null);
    }

    private static void TestCanvasExpand()
    {
        Section("캔버스 여백 늘리기");

        BitmapSource red = SolidGif(20, 20, Colors.Red);
        BitmapSource padded = AnnotationRenderer.Expand(red, 5, 8, 2, 3, Colors.White);

        Check("크기가 여백만큼 늘어난다", padded.PixelWidth == 27 && padded.PixelHeight == 31,
              $"{padded.PixelWidth}x{padded.PixelHeight}");
        Check("여백은 채운 색", PixelAtRgb(padded, 2, 2).G > 240);
        Check("그림은 제자리(안 늘어남)", PixelAtRgb(padded, 5 + 10, 8 + 10).R > 200 &&
                                          PixelAtRgb(padded, 15, 18).G < 90);

        BitmapSource clear = AnnotationRenderer.Expand(red, 4, 4, 0, 0, null);
        var conv = new FormatConvertedBitmap(clear, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        conv.CopyPixels(new Int32Rect(1, 1, 1, 1), one, 4, 0);
        Check("투명 여백은 정말 투명", one[3] == 0, "alpha=" + one[3]);
    }

    private static void TestLineStyles()
    {
        Section("점선 · 양촉 화살표");

        var solid = new ShapeAnnotation
        {
            Kind = ToolKind.Line, Start = new Point(5, 20), End = new Point(55, 20),
            Color = Colors.Red, Thickness = 3
        };
        var dashed = (ShapeAnnotation)solid.Clone();
        dashed.Dashed = true;

        int CountRed(BitmapSource src)
        {
            int n = 0;
            for (int x = 7; x < 53; x++)
                if (PixelAtRgb(src, x, 20).R > 180 && PixelAtRgb(src, x, 20).G < 100) n++;
            return n;
        }

        BitmapSource s1 = AnnotationRenderer.Flatten(SolidGif(60, 40, Colors.White), new[] { solid });
        BitmapSource s2 = AnnotationRenderer.Flatten(SolidGif(60, 40, Colors.White), new[] { dashed });
        int solidN = CountRed(s1), dashN = CountRed(s2);
        Check("실선은 끊김 없이 이어진다", solidN >= 44, solidN + "px");
        Check("점선은 빈 구간이 생긴다", dashN < solidN - 8 && dashN > 8, dashN + "px");
        Check("복제가 점선을 지킨다", ((ShapeAnnotation)dashed.Clone()).Dashed);

        var one = new ShapeAnnotation
        {
            Kind = ToolKind.Arrow, Start = new Point(10, 20), End = new Point(70, 20),
            Color = Colors.Blue, Thickness = 3
        };
        var both = (ShapeAnnotation)one.Clone();
        both.BothArrows = true;

        BitmapSource a1 = AnnotationRenderer.Flatten(SolidGif(80, 40, Colors.White), new[] { one });
        BitmapSource a2 = AnnotationRenderer.Flatten(SolidGif(80, 40, Colors.White), new[] { both });

        // 굵기 3의 촉 길이 9.6px 안쪽, 몸통 밖을 검사한다.
        Check("한쪽 화살표: 시작점 위는 비어 있다", PixelAtRgb(a1, 18, 17).R > 240);
        Check("양촉: 시작점에도 촉이 달린다", PixelAtRgb(a2, 18, 17).B > 150 &&
                                              PixelAtRgb(a2, 18, 17).R < 120);
        Check("복제가 양촉을 지킨다", ((ShapeAnnotation)both.Clone()).BothArrows);
    }

    private static void TestNumberArrow()
    {
        Section("번호 + 화살표");

        var na = new NumberArrowAnnotation
        {
            Tip = new Point(15, 30), Center = new Point(60, 30),
            Number = 1, Radius = 14, Color = Colors.Blue, Thickness = 3
        };

        Check("경계가 촉과 원을 다 품는다", na.Bounds.Contains(new Point(15, 30)) &&
                                            na.Bounds.Contains(new Point(72, 30)));
        Check("조절점 2개(촉·원)", na.Handles().Count == 2);

        na.DragHandle(0, new Point(10, 10));
        Check("촉 조절점이 촉을 옮긴다", na.Tip == new Point(10, 10));
        na.DragHandle(1, new Point(62, 32));
        Check("원 조절점이 번호를 옮긴다", na.Center == new Point(62, 32));
        na.Tip = new Point(15, 30); na.Center = new Point(60, 30);

        BitmapSource outp = AnnotationRenderer.Flatten(SolidGif(90, 60, Colors.White),
                                                       new[] { (Annotation)na });
        var shaft = PixelAtRgb(outp, 35, 30);
        Check("화살표 몸통이 그려진다", shaft.B > 150 && shaft.R < 120, shaft.ToString());
        var circle = PixelAtRgb(outp, 52, 22);
        Check("번호 원이 그려진다", circle.B > 150 && circle.R < 120, circle.ToString());

        Check("촉 근처 적중", na.HitTest(new Point(20, 31)));
        Check("멀리는 비적중", !na.HitTest(new Point(20, 55)));

        var clone = (NumberArrowAnnotation)na.Clone();
        Check("복제가 번호·반지름·두 점을 지킨다",
              clone.Number == 1 && Math.Abs(clone.Radius - 14) < 0.01 &&
              clone.Tip == na.Tip && clone.Center == na.Center);

        na.Flip(horizontal: true, 90, 60);
        Check("좌우 뒤집기에 두 점이 따라간다", Math.Abs(na.Tip.X - 75) < 0.01 &&
                                                Math.Abs(na.Center.X - 30) < 0.01);
    }

    private static void TestFreeRotation()
    {
        Section("자유 회전");

        // 가로로 긴 빨간 막대(40×10)를 90도 세우면, 세로로 길어야 한다.
        var bar = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(30, 40), End = new Point(70, 50),
            Color = Colors.Red, Filled = true, Thickness = 2
        };

        BitmapSource flat = AnnotationRenderer.Flatten(SolidGif(100, 100, Colors.White), new[] { bar });
        Check("눕힌 상태: 가로 끝이 빨강", PixelAtRgb(flat, 66, 45).R > 180 &&
                                           PixelAtRgb(flat, 66, 45).G < 90);
        Check("눕힌 상태: 세로 위쪽은 흰색", PixelAtRgb(flat, 50, 63).R > 240 &&
                                             PixelAtRgb(flat, 50, 63).G > 240);

        bar.RotationDeg = 90;
        BitmapSource rot = AnnotationRenderer.Flatten(SolidGif(100, 100, Colors.White), new[] { bar });
        Check("90도 세우면 세로 아래가 빨강", PixelAtRgb(rot, 50, 63).R > 180 &&
                                              PixelAtRgb(rot, 50, 63).G < 90);
        Check("세우면 원래 가로 끝은 흰색", PixelAtRgb(rot, 66, 45).G > 240);

        Check("회전 반영 적중 판정", bar.HitTestRotated(new Point(50, 63)));
        Check("눕기 전 좌표로는 그 자리가 밖", !bar.Bounds.Contains(new Point(50, 63)));

        Point local = bar.ToLocal(new Point(50, 63));
        Check("ToLocal 이 눕기 전 좌표로 되돌린다", bar.Bounds.Contains(local),
              local.ToString());

        var clone = bar.Clone();
        Check("복제가 회전각을 지킨다", Math.Abs(clone.RotationDeg - 90) < 0.01);

        bar.Visible = false;
        Check("복제가 보임 여부도 지킨다", !bar.Clone().Visible);
        bar.Visible = true;

        Check("상자 도형은 회전 가능", bar.CanRotate);
        Check("선·화살표는 회전 손잡이가 없다",
              !new ShapeAnnotation { Kind = ToolKind.Arrow }.CanRotate);
        Check("모자이크는 회전하지 않는다", !new PixelateAnnotation().CanRotate);
    }

    private static void TestEraseTouches()
    {
        Section("지우개 적중(빈 획 버리기용)");

        var rect = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(10, 10), End = new Point(40, 40),
            Thickness = 3
        };

        var near = new EraseAnnotation { Radius = 8 };
        near.Add(new Point(45, 25));   // 굵기+반지름 여유 안
        Check("스친 획은 닿은 것으로 본다", near.Touches(rect));

        var far = new EraseAnnotation { Radius = 8 };
        far.Add(new Point(90, 90));
        Check("먼 획은 닿지 않은 것", !far.Touches(rect));

        Check("지우개끼리는 닿음이 아니다", !near.Touches(far));
    }

    /// <summary>왼쪽 절반과 오른쪽 절반의 색이 다른 시험 그림.</summary>
    private static BitmapSource HalfAndHalf(int w, int h, Color left, Color right)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            Color cc = x < w / 2 ? left : right;
            int i = (y * w + x) * 4;
            px[i] = cc.B; px[i + 1] = cc.G; px[i + 2] = cc.R; px[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>합쳐진 결과에서 픽셀 하나(RGB).</summary>
    private static (byte R, byte G, byte B) PixelAtRgb(BitmapSource src, int x, int y)
    {
        BitmapSource s = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
            ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        s.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return (one[2], one[1], one[0]);
    }
}
