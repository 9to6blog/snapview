using System;
using System.IO;
using System.Linq;
using SnapView.Core;

// 다룰 수 있는 확장자 목록과, 이 PC 에 남는 것들.

internal static partial class SelfTest
{
    private static void TestFormatLists()
    {
        Section("다룰 수 있는 형식");

        Check("webp 는 그림", MediaKinds.IsImage("a.webp"));
        Check("heic·avif 도 그림", MediaKinds.IsImage("a.heic") && MediaKinds.IsImage("b.avif"));
        Check("RAW 도 그림", MediaKinds.IsImage("a.dng") && MediaKinds.IsImage("b.cr2"));
        Check("대소문자 무시", MediaKinds.IsImage("A.WEBP") && MediaKinds.IsVideo("B.MKV"));

        Check("mkv·webm 은 동영상", MediaKinds.IsVideo("a.mkv") && MediaKinds.IsVideo("b.webm"));
        Check("m2ts·3gp 도 동영상", MediaKinds.IsVideo("a.m2ts") && MediaKinds.IsVideo("b.3gp"));
        Check("gif 는 그림 쪽", MediaKinds.IsImage("a.gif") && !MediaKinds.IsVideo("a.gif"));

        Check("txt 는 아무것도 아니다", !MediaKinds.IsMedia("a.txt"));

        // 그림과 동영상이 겹치면 어느 쪽으로 열지 정할 수가 없다.
        var both = MediaKinds.ImageExtensions.Intersect(MediaKinds.VideoExtensions).ToArray();
        Check("그림·동영상 목록이 안 겹친다", both.Length == 0, string.Join(" ", both));

        // 목록 자체가 성한지
        Check("모두 점으로 시작하고 소문자",
              MediaKinds.All.All(e => e.StartsWith(".", StringComparison.Ordinal) &&
                                      e == e.ToLowerInvariant()));
        var dup = MediaKinds.All.GroupBy(e => e).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Check("중복된 확장자가 없다", dup.Length == 0, string.Join(" ", dup));

        Check("예전보다 다루는 게 늘었다", MediaKinds.All.Length >= 40, MediaKinds.All.Length + "가지");

        // 확장자 하나만 넘겨도 통해야 한다 — 파일 연결 등록이 그렇게 쓴다.
        Check("확장자만 넘겨도 판정된다", MediaKinds.IsVideo(".mp4") && MediaKinds.IsImage(".png"));
    }

