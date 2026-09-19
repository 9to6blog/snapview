using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnapView.Core;
using static SnapView.Native.NativeMethods;

namespace SnapView.Prefs
{
    /// <summary>
    /// 키를 실제로 눌러서 단축키를 지정하는 칸.
    /// 수식키만 누른 상태에서는 "Ctrl+Shift+…" 처럼 진행 상황만 보여 주고,
    /// 진짜 키가 눌렸을 때 확정한다.
    /// </summary>
    public sealed class HotKeyBox : TextBox
    {
        private static readonly HashSet<Key> ModifierKeys = new()
        {
            Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift,
            Key.LeftAlt, Key.RightAlt, Key.System, Key.LWin, Key.RWin
        };

        private string _value = "";
        private Key _downSeen = Key.None;

        /// <summary>"Ctrl+Shift+A" 형식. 비어 있으면 지정 안 함.</summary>
        internal string HotKeyText
        {
            get => _value;
            set
            {
                _value = value ?? "";
                Text = _value.Length == 0 ? "(없음)" : _value;
            }
        }

        internal event Action? HotKeyChanged;

        public HotKeyBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;

            // 한글 IME 가 켜진 상태에서는 글자 키가 IME 로 먼저 넘어가 버려서
            // 여기까지 원래 키가 오지 않는다. 이 칸에서는 IME 를 아예 끈다.
            InputMethod.SetIsInputMethodEnabled(this, false);
            InputMethod.SetPreferredImeState(this, InputMethodState.Off);
            CaretBrush = System.Windows.Media.Brushes.Transparent;
            Cursor = Cursors.Hand;
            TextAlignment = TextAlignment.Center;
            ToolTip = "여기를 클릭한 뒤 원하는 키 조합을 누르세요. ESC 또는 Delete 로 지웁니다.";

            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            PreviewTextInput += (_, e) => e.Handled = true;
            GotKeyboardFocus += (_, _) => SelectAll();
            LostKeyboardFocus += (_, _) => HotKeyText = _value;   // 진행 중 표시를 되돌린다
            PreviewMouseDown += (_, e) =>
            {
                if (!IsKeyboardFocusWithin) { Focus(); e.Handled = true; }
            };

            HotKeyText = "";   // 값을 넣기 전에도 빈칸이 아니라 "(없음)" 이 보이게
        }

        /// <summary>
        /// PrintScreen 은 윈도우가 <b>WM_KEYDOWN 을 보내지 않고 KEYUP 만</b> 보낸다.
        /// 그래서 KeyDown 만 듣고 있으면 이 키는 영원히 안 잡힌다.
        /// (다른 키는 KeyDown 에서 이미 처리되므로 여기서 두 번 처리하지 않는다.)
        /// </summary>
        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            Key key = RealKey(e);
            if (key != Key.Snapshot) return;      // Key.Snapshot == Key.PrintScreen
            if (_downSeen == key) { _downSeen = Key.None; return; }

            e.Handled = true;
            Accept(key);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            Key key = RealKey(e);
            _downSeen = key;

            if (key is Key.Escape or Key.Delete or Key.Back)
            {
                HotKeyText = "";
                HotKeyChanged?.Invoke();
                return;
            }

            if (key == Key.Tab) { e.Handled = false; return; }   // 다음 칸으로 이동은 살려 둔다

            if (ModifierKeys.Contains(key))
            {
                // 아직 수식키만 눌린 상태 — 진행 상황만 보여 준다
                List<string> shown = CurrentModifiers();
                Text = shown.Count == 0 ? "(없음)" : string.Join("+", shown) + "+…";
                return;
            }

            Accept(key);
        }

        private void Accept(Key key)
        {
            List<string> parts = CurrentModifiers();
            parts.Add(KeyName(key));
            string candidate = string.Join("+", parts);

            if (HotKeySpec.Parse(candidate) == null)
            {
                Text = "쓸 수 없는 키입니다";
                return;
            }

            HotKeyText = candidate;
            HotKeyChanged?.Invoke();
        }

        /// <summary>
        /// WPF 가 감싸 놓은 키에서 실제 키를 꺼낸다.
        ///   · Alt 를 누르면 e.Key 가 System 이 되고 진짜 키는 SystemKey 에 있다.
        ///   · IME 가 먹은 키는 ImeProcessed 로 오고 진짜 키는 ImeProcessedKey 에 있다.
        /// </summary>
        internal static Key RealKey(KeyEventArgs e)
        {
            if (e.Key == Key.System) return e.SystemKey;
            if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed)
            {
                Key ime = e.ImeProcessedKey;
                if (ime != Key.None) return ime;
            }
            return e.Key;
        }

        private static List<string> CurrentModifiers()
        {
            var parts = new List<string>();
            ModifierKeys mods = Keyboard.Modifiers;
            if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & System.Windows.Input.ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & System.Windows.Input.ModifierKeys.Windows) != 0) parts.Add("Win");
            return parts;
        }

        internal static string KeyName(Key key)
        {
            // WPF 의 Key 이름을 사람이 읽는 이름으로. HotKeySpec 이 되돌려 읽을 수 있어야 한다.
            string n = key.ToString();
            if (n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1])) return n[1].ToString();
            return n switch
            {
                "Return" => "Enter",
                "Snapshot" or "PrintScreen" => "PrintScreen",
                "Next" => "PageDown",
                "Prior" => "PageUp",
                _ => n
            };
        }

        /// <summary>
        /// 이 단축키를 지금 실제로 잡을 수 있는지 확인한다.
        /// (다른 프로그램이 이미 쓰고 있으면 false)
        /// </summary>
        internal static bool IsAvailable(string text, IntPtr hwnd, int probeId)
        {
            HotKeySpec? spec = HotKeySpec.Parse(text);
            if (spec == null) return false;

            if (!RegisterHotKey(hwnd, probeId, spec.Modifiers | MOD_NOREPEAT, spec.VirtualKey))
                return false;

            UnregisterHotKey(hwnd, probeId);
            return true;
        }
    }
}
