using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 편집기의 순수 로직: 단축키 라우팅 · 패널 활성 규칙 · 실행취소 스택 · 배율 수학 · 최근 색 · 창 기억 · 복구 저장.

internal static partial class SelfTest
{
    private static void TestEditorKeyMap()
    {
        Section("편집기 단축키 라우팅");

        var idle = new EditorKeyState();
        KeyAction R(Key k, ModifierKeys m, EditorKeyState s) => EditorKeyMap.Resolve(k, m, s);

        Check("Ctrl+Z 는 실행취소", R(Key.Z, ModifierKeys.Control, idle).Command == EditorCommand.Undo);
        Check("Ctrl+Shift+Z 는 다시실행", R(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, idle).Command == EditorCommand.Redo);
        Check("Ctrl+Y 도 다시실행", R(Key.Y, ModifierKeys.Control, idle).Command == EditorCommand.Redo);
        Check("Ctrl+Shift+S 는 프로젝트 저장", R(Key.S, ModifierKeys.Control | ModifierKeys.Shift, idle).Command == EditorCommand.SaveProject);
        Check("Ctrl+Shift+0 은 창에 맞춤", R(Key.D0, ModifierKeys.Control | ModifierKeys.Shift, idle).Command == EditorCommand.ZoomFit);

        KeyAction tool = R(Key.A, ModifierKeys.None, idle);
        Check("A 는 화살표 도구", tool.Command == EditorCommand.Tool && tool.Tool == ToolKind.Arrow);
        Check("S 는 영역 선택 도구", R(Key.S, ModifierKeys.None, idle).Tool == ToolKind.RegionSelect);
        Check("Shift+E 는 픽셀지우개", R(Key.E, ModifierKeys.Shift, idle).Tool == ToolKind.PixelEraser);
        Check("숫자 3 은 세 번째 색", R(Key.D3, ModifierKeys.None, idle) is { Command: EditorCommand.PickColor, ColorIndex: 2 });
        Check("F1 은 도움말", R(Key.F1, ModifierKeys.None, idle).Command == EditorCommand.Help);

        KeyAction nudge = R(Key.Left, ModifierKeys.Shift, idle);
        Check("Shift+← 는 10px 이동", nudge.Command == EditorCommand.Nudge && nudge.Dx == -10 && nudge.Dy == 0);

        // 입력칸에 포커스가 있으면 어떤 키도 편집기 명령이 아니다 — 색상판에 'a' 를 치면 도구가 바뀌던 문제.
        var typing = new EditorKeyState { FocusInTextInput = true };
        Check("입력칸 포커스면 글자 키를 안 가로챈다", R(Key.A, ModifierKeys.None, typing).Command == EditorCommand.None);
        Check("입력칸 포커스면 Backspace 도 안 가로챈다", R(Key.Back, ModifierKeys.None, typing).Command == EditorCommand.None);
        Check("입력칸 포커스면 Ctrl+Z 도 입력칸 몫", R(Key.Z, ModifierKeys.Control, typing).Command == EditorCommand.None);

        // Ctrl+C 우선순위: 영역 > 주석 > 결과
        Check("영역이 있으면 영역 복사", R(Key.C, ModifierKeys.Control, new EditorKeyState { HasRegion = true, SelectionCount = 2 }).Command == EditorCommand.CopyRegion);
        Check("주석을 골랐으면 주석 복사", R(Key.C, ModifierKeys.Control, new EditorKeyState { SelectionCount = 2 }).Command == EditorCommand.CopyAnnotations);
        Check("아무것도 없으면 결과 복사", R(Key.C, ModifierKeys.Control, idle).Command == EditorCommand.CopyResult);

        // Delete · Enter · Esc 는 상황에 따라
        Check("마술봉 영역이 있으면 Delete 는 투명 지우기", R(Key.Delete, ModifierKeys.None, new EditorKeyState { HasWandMask = true }).Command == EditorCommand.WandErase);
        Check("확정 대기가 있으면 Delete 는 취소", R(Key.Delete, ModifierKeys.None, new EditorKeyState { HasActive = true }).Command == EditorCommand.CancelActive);
        Check("선택이 있으면 Delete 는 삭제", R(Key.Delete, ModifierKeys.None, new EditorKeyState { SelectionCount = 1 }).Command == EditorCommand.DeleteSelection);
        Check("Esc: 선택이 있으면 선택 해제", R(Key.Escape, ModifierKeys.None, new EditorKeyState { SelectionCount = 1 }).Command == EditorCommand.Deselect);
        Check("Esc: 아무것도 없으면 닫기 요청", R(Key.Escape, ModifierKeys.None, idle).Command == EditorCommand.Close);
        Check("Enter 는 확정", R(Key.Enter, ModifierKeys.None, new EditorKeyState { HasActive = true }).Command == EditorCommand.Confirm);
    }

