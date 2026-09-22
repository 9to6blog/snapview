using System;
using System.Runtime.InteropServices;

namespace SnapView.Native
{
    /// <summary>Win32 / GDI P/Invoke 모음. 캡처와 창 조작에 필요한 것만 담는다.</summary>
    internal static class NativeMethods
    {
        // ---------- 상수 ----------
        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        internal const int SRCCOPY = 0x00CC0020;
        internal const int CAPTUREBLT = 0x40000000;
        internal const uint DIB_RGB_COLORS = 0;

        internal const int WM_HOTKEY = 0x0312;
        internal const int WM_COPYDATA = 0x004A;

        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_WIN = 0x0008;
        internal const uint MOD_NOREPEAT = 0x4000;

        // ---------- 저수준 키보드 훅 ----------
        internal const int WH_KEYBOARD_LL = 13;
        internal const int WM_KEYDOWN = 0x0100;
        internal const int WM_KEYUP = 0x0101;
        internal const int WM_SYSKEYDOWN = 0x0104;
        internal const int WM_SYSKEYUP = 0x0105;

        internal const int VK_SHIFT = 0x10;
        internal const uint VK_TAB = 0x09;
        internal const uint LLKHF_ALTDOWN = 0x20;
        internal const int VK_CONTROL = 0x11;
        internal const int VK_MENU = 0x12;      // Alt
        internal const int VK_LWIN = 0x5B;
        internal const int VK_RWIN = 0x5C;
        internal const uint VK_SNAPSHOT = 0x2C; // PrintScreen

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        // ---- 모니터 / 창 위치 ----
        internal const uint MONITOR_DEFAULTTONULL = 0;
        internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        // ---- SendMessageTimeout ----
        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        internal const int DWMWA_CLOAKED = 14;

        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        /// <summary>마우스가 이 창을 통과해 아래로 내려간다.</summary>
        internal const int WS_EX_TRANSPARENT = 0x00000020;

        internal const int DI_NORMAL = 0x0003;
        internal const int CURSOR_SHOWING = 0x00000001;

        // 휴지통 이동용
        internal const uint FO_DELETE = 0x0003;
        internal const ushort FOF_ALLOWUNDO = 0x0040;
        internal const ushort FOF_NOCONFIRMATION = 0x0010;
        internal const ushort FOF_SILENT = 0x0004;
        internal const ushort FOF_NOERRORUI = 0x0400;

        // ---------- 구조체 ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        /// <summary>WH_KEYBOARD_LL 훅이 lParam 으로 넘겨 주는 키 정보.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        // ---------- user32 ----------
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>창을 화면 캡처(BitBlt·Graphics Capture)에서 뺀다. Win10 2004 이상.</summary>
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn,
                                                        IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        /// <summary>상대가 멎어 있어도 영원히 매달리지 않는 SendMessage.</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int Msg, IntPtr wParam,
            ref COPYDATASTRUCT lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);

        /// <summary>PW_RENDERFULLCONTENT — 이게 있어야 DirectComposition 으로 그리는 창이 검게 안 나온다.</summary>
        internal const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll")]
        internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        /// <summary>32/64비트 양쪽에서 안전한 GetWindowLong.</summary>
        internal static long GetWindowLongSafe(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        internal static void SetWindowLongSafe(IntPtr hWnd, int nIndex, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
            else SetWindowLong32(hWnd, nIndex, (int)value);
        }

        [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] internal static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        internal static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
            int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

        // ---------- gdi32 ----------
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] internal static extern bool GdiFlush();

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
            uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        internal static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
            IntPtr hdcSrc, int x1, int y1, int rop);

        // ---------- dwmapi ----------
        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        /// <summary>제목 표시줄을 어둡게. Win10 2004 미만에서는 조용히 무시된다.</summary>
        internal static void TryEnableDarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)); }
            catch { }
        }

        // ---------- shell32 ----------
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

        // ---------- shlwapi ----------
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        internal static extern int StrCmpLogicalW(string psz1, string psz2);

        // ---------- winmm ----------
        // 윈도우의 타이머 눈금을 잠깐 잘게 만든다. 기본은 약 15.6ms 라 그보다 짧은
        // 간격을 달라고 해 봐야 안 지켜진다(녹화의 초당 장수가 여기서 막힌다).
        // 반드시 짝을 맞춰 되돌려야 한다 — 안 그러면 시스템이 계속 잘게 깨어 있다.
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        internal static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        internal static extern uint timeEndPeriod(uint ms);
    }
}
