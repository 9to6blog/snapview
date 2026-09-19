using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;
using SnapView.Native;

// 2차 기능(창 단위 캡처 · 이미지 효과 · 주석) 검사.
// Program.cs 의 SelfTest 와 같은 클래스다.

internal static partial class SelfTest
{
    // ---------------------------------------------------------------- Graphics Capture

    /// <summary>
    /// D3D11 을 vtable 슬롯으로 직접 호출하므로 번호가 하나라도 틀리면
    /// 프로세스가 그 자리에서 죽는다. 실제 창을 한 번 떠 보는 이 검사가 그 안전망이다.
    /// </summary>
    private static void TestGraphicsCapture()
    {
        Section("Windows.Graphics.Capture (창 단위 정확 캡처)");

        Check("이 윈도우에서 지원됨", WindowsGraphicsCapture.IsSupported);
        if (!WindowsGraphicsCapture.IsSupported)
        {
            Console.WriteLine("         (지원되지 않아 이후 검사를 건너뜁니다)");
            return;
        }

        IntPtr target = PickTargetWindow(out Int32Rect targetRect);
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("         (캡처할 창을 못 찾아 건너뜁니다)");
            return;
        }
        Console.WriteLine($"         (대상 창 {targetRect.Width}x{targetRect.Height})");

        BitmapSource? wgc = WindowsGraphicsCapture.TryCaptureWindow(target, false, out string err);
        Check("WGC 로 창 캡처 성공 — D3D11 슬롯 검증", wgc != null, err);

        if (wgc != null)
        {
            Check("캡처 크기가 창 크기와 비슷",
                  Math.Abs(wgc.PixelWidth - targetRect.Width) <= 24 &&
                  Math.Abs(wgc.PixelHeight - targetRect.Height) <= 24,
                  $"{wgc.PixelWidth}x{wgc.PixelHeight} vs {targetRect.Width}x{targetRect.Height}");
            Check("결과는 Freeze 됨", wgc.IsFrozen);

            var conv = new FormatConvertedBitmap(wgc, PixelFormats.Bgra32, null, 0);
            var px = new byte[4];
            new CroppedBitmap(conv, new Int32Rect(wgc.PixelWidth / 2, wgc.PixelHeight / 2, 1, 1))
                .CopyPixels(px, 4, 0);
            Check("가운데 픽셀이 투명하지 않음(알파 보정 동작)", px[3] > 0, "alpha=" + px[3]);
        }

        // PrintWindow 가 <b>언제나</b> 되기를 바라면 안 된다. 그리기를 GPU 에 맡기는 창
        // (요즘 브라우저·UWP 앱)은 아무리 제대로 불러도 빈 그림이나 null 을 준다 —
        // 창을 가진 쪽이 응하지 않으면 그만인 방식이라서다. 대상 창은 그때그때 화면에
        // 떠 있는 아무 창이라, 이걸 실패로 세면 <b>검사가 날씨처럼 바뀐다</b>.
        // 그래서 여기서는 "죽지 않고 답을 준다" 까지만 본다. 정작 지켜야 할 것 —
        // 어떤 창이든 무언가는 잡힌다 — 은 바로 아래 자동 선택 검사가 맡는다.
        BitmapSource? printed = ScreenCapture.TryPrintWindow(target);
        Console.WriteLine("         (PrintWindow: " +
                          (printed == null ? "이 창은 응하지 않음" : $"{printed.PixelWidth}x{printed.PixelHeight}") + ")");

        BitmapSource? smart = ScreenCapture.CaptureWindowSmart(target, false, true,
            out ScreenCapture.WindowCaptureMethod used, out string note);
        Check("자동 선택 경로가 이미지를 돌려줌", smart != null, note);
        Console.WriteLine($"         (선택된 방식: {used})");

        BitmapSource? noWgc = ScreenCapture.CaptureWindowSmart(target, false, false,
            out ScreenCapture.WindowCaptureMethod used2, out _);
        Check("WGC 를 끄면 다른 방식으로 넘어감",
              noWgc != null && used2 != ScreenCapture.WindowCaptureMethod.GraphicsCapture,
              used2.ToString());