    private static void TestEditorPanelRules()
    {
        Section("편집기 패널 활성 규칙");

        var none = Array.Empty<Annotation>();
        Check("선택 도구에 아무것도 안 골랐으면 특수 패널 없음", EditorRules.Panels(ToolKind.Select, none) == EditorPanel.None);
        Check("텍스트 도구면 글꼴 패널", EditorRules.Panels(ToolKind.Text, none).HasFlag(EditorPanel.Font));
        Check("선택 도구로 글자를 고르면 글꼴 패널이 켜진다",
              EditorRules.Panels(ToolKind.Select, new[] { new TextAnnotation() }).HasFlag(EditorPanel.Font));
        Check("모자이크를 고르면 세기와 모양 패널",
              EditorRules.Panels(ToolKind.Select, new[] { new PixelateAnnotation() }) is var p && p.HasFlag(EditorPanel.Mask) && p.HasFlag(EditorPanel.MaskShape));
        Check("화살표 도구면 촉 패널", EditorRules.Panels(ToolKind.Arrow, none).HasFlag(EditorPanel.ArrowHead));
        Check("별 도형이면 채우기 패널", EditorRules.Panels(ToolKind.Star5, none).HasFlag(EditorPanel.Fill));
        Check("자르기 도구면 비율 패널", EditorRules.Panels(ToolKind.Crop, none).HasFlag(EditorPanel.Crop));
        Check("강조 도구면 모양 패널", EditorRules.Panels(ToolKind.Spotlight, none).HasFlag(EditorPanel.MaskShape));
        Check("돋보기면 배율 패널", EditorRules.Panels(ToolKind.Magnifier, none).HasFlag(EditorPanel.Magnifier));
        Check("번호면 시작 번호 패널", EditorRules.Panels(ToolKind.Counter, none).HasFlag(EditorPanel.Counter));
    }

    private static void TestUndoStack()
    {
        Section("실행취소 스택 (묶음·메모리 예산)");

        long now = 0;
        var stack = new UndoStack(limit: 3, budgetBytes: long.MaxValue, clock: () => now);
        EditorSnapshot Snap(int n) => new(SolidGif(4, 4, Colors.White), new List<Annotation>(), n);

        Check("처음엔 비어 있다", stack.UndoCount == 0 && !stack.CanUndo);
        stack.Push(Snap(1));
        stack.Push(Snap(2), coalesceKey: "굵기");
        now += 100;
        stack.Push(Snap(3), coalesceKey: "굵기");   // 400ms 안의 같은 종류 → 한 묶음
        Check("연타는 한 칸으로 묶인다", stack.UndoCount == 2, stack.UndoCount.ToString());
        now += 1000;
        stack.Push(Snap(4), coalesceKey: "굵기");
        Check("시간이 지나면 새 칸", stack.UndoCount == 3);
        stack.Push(Snap(5));
        Check("한도를 넘으면 오래된 것부터 버린다", stack.UndoCount == 3);

        EditorSnapshot? back = stack.Undo(Snap(9));
        Check("되돌리면 마지막 칸이 나오고 다시실행이 생긴다", back != null && back.Counter == 5 && stack.CanRedo);
        EditorSnapshot? fwd = stack.Redo(Snap(8));
        Check("다시실행은 현재 상태를 돌려준다", fwd != null && fwd.Counter == 9);
        stack.Push(Snap(6));
        Check("새로 쌓으면 다시실행은 사라진다", !stack.CanRedo);

        // 메모리 예산: 그림이 다른 스냅샷은 그림 크기만큼 센다. 같은 그림을 공유하면 한 번만.
        BitmapSource shared = SolidGif(10, 10, Colors.White);   // 400바이트
        var tight = new UndoStack(limit: 50, budgetBytes: 1000, clock: () => 0);
        for (int i = 0; i < 5; i++) tight.Push(new EditorSnapshot(shared, new List<Annotation>(), i));
        Check("같은 그림을 공유하면 예산을 안 먹는다", tight.UndoCount == 5, tight.UndoCount.ToString());
        for (int i = 0; i < 5; i++) tight.Push(new EditorSnapshot(SolidGif(10, 10, Colors.Red), new List<Annotation>(), 10 + i));
        Check("그림이 다른 스냅샷은 예산(1000바이트=2장)만큼만 남는다", tight.UndoCount <= 3 && tight.UndoCount >= 1, tight.UndoCount.ToString());
        Check("예산에 밀려도 최신 것은 남는다", tight.Undo(Snap(0))?.Counter == 14);
    }

