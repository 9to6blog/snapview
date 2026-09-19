using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static SnapView.Native.NativeMethods;

namespace SnapView.Native
{
    /// <summary>
    /// 창이 지금 화면 안에 있는지 확인하고, 밖으로 나갔으면 끌어다 놓는다.
    ///
    /// 모니터를 끄면 — 특히 DisplayPort 는 전원을 끄면 연결 자체가 끊긴다 —
    /// 윈도우가 디스플레이를 떼어 내고 임시 화면으로 재구성한다. 그때 열려 있던 창은
    /// 사라진 모니터의 좌표에 남거나 쭈그러든 채로 방치되고, 모니터가 돌아와도
    /// 제자리로 안 돌아온다. 그 창은 Activate() 를 불러도 화면에 안 나타난다.
    /// "분명 열라고 했는데 아무 일도 안 일어남" 의 정체가 이거다.
    /// </summary>
    internal static class WindowPlacement
    {
        // 이만큼도 안 보이면 사용자가 잡을 수 없는 창으로 친다.
        private const int MinVisibleWidth = 200;
        private const int MinVisibleHeight = 100;

        /// <summary>창이 화면 밖이면 주 모니터 작업 영역 가운데로 되돌린다. 옮겼으면 true.</summary>
        internal static bool EnsureOnScreen(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out RECT r)) return false;

            if (IsReachable(r)) return false;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
            if (!TryGetWorkArea(monitor, out RECT work)) return false;

            // 창이 작아져 있으면 크기도 되살린다. 1픽셀짜리로 남는 경우가 있다.
            int w = Clamp(r.Width, MinVisibleWidth, work.Width);
            int h = Clamp(r.Height, MinVisibleHeight, work.Height);
            if (r.Width < MinVisibleWidth) w = Math.Min(1100, work.Width);
            if (r.Height < MinVisibleHeight) h = Math.Min(800, work.Height);

            int x = work.Left + (work.Width - w) / 2;
            int y = work.Top + (work.Height - h) / 2;

            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            return true;
        }

        /// <summary>창이 어느 모니터엔가 사람이 잡을 만큼 걸쳐 있는가.</summary>
        private static bool IsReachable(RECT r)
        {
            IntPtr monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero) return false;      // 어느 화면에도 안 걸친다

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref mi)) return true;   // 알 수 없으면 건드리지 않는다

            RECT m = mi.rcMonitor;
            int overlapW = Math.Min(r.Right, m.Right) - Math.Max(r.Left, m.Left);
            int overlapH = Math.Min(r.Bottom, m.Bottom) - Math.Max(r.Top, m.Top);
            return overlapW >= MinVisibleWidth && overlapH >= MinVisibleHeight;
        }

        private static bool TryGetWorkArea(IntPtr monitor, out RECT work)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref mi))
            {
                work = mi.rcWork;
                return work.Width > 0 && work.Height > 0;
            }
            work = default;
            return false;
        }

        /// <summary>
        /// 이 창을 화면 캡처에서 뺀다(WDA_EXCLUDEFROMCAPTURE, Win10 2004+).
        /// 녹화 막대·테두리처럼 "찍는 동안 떠 있지만 찍히면 안 되는" 창에 쓴다.
        /// 창 핸들이 생긴 뒤(SourceInitialized)에 불러야 한다. 성공하면 true.
        /// </summary>
        internal static bool ExcludeFromCapture(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            bool ok = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            if (!ok) Core.Log.Write("창을 캡처에서 빼지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")");
            return ok;
        }

        /// <summary>창이 놓인 모니터의 DPI 배율(1.0 = 96dpi).</summary>
        internal static double DpiScaleOf(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return 1.0;
            uint dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        /// <summary>
        /// 창을 <b>물리 픽셀</b> 사각형에 딱 맞춘다. WPF 의 Left/Top/Width/Height 는 DIP 라
        /// 배율이 100% 가 아닌 모니터에서는 물리 좌표를 그대로 넣으면 어긋난다.
        /// </summary>
        internal static void PlacePhysical(Window window, int x, int y, int width, int height)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height),
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// <paramref name="region"/>(물리 픽셀)이 걸친 모니터의 작업 영역 오른쪽 아래 구석에 창을 놓는다.
        /// 먼저 그 모니터로 옮기고(배율이 다르면 창 크기가 바뀐다) 바뀐 크기로 다시 맞춘다.
        /// </summary>
        internal static void PlaceAtWorkAreaCorner(Window window, Int32Rect region, int marginPx)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var rc = new RECT
            {
                Left = region.X, Top = region.Y,
                Right = region.X + region.Width, Bottom = region.Y + region.Height
            };
            IntPtr monitor = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
            if (!TryGetWorkArea(monitor, out RECT work)) return;

            for (int pass = 0; pass < 2; pass++)
            {
                if (!GetWindowRect(hwnd, out RECT wr)) return;
                int x = work.Right - wr.Width - marginPx;
                int y = work.Bottom - wr.Height - marginPx;
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        private static int Clamp(int v, int min, int max)
            => max < min ? min : Math.Min(Math.Max(v, min), max);
    }
}
