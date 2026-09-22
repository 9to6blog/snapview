using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using static SnapView.Native.NativeMethods;

namespace SnapView.Native
{
    /// <summary>
    /// 저수준 키보드 훅(WH_KEYBOARD_LL)으로 단축키를 직접 가로챈다.
    ///
    /// RegisterHotKey 로는 PrintScreen 을 못 쓴다. 등록 자체는 성공하지만
    /// 윈도우 11 의 "PrtScn 으로 캡처 도구 열기" 가 입력 단계에서 먼저 먹어 버려서
    /// WM_HOTKEY 가 오지 않는다. 저수준 훅은 그 처리보다 앞단이라 키를 먼저 보고,
    /// 0 이 아닌 값을 돌려주면 뒤로 넘어가지 않는다 — 즉 캡처 도구도 못 본다.
    /// 그래서 윈도우 설정을 건드리지 않고도 PrtScn 이 그냥 동작한다.
    ///
    /// 훅 콜백은 훅을 건 스레드의 메시지 펌프 위에서 돈다. 여기서 오래 붙잡고 있으면
    /// 윈도우가 (LowLevelHooksTimeout, 기본 5초) 훅을 조용히 떼어 버리므로,
    /// 실제 일은 반드시 Dispatcher 로 넘기고 즉시 돌아온다.
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        /// <summary>단축키 하나. modifiers 는 MOD_* 조합.</summary>
        internal readonly struct Binding
        {
            internal Binding(int id, uint modifiers, uint virtualKey)
            {
                Id = id; Modifiers = modifiers; VirtualKey = virtualKey;
            }
            internal int Id { get; }
            internal uint Modifiers { get; }
            internal uint VirtualKey { get; }
        }

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly List<Binding> _bindings = new();

        // 델리게이트를 필드로 붙들어 둔다. 지역 변수로 두면 GC 가 걷어가고
        // 그 순간 훅 콜백이 죽은 주소를 부른다.
        private readonly LowLevelKeyboardProc _proc;

        private IntPtr _hook = IntPtr.Zero;
        private uint _firedOnDown;      // 눌림에서 이미 발동한 키 (자동 반복 방지)
        private bool _disposed;
        private bool _blockAltTab;
        private bool _blockedTabDown;

        internal event Action<int>? HotKeyPressed;

        internal KeyboardHook() => _proc = OnKey;

        internal bool IsInstalled => _hook != IntPtr.Zero;

        /// <summary>캡처 오버레이가 떠 있는 동안만 작업 전환을 막는다.</summary>
        internal bool BlockAltTab
        {
            get => _blockAltTab;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _blockAltTab = value;
                if (_blockAltTab || _bindings.Count > 0) Install();
                else Uninstall();
            }
        }

        /// <summary>이 훅이 맡을 단축키 목록을 갈아 끼운다. 비면 훅을 떼어 낸다.</summary>
        internal void SetBindings(IEnumerable<Binding> bindings)
        {
            _bindings.Clear();
            foreach (Binding b in bindings)
                if (b.VirtualKey != 0) _bindings.Add(b);

            if (_bindings.Count == 0 && !_blockAltTab) { Uninstall(); return; }
            Install();
        }

        private bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            return _hook != IntPtr.Zero;
        }

        private void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _firedOnDown = 0;
            _blockedTabDown = false;
        }

        private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!down && !up) return CallNextHookEx(_hook, nCode, wParam, lParam);

            KBDLLHOOKSTRUCT key;
            try { key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam); }
            catch { return CallNextHookEx(_hook, nCode, wParam, lParam); }
            uint vk = key.vkCode;

            if (FilterAltTab(vk, down, (key.flags & LLKHF_ALTDOWN) != 0)) return new IntPtr(1);

            // 이미 눌림에서 발동한 키의 떼임은 조용히 삼킨다.
            // (PrtScn 은 KEYUP 에서 캡처 도구가 열리는 경로도 있어서 짝을 맞춰 막아야 한다)
            if (up && _firedOnDown != 0 && vk == _firedOnDown)
            {
                _firedOnDown = 0;
                return new IntPtr(1);
            }

            int id = Match(vk);
            if (id == 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            if (down)
            {
                if (_firedOnDown == vk) return new IntPtr(1);   // 누르고 있는 중 = 자동 반복
                _firedOnDown = vk;
            }
            else
            {
                // 눌림이 아예 안 왔는데 떼임만 온 경우. PrintScreen 이 이렇게 온다.
                _firedOnDown = 0;
            }

            Fire(id);
            return new IntPtr(1);   // 뒤로 넘기지 않는다 = 캡처 도구도 못 본다
        }

        /// <summary>Alt+Tab의 누름·반복·떼임을 한 쌍으로 소비한다. Shift 유무와 무관하다.</summary>
        internal bool FilterAltTab(uint vk, bool down, bool altDown)
        {
            if (vk != VK_TAB) return false;
            if (down && (_blockedTabDown || (_blockAltTab && altDown)))
            {
                _blockedTabDown = true;
                return true;
            }
            // Alt를 먼저 떼어도 이미 막은 Tab의 키업은 함께 소비한다.
            if (!down && _blockedTabDown) { _blockedTabDown = false; return true; }
            return false;
        }

        /// <summary>지금 눌린 수식키까지 정확히 맞는 단축키의 id. 없으면 0.</summary>
        private int Match(uint vk)
        {
            if (vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN) return 0;

            uint mods = CurrentModifiers();
            foreach (Binding b in _bindings)
                if (b.VirtualKey == vk && b.Modifiers == mods) return b.Id;

            return 0;
        }

        private static uint CurrentModifiers()
        {
            uint mods = 0;
            if (Held(VK_CONTROL)) mods |= MOD_CONTROL;
            if (Held(VK_SHIFT)) mods |= MOD_SHIFT;
            if (Held(VK_MENU)) mods |= MOD_ALT;
            if (Held(VK_LWIN) || Held(VK_RWIN)) mods |= MOD_WIN;
            return mods;
        }

        private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>훅 콜백을 붙잡아 두지 않도록 실제 처리는 UI 스레드로 넘긴다.</summary>
        private void Fire(int id)
        {
            Action<int>? handler = HotKeyPressed;
            if (handler == null) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, handler, id);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _blockAltTab = false;
            Uninstall();
        }
    }
}
