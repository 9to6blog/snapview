using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using SnapView.Core;
using SnapView.Prefs;

// PrintScreen 단축키가 실제로 통하는지 끝까지 따라가는 검사.
// (WPF 키 이름 → HotKeySpec → RegisterHotKey 까지)

internal static partial class SelfTest
{
    private static void TestCaptureAltTab()
    {
        Section("캡처 Alt+Tab 차단과 해제");
        using var hook = new SnapView.Native.KeyboardHook();
        Check("캡처 밖에서는 Alt+Tab 허용", !hook.FilterAltTab(0x09, true, true));
        hook.BlockAltTab = true;
        Check("전역 단축키가 없어도 캡처 보호 훅 설치", hook.IsInstalled);
        Check("일반 Tab은 툴바 탐색에 전달", !hook.FilterAltTab(0x09, true, false));
        Check("일반 Tab 키업도 전달", !hook.FilterAltTab(0x09, false, false));
        Check("Alt 드래그의 Alt는 그대로 전달", !hook.FilterAltTab(0x12, true, true));
        Check("Esc 취소 키는 그대로 전달", !hook.FilterAltTab(0x1B, true, true));
        Check("저장 키는 그대로 전달", !hook.FilterAltTab(0x53, true, false));
        Check("Alt+Tab 누름 차단", hook.FilterAltTab(0x09, true, true));
        Check("Tab 자동 반복도 차단", hook.FilterAltTab(0x09, true, true));
        Check("Alt를 먼저 떼어도 Tab 반복 차단", hook.FilterAltTab(0x09, true, false));
        Check("Alt를 먼저 떼어도 Tab 키업 차단", hook.FilterAltTab(0x09, false, false));
        Check("다음 일반 Tab은 정상 동작", !hook.FilterAltTab(0x09, true, false));
        Check("누름이 없던 Tab 키업은 통과", !hook.FilterAltTab(0x09, false, true));

        // Exercise the real native callback with LLKHF_ALTDOWN, without sending keys to Windows.
        IntPtr data = System.Runtime.InteropServices.Marshal.AllocHGlobal(
            System.Runtime.InteropServices.Marshal.SizeOf<SnapView.Native.NativeMethods.KBDLLHOOKSTRUCT>());
        try
        {
            var key = new SnapView.Native.NativeMethods.KBDLLHOOKSTRUCT { vkCode = 0x09, flags = 0x20 };
            System.Runtime.InteropServices.Marshal.StructureToPtr(key, data, false);
            var callback = typeof(SnapView.Native.KeyboardHook).GetMethod("OnKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Check("네이티브 Alt+Tab 이벤트가 실제로 소비됨",
                (IntPtr)callback.Invoke(hook, new object[] { 0, new IntPtr(0x0104), data })! == new IntPtr(1));
            key.flags = 0;
            System.Runtime.InteropServices.Marshal.StructureToPtr(key, data, false);
            Check("네이티브 Tab 키업도 실제로 소비됨",
                (IntPtr)callback.Invoke(hook, new object[] { 0, new IntPtr(0x0101), data })! == new IntPtr(1));
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(data); }

        hook.SetBindings(Array.Empty<SnapView.Native.KeyboardHook.Binding>());
        Check("캡처 중 단축키가 비어도 보호 유지", hook.IsInstalled);
        hook.BlockAltTab = false;
        Check("캡처 보호 해제 시 빈 훅 제거", !hook.IsInstalled);
        Check("캡처 후 Alt+Tab 즉시 허용", !hook.FilterAltTab(0x09, true, true));
        hook.BlockAltTab = true;
        hook.Dispose();
        Check("오버레이 종료 시 훅 완전 해제", !hook.IsInstalled && !hook.BlockAltTab);
        Check("종료 뒤 차단 상태가 남지 않음", !hook.FilterAltTab(0x09, true, true));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint VK_SNAPSHOT = 0x2C;

    private static void TestPrintScreenChain()
    {
        Section("PrintScreen 단축키 (WPF 키 이름 → 등록까지)");

        // 1) WPF 가 주는 Key 값을 우리 이름으로 옮길 수 있는가.
        //    Key.Snapshot 과 Key.PrintScreen 은 같은 값이라 ToString() 이 어느 쪽으로
        //    나오든 상관없이 통해야 한다.
        Check("Key.Snapshot 과 Key.PrintScreen 은 같은 값", Key.Snapshot == Key.PrintScreen,
              $"{(int)Key.Snapshot} / {(int)Key.PrintScreen}");
        Console.WriteLine($"         (Key.Snapshot.ToString() = \"{Key.Snapshot}\")");

        string name = HotKeyBox.KeyName(Key.Snapshot);
        Check("키 이름이 PrintScreen 으로 정규화됨", name == "PrintScreen", name);

        // 2) 그 이름을 되돌려 읽으면 VK_SNAPSHOT 이 나오는가.
        foreach (string text in new[] { "PrintScreen", "PrtSc", "PrtScr", "Snapshot", "printscreen" })
        {
            HotKeySpec? spec = HotKeySpec.Parse(text);
            Check($"\"{text}\" -> VK_SNAPSHOT", spec != null && spec.VirtualKey == VK_SNAPSHOT,
                  spec == null ? "null" : $"0x{spec.VirtualKey:X}");
        }

        HotKeySpec? plain = HotKeySpec.Parse(HotKeyBox.KeyName(Key.Snapshot));
        Check("수식키 없는 단독 PrintScreen 도 유효", plain != null && plain.Modifiers == 0);

        var combos = new[]
        {
            ("Ctrl+PrintScreen", 0x2u), ("Alt+PrintScreen", 0x1u), ("Shift+PrintScreen", 0x4u)
        };
        foreach ((string text, uint mods) in combos)
        {
            HotKeySpec? s = HotKeySpec.Parse(text);
            Check($"{text} 파싱", s != null && s.VirtualKey == VK_SNAPSHOT && s.Modifiers == mods);
        }

        // 3) 이 PC 에서 실제로 등록이 되는가. (RegisterHotKey 는 hWnd 가 NULL 이어도 된다)
        const int probeId = 51234;
        const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        bool registered = RegisterHotKey(IntPtr.Zero, probeId, 0x4000 /*NOREPEAT*/, VK_SNAPSHOT);
        int err = Marshal.GetLastWin32Error();
        if (registered) UnregisterHotKey(IntPtr.Zero, probeId);

        // 1409 는 "누군가 이미 잡고 있다" 는 뜻이다. SnapView 본체가 돌면서 PrtScn 을
        // 쓰고 있으면 당연히 이 값이 나오므로 실패로 보면 안 된다.
        // 여기서 보려는 건 "윈도우가 VK_SNAPSHOT 자체를 막고 있지는 않은가" 다.
        bool usable = registered || err == ERROR_HOTKEY_ALREADY_REGISTERED;
        Check("PrintScreen 을 전역 단축키로 쓸 수 있음", usable, $"err={err}");
        if (!registered && usable)
            Console.WriteLine("         (지금 다른 프로그램 — 아마 SnapView 본체 — 이 잡고 있습니다)");

        // 4) 윈도우가 PrtScn 을 가로채고 있는지 판정.
        bool taken = SystemHotKeys.PrintScreenTakenByWindows();
        Check("가로챔 여부 판정이 예외 없이 동작", true,
              taken ? "지금 윈도우 캡처 도구가 물고 있음" : "지금은 비어 있음");

        // 4-1) 값이 없을 때 "비어 있다" 고 단정하면 안 된다.
        //      윈도우 11 22H2(빌드 22621) 이상은 값이 없어도 기본이 "켜짐" 이라
        //      여기서 false 가 나오면 경고도 해제 안내도 전부 안 뜬 채 키가 씹힌다.
        object? raw = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Control Panel\Keyboard",
            "PrintScreenKeyForSnippingEnabled", null);

        Console.WriteLine($"         (레지스트리 값 = {(raw == null ? "없음" : raw.ToString())}, " +
                          $"OS 빌드 = {Environment.OSVersion.Version})");

        if (raw == null)
        {
            Check("값이 없으면 OS 기본값을 따름",
                  taken == SystemHotKeys.DefaultsToSnippingTool(),
                  $"판정={taken}, 이 버전의 기본={SystemHotKeys.DefaultsToSnippingTool()}");
        }
        else
        {
            Console.WriteLine("         (값이 명시돼 있어 기본값 경로는 이 PC 에서 검사 못 함)");
        }

        Check("22621 이상은 기본이 켜짐으로 취급",
              SystemHotKeys.DefaultsToSnippingTool() ==
              (Environment.OSVersion.Version.Build >= 22621));

        Check("PrtScn 쓰는 단축키 인식", SystemHotKeys.UsesPrintScreen("Ctrl+PrintScreen"));
        Check("PrtScn 안 쓰는 단축키는 아님", !SystemHotKeys.UsesPrintScreen("Ctrl+Shift+A"));
        Check("빈 문자열도 안전", !SystemHotKeys.UsesPrintScreen(""));

        // 4-2) PrtScn 을 실제로 가져오는 건 저수준 훅이다. 훅이 걸리는지 확인한다.
        //      (여기서 실패하면 보안 프로그램이 훅 설치를 막는 환경이다)
        using (var hook = new SnapView.Native.KeyboardHook())
        {
            Check("훅을 걸기 전에는 안 걸려 있음", !hook.IsInstalled);

            hook.SetBindings(new[]
            {
                new SnapView.Native.KeyboardHook.Binding(1, 0, VK_SNAPSHOT)
            });
            Check("PrtScn 단축키로 훅이 설치됨", hook.IsInstalled);

            hook.SetBindings(Array.Empty<SnapView.Native.KeyboardHook.Binding>());
            Check("맡을 키가 없으면 훅을 떼어 냄", !hook.IsInstalled);

            hook.SetBindings(new[]
            {
                new SnapView.Native.KeyboardHook.Binding(1, 0, VK_SNAPSHOT)
            });
            Check("다시 걸림", hook.IsInstalled);
        }

        // 4-3) 기본 단축키가 전부 되돌려 읽히는가. 여기서 깨지면 새로 깐 사람은
        //      단축키가 하나도 안 먹는 채로 시작한다.
        var def = new Settings();
        foreach ((string label, string text) in new[]
                 {
                     ("영역 캡처", def.HotKeyRegion),
                     ("전체 화면", def.HotKeyFullScreen),
                     ("활성 창", def.HotKeyActiveWindow)
                 })
        {
            HotKeySpec? s2 = HotKeySpec.Parse(text);
            Check($"기본 단축키 {label} = {text}", s2 != null,
                  s2 == null ? "파싱 실패" : $"mods=0x{s2.Modifiers:X} vk=0x{s2.VirtualKey:X}");
        }

        // Alt+Shift+PrtScn 은 윈도우 "고대비 켜기/끄기" 핫키다. 기본값으로 쓰면
        // 처음 깐 사람이 전체 화면을 찍으려다 화면이 고대비로 뒤집힌다.
        const uint MOD_ALT_SHIFT = 0x1 | 0x4;
        foreach (string text in new[] { def.HotKeyRegion, def.HotKeyFullScreen, def.HotKeyActiveWindow })
        {
            HotKeySpec? s3 = HotKeySpec.Parse(text);
            bool highContrast = s3 != null && s3.VirtualKey == VK_SNAPSHOT && s3.Modifiers == MOD_ALT_SHIFT;
            Check($"기본 단축키 {text} 는 고대비 핫키가 아님", !highContrast);
        }

        Check("기본 단축키끼리 겹치지 않음",
              def.HotKeyRegion != def.HotKeyFullScreen &&
              def.HotKeyFullScreen != def.HotKeyActiveWindow &&
              def.HotKeyRegion != def.HotKeyActiveWindow);

        // 5) 설정 창에서 쓰는 이름들이 전부 되돌려 읽히는가.
        //    (여기서 빠진 키는 지정해도 저장 후 살아나지 않는다)
        Key[] common =
        {
            Key.F1, Key.F12, Key.Snapshot, Key.Insert, Key.Delete, Key.Home, Key.End,
            Key.PageUp, Key.PageDown, Key.Space, Key.Enter, Key.A, Key.Z,
            Key.D0, Key.D9, Key.NumPad0, Key.NumPad9, Key.OemTilde, Key.Pause, Key.Scroll
        };
        int bad = 0;
        foreach (Key k in common)
        {
            string n = HotKeyBox.KeyName(k);
            HotKeySpec? s = HotKeySpec.Parse("Ctrl+" + n);
            if (s == null || s.VirtualKey != (uint)KeyInterop.VirtualKeyFromKey(k))
            {
                bad++;
                Console.WriteLine($"         (되돌려 읽기 실패: {k} -> \"{n}\")");
            }
        }
        Check("자주 쓰는 키가 모두 왕복됨", bad == 0, bad + "개 실패");

        // 6) 한글 IME 가 켜져 있으면 글자 키가 IME 로 먼저 넘어가 이 칸까지 안 온다.
        //    입력 칸에서 IME 를 꺼 두는지 확인한다.
        var box = new HotKeyBox();
        Check("입력 칸에서 IME 가 꺼져 있음",
              !System.Windows.Input.InputMethod.GetIsInputMethodEnabled(box));
        Check("빈 값이면 (없음) 으로 보임", box.Text == "(없음)", box.Text);

        box.HotKeyText = "Ctrl+Shift+A";
        Check("지정한 값이 그대로 보임", box.Text == "Ctrl+Shift+A", box.Text);
        box.HotKeyText = "";
        Check("지우면 다시 (없음)", box.Text == "(없음)", box.Text);
    }
}
