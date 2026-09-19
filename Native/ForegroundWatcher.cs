using System;
using System.Runtime.InteropServices;
using System.Text;
using static SnapView.Native.NativeMethods;

namespace SnapView.Native
{
    /// <summary>
    /// 마지막으로 앞에 나와 있던 <b>남의 창</b>을 기억한다.
    ///
    /// "활성 창 캡처" 를 부르는 순간에는 이미 활성 창이 우리 쪽으로 넘어와 있을 때가 있다.
    ///   · 트레이 메뉴에서 고르면 그 메뉴를 띄운 우리 창이 앞이다.
    ///   · 뷰어·편집기를 보다가 단축키를 누르면 그것도 우리 창이다.
    /// 그때 GetForegroundWindow 를 그대로 쓰면 SnapView 자신을 찍거나 아무것도 못 찍는다.
    /// 그래서 앞으로 나오는 창을 계속 지켜보면서 우리 것이 아닌 마지막 창을 들고 있는다.
    /// </summary>
    internal sealed class ForegroundWatcher : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd,
                                           int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
                                                     WinEventProc proc, uint process, uint thread, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        // 델리게이트를 붙들어 둔다. 놓치면 GC 가 걷어가고 콜백이 죽은 주소를 부른다.
        private readonly WinEventProc _proc;
        private readonly uint _ownProcess;
        private IntPtr _hook;
        private bool _disposed;

        /// <summary>우리 것이 아닌, 마지막으로 앞에 있던 창. 없으면 IntPtr.Zero.</summary>
        internal IntPtr LastForeign { get; private set; }

        /// <summary>
        /// 남의 창이 새로 앞에 나왔다. 훅을 건 스레드(UI)에서 불린다.
        /// 관리자 권한 창 감지(단축키가 안 먹는 상황 안내)에 쓴다.
        /// </summary>
        internal event Action<IntPtr>? ForeignChanged;

        internal ForegroundWatcher()
        {
            _ownProcess = (uint)Environment.ProcessId;
            _proc = OnForeground;

            // SKIPOWNPROCESS 를 줘도 우리 창은 애초에 알림이 안 온다. 그래도 한 번 더 거른다.
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                                    _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            Remember(GetForegroundWindow());
        }

        internal bool IsInstalled => _hook != IntPtr.Zero;

        private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd,
                                  int idObject, int idChild, uint thread, uint time)
            => Remember(hwnd);

        private void Remember(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || IsOurs(hwnd) || !IsWindowVisible(hwnd)) return;

            bool changed = hwnd != LastForeign;
            LastForeign = hwnd;
            if (changed) ForeignChanged?.Invoke(hwnd);
        }

        private bool IsOurs(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _ownProcess;
        }

        /// <summary>지금 찍어야 할 창. 앞에 있는 게 남의 창이면 그것, 아니면 기억해 둔 것.</summary>
        internal IntPtr Target()
        {
            IntPtr front = GetForegroundWindow();
            if (front != IntPtr.Zero && !IsOurs(front) && IsWindowVisible(front)) return front;

            return IsWindowVisible(LastForeign) ? LastForeign : IntPtr.Zero;
        }

        /// <summary>기록용 창 설명. 무엇을 찍으려 했는지 남기려고 쓴다.</summary>
        internal static string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "(없음)";

            var title = new StringBuilder(200);
            GetWindowTextW(hwnd, title, title.Capacity);
            return "0x" + hwnd.ToInt64().ToString("X") + " [" + title + "]";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