    /// <summary>
    /// 그림 창과 재생 창은 서로 다른 폴더 목록을 본다.
    ///
    /// 하나로 돌려쓰면 그림을 보다가 →를 눌렀을 때 영상이 튀어나오고, 영상을 열면
    /// 보던 그림이 사라진다. 실제로 그렇게 서로를 밀어냈다.
    /// </summary>
    private static void TestSiblingsByKind()
    {
        Section("그림 목록과 영상 목록 가르기");

        string dir = Path.Combine(Path.GetTempPath(), "snapview_sib_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);

        try
        {
            foreach (string name in new[] { "a.png", "b.jpg", "c.mp4", "d.mkv", "e.txt" })
                File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1 });

            string png = Path.Combine(dir, "a.png");
            string mp4 = Path.Combine(dir, "c.mp4");

            var images = ImageIO.Siblings(png, videos: false);
            var videos = ImageIO.Siblings(mp4, videos: true);

            Check("그림 목록에 그림만", images.Count == 2 && images.All(MediaKinds.IsImage),
                  string.Join(" ", images.Select(Path.GetFileName)));
            Check("그림 목록에 영상이 없다", !images.Any(MediaKinds.IsVideo));
            Check("영상 목록에 영상만", videos.Count == 2 && videos.All(MediaKinds.IsVideo),
                  string.Join(" ", videos.Select(Path.GetFileName)));
            Check("영상 목록에 그림이 없다", !videos.Any(MediaKinds.IsImage));
            Check("txt 는 어느 쪽에도 없다",
                  !images.Concat(videos).Any(f => f.EndsWith(".txt", StringComparison.Ordinal)));

            // 목록에 없는 확장자를 억지로 열어도 그 파일만은 남아야 한다.
            string odd = Path.Combine(dir, "f.얍");
            File.WriteAllBytes(odd, new byte[] { 1 });
            var one = ImageIO.Siblings(odd, videos: false);
            Check("모르는 확장자도 자기 자신은 목록에 있다",
                  one.Any(f => string.Equals(f, odd, StringComparison.OrdinalIgnoreCase)),
                  string.Join(" ", one.Select(Path.GetFileName)));

            // 순서는 탐색기와 같아야 한다(숫자 인식).
            foreach (string name in new[] { "img2.png", "img10.png", "img1.png" })
                File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1 });
            var sorted = ImageIO.Siblings(Path.Combine(dir, "img1.png"), videos: false)
                                .Select(Path.GetFileName).ToList();
            int i1 = sorted.IndexOf("img1.png"), i2 = sorted.IndexOf("img2.png"), i10 = sorted.IndexOf("img10.png");
            Check("숫자 순서대로 (2 가 10 보다 앞)", i1 < i2 && i2 < i10, string.Join(" ", sorted));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // 녹화 기본값
        Check("녹화 기본값이 60fps", new Settings().RecordingFps == 60,
              new Settings().RecordingFps + "fps");
    }

    /// <summary>
    /// 영상에서 뜬 장면의 파일 이름.
    /// 날짜만 있으면 나중에 폴더를 열었을 때 무엇을 찍은 것인지 알 수 없다.
    /// </summary>
    private static void TestFrameNaming()
    {
        Section("장면 파일 이름");

        var when = new DateTime(2026, 8, 23, 18, 45, 12);
        var s = new Settings();

        string made = ImageIO.BuildName(s.FrameNamePattern, when, "휴가 영상");
        Check("원본 이름이 들어간다", made.Contains("휴가 영상"), made);
        Check("날짜와 시각이 들어간다", made.Contains("2026-08-23") && made.Contains("184512"), made);
        Check("SnapView로 시작한다", made.StartsWith("SnapView_", StringComparison.Ordinal), made);

        // 원본 이름을 모를 때 밑줄이 두 개 붙으면 안 된다.
        string none = ImageIO.BuildName(s.FrameNamePattern, when, null);
        Check("이름이 없으면 밑줄이 겹치지 않는다", !none.Contains("__"), none);
        Check("이름이 없어도 날짜는 남는다", none.Contains("2026-08-23"), none);

        // 파일 이름에 못 쓰는 글자가 원본에 있어도 저장되어야 한다.
        string bad = ImageIO.BuildName(s.FrameNamePattern, when, @"a/b\c:d*e?f""g<h>i|j");
        bool clean = bad.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        Check("못 쓰는 글자가 걸러진다", clean, bad);

        // 아주 긴 이름은 경로 길이 제한에 걸린다.
        string longName = ImageIO.BuildName(s.FrameNamePattern, when, new string('가', 300));
        Check("너무 길면 잘린다", longName.Length < 160, longName.Length + "자");

        // 캡처는 날짜·시간·UUID를 항상 포함한다.
        string shot = ImageIO.BuildName(s.FileNamePattern, when, null);
        Check("캡처 기본 이름 날짜·밀리초·UUID", shot.StartsWith("SnapView_2026-08-23_184512_000_") && Guid.TryParseExact(shot[^32..], "N", out _), shot);

        // 규칙이 깨져 있어도 저장은 되어야 한다.
        string broken = ImageIO.BuildName("{99}", when, "x");
        Check("규칙이 틀려도 이름이 나온다", broken.Length > 0, broken);
    }

    private static void TestCaptureNaming()
    {
        Section("날짜·시간·UUID 자동 이름과 기존 설정 이행");
        var when = new DateTime(2026, 9, 24, 1, 2, 3, 456);
        var names = new System.Collections.Generic.HashSet<string>();
        bool valid = true;
        for (int i = 0; i < 1000; i++)
        {
            string name = CaptureNames.Build(null, when, null);
            valid &= name.StartsWith("SnapView_2026-09-24_010203_456_") && Guid.TryParseExact(name[^32..], "N", out _);
            names.Add(name);
        }
        Check("같은 시각 1000개 모두 날짜·밀리초·UUID", valid);
        Check("동시각에도 UUID 중복 없음", names.Count == 1000);
        foreach (string pattern in new[] { "custom", "{0:yyyy}", "{99}", "", "{2:N}" })
        {
            string name = CaptureNames.Build(pattern, when, null);
            Check("옛 규칙·깨진 규칙에도 날짜·시간 보충: " + pattern, name.Contains("2026-09-24_010203_456"));
        }
        string canonical = CaptureNames.Build(CaptureNames.DefaultPattern, when, null);
        Check("새 기본 규칙은 시각·UUID를 한 번만 넣음", canonical.Length == "SnapView_2026-09-24_010203_456_".Length + 32);
        var old = new Settings { SettingsVersion = 2, FileNamePattern = "SnapView_{0:yyyy-MM-dd_HHmmss}",
            FrameNamePattern = "스냅뷰_{1}_{0:yyyy-MM-dd_HHmmss}", RecordNamePattern = "SnapView_{1}_{0:yyyy-MM-dd_HHmmss}", CaptureBoundarySnap = false };
        old.Migrate();
        Check("기존 캡처·장면·녹화 기본 규칙을 UUID 규칙으로 이행", old.FileNamePattern == CaptureNames.DefaultPattern && old.FrameNamePattern == CaptureNames.FramePattern && old.RecordNamePattern == RecordingNames.DefaultPattern);
        string json = System.Text.Json.JsonSerializer.Serialize(old);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        Check("핀 끄기는 저장·다시 읽기·설정 이행 뒤에도 유지", !reloaded.CaptureBoundarySnap);
        var custom = new Settings { SettingsVersion = 2, FileNamePattern = "user-prefix" }; custom.Migrate();
        Check("사용자 접두사는 유지하고 날짜·UUID만 보완", custom.FileNamePattern == "user-prefix" && ImageIO.BuildName(custom.FileNamePattern, when, null).StartsWith("user-prefix_2026-09-24_010203_456_"));
    }

    /// <summary>
    /// 이 PC 에 남는 것. 그림·영상을 캐시해 두지는 않지만 기록에는 열어 본 경로가 남아서,
    /// 지우고 싶은 사람이 지울 수 있어야 한다.
    /// </summary>
    private static void TestStoredData()
    {
        Section("이 PC 에 남는 것");

        long before = AppData.ClearableBytes();
        Log.Write("검사용 한 줄");

        Check("기록이 쌓인다", AppData.ClearableBytes() > 0);
        Check("줄 수를 셀 수 있다", AppData.HistoryLines() > 0, AppData.HistoryLines() + "줄");

        bool listed = false;
        foreach (AppData.Item i in AppData.Stored())
            if (i.Clearable && i.Name == "기록") listed = true;
        Check("지울 수 있는 것으로 나온다", listed);

        Check("설정은 지우는 대상이 아니다",
              AppData.Stored().All(i => i.Name != "설정" || !i.Clearable));

        Check("지워진다", AppData.Clear(out string error), error);
        Check("지우고 나면 남는 게 없다", AppData.ClearableBytes() == 0);
        Check("두 번 지워도 괜찮다", AppData.Clear(out _));

        Check("크기를 사람이 읽게 적는다",
              AppData.SizeText(0) == "0 B" && AppData.SizeText(2048).Contains("KB") &&
              AppData.SizeText(5 * 1024 * 1024).Contains("MB"),
              AppData.SizeText(2048) + " / " + AppData.SizeText(5 * 1024 * 1024));

        if (before > 0) Console.WriteLine("         (검사 전에 쌓여 있던 기록은 지워졌습니다)");
    }
}