        BitmapSource? dead = ScreenCapture.CaptureWindowSmart(IntPtr.Zero, false, true, out _, out string deadNote);
        Check("잘못된 핸들은 예외 대신 null", dead == null, deadNote);

        // 슬롯이 어긋나 있으면 여기까지 오지 못한다. 반복 캡처가 버티는지도 함께 본다.
        bool repeated = true;
        for (int i = 0; i < 5; i++)
        {
            if (WindowsGraphicsCapture.TryCaptureWindow(target, false, out _) == null) { repeated = false; break; }
        }
        Check("연속 5회 캡처해도 안정적", repeated);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static IntPtr PickTargetWindow(out Int32Rect rect)
    {
        rect = default;

        IntPtr console = GetConsoleWindow();
        Int32Rect? r = console != IntPtr.Zero ? ScreenCapture.WindowRect(console) : null;
        if (r != null && r.Value.Width >= 64 && r.Value.Height >= 64)
        {
            rect = r.Value;
            return console;
        }

        foreach (ScreenCapture.CapturableWindow w in ScreenCapture.EnumerateVisibleWindows())
        {
            if (w.Rect.Width >= 200 && w.Rect.Height >= 150)
            {
                rect = w.Rect;
                return w.Hwnd;
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 이미지 효과

    private static void TestImageEffects()
    {
        Section("모자이크 · 흐림");

        const int W = 64, H = 64;
        BitmapSource src = HalfWhiteHalfBlack(W, H);

        BitmapSource? mosaic = ImageEffects.Pixelate(src, new Int32Rect(0, 0, W, H), 16);
        Check("모자이크 결과 크기 유지", mosaic != null && mosaic.PixelWidth == W && mosaic.PixelHeight == H);

        if (mosaic != null)
        {
            var block = new byte[16 * 16 * 4];
            new CroppedBitmap(mosaic, new Int32Rect(0, 0, 16, 16)).CopyPixels(block, 16 * 4, 0);
            bool uniform = true;
            for (int i = 4; i < block.Length; i += 4)
            {
                if (block[i] != block[0]) { uniform = false; break; }
            }
            Check("한 블록 안은 같은 색", uniform);

            byte left = SamplePixel(mosaic, 4, 4)[0];
            byte right = SamplePixel(mosaic, W - 5, 4)[0];
            Check("왼쪽(흰)과 오른쪽(검정)은 여전히 구분됨", left != right, $"{left} vs {right}");
        }

        BitmapSource? blur = ImageEffects.Blur(src, new Int32Rect(0, 0, W, H), 6);
        Check("흐림 결과 크기 유지", blur != null && blur.PixelWidth == W && blur.PixelHeight == H);

        if (blur != null)
        {
            byte edge = SamplePixel(blur, W / 2, H / 2)[0];
            Check("경계가 중간색으로 섞임", edge > 20 && edge < 235, "v=" + edge);
        }

        BitmapSource? partial = ImageEffects.Pixelate(src, new Int32Rect(10, 10, 20, 20), 5);
        Check("영역을 지정하면 그 크기만 돌려줌",
              partial != null && partial.PixelWidth == 20 && partial.PixelHeight == 20);

        BitmapSource? outOfRange = ImageEffects.Pixelate(src, new Int32Rect(-50, -50, 5000, 5000), 8);
        Check("영역이 이미지 밖이면 안쪽으로 잘림",
              outOfRange != null && outOfRange.PixelWidth <= W && outOfRange.PixelHeight <= H);

        BitmapSource? tiny = ImageEffects.Pixelate(src, new Int32Rect(0, 0, 3, 3), 64);
        Check("블록이 영역보다 커도 죽지 않음", tiny != null);
    }

    private static BitmapSource HalfWhiteHalfBlack(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte v = (byte)(x < w / 2 ? 255 : 0);
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        }
        BitmapSource b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    private static byte[] SamplePixel(BitmapSource src, int x, int y)
    {
        BitmapSource conv = src.Format == PixelFormats.Bgra32
            ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        new CroppedBitmap(conv, new Int32Rect(x, y, 1, 1)).CopyPixels(px, 4, 0);
        return px;
    }

    // ---------------------------------------------------------------- 주석

    private static void TestAnnotations()
    {
        Section("주석 그리기 · 합치기");

        const int W = 200, H = 120;
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255;
        }
        BitmapSource src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
        src.Freeze();

        var items = new List<Annotation>();

        BitmapSource empty = AnnotationRenderer.Flatten(src, items);
        Check("주석이 없으면 원본 크기 그대로", empty.PixelWidth == W && empty.PixelHeight == H);

        var arrow = new ShapeAnnotation
        {
            Kind = ToolKind.Arrow,
            Start = new Point(20, 60),
            End = new Point(180, 60),
            Color = Colors.Red,
            Thickness = 4
        };
        items.Add(arrow);

        BitmapSource withArrow = AnnotationRenderer.Flatten(src, items);
        Check("합친 결과도 원본 해상도", withArrow.PixelWidth == W && withArrow.PixelHeight == H);

        byte[] mid = SamplePixel(withArrow, 100, 60);
        Check("화살표 자리 픽셀이 실제로 빨개짐", mid[2] > 150 && mid[0] < 120,
              $"B={mid[0]} G={mid[1]} R={mid[2]}");

        byte[] corner = SamplePixel(withArrow, 2, 2);
        Check("건드리지 않은 곳은 원본 그대로", corner[0] > 240 && corner[2] > 240);

        Check("화살표 적중 판정 — 선 위", arrow.HitTest(new Point(100, 61)));
        Check("화살표 적중 판정 — 선 밖", !arrow.HitTest(new Point(100, 100)));

        var rect = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle,
            Start = new Point(10, 10),
            End = new Point(50, 40)
        };
        Check("사각형 Bounds 계산",
              Math.Abs(rect.Bounds.Width - 40) < 1e-9 && Math.Abs(rect.Bounds.Height - 30) < 1e-9);

        rect.Move(new Vector(5, -5));
        Check("이동하면 Bounds 도 따라감",
              Math.Abs(rect.Bounds.X - 15) < 1e-9 && Math.Abs(rect.Bounds.Y - 5) < 1e-9);

        // 실행취소 스냅샷이 원본과 얽히면 되돌리기가 망가진다
        Annotation clone = rect.Clone();
        clone.Move(new Vector(100, 100));
        Check("Clone 은 원본과 독립(실행취소 안전)", Math.Abs(rect.Bounds.X - 15) < 1e-9,
              rect.Bounds.X.ToString(CultureInfo.InvariantCulture));

        var path = new PathAnnotation { Thickness = 3 };
        path.Points.Add(new Point(5, 5));
        path.Points.Add(new Point(60, 40));
        Check("자유 곡선 Bounds", Math.Abs(path.Bounds.Width - 55) < 1e-9);
        Check("자유 곡선 적중 판정", path.HitTest(new Point(60, 40)));

        var pathClone = (PathAnnotation)path.Clone();
        pathClone.Points.Add(new Point(999, 999));
        Check("곡선 Clone 도 점 목록이 독립", path.Points.Count == 2, path.Points.Count.ToString());

        var text = new TextAnnotation { Origin = new Point(10, 10), Text = "가나다 ABC", FontSize = 20 };
        Check("텍스트 Bounds 가 0 이 아님", text.Bounds.Width > 10 && text.Bounds.Height > 10,
              $"{text.Bounds.Width:0.#}x{text.Bounds.Height:0.#}");

        var counter = new CounterAnnotation { Center = new Point(50, 50), Number = 3, Radius = 16 };
        Check("번호 적중 판정 — 안", counter.HitTest(new Point(55, 55)));
        Check("번호 적중 판정 — 밖", !counter.HitTest(new Point(100, 100)));

        var pix = new PixelateAnnotation { Start = new Point(0, 0), End = new Point(60, 60), Strength = 10 };
        items.Add(pix);
        BitmapSource all = AnnotationRenderer.Flatten(src, items);
        Check("모자이크 주석까지 합쳐짐", all.PixelWidth == W && all.PixelHeight == H);

        // 화면 배율과 무관하게 원본 해상도로 나와야 한다
        var big = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(0, 0), End = new Point(W, H),
            Color = Colors.Blue, Thickness = 2
        };
        BitmapSource framed = AnnotationRenderer.Flatten(src, new List<Annotation> { big });
        Check("가장자리까지 그려도 잘리지 않음", framed.PixelWidth == W && framed.PixelHeight == H);
    }
}
