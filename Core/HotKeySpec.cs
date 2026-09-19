using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using static SnapView.Native.NativeMethods;

namespace SnapView.Core
{
    /// <summary>"Ctrl+Shift+A" 같은 문자열을 RegisterHotKey 인자로 바꿔 준다.</summary>
    internal sealed class HotKeySpec
    {
        internal uint Modifiers { get; }
        internal uint VirtualKey { get; }
        internal string Display { get; }

        private HotKeySpec(uint modifiers, uint vk, string display)
        {
            Modifiers = modifiers;
            VirtualKey = vk;
            Display = display;
        }

        internal static HotKeySpec? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            uint mods = 0;
            Key key = Key.None;
            var shown = new List<string>();

            foreach (string rawToken in text.Split('+'))
            {
                string t = rawToken.Trim();
                if (t.Length == 0) continue;

                switch (t.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= MOD_CONTROL; shown.Add("Ctrl"); continue;
                    case "shift": mods |= MOD_SHIFT; shown.Add("Shift"); continue;
                    case "alt": mods |= MOD_ALT; shown.Add("Alt"); continue;
                    case "win":
                    case "windows": mods |= MOD_WIN; shown.Add("Win"); continue;
                }

                string keyName = NormalizeKeyName(t);
                if (!Enum.TryParse(keyName, ignoreCase: true, out key))
                    return null;
                shown.Add(t.ToUpperInvariant());
            }

            if (key == Key.None) return null;

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return null;

            return new HotKeySpec(mods, vk, string.Join("+", shown));
        }

        private static string NormalizeKeyName(string t)
        {
            // "1" -> Key.D1, "PrtSc" -> Key.PrintScreen 처럼 WPF Key 이름으로 맞춘다.
            if (t.Length == 1 && t[0] >= '0' && t[0] <= '9') return "D" + t;
            return t.ToLowerInvariant() switch
            {
                "prtsc" or "printscreen" or "prtscr" or "snapshot" => "PrintScreen",
                "esc" => "Escape",
                "enter" => "Return",
                "pgup" => "PageUp",
                "pgdn" or "pgdown" => "PageDown",
                "ins" => "Insert",
                "del" => "Delete",
                "grave" or "`" => "OemTilde",
                _ => t
            };
        }

        /// <summary>
        /// 지금 눌린 키가 이 지정과 같은가. 창 안에서만 듣는 키(뷰어 재생 키)에 쓴다.
        ///
        /// 전역 단축키와 달리 <b>거들 키를 정확히</b> 본다. "Left" 로 지정해 뒀는데
        /// Ctrl+Left 까지 먹으면, 한 프레임씩 옮기려고 Ctrl 을 붙인 사람이 10초를 건너뛰게 된다.
        /// </summary>
        internal bool Matches(Key key, ModifierKeys pressed)
        {
            if ((uint)KeyInterop.VirtualKeyFromKey(key) != VirtualKey) return false;

            uint mods = 0;
            if (pressed.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
            if (pressed.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
            if (pressed.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
            if (pressed.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;

            return mods == Modifiers;
        }

        public override string ToString() => Display;
    }
}
