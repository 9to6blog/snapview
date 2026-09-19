using System;
using System.Runtime.InteropServices;

namespace SnapView.Core
{
    /// <summary>
    /// 관리자 권한(권한 상승) 판정.
    ///
    /// <b>관리자 권한 창이 앞에 있는 동안에는 일반 권한 프로세스가 등록한 전역 단축키가
    /// 눌리지 않는다</b>(UIPI — 낮은 무결성에서 높은 무결성으로는 입력이 전달되지 않는다).
    /// 게임·런처·안티치트가 관리자로 도는 경우가 많아서, 사용자 눈에는 "게임 안에서만
    /// 단축키가 안 먹는" 것으로 보인다. 등록 자체는 성공하므로 설정 화면의 겹침 검사로는
    /// 안 잡힌다 — 앞 창이 관리자인 것을 보고 알려 주는 수밖에 없다.
    /// </summary>
    internal static class Elevation
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevationClass = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass,
                                                       out int info, int length, out int returned);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private static readonly Lazy<bool> _self = new(() =>
            TokenIsElevated(GetCurrentProcess()) == true);

        /// <summary>이 프로세스가 관리자 권한으로 떠 있는가.</summary>
        internal static bool IsSelfElevated => _self.Value;

        /// <summary>
        /// 이 창의 프로세스가 관리자 권한인가. 물어볼 수 없으면(보호된 프로세스 등) null.
        /// </summary>
        internal static bool? IsWindowElevated(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;

            try { return TokenIsElevated(process); }
            finally { CloseHandle(process); }
        }

        private static bool? TokenIsElevated(IntPtr process)
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;

            try
            {
                if (!GetTokenInformation(token, TokenElevationClass, out int elevated,
                                         sizeof(int), out _))
                    return null;
                return elevated != 0;
            }
            finally { CloseHandle(token); }
        }
    }
}
