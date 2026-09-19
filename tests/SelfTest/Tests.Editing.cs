using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 크롭 · 뒤집기 · 조절점 · 여러 줄 텍스트 · 파일 연결 검사.

internal static partial class SelfTest
{
    private static void TestHandlesAndFlip()
    {
        Section("주석 조절점 · 뒤집기");

        // --- 사각형 8 조절점 ---
        var rect = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle,
            Start = new Point(10, 20),
            End = new Point(110, 70)
        };
        IReadOnlyList<Point> h = rect.Handles();
        Check("사각형은 조절점이 8개", h.Count == 8, h.Count.ToString());
        Check("첫 조절점은 좌상단", h[0] == new Point(10, 20), h[0].ToString());
        Check("다섯째 조절점은 우하단", h[4] == new Point(110, 70), h[4].ToString());

        rect.DragHandle(0, new Point(30, 40));          // NW 를 안쪽으로
        Check("NW 를 끌면 좌상단만 움직인다",
              Math.Abs(rect.Bounds.X - 30) < 1e-9 && Math.Abs(rect.Bounds.Y - 40) < 1e-9 &&
              Math.Abs(rect.Bounds.Right - 110) < 1e-9,
              rect.Bounds.ToString());

        rect.DragHandle(3, new Point(200, 999));        // E 는 가로만 바꿔야 한다
        Check("E 조절점은 세로를 건드리지 않는다",
              Math.Abs(rect.Bounds.Right - 200) < 1e-9 && Math.Abs(rect.Bounds.Bottom - 70) < 1e-9,
              rect.Bounds.ToString());

        // 반대쪽을 지나쳐 끌어도 뒤집힌 사각형이 되지 않아야 한다
        rect.DragHandle(0, new Point(400, 400));
        Check("조절점이 반대편을 지나가도 폭/높이가 음수가 아님",
              rect.Bounds.Width >= 0 && rect.Bounds.Height >= 0, rect.Bounds.ToString());

        // --- 선/화살표는 양 끝점 ---
        var arrow = new ShapeAnnotation
        {
            Kind = ToolKind.Arrow, Start = new Point(0, 0), End = new Point(100, 0)
        };
        Check("화살표는 조절점이 2개", arrow.Handles().Count == 2);
        arrow.DragHandle(1, new Point(50, 60));
        Check("끝점만 움직인다", arrow.End == new Point(50, 60) && arrow.Start == new Point(0, 0));

        // --- 조절점이 없는 것들 ---
        Check("글자는 폭 조절점 하나(오른쪽 가장자리)", new TextAnnotation { Text = "가" }.Handles().Count == 1);
        Check("번호는 조절점 없음(이동만)", new CounterAnnotation().Handles().Count == 0);

        var pix = new PixelateAnnotation { Start = new Point(0, 0), End = new Point(50, 50) };
        Check("모자이크는 조절점 8개", pix.Handles().Count == 8);

        // --- 뒤집기 ---
        const double W = 200, H = 100;
        var line = new ShapeAnnotation
        {
            Kind = ToolKind.Line, Start = new Point(20, 30), End = new Point(60, 80)
        };
        line.Flip(horizontal: true, W, H);
        Check("좌우 뒤집기: x 가 W-x 로",
              line.Start == new Point(180, 30) && line.End == new Point(140, 80),
              $"{line.Start} {line.End}");

        line.Flip(horizontal: true, W, H);
        Check("두 번 뒤집으면 제자리", line.Start == new Point(20, 30) && line.End == new Point(60, 80));

        line.Flip(horizontal: false, W, H);
        Check("상하 뒤집기: y 가 H-y 로",
              line.Start == new Point(20, 70) && line.End == new Point(60, 20),
              $"{line.Start} {line.End}");

        var path = new PathAnnotation();
        path.Points.Add(new Point(10, 10));
        path.Points.Add(new Point(30, 40));
        path.Flip(true, W, H);
        Check("자유 곡선도 모든 점이 뒤집힘",
              path.Points[0] == new Point(190, 10) && path.Points[1] == new Point(170, 40));

        var counter = new CounterAnnotation { Center = new Point(50, 25), Radius = 10 };
        counter.Flip(false, W, H);
        Check("번호도 뒤집힘", counter.Center == new Point(50, 75), counter.Center.ToString());

