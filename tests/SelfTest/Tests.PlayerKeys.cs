using System;
using System.Windows.Input;
using SnapView.Core;

// 재생 키와 떠 있는 것 가두기.
// 둘 다 화면을 봐야 알 수 있는 것 같지만, 판단 자체는 순수 계산이라 값으로 묶을 수 있다.

internal static partial class SelfTest
{
    private static void TestPlayerKeys()
    {
        Section("재생 키 맞추기");

        HotKeySpec? space = HotKeySpec.Parse("Space");
        HotKeySpec? left = HotKeySpec.Parse("Left");
        HotKeySpec? ctrlLeft = HotKeySpec.Parse("Ctrl+Left");
        HotKeySpec? shiftS = HotKeySpec.Parse("Shift+S");

        Check("맨 키도 읽힌다", space != null && left != null);
        Check("거들 키가 붙은 것도 읽힌다", ctrlLeft != null && shiftS != null);

        Check("Space 는 Space 에 맞는다", space!.Matches(Key.Space, ModifierKeys.None));
        Check("Space 는 다른 키에 안 맞는다", !space.Matches(Key.Enter, ModifierKeys.None));

        // 여기가 핵심이다. "Left" 가 Ctrl+Left 까지 먹으면, 한 장씩 옮기려고 Ctrl 을 붙인
        // 사람이 통째로 건너뛰게 된다.
        Check("Left 는 Ctrl+Left 를 먹지 않는다", !left!.Matches(Key.Left, ModifierKeys.Control));
        Check("Ctrl+Left 는 맨 Left 를 먹지 않는다", !ctrlLeft!.Matches(Key.Left, ModifierKeys.None));
        Check("Ctrl+Left 는 Ctrl+Left 에 맞는다", ctrlLeft.Matches(Key.Left, ModifierKeys.Control));
        Check("거들 키가 더 붙으면 안 맞는다",
              !ctrlLeft.Matches(Key.Left, ModifierKeys.Control | ModifierKeys.Shift));

        Check("Shift+S 는 맞는다", shiftS!.Matches(Key.S, ModifierKeys.Shift));
        Check("맨 S 는 Shift+S 가 아니다", !shiftS.Matches(Key.S, ModifierKeys.None));

        Check("빈 칸은 아무것도 아니다", HotKeySpec.Parse("") == null && HotKeySpec.Parse(null) == null);
        Check("말이 안 되는 값도 조용히 null", HotKeySpec.Parse("Ctrl+없는키") == null);

        // 기본값이 전부 읽혀야 한다. 하나라도 못 읽으면 그 기능은 키로 못 쓴다.
        var s = new Settings();
        string[] keys =
        {
            s.PlayerKeyPlayPause, s.PlayerKeyBack, s.PlayerKeyForward,
            s.PlayerKeyPrevFrame, s.PlayerKeyNextFrame, s.PlayerKeyVolumeUp,
            s.PlayerKeyVolumeDown, s.PlayerKeyMute, s.PlayerKeyLoop, s.PlayerKeyGrabFrame
        };
        int unreadable = 0;
        foreach (string k in keys) if (HotKeySpec.Parse(k) == null) unreadable++;
        Check("기본 재생 키가 모두 읽힌다", unreadable == 0, unreadable + "개 못 읽음");

        // 기본값끼리 겹치면 하나는 영영 안 먹는다.
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dup = 0;
        foreach (string k in keys) if (!seen.Add(k)) dup++;
        Check("기본 재생 키끼리 안 겹친다", dup == 0, dup + "개 겹침");

        Check("반복은 기본으로 켜져 있다", s.PlayerLoop);
        Check("스페이스가 재생/멈춤", s.PlayerKeyPlayPause == "Space");
        Check("방향키가 앞뒤로 건너뛰기",
              s.PlayerKeyBack == "Left" && s.PlayerKeyForward == "Right");
        Check("건너뛸 시간이 정해져 있다", s.PlayerSkipSeconds > 0, s.PlayerSkipSeconds + "초");

        // 녹화 키도 서로, 그리고 캡처 키와 겹치면 안 된다.
        string[] global =
        {
            s.HotKeyRegion, s.HotKeyFullScreen, s.HotKeyActiveWindow,
            s.HotKeyRecord, s.HotKeyRecordFullScreen, s.HotKeyRecordWindow
        };
        var seenGlobal = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dupGlobal = 0;
        foreach (string g in global) if (!seenGlobal.Add(g)) dupGlobal++;
        Check("기본 전역 단축키끼리 안 겹친다", dupGlobal == 0, dupGlobal + "개 겹침");

        int badGlobal = 0;
        foreach (string g in global) if (HotKeySpec.Parse(g) == null) badGlobal++;
        Check("기본 전역 단축키가 모두 읽힌다", badGlobal == 0, badGlobal + "개 못 읽음");
    }

    /// <summary>
    /// 떠 있는 것을 모니터 안으로 가두기.
    ///
    /// 모니터 크기가 다르면 가상 화면에 어느 모니터에도 없는 빈 구역이 생긴다.
    /// 거기 놓인 도구 막대는 아무 데도 안 보인다 — 실제로 그래서 안 보였다.
    /// </summary>
    private static void TestClampIntoMonitor()
    {
        Section("떠 있는 것 가두기");

        // 2560×1440 옆에 1920×1080. 오른쪽 모니터 아래 360줄이 빈 구역이다.
        const double monX = 2560, monY = 0, monW = 1920, monH = 1080;

        // 화면 아래쪽에서 고른 영역 밑에 놓으려 하면 모니터를 벗어난다 → 위로 끌어올린다.
        (double left, double top) = ViewMath.ClampInto(monX, monY, monW, monH, 3000, 1075, 300, 40);
        Check("모니터 아래로 삐져나가면 끌어올린다", top <= monY + monH - 40, top.ToString("0"));
        Check("빈 구역(1080 아래)에 놓이지 않는다", top + 40 <= 1080, (top + 40).ToString("0"));

        // 오른쪽으로도 마찬가지.
        (left, top) = ViewMath.ClampInto(monX, monY, monW, monH, 4400, 500, 300, 40);
        Check("모니터 오른쪽을 안 넘는다", left + 300 <= monX + monW, (left + 300).ToString("0"));

        // 왼쪽 모니터로 넘어가지도 않아야 한다.
        (left, top) = ViewMath.ClampInto(monX, monY, monW, monH, 100, 500, 300, 40);
        Check("옆 모니터로 넘어가지 않는다", left >= monX, left.ToString("0"));

        // 이미 안에 있으면 그대로 둔다.
        (left, top) = ViewMath.ClampInto(monX, monY, monW, monH, 3000, 500, 300, 40);
        Check("이미 안에 있으면 안 건드린다", left == 3000 && top == 500);

        // 모니터보다 큰 것은 다 못 담는다 — 왼쪽·위를 맞춰 적어도 시작 부분은 보이게.
        (left, top) = ViewMath.ClampInto(monX, monY, monW, monH, 3000, 500, 4000, 2000);
        Check("모니터보다 크면 좌상단을 맞춘다", left == monX && top == monY);

        // 원점이 0 인 흔한 경우도 그대로 통해야 한다.
        (left, top) = ViewMath.ClampInto(0, 0, 2560, 1440, 2500, 1430, 200, 30);
        Check("주 모니터에서도 같은 규칙", left + 200 <= 2560 && top + 30 <= 1440,
              $"{left:0},{top:0}");
    }
}