    private static void TestEditorMath()
    {
        Section("편집기 수학 (커서 기준 확대 · 비율 자르기 · 가운데 그리기)");

        // 배율 1 → 2, 커서가 뷰포트 (100,100), 스크롤 0: 커서 아래 그림 점(100,100)이 그대로 있으려면 오프셋 (100,100).
        Vector off = EditorMath.AnchoredOffset(1, 2, new Point(100, 100), new Vector(0, 0));
        Check("커서 기준 확대 오프셋", Math.Abs(off.X - 100) < 0.01 && Math.Abs(off.Y - 100) < 0.01, off.ToString());
        Vector off2 = EditorMath.AnchoredOffset(2, 1, new Point(100, 100), new Vector(100, 100));
        Check("되돌리면 원래 오프셋", Math.Abs(off2.X) < 0.01 && Math.Abs(off2.Y) < 0.01, off2.ToString());

        Point end = EditorMath.FitAspect(new Point(0, 0), new Point(160, 30), 16.0 / 9);
        Check("16:9 비율 자르기는 긴 변에 맞춘다", Math.Abs(end.X - 160) < 0.01 && Math.Abs(end.Y - 90) < 0.01, end.ToString());
        Point end2 = EditorMath.FitAspect(new Point(100, 100), new Point(40, 10), 1);
        Check("왼쪽 위로 끌어도 정사각형", Math.Abs((100 - end2.X) - (100 - end2.Y)) < 0.01 && end2.X < 100 && end2.Y < 100, end2.ToString());

        (Point s, Point e) = EditorMath.FromCenter(new Point(50, 50), new Point(70, 60));
        Check("Alt=가운데에서 그리기", s == new Point(30, 40) && e == new Point(70, 60), $"{s} {e}");

        Check("창 맞춤 배율은 작은 그림을 키운다", EditorMath.FitZoom(200, 100, 800, 800, 8) > 1.0);
        Check("창 맞춤 배율은 큰 그림을 줄인다", EditorMath.FitZoom(4000, 3000, 800, 600, 8) < 0.3);
    }

    private static void TestRecentColorsAndWindowMemory()
    {
        Section("최근 색 · 창 자리 기억");

        var recent = new List<Color>();
        RecentColors.Push(recent, Colors.Red);
        RecentColors.Push(recent, Colors.Blue);
        RecentColors.Push(recent, Colors.Red);
        Check("같은 색은 앞으로 옮겨질 뿐 중복되지 않는다", recent.Count == 2 && recent[0] == Colors.Red && recent[1] == Colors.Blue);
        for (int i = 0; i < 10; i++) RecentColors.Push(recent, Color.FromRgb((byte)i, 0, 0));
        Check("여섯 칸까지만", recent.Count == RecentColors.Max);
        string text = RecentColors.Serialize(recent);
        List<Color> back = RecentColors.Parse(text);
        Check("문자열 왕복", back.Count == recent.Count && back[0] == recent[0], text);
        Check("깨진 문자열은 빈 목록", RecentColors.Parse("잡동사니,#ZZ").Count == 0);

        var mem = new WindowMemory(100, 50, 1200, 800, true);
        Check("창 자리 문자열 왕복", WindowMemory.TryParse(mem.Format(), out WindowMemory? m2) && m2!.Width == 1200 && m2.Maximized);
        Check("이상한 값은 거부", !WindowMemory.TryParse("a,b,c", out _) && !WindowMemory.TryParse("0,0,10,10,false", out _));
    }

    private static void TestRecoveryStore(string tmp)
    {
        Section("편집 중 임시 저장·복구");

        var store = new RecoveryStore(Path.Combine(tmp, "recover"));
        Check("처음엔 없다", !store.Exists);
        var items = new List<Annotation> { new CounterAnnotation { Center = new Point(5, 5), Number = 3 } };
        store.Save(SolidGif(8, 8, Colors.White), items, 4);
        Check("저장하면 생긴다", store.Exists);
        (BitmapSource img, List<Annotation> back, int counter) = store.Load();
        Check("되읽으면 그대로", img.PixelWidth == 8 && back.Count == 1 && counter == 4);
        store.Clear();
        Check("지우면 없다", !store.Exists);
    }

    private static void TestGroupScale()
    {
        Section("여럿 함께 크기 조절");

        var a = new ShapeAnnotation { Kind = ToolKind.Rectangle, Start = new Point(0, 0), End = new Point(10, 10) };
        var b = new ShapeAnnotation { Kind = ToolKind.Rectangle, Start = new Point(20, 0), End = new Point(40, 10) };
        Rect before = ArrangeTools.Union(new[] { a, b });
        ArrangeTools.ScaleGroup(new[] { a, b }, before, new Rect(100, 100, 80, 20));
        Rect after = ArrangeTools.Union(new[] { a, b });
        Check("무리 전체가 목표 사각형에 맞는다", Math.Abs(after.X - 100) < 0.01 && Math.Abs(after.Width - 80) < 0.01 && Math.Abs(after.Height - 20) < 0.01, after.ToString());
        Check("서로의 비율이 유지된다", Math.Abs(a.Bounds.Width - 20) < 0.01 && Math.Abs(b.Bounds.X - 140) < 0.01, $"{a.Bounds} {b.Bounds}");
    }
}
