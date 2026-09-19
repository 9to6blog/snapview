using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using static SnapView.Native.NativeMethods;

namespace SnapView.Native
{
    /// <summary>
    /// 보이지 않는 최상위 창. 전역 단축키 수신과 두 번째 실행 인스턴스로부터의
    /// 파일 경로 전달(WM_COPYDATA)을 맡는다.
    /// (메시지 전용 창 HWND_MESSAGE 는 FindWindow 로 못 찾으므로 일부러 일반 창을 쓴다.)
    /// </summary>
    internal sealed class MessageWindow : IDisposable
    {
        internal const string WindowTitle = "SnapView::Ipc::v1";

        /// <summary>기존 인스턴스가 이 시간 안에 대답 못 하면 멎은 것으로 본다.</summary>
        private const uint SendTimeoutMs = 3000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly HwndSource _source;
        private readonly List<int> _hotKeyIds = new();
        private bool _disposed;

        internal IntPtr Handle => _source.Handle;

        /// <summary>등록된 단축키 id 가 눌렸을 때.</summary>
        internal event Action<int>? HotKeyPressed;

        /// <summary>다른 인스턴스가 보낸 요청. 경로가 비어 있으면 "그냥 띄워달라"는 뜻.</summary>
        internal event Action<string>? RequestReceived;

        internal MessageWindow()
        {
            var p = new HwndSourceParameters(WindowTitle)
            {
                Width = 1,
                Height = 1,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = 0,                        // WS_VISIBLE 없음 = 화면에 안 뜬다
                ExtendedWindowStyle = WS_EX_TOOLWINDOW  // Alt+Tab 목록에서 제외
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_HOTKEY:
                    HotKeyPressed?.Invoke(wParam.ToInt32());
                    handled = true;
                    break;

                case WM_COPYDATA:
                    var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                    string payload = cds.cbData > 0 && cds.lpData != IntPtr.Zero
                        ? Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) ?? string.Empty
                        : string.Empty;
                    RequestReceived?.Invoke(payload.TrimEnd('\0'));
                    handled = true;
                    return new IntPtr(1);
            }
            return IntPtr.Zero;
        }

        /// <summary>단축키를 등록한다. 이미 다른 프로그램이 쓰고 있으면 false.</summary>
        internal bool TryRegisterHotKey(int id, uint modifiers, uint virtualKey)
        {
            if (virtualKey == 0) return false;
            if (RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, virtualKey))
            {
                _hotKeyIds.Add(id);
                return true;
            }
            return false;
        }

        internal void UnregisterAll()
        {
            foreach (int id in _hotKeyIds)
                UnregisterHotKey(Handle, id);
            _hotKeyIds.Clear();
        }

        /// <summary>
        /// 이미 떠 있는 인스턴스에 요청을 보낸다. 성공하면 true.
        ///
        /// SendMessage 를 그냥 쓰면 안 된다. 저쪽 UI 스레드가 모달 창 같은 데
        /// 붙들려 있으면 <b>영원히</b> 안 돌아온다. 그러면 탐색기에서 이미지를
        /// 더블클릭한 사용자는 아무 반응도 못 본 채로 기다리게 된다.
        /// 시간을 끊고 실패로 돌려주면 부른 쪽이 대신 처리할 수 있다.
        /// </summary>
        internal static bool SendToExistingInstance(string payload)
        {
            // 저쪽이 막 뜨는 중일 수 있다. 뮤텍스는 먼저 잡히고 창구는 조금 뒤에 열려서,
            // 한 번만 찾아보고 포기하면 "이미 떠 있는데 못 찾겠다" 가 된다.
            IntPtr target = IntPtr.Zero;
            for (int attempt = 0; attempt < 10 && target == IntPtr.Zero; attempt++)
            {
                target = FindWindowW(null, WindowTitle);
                if (target == IntPtr.Zero) System.Threading.Thread.Sleep(100);
            }
            if (target == IntPtr.Zero) return false;

            IntPtr buffer = Marshal.StringToHGlobalUni(payload ?? string.Empty);
            try
            {
                var cds = new COPYDATASTRUCT
                {
                    dwData = IntPtr.Zero,
                    cbData = ((payload?.Length ?? 0) + 1) * 2,
                    lpData = buffer
                };

                IntPtr sent = SendMessageTimeoutW(target, WM_COPYDATA, IntPtr.Zero, ref cds,
                                                  SMTO_ABORTIFHUNG, SendTimeoutMs, out IntPtr answer);
                return sent != IntPtr.Zero && answer != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