        // 글자는 거울로 뒤집지 않고 위치만 옮긴다 — 뒤집어도 이미지 안에 남아야 한다
        var text = new TextAnnotation { Origin = new Point(10, 10), Text = "가나다", FontSize = 20 };
        double before = text.Bounds.Width;
        text.Flip(true, W, H);
        Check("글자는 폭이 그대로(거울 반전 아님)", Math.Abs(text.Bounds.Width - before) < 0.01);
        Check("글자가 이미지 안에 남음",
              text.Bounds.Left >= -0.01 && text.Bounds.Right <= W + 0.01,
              text.Bounds.ToString());

        // --- 이미지 자체 뒤집기 ---
        BitmapSource src = HalfWhiteHalfBlack(40, 40);
        byte leftBefore = SamplePixel(src, 5, 20)[0];
        byte rightBefore = SamplePixel(src, 35, 20)[0];

        BitmapSource flipped = AnnotationRenderer.FlipImage(src, horizontal: true);
        Check("뒤집어도 크기 유지", flipped.PixelWidth == 40 && flipped.PixelHeight == 40);
        Check("좌우가 실제로 바뀜",
              SamplePixel(flipped, 5, 20)[0] == rightBefore &&
              SamplePixel(flipped, 35, 20)[0] == leftBefore,
              $"{leftBefore}/{rightBefore} -> {SamplePixel(flipped, 5, 20)[0]}/{SamplePixel(flipped, 35, 20)[0]}");
    }

    private static void TestCrop()
    {
        Section("자르기");

        BitmapSource src = HalfWhiteHalfBlack(100, 60);

        var crop = new CropAnnotation { Start = new Point(20, 10), End = new Point(80, 50) };
        Int32Rect r = crop.ToRegion(100, 60);
        Check("자를 영역 계산", r.X == 20 && r.Y == 10 && r.Width == 60 && r.Height == 40,
              $"{r.X},{r.Y} {r.Width}x{r.Height}");

        // 거꾸로 끌어도 정상 영역이 나와야 한다
        var backwards = new CropAnnotation { Start = new Point(80, 50), End = new Point(20, 10) };
        Int32Rect rb = backwards.ToRegion(100, 60);
        Check("거꾸로 끌어도 같은 영역", rb.X == 20 && rb.Y == 10 && rb.Width == 60 && rb.Height == 40,
              $"{rb.X},{rb.Y} {rb.Width}x{rb.Height}");

        var outside = new CropAnnotation { Start = new Point(-50, -50), End = new Point(500, 500) };
        Int32Rect ro = outside.ToRegion(100, 60);
        Check("이미지 밖으로 나가면 안쪽으로 잘림",
              ro.X == 0 && ro.Y == 0 && ro.Width == 100 && ro.Height == 60,
              $"{ro.X},{ro.Y} {ro.Width}x{ro.Height}");

        // 실제로 자르고 주석이 따라 옮겨지는지
        var cropped = new CroppedBitmap(src, r);
        cropped.Freeze();
        Check("잘린 이미지 크기", cropped.PixelWidth == 60 && cropped.PixelHeight == 40);

        var mark = new ShapeAnnotation
        {
            Kind = ToolKind.Rectangle, Start = new Point(30, 20), End = new Point(50, 30)
        };
        mark.Move(new Vector(-r.X, -r.Y));
        Check("주석도 잘린 만큼 옮겨짐",
              Math.Abs(mark.Bounds.X - 10) < 1e-9 && Math.Abs(mark.Bounds.Y - 10) < 1e-9,
              mark.Bounds.ToString());

        // 자르기는 결과물에 그려지지 않고 화면 안내용으로만 그려진다
        BitmapSource flat = AnnotationRenderer.Flatten(src, new List<Annotation>());
        Check("자르기 주석 없이 합치면 원본 그대로",
              flat.PixelWidth == 100 && flat.PixelHeight == 60);

        TestAutoCropAndSnap();
    }

    private static void TestAutoCropAndSnap()
    {
        // 흰 테두리 8/6/10/7px 안에 청록색 콘텐츠.
        BitmapSource whiteFrame = FramedImage(120, 80, 8, 6, 10, 7, 255, 255, 255);
        CropBoundaryAnalysis white = CropBoundaryAnalysis.Analyze(whiteFrame);
        Int32Rect rw = white.AutoCrop?.Region ?? Int32Rect.Empty;
        Check("흰색 사방 여백 자동 감지", rw == new Int32Rect(8, 6, 102, 67), rw.ToString());

        // 위·아래에만 검은 레터박스. 콘텐츠가 좌우 끝까지 닿으므로 좌우는 자르면 안 된다.
        BitmapSource letterbox = FramedImage(140, 90, 0, 12, 0, 14, 0, 0, 0);
        CropBoundaryAnalysis black = CropBoundaryAnalysis.Analyze(letterbox);
        Int32Rect rb = black.AutoCrop?.Region ?? Int32Rect.Empty;
        Check("검은 위아래 레터박스만 제거", rb == new Int32Rect(0, 12, 140, 64), rb.ToString());

        CropSnapResult snap = black.Snap(new Point(31, 13.4), 2);
        Check("자르기 선이 콘텐츠 위 경계에 자석처럼 붙음", snap.Y == 12, snap.Y?.ToString());

        BitmapSource plain = FramedImage(80, 50, 0, 0, 0, 0, 255, 255, 255);
        Check("여백 없는 그림은 자동 자르기 없음", CropBoundaryAnalysis.Analyze(plain).AutoCrop == null);

        BitmapSource onePixel = FramedImage(80, 50, 1, 1, 1, 1, 255, 255, 255);
        Check("1픽셀 테두리는 우발 자르기 방지를 위해 무시", CropBoundaryAnalysis.Analyze(onePixel).AutoCrop == null);

        BitmapSource transparent = FramedImage(90, 70, 5, 4, 6, 7, 0, 0, 0, borderA: 0);
        Int32Rect rt = CropBoundaryAnalysis.Analyze(transparent).AutoCrop?.Region ?? Int32Rect.Empty;
        Check("투명 테두리 자동 감지", rt == new Int32Rect(5, 4, 79, 59), rt.ToString());

        BitmapSource noisyWhite = FramedImage(120, 80, 7, 7, 7, 7, 255, 255, 255, noisyBorder: true);
        Int32Rect rn = CropBoundaryAnalysis.Analyze(noisyWhite).AutoCrop?.Region ?? Int32Rect.Empty;
        Check("압축 얼룩이 조금 있는 흰 여백도 감지", rn == new Int32Rect(7, 7, 106, 66), rn.ToString());

        // 캡처 오버레이의 자석 버튼은 흰색/검은색뿐 아니라 대충 잡은 단색 바깥도 걷어 낸다.
        BitmapSource coloredFrame = FramedImage(150, 100, 11, 9, 13, 12, 82, 47, 126);
        CropBoundaryAnalysis colored = CropBoundaryAnalysis.Analyze(coloredFrame);
        Int32Rect rm = colored.MagneticCrop?.Region ?? Int32Rect.Empty;
        Check("자석 맞춤이 유색 바깥 배경까지 콘텐츠에 맞춤",
              rm == new Int32Rect(11, 9, 126, 79), rm.ToString());
        Check("유색 바깥은 편집기 자동 레터박스로 오인하지 않음", colored.AutoCrop == null);

        BitmapSource social = SocialMediaCard();
        Int32Rect rs = CropBoundaryAnalysis.Analyze(social).MagneticCrop?.Region ?? Int32Rect.Empty;
        Check("SNS 머리글·하단 버튼보다 가운데 미디어 카드 경계를 우선",
              rs == new Int32Rect(25, 30, 140, 100), rs.ToString());

        BitmapSource flushSocial = SocialMediaCardTouchesTop();
        CropBoundaryAnalysis flush = CropBoundaryAnalysis.Analyze(flushSocial);
        Int32Rect rf = flush.MagneticCrop?.Region ?? Int32Rect.Empty;
        Check("SNS 미디어가 선택 위·양옆 끝에 닿아도 하단 본문 UI를 제외",
              rf == new Int32Rect(1, 0, 198, 130),
              flush.MagneticCrop is { } result ? $"{rf} ({result.Description})" : "결과 없음");
    }

    private static BitmapSource FramedImage(int width, int height,
                                            int left, int top, int right, int bottom,
                                            byte borderR, byte borderG, byte borderB,
                                            byte borderA = 255, bool noisyBorder = false)
    {
        int stride = width * 4;
        var px = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool border = x < left || x >= width - right || y < top || y >= height - bottom;
            int i = y * stride + x * 4;
            if (border)
            {
                bool noise = noisyBorder && (x * 17 + y * 31) % 53 == 0;
                px[i] = noise ? (byte)210 : borderB;
                px[i + 1] = noise ? (byte)210 : borderG;
                px[i + 2] = noise ? (byte)210 : borderR;
                px[i + 3] = borderA;
            }
            else
            {
                // 단색 사진으로 오인하지 않도록 내용에는 약한 무늬를 넣는다.
                px[i] = (byte)(90 + (x + y) % 30);
                px[i + 1] = (byte)(150 + (x * 3 + y) % 40);
                px[i + 2] = (byte)(35 + (x + y * 2) % 40);
                px[i + 3] = 255;
            }
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, px, stride);
        image.Freeze();
        return image;
    }

    private static BitmapSource SocialMediaCard()
    {
        const int width = 200, height = 160, stride = width * 4;
        var px = new byte[stride * height];
        for (int i = 0; i < px.Length; i += 4)
        { px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 255; }

        // 가운데 미디어 카드와 카드 내부의 검은 제목 띠.
        Paint(25, 30, 140, 100, 48, 78, 112);
        Paint(25, 100, 140, 30, 12, 12, 14);

        // 위 계정/팔로우와 아래 하트·댓글 같은 짧은 UI 조각.
        Paint(10, 8, 48, 8, 25, 25, 28);
        Paint(150, 8, 30, 8, 225, 228, 232);
        Paint(15, 142, 12, 8, 20, 20, 22);
        Paint(40, 142, 12, 8, 20, 20, 22);
        Paint(66, 142, 12, 8, 20, 20, 22);

        BitmapSource image = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, px, stride);
        image.Freeze();
        return image;

        void Paint(int left, int top, int w, int h, byte r, byte g, byte b)
        {
            for (int y = top; y < top + h; y++)
            for (int x = left; x < left + w; x++)
            {
                int i = y * stride + x * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
        }
    }

    private static BitmapSource SocialMediaCardTouchesTop()
    {
        const int width = 200, height = 160, stride = width * 4;
        var px = new byte[stride * height];
        for (int i = 0; i < px.Length; i += 4)
        { px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 255; }

        // 실제 제보 이미지처럼 미디어가 선택 맨 위에서 시작하고 좌우로 1px 여백만 둔다.
        Paint(1, 0, 198, 130, 54, 92, 118);
        Paint(1, 82, 198, 48, 18, 18, 20);

        // 미디어 아래의 좋아요/댓글 및 본문 조각. 자석 결과에는 들어가면 안 된다.
        Paint(12, 141, 12, 8, 20, 20, 22);
        Paint(38, 141, 12, 8, 20, 20, 22);
        Paint(66, 141, 12, 8, 20, 20, 22);

        BitmapSource image = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, px, stride);
        image.Freeze();
        return image;

        void Paint(int left, int top, int w, int h, byte r, byte g, byte b)
        {
            for (int y = top; y < top + h; y++)
            for (int x = left; x < left + w; x++)
            {
                int i = y * stride + x * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
        }
    }

    private static void TestMultilineText()
    {
        Section("텍스트 줄바꿈");

        var one = new TextAnnotation { Origin = new Point(0, 0), Text = "한 줄", FontSize = 20 };
        var two = new TextAnnotation { Origin = new Point(0, 0), Text = "한 줄\n두 줄", FontSize = 20 };
        var three = new TextAnnotation { Origin = new Point(0, 0), Text = "한 줄\n두 줄\n세 줄", FontSize = 20 };

        Check("줄이 늘면 높이도 늘어난다", two.Bounds.Height > one.Bounds.Height * 1.5,
              $"{one.Bounds.Height:0.#} -> {two.Bounds.Height:0.#}");
        Check("세 줄은 두 줄보다 더 높다", three.Bounds.Height > two.Bounds.Height,
              $"{two.Bounds.Height:0.#} -> {three.Bounds.Height:0.#}");

        var crlf = new TextAnnotation { Origin = new Point(0, 0), Text = "한 줄\r\n두 줄", FontSize = 20 };
        Check("CRLF 도 같은 높이", Math.Abs(crlf.Bounds.Height - two.Bounds.Height) < 0.01,
              $"{crlf.Bounds.Height:0.#} vs {two.Bounds.Height:0.#}");

        // 여러 줄이 실제로 그려지는지 — 아래쪽 줄 자리에 글자 픽셀이 있어야 한다
        var white = new byte[120 * 120 * 4];
        for (int i = 0; i < white.Length; i += 4)
        {
            white[i] = 255; white[i + 1] = 255; white[i + 2] = 255; white[i + 3] = 255;
        }
        BitmapSource canvas = BitmapSource.Create(120, 120, 96, 96, PixelFormats.Bgra32, null, white, 120 * 4);
        canvas.Freeze();

        var multi = new TextAnnotation
        {
            Origin = new Point(4, 4), Text = "AAAA\nAAAA", FontSize = 28, Color = Colors.Red
        };
        BitmapSource drawn = AnnotationRenderer.Flatten(canvas, new List<Annotation> { multi });

        bool secondLineHasInk = false;
        int y2 = (int)(multi.Bounds.Top + multi.Bounds.Height * 0.75);
        for (int x = 4; x < 100 && !secondLineHasInk; x++)
        {
            byte[] px = SamplePixel(drawn, x, Math.Clamp(y2, 0, 119));
            if (px[0] < 200 || px[1] < 200) secondLineHasInk = true;   // 흰색이 아니면 글자
        }
        Check("둘째 줄도 실제로 그려진다", secondLineHasInk, "y=" + y2);
    }

    private static void TestFileAssociation()
    {
        Section("윈도우 이미지 연결");

        // 이 검사는 진짜 레지스트리를 건드린다. 이미 SnapView 본체가 등록해 둔 상태라면
        // 덮어썼다가 지우면서 사용자의 연결을 날리게 되므로 건너뛴다.
        string? existing = FileAssociation.RegisteredCommand();
        if (existing != null && !FileAssociation.IsRegistered())
        {
            Console.WriteLine("         (다른 실행 파일이 이미 등록돼 있어 건너뜁니다: " + existing + ")");
            Check("등록 상태를 읽을 수 있음", true);
            return;
        }

        bool ok = FileAssociation.Register(out string error);
        Check("연결 등록", ok, error);
        Check("등록 뒤 IsRegistered 가 참", FileAssociation.IsRegistered());

        string? editCommand = FileAssociation.RegisteredEditorCommand();
        Check("이미지 우클릭 편집 메뉴가 등록됨",
              editCommand != null && editCommand.Contains("--edit", StringComparison.OrdinalIgnoreCase),
              editCommand);

        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                   @"Software\Classes\.png\OpenWithProgids"))
        {
            Check("PNG 가 연결 프로그램 후보에 들어감",
                  key?.GetValue(FileAssociation.ProgId) != null);
        }

        // 동영상도 뷰어가 재생한다. 그림 확장자만 등록하면 mp4 를 SnapView 로 열 수 없다.
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                   @"Software\Classes\.mp4\OpenWithProgids"))
        {
            Check("MP4 도 연결 프로그램 후보에 들어감",
                  key?.GetValue(FileAssociation.VideoProgId) != null);
        }

        using (var caps = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                   @"Software\SnapView\Capabilities\FileAssociations"))
        {
            int n = caps?.GetValueNames().Length ?? 0;
            int want = FileAssociation.AssociatedExtensions.Length;
            Check("그림·동영상 확장자가 모두 등록됨", n == want, $"{n} / {want}");
        }

        using (var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                   @"Software\RegisteredApplications"))
        {
            Check("기본 앱 설정 화면에 뜨도록 등록됨", reg?.GetValue("SnapView") != null);
        }

        // 예전 판은 그림만 등록했다. 그 상태를 "등록됨" 으로 보면 동영상 확장자가
        // 영영 안 붙어서 mp4 를 SnapView 로 열 수가 없다. 낡았으면 스스로 다시 써야 한다.
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Classes\{FileAssociation.VideoProgId}", throwOnMissingSubKey: false);
        Check("동영상 쪽이 빠지면 등록됨으로 안 본다", !FileAssociation.IsRegistered());
        Check("한 번이라도 등록한 적은 있다고 본다", FileAssociation.WasEverRegistered());
        Check("낡은 등록은 스스로 새로 고친다",
              FileAssociation.RefreshIfStale() && FileAssociation.IsRegistered());
        Check("이미 최신이면 아무것도 안 한다", !FileAssociation.RefreshIfStale());

        bool removed = FileAssociation.Unregister(out string removeError);
        Check("연결 해제", removed, removeError);
        Check("해제 뒤 IsRegistered 가 거짓", !FileAssociation.IsRegistered());
        Check("해제 뒤 우클릭 편집 메뉴도 지워짐", FileAssociation.RegisteredEditorCommand() == null);
        Check("해제 뒤에는 새로 고치지도 않는다", !FileAssociation.RefreshIfStale());

    }
}
