using System;
using System.Windows;
using SnapView.Native;
using static SnapView.Native.NativeMethods;

// 모니터가 꺼졌다 켜진 뒤 창이 화면 밖에 남는 문제를 되돌리는지 확인한다.
// 실제로 창을 만들어 화면 밖으로 밀어낸 뒤 되돌아오는지 본다.
// (투명·작업표시줄 제외 창이라 사용자 눈에는 안 보인다)

internal static partial class SelfTest
{
    private static void TestWindowPlacement()
    {
        Section("창 위치 되돌리기 (모니터 껐다 켬 대비)");

        Window? w = null;
        try
        {
            w = new Window
            {
                Width = 400,
                Height = 300,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            w.Show();

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            Check("검사용 창이 만들어짐", hwnd != IntPtr.Zero);

            // 화면 안에 있을 때는 건드리지 않아야 한다.
            Check("화면 안이면 그대로 둔다", !WindowPlacement.EnsureOnScreen(w));

            // 사라진 모니터에 남은 상황을 흉내 낸다.
            SetWindowPos(hwnd, IntPtr.Zero, -30000, -30000, 400, 300,
                         SWP_NOZORDER | SWP_NOACTIVATE);
            GetWindowRect(hwnd, out RECT gone);
            Check("창을 화면 밖으로 밀어냄", gone.Left < -10000, $"left={gone.Left}");
            Check("그 자리는 어느 모니터에도 안 걸침",
                  MonitorFromRect(ref gone, MONITOR_DEFAULTTONULL) == IntPtr.Zero);

            Check("화면 밖이면 되돌린다", WindowPlacement.EnsureOnScreen(w));

            GetWindowRect(hwnd, out RECT back);
            Check("되돌린 뒤에는 모니터에 걸쳐 있다",
                  MonitorFromRect(ref back, MONITOR_DEFAULTTONULL) != IntPtr.Zero,
                  $"({back.Left},{back.Top}) {back.Width}x{back.Height}");
            Check("크기는 그대로", back.Width == 400 && back.Height == 300,
                  $"{back.Width}x{back.Height}");

            // 이미 제자리면 두 번째 호출은 아무것도 안 해야 한다.
            Check("한 번 되돌린 뒤엔 다시 안 건드린다", !WindowPlacement.EnsureOnScreen(w));

            // 쭈그러든 창은 크기까지 되살린다.
            SetWindowPos(hwnd, IntPtr.Zero, -30000, -30000, 1, 1,
                         SWP_NOZORDER | SWP_NOACTIVATE);
            Check("찌그러진 창도 되돌린다", WindowPlacement.EnsureOnScreen(w));
            GetWindowRect(hwnd, out RECT grown);
            Check("사람이 잡을 수 있는 크기로 되살림",
                  grown.Width >= 200 && grown.Height >= 100,
                  $"{grown.Width}x{grown.Height}");
        }
        finally
        {
            try { w?.Close(); } catch { }
        }
    }
}
