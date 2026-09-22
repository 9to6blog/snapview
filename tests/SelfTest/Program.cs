using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;
using SnapView.Native;

// SnapView 자체 검사. 창을 하나도 띄우지 않고 콘솔에서만 돌린다.
// (WinPurge 의 selftest.c 와 같은 방식 — GUI 를 반복 실행하지 않기 위한 것)

internal static partial class SelfTest
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("  [OK]   " + name); }
        else { _fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    private static void Section(string title) => Console.WriteLine("\n== " + title + " ==");

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string tmp = Path.Combine(Path.GetTempPath(), "SnapViewSelfTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);

        try
        {
            if (Array.IndexOf(args, "--capture-input") >= 0)
            {
                TestCaptureAltTab();
                Console.WriteLine($"\n결과: 통과 {_pass}, 실패 {_fail}");
                return _fail == 0 ? 0 : 1;
            }
            if (Array.IndexOf(args, "--editor") >= 0)
            {
                TestEditorRegressions(tmp);
                TestEditorKeyMap(); TestEditorPanelRules(); TestUndoStack(); TestEditorMath();
                TestAnnotationStyle(); TestImageFilters(); TestPreviewMatchesResult();
                TestProAnnotations(); TestBlendModes(); TestTargetedEraser(); TestProjectFile();
                TestGradientAndLock(); TestLineStyles(); TestNumberArrow(); TestFreeRotation();
                TestShapedMasks(); TestArrowHeadsAndDashes(); TestHighlighterMultiply();
                TestFlattenDpi(); TestProjectRoundTripV2(); TestHandlesAndFlip();
                Console.WriteLine($"\n결과: 통과 {_pass}, 실패 {_fail}");
                return _fail == 0 ? 0 : 1;
            }
            TestEditorRegressions(tmp);
            TestHotKeys();
            TestCaptureAltTab();
            TestLaunchRequest(tmp);
            TestPrintScreenChain();
            TestElevation();
            TestWindowPlacement();
            TestViewMath();
            TestScreenCapture();
            TestImageIO(tmp);
            TestClipboard();
            TestSettings(tmp);
            TestRecordingNames();
            TestEditorKeyMap();
            TestEditorPanelRules();
            TestUndoStack();
            TestEditorMath();
            TestRecentColorsAndWindowMemory();
            TestRecoveryStore(tmp);
            TestGroupScale();
            TestWindowEnum();
            TestGraphicsCapture();
            TestImageEffects();
            TestAnnotations();
            TestAnnotationStyle();
            TestProAnnotations();
            TestShapedMasks();
            TestTextWrapAlign();
            TestArrowHeadsAndDashes();
            TestHighlighterMultiply();
            TestFlattenDpi();
            TestExportDecor();
            TestProjectRoundTripV2();
            TestPreviewMatchesResult();
            TestImageFilters();
            TestShapeGallery();
            TestLayerVisibility();
            TestColorConversion();
            TestEraserHitTest();
            TestRotate();
            TestResize();
            TestTextStyle();
            TestPastedImage();
            TestGifWriter();
            TestGifStreaming();
            TestGifDelayRounding();
            TestMediaKinds();
            TestMp4Writer();
            TestRecorderTiming();
            TestRecorderPause();
            TestVideoInfo();
            TestVideoEdit(tmp);
            TestPlayerKeys();
            TestClampIntoMonitor();
            TestFormatLists();
            TestFrameNaming();
            TestSiblingsByKind();
            TestStoredData();
            TestYuvConversion();
            TestRecordSound();
            TestAudioConvert();
            TestAudioDownmix();
            TestAudioResample();
            TestLoopbackCapture();
            TestRecordSoundBeforeCapture();
            TestMp4WithAudio();
            TestHandlesAndFlip();
            TestCrop();
            TestMultilineText();
            TestFileAssociation();
            TestCaptureSound();
            TestInstallerSafety();
            TestInstallerPackaging();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("\n[예외] " + ex);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine($"\n결과: 통과 {_pass}, 실패 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 파일 열기 요청
    private static void TestLaunchRequest(string tmp)
    {
        Section("파일 열기 요청");

        string image = Path.Combine(tmp, "우클릭 테스트.png");
        File.WriteAllBytes(image, Array.Empty<byte>());

        LaunchRequest view = LaunchRequest.FromArguments(new[] { image });
        Check("일반 파일 인자는 뷰어 요청", view.HasFile && !view.OpensEditor && view.FilePath == image);

        LaunchRequest edit = LaunchRequest.FromArguments(new[] { "--edit", image });
        Check("--edit 파일 인자는 편집기 요청", edit.HasFile && edit.OpensEditor && edit.FilePath == image);

        LaunchRequest roundTrip = LaunchRequest.FromIpcPayload(edit.ToIpcPayload());
        Check("편집기 요청 IPC 왕복", roundTrip == edit);

        LaunchRequest legacy = LaunchRequest.FromIpcPayload(image);
        Check("예전 경로 전용 IPC 호환", legacy.HasFile && !legacy.OpensEditor && legacy.FilePath == image);
    }

    // ---------------------------------------------------------------- 단축키
    private static void TestHotKeys()
    {
        Section("단축키 파싱");

        var a = HotKeySpec.Parse("Ctrl+Shift+A");
        Check("Ctrl+Shift+A 파싱", a != null && a.Modifiers == (0x2 | 0x4) && a.VirtualKey == 0x41,
              a == null ? "null" : $"mod={a.Modifiers} vk=0x{a.VirtualKey:X}");

        var f = HotKeySpec.Parse("ctrl + shift + f");   // 공백·대소문자 섞어서
        Check("공백/대소문자 무시", f != null && f.VirtualKey == 0x46);

        var p = HotKeySpec.Parse("Alt+PrtSc");
        Check("Alt+PrtSc -> VK_SNAPSHOT(0x2C)", p != null && p.VirtualKey == 0x2C && p.Modifiers == 0x1,
              p == null ? "null" : $"vk=0x{p.VirtualKey:X}");

        var d1 = HotKeySpec.Parse("Ctrl+1");
        Check("숫자키 Ctrl+1 -> 0x31", d1 != null && d1.VirtualKey == 0x31);

        var f5 = HotKeySpec.Parse("F5");
        Check("F5 (수식키 없음)", f5 != null && f5.VirtualKey == 0x74 && f5.Modifiers == 0);

        var win = HotKeySpec.Parse("Win+Shift+S");
        Check("Win 수식키", win != null && win.Modifiers == (0x8 | 0x4));

        Check("빈 문자열 -> null", HotKeySpec.Parse("") == null);
        Check("수식키만 -> null", HotKeySpec.Parse("Ctrl+Shift") == null);
        Check("모르는 키 -> null", HotKeySpec.Parse("Ctrl+없는키") == null);
        Check("표시 문자열 유지", HotKeySpec.Parse("Ctrl+Shift+A")!.Display == "Ctrl+Shift+A",
              HotKeySpec.Parse("Ctrl+Shift+A")!.Display);
    }

    // ---------------------------------------------------------------- 좌표/배율
    private static void TestViewMath()
    {
        Section("좌표 · 배율 계산");

        // 1920x1080 비트맵을 960x540 DIP 창에 Fill 로 깔았을 때 (배율 200%)
        Int32Rect r = ViewMath.ToPixels(new Rect(100, 50, 200, 100), 2.0, 2.0, 1920, 1080);
        Check("DIP->픽셀 변환(2배)", r.X == 200 && r.Y == 100 && r.Width == 400 && r.Height == 200,
              $"{r.X},{r.Y} {r.Width}x{r.Height}");

        Int32Rect over = ViewMath.ToPixels(new Rect(-50, -50, 5000, 5000), 1.0, 1.0, 1920, 1080);
        Check("이미지 밖 선택은 안쪽으로 잘림",
              over.X == 0 && over.Y == 0 && over.Width == 1920 && over.Height == 1080,
              $"{over.X},{over.Y} {over.Width}x{over.Height}");

        Int32Rect edge = ViewMath.ToPixels(new Rect(1919, 1079, 100, 100), 1.0, 1.0, 1920, 1080);
        Check("우하단 모서리에서도 폭/높이 >= 1",
              edge.X + edge.Width <= 1920 && edge.Y + edge.Height <= 1080 &&
              edge.Width >= 1 && edge.Height >= 1,
              $"{edge.X},{edge.Y} {edge.Width}x{edge.Height}");

        Int32Rect zero = ViewMath.ToPixels(new Rect(10, 10, 0, 0), 1.0, 1.0, 1920, 1080);
        Check("크기 0 선택도 최소 1픽셀", zero.Width == 1 && zero.Height == 1);

        int xEdgeCalls = 0, yEdgeCalls = 0;
        Rect fourSides = ViewMath.SnapRectEdges(new Rect(10, 20, 100, 80), (point, snapX, snapY) =>
        {
            if (snapX)
            {
                xEdgeCalls++;
                return new Point(point.X < 60 ? 8 : 112, point.Y);
            }
            if (snapY)
            {
                yEdgeCalls++;
                return new Point(point.X, point.Y < 60 ? 18 : 104);
            }
            return point;
        });
        Check("영역 드래그 자석이 좌·우·위·아래 네 변을 각각 맞춤",
              fourSides == new Rect(8, 18, 104, 86) && xEdgeCalls == 2 && yEdgeCalls == 2,
              $"{fourSides}, X호출={xEdgeCalls}, Y호출={yEdgeCalls}");

        // 배율 (DPI 100%): 4000x3000 이미지를 1000x800 창에
        double z = ViewMath.FitZoom(FitMode.ShrinkToFit, 1000, 800, 4000, 3000, 1.0, 0.02, 64);
        Check("창보다 큰 그림은 줄인다", Math.Abs(z - 0.25) < 1e-9, z.ToString());

        // 작은 그림은 원본 크기 유지 (사용자 확정 방침)
        double z2 = ViewMath.FitZoom(FitMode.ShrinkToFit, 1000, 800, 100, 80, 1.0, 0.02, 64);
        Check("작은 그림은 확대하지 않는다(100%)", Math.Abs(z2 - 1.0) < 1e-9, z2.ToString());

        double z3 = ViewMath.FitZoom(FitMode.StretchToFit, 1000, 800, 100, 80, 1.0, 0.02, 64);
        Check("StretchToFit 은 작은 그림도 늘린다", Math.Abs(z3 - 10.0) < 1e-9, z3.ToString());

        double z4 = ViewMath.FitZoom(FitMode.FitWidth, 1000, 800, 500, 4000, 1.0, 0.02, 64);
        Check("폭 맞춤은 세로를 무시한다", Math.Abs(z4 - 2.0) < 1e-9, z4.ToString());

        double z5 = ViewMath.FitZoom(FitMode.Actual, 1000, 800, 4000, 3000, 1.5, 0.02, 64);
        Check("100% 는 DPI 와 무관하게 1.0", Math.Abs(z5 - 1.0) < 1e-9, z5.ToString());

        // 150% DPI 에서 "창에 맞춤": 물리 픽셀 기준 배율이 나와야 한다
        double z6 = ViewMath.FitZoom(FitMode.ShrinkToFit, 1000, 800, 4000, 3000, 1.5, 0.02, 64);
        Check("고DPI 에서 맞춤 배율은 물리 픽셀 기준", Math.Abs(z6 - 0.375) < 1e-9, z6.ToString());

        // 원점 고정
        var c1 = ViewMath.ClampOrigin(-500, -500, 200, 200, 1000, 800);
        Check("내용이 작으면 가운데 정렬", Math.Abs(c1.X - 400) < 1e-9 && Math.Abs(c1.Y - 300) < 1e-9,
              $"{c1.X},{c1.Y}");

        var c2 = ViewMath.ClampOrigin(500, 500, 3000, 2000, 1000, 800);
        Check("내용이 크면 여백이 안 생기게 붙든다", Math.Abs(c2.X) < 1e-9 && Math.Abs(c2.Y) < 1e-9,
              $"{c2.X},{c2.Y}");

        var c3 = ViewMath.ClampOrigin(-99999, -99999, 3000, 2000, 1000, 800);
        Check("반대쪽 끝도 붙든다", Math.Abs(c3.X - (-2000)) < 1e-9 && Math.Abs(c3.Y - (-1200)) < 1e-9,
              $"{c3.X},{c3.Y}");

        // 커서 기준 확대: 커서 밑 지점이 그대로 있어야 한다
        double oldEff = 1.0, newEff = 2.0, ax = 640, ay = 360, ox = -100, oy = -50;
        var za = ViewMath.ZoomAnchor(ax, ay, ox, oy, oldEff, newEff);
        double beforeU = (ax - ox) / oldEff;
        double afterU = (ax - za.X) / newEff;
        Check("확대해도 커서 밑 지점이 유지된다", Math.Abs(beforeU - afterU) < 1e-9,
              $"{beforeU} vs {afterU}");

        // 회전 후 이동량: 90도 회전한 1000x400 이미지의 좌상단이 (10,20) 에 오는지
        var t = ViewMath.TranslationFor(10, 20, 1000, 400, 400, 1000, 1.0);
        double left = t.X + 1.0 * (1000.0 / 2 - 400.0 / 2);
        double top = t.Y + 1.0 * (400.0 / 2 - 1000.0 / 2);
        Check("90도 회전 시 좌상단 위치 계산", Math.Abs(left - 10) < 1e-9 && Math.Abs(top - 20) < 1e-9,
              $"{left},{top}");
    }

    // ---------------------------------------------------------------- 화면 캡처
    private static void TestScreenCapture()
    {
        Section("화면 캡처");

        Int32Rect vs = ScreenCapture.VirtualScreen();
        Check("가상 화면 크기 > 0", vs.Width > 0 && vs.Height > 0, $"{vs.Width}x{vs.Height}");

        BitmapSource small = ScreenCapture.CaptureRect(vs.X, vs.Y, 64, 48);
        Check("64x48 캡처 크기 일치", small.PixelWidth == 64 && small.PixelHeight == 48,
              $"{small.PixelWidth}x{small.PixelHeight}");
        Check("픽셀 형식 Bgr32 (알파 0 때문에 투명해지지 않음)", small.Format == PixelFormats.Bgr32,
              small.Format.ToString());
        Check("캡처 결과는 Freeze 됨", small.IsFrozen);

        // 알파가 0 이어도 불투명하게 보이는지 — Bgr32 는 알파를 무시한다
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        new CroppedBitmap(conv, new Int32Rect(0, 0, 1, 1)).CopyPixels(one, 4, 0);
        Check("Bgra32 로 바꿔도 알파 255", one[3] == 255, "alpha=" + one[3]);

        BitmapSource full = ScreenCapture.CaptureVirtualScreen();
        Check("전체 화면 캡처 크기 일치",
              full.PixelWidth == vs.Width && full.PixelHeight == vs.Height,
              $"{full.PixelWidth}x{full.PixelHeight} vs {vs.Width}x{vs.Height}");

        // 오버레이가 하는 것과 같은 잘라내기
        Int32Rect sel = ViewMath.ToPixels(new Rect(10, 10, 200, 150), 1, 1, full.PixelWidth, full.PixelHeight);
        var crop = new CroppedBitmap(full, sel);
        Check("전체 캡처에서 잘라내기", crop.PixelWidth == 200 && crop.PixelHeight == 150,
              $"{crop.PixelWidth}x{crop.PixelHeight}");

        bool threw = false;
        try { ScreenCapture.CaptureRect(0, 0, 0, 10); } catch (ArgumentException) { threw = true; }
        Check("크기 0 캡처는 예외", threw);

        BitmapSource withCursor = ScreenCapture.CaptureRect(vs.X, vs.Y, 32, 32, includeCursor: true);
        Check("커서 포함 캡처도 동작", withCursor.PixelWidth == 32);
    }

    // ---------------------------------------------------------------- 관리자 권한 판정
    // 관리자 창이 앞에 있으면 일반 권한의 전역 단축키가 안 눌린다(게임에서 흔함).
    // 그 안내에 쓰는 판정이 헛짚지 않는지 본다.
    private static void TestElevation()
    {
        Section("관리자 권한 판정 (게임에서 단축키 안 먹는 원인 안내)");

        bool self = Elevation.IsSelfElevated;
        Check("자기 자신 판정이 예외 없이 동작", true, $"이 프로세스 관리자 여부 = {self}");

        Check("빈 핸들은 null", Elevation.IsWindowElevated(IntPtr.Zero) == null);
        Check("엉터리 핸들도 예외 없이", Elevation.IsWindowElevated((IntPtr)0x13579) == null);

        // 우리 창의 판정은 우리 프로세스의 판정과 같아야 한다.
        var w = new Window
        {
            ShowInTaskbar = false, Width = 10, Height = 10,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        var helper = new System.Windows.Interop.WindowInteropHelper(w);
        helper.EnsureHandle();
        bool? mine = Elevation.IsWindowElevated(helper.Handle);
        Check("자기 창 판정 = 자기 프로세스 판정", mine == self, $"{mine} vs {self}");
        w.Close();
    }

    // ---------------------------------------------------------------- 파일 입출력
    private static void TestImageIO(string tmp)
    {
        Section("이미지 저장 · 불러오기");

        BitmapSource img = ScreenCapture.CaptureRect(0, 0, 120, 90);

        string png = Path.Combine(tmp, "a.png");
        ImageIO.SaveTo(img, png);
        Check("PNG 저장", File.Exists(png) && new FileInfo(png).Length > 0);

        BitmapSource back = ImageIO.Load(png);
        Check("PNG 되읽기 크기 일치", back.PixelWidth == 120 && back.PixelHeight == 90,
              $"{back.PixelWidth}x{back.PixelHeight}");
        Check("되읽은 이미지는 Freeze 됨", back.IsFrozen);

        string jpg = Path.Combine(tmp, "b.jpg");
        ImageIO.SaveTo(img, jpg, 90);
        BitmapSource jback = ImageIO.Load(jpg);
        Check("JPEG 저장/되읽기", jback.PixelWidth == 120 && jback.PixelHeight == 90);

        Check("인코더 선택 png", ImageIO.CreateEncoder("png", 90) is PngBitmapEncoder);
        Check("인코더 선택 jpg", ImageIO.CreateEncoder("jpg", 90) is JpegBitmapEncoder);
        Check("모르는 확장자는 png 로", ImageIO.CreateEncoder("xyz", 90) is PngBitmapEncoder);

        // 파일 잠금 해제 확인 — 열어 둔 채로 지울 수 있어야 한다
        bool deleted = true;
        try { File.Delete(jpg); } catch { deleted = false; }
        Check("불러온 뒤 파일이 잠기지 않음", deleted);

        // 자동 저장 이름 규칙과 중복 회피
        var s = new Settings { SaveFolder = tmp, ImageFormat = "png", FileNamePattern = "shot_{0:yyyyMMdd}" };
        var when = new DateTime(2026, 8, 22, 13, 5, 0);
        string p1 = ImageIO.SaveAuto(img, s, when);
        string p2 = ImageIO.SaveAuto(img, s, when);
        Check("자동 저장 이름 규칙", Path.GetFileName(p1) == "shot_20260822.png", Path.GetFileName(p1));
        Check("같은 이름이면 (2) 를 붙인다", Path.GetFileName(p2) == "shot_20260822 (2).png", Path.GetFileName(p2));

        var bad = new Settings { SaveFolder = tmp, ImageFormat = "png", FileNamePattern = "a/b:c*{0:HHmmss}" };
        string p3 = ImageIO.SaveAuto(img, bad, when);
        Check("파일명 금지문자 정리", File.Exists(p3) && !Path.GetFileName(p3).Contains(':'),
              Path.GetFileName(p3));

        var broken = new Settings { SaveFolder = tmp, ImageFormat = "png", FileNamePattern = "{0:" };
        string p4 = ImageIO.SaveAuto(img, broken, when);
        Check("이름 규칙이 깨져도 저장은 된다", File.Exists(p4), Path.GetFileName(p4));

        // 미리 인코딩한 PNG 를 나눠 쓰는 경로 (클립보드+저장 이중 인코딩 제거)
        byte[] shared = ImageIO.EncodePng(img);
        Check("EncodePng 가 PNG 서명으로 시작",
              shared.Length > 8 && shared[0] == 0x89 && shared[1] == (byte)'P' &&
              shared[2] == (byte)'N' && shared[3] == (byte)'G');

        var s2 = new Settings { SaveFolder = tmp, ImageFormat = "png", FileNamePattern = "pre_{0:HHmmss}" };
        string p5 = ImageIO.SaveAuto(img, s2, when, shared);
        Check("공유 PNG 로 저장하면 그 바이트가 그대로 파일이 된다",
              File.Exists(p5) && File.ReadAllBytes(p5).AsSpan().SequenceEqual(shared));
        BitmapSource pre = ImageIO.Load(p5);
        Check("공유 PNG 로 저장한 파일도 정상 디코딩", pre.PixelWidth == 120 && pre.PixelHeight == 90);

        var s3 = new Settings { SaveFolder = tmp, ImageFormat = "jpg", FileNamePattern = "pre_{0:HHmmss}" };
        string p6 = ImageIO.SaveAuto(img, s3, when, shared);
        Check("jpg 형식이면 공유 PNG 를 무시하고 JPEG 로 저장",
              Path.GetExtension(p6) == ".jpg" && File.ReadAllBytes(p6)[0] == 0xFF);

        // 탐색기와 같은 숫자 정렬
        foreach (string n in new[] { "img10.png", "img2.png", "img1.png", "note.txt", "img20.png" })
            File.WriteAllBytes(Path.Combine(tmp, n), File.ReadAllBytes(png));
        List<string> sib = ImageIO.Siblings(Path.Combine(tmp, "img2.png"), videos: false);
        var names = sib.ConvertAll(Path.GetFileName);
        int i1 = names.IndexOf("img1.png"), i2 = names.IndexOf("img2.png"), i10 = names.IndexOf("img10.png");
        Check("숫자 정렬 img1 < img2 < img10", i1 >= 0 && i1 < i2 && i2 < i10,
              string.Join(", ", names));
        Check("이미지가 아닌 파일은 제외", !names.Contains("note.txt"));

        Check("확장자 판정", ImageIO.IsSupported("x.PNG") && ImageIO.IsSupported("x.jpeg") &&
                             !ImageIO.IsSupported("x.txt"));

        // 휴지통
        string doomed = Path.Combine(tmp, "doomed.png");
        File.Copy(png, doomed);
        bool recycled = ImageIO.RecycleFile(doomed);
        Check("휴지통으로 보내기", recycled && !File.Exists(doomed),
              $"반환={recycled} 남아있음={File.Exists(doomed)}");
    }

    // ---------------------------------------------------------------- 클립보드
    private static void TestClipboard()
    {
        Section("클립보드");

        BitmapSource img = ScreenCapture.CaptureRect(0, 0, 40, 30);
        ImageIO.CopyToClipboard(img);

        BitmapSource? got = null;
        try { got = Clipboard.GetImage(); } catch { }
        Check("클립보드에 이미지가 올라감", got != null && got.PixelWidth == 40 && got.PixelHeight == 30,
              got == null ? "null" : $"{got.PixelWidth}x{got.PixelHeight}");

        bool hasPng = false;
        try { hasPng = Clipboard.ContainsData("PNG"); } catch { }
        Check("PNG 형식도 함께 올라감(브라우저 붙여넣기용)", hasPng);
    }

    // ---------------------------------------------------------------- 설정
    private static void TestSettings(string tmp)
    {
        Section("설정 저장 · 불러오기");

        var s = new Settings
        {
            SaveFolder = tmp,
            ImageFormat = "jpg",
            JpegQuality = 77,
            HotKeyRegion = "Ctrl+Alt+X",
            DefaultFitMode = FitMode.FitWidth,
            IncludeCursor = true
        };

        string json = System.Text.Json.JsonSerializer.Serialize(s,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
        Check("열거형이 이름으로 직렬화됨", json.Contains("\"FitWidth\""));

        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            })!;
        Check("왕복 후 값 유지",
              back.JpegQuality == 77 && back.HotKeyRegion == "Ctrl+Alt+X" &&
              back.DefaultFitMode == FitMode.FitWidth && back.IncludeCursor,
              $"{back.JpegQuality}/{back.HotKeyRegion}/{back.DefaultFitMode}");

        var def = new Settings();
        Check("기본 단축키가 모두 파싱됨",
              HotKeySpec.Parse(def.HotKeyRegion) != null &&
              HotKeySpec.Parse(def.HotKeyFullScreen) != null &&
              HotKeySpec.Parse(def.HotKeyActiveWindow) != null);
        Check("기본 저장 폴더는 내 그림 아래",
              Settings.DefaultSaveFolder.EndsWith(@"\SnapView"), Settings.DefaultSaveFolder);
        Check("기본값은 항상 창에 맞춤", def.DefaultFitMode == FitMode.StretchToFit);

        // 옛 판(녹화음 항목이 없던) 파일은 캡처음 설정을 녹화음으로 잇는다 — 갈라지기 전엔 하나였다.
        var old = System.Text.Json.JsonSerializer.Deserialize<Settings>(
            "{\"PlayShutterSound\":false,\"ShutterVolume\":10}")!;
        old.Migrate();
        Check("옛 파일은 녹화음을 캡처음 값으로 잇는다",
              !old.PlayRecordSound && old.RecordSoundVolume == 10 &&
              old.SettingsVersion == Settings.CurrentSettingsVersion,
              $"{old.PlayRecordSound}/{old.RecordSoundVolume}/v{old.SettingsVersion}");

        var fresh = System.Text.Json.JsonSerializer.Deserialize<Settings>(
            "{\"SettingsVersion\":2,\"PlayShutterSound\":false,\"PlayRecordSound\":true,\"RecordSoundVolume\":80}")!;
        fresh.Migrate();
        Check("새 파일은 녹화음 설정을 그대로 둔다", fresh.PlayRecordSound && fresh.RecordSoundVolume == 80,
              $"{fresh.PlayRecordSound}/{fresh.RecordSoundVolume}");
    }

    private static void TestRecordingNames()
    {
        Section("녹화 파일 이름 규칙");

        var when = new DateTime(2026, 9, 12, 16, 5, 7);
        string a = RecordingNames.Build("SnapView_{1}_{0:yyyyMMdd_HHmmss}", when, "전체 화면");
        Check("{0} 은 시각, {1} 은 대상", a == "SnapView_전체 화면_20260912_160507", a);

        string b = RecordingNames.Build("{1}", when, "메모장 - a:b*c?");
        Check("파일에 못 쓰는 글자는 바꾼다", b == "메모장 - a_b_c_", b);

        Check("규칙이 틀리면 기본 규칙으로", RecordingNames.Build("{0:", when, "x").StartsWith("SnapView_"));
        Check("빈 규칙도 기본 규칙으로", RecordingNames.Build("", when, "x").StartsWith("SnapView_"));
        Check("너무 긴 대상 이름은 자른다", RecordingNames.Build("{1}", when, new string('가', 100)).Length <= 40);
        Check("대상이 없어도 이름이 된다", RecordingNames.Build(RecordingNames.DefaultPattern, when, null).Length > 10);
    }

    // ---------------------------------------------------------------- 창 열거
    private static void TestWindowEnum()
    {
        Section("창 열거 (클릭 한 번으로 창 캡처)");

        List<ScreenCapture.CapturableWindow> wins = ScreenCapture.EnumerateVisibleWindows();
        List<Int32Rect> rects = wins.ConvertAll(w => w.Rect);
        Check("보이는 창 목록을 얻는다(예외 없음)", rects != null);
        Check("모든 후보가 유효한 HWND 를 갖는다", wins.TrueForAll(w => w.Hwnd != IntPtr.Zero));

        Int32Rect vs = ScreenCapture.VirtualScreen();
        bool allInside = true, allSized = true;
        foreach (Int32Rect r in rects!)
        {
            if (r.Width < 24 || r.Height < 24) allSized = false;
            if (r.X + r.Width < vs.X || r.Y + r.Height < vs.Y) allInside = false;
        }
        Check("너무 작은 창은 후보에서 제외됨", allSized);
        Check("모든 후보가 화면 영역 안", allInside);
        Console.WriteLine($"         (후보 창 {rects.Count}개)");

        Int32Rect? fg = ScreenCapture.ForegroundWindowRect();
        Check("활성 창 영역 조회(예외 없음)", true,
              fg == null ? "null (콘솔이라 정상)" : $"{fg.Value.Width}x{fg.Value.Height}");
    }
}
