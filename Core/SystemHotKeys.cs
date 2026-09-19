using System;
using Microsoft.Win32;

namespace SnapView.Core
{
    /// <summary>
    /// 윈도우가 먼저 가로채는 키에 대한 처리.
    ///
    /// PrintScreen 은 두 군데서 막힐 수 있다:
    ///  1) 윈도우 설정 "Print screen 키로 화면 캡처 열기" — 켜져 있으면 OS 가 먼저 먹는다.
    ///  2) 다른 캡처 프로그램이 이미 RegisterHotKey 로 잡아 둔 경우.
    /// 여기서는 1)만 다룬다. 2)는 등록 실패로 드러난다.
    /// </summary>
    internal static class SystemHotKeys
    {
        private const string KeyboardKey = @"HKEY_CURRENT_USER\Control Panel\Keyboard";
        private const string SnipValue = "PrintScreenKeyForSnippingEnabled";

        /// <summary>단축키 문자열이 PrintScreen 을 쓰는가.</summary>
        internal static bool UsesPrintScreen(string? hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey)) return false;
            HotKeySpec? spec = HotKeySpec.Parse(hotkey);
            return spec != null && spec.VirtualKey == 0x2C;   // VK_SNAPSHOT
        }

        /// <summary>윈도우 캡처 도구가 PrtScn 을 물고 있는가.</summary>
        /// <remarks>
        /// 이 레지스트리 값은 사용자가 설정 화면에서 토글을 <b>한 번이라도 건드려야</b> 생긴다.
        /// 안 건드린 새 PC 에서는 값이 아예 없는데, 윈도우 11 22H2(빌드 22621) 부터는
        /// 그 상태의 기본 동작이 "켜짐" 이다. 없다고 false 를 돌려주면 OS 는 키를 물고 있는데
        /// 우리만 비어 있다고 착각해서, 경고도 해제 안내도 전부 안 뜬 채로 키가 씹힌다.
        /// </remarks>
        internal static bool PrintScreenTakenByWindows()
        {
            try
            {
                object? v = Registry.GetValue(KeyboardKey, SnipValue, null);

                if (v is int i) return i != 0;
                // 드물게 REG_SZ 로 들어가 있는 경우도 있다.
                if (v is string s && int.TryParse(s, out int parsed)) return parsed != 0;

                return DefaultsToSnippingTool();   // 값 없음 = OS 기본값을 따른다
            }
            catch { return false; }
        }

        /// <summary>값이 없을 때 이 윈도우가 PrtScn 을 캡처 도구에 넘기는 버전인가.</summary>
        internal static bool DefaultsToSnippingTool()
        {
            // .NET 8 의 OSVersion 은 매니페스트 호환성 심에 영향받지 않고 진짜 빌드를 준다.
            Version v = Environment.OSVersion.Version;
            return v.Major > 10 || (v.Major == 10 && v.Build >= 22621);
        }

    }
}
