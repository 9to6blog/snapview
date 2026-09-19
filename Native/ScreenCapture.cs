using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static SnapView.Native.NativeMethods;

namespace SnapView.Native
{
    /// <summary>
    /// 화면 픽셀을 그대로 떠오는 계층. 모든 좌표는 <b>물리 픽셀</b>이다
    /// (앱이 PerMonitorV2 라서 GetSystemMetrics/DWM 이 물리 픽셀을 돌려준다).
    /// </summary>
    internal static class ScreenCapture
    {
        private const uint GA_ROOT = 2;

        /// <summary>모든 모니터를 감싸는 가상 화면 영역(물리 픽셀).</summary>
        internal static Int32Rect VirtualScreen()
        {
            return new Int32Rect(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        internal static BitmapSource CaptureVirtualScreen(bool includeCursor = false)
            => CaptureRect(VirtualScreen(), includeCursor);

        internal static BitmapSource CaptureRect(Int32Rect r, bool includeCursor = false)
            => CaptureRect(r.X, r.Y, r.Width, r.Height, includeCursor);

        /// <summary>지정한 화면 영역을 32bpp 로 떠서 고정(Freeze)된 BitmapSource 로 돌려준다.</summary>
        internal static BitmapSource CaptureRect(int x, int y, int w, int h, bool includeCursor = false)
        {
            if (w <= 0 || h <= 0)
                throw new ArgumentException("캡처 영역의 크기가 0 이하입니다.");

            BitmapSource? result = RenderToBitmap(w, h, memDc => BlitScreen(memDc, x, y, w, h, includeCursor));
            return result ?? throw new InvalidOperationException("화면 복사에 실패했습니다.");
        }

        /// <summary>
        /// 지정한 화면 영역을 <paramref name="into"/>(BGRX, 줄 간격 w×4)에 바로 떠 넣는다.
        /// 녹화처럼 초당 수십 번 부를 때 BitmapSource 를 만들지 않고 버퍼를 재사용하기 위한 것 —
        /// 1080p 한 장이 8MB 라 프레임마다 새로 잡으면 초당 수백 MB 를 할당하게 된다.
        /// 버퍼는 w×h×4 이상이어야 한다. 성공하면 true.
        /// </summary>
        internal static bool CaptureRectInto(Int32Rect r, bool includeCursor, byte[] into)
        {
            if (r.Width <= 0 || r.Height <= 0)
                throw new ArgumentException("캡처 영역의 크기가 0 이하입니다.");
            if (into.Length < checked(r.Width * r.Height * 4))
                throw new ArgumentException("픽셀 버퍼가 영역보다 작습니다.");

            return RenderToBuffer(r.Width, r.Height,
                                  memDc => BlitScreen(memDc, r.X, r.Y, r.Width, r.Height, includeCursor), into);
        }

        /// <summary>화면 DC 에서 메모리 DC 로 한 장 복사한다(커서 포함 선택).</summary>
        private static bool BlitScreen(IntPtr memDc, int x, int y, int w, int h, bool includeCursor)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            try
            {
                // CAPTUREBLT 를 넣어야 레이어드 창(반투명 UI, 툴팁)이 같이 찍힌다.
                if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                    return false;
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }

            if (includeCursor) TryDrawCursor(memDc, x, y);
            return true;
        }

        /// <summary>
        /// 32bpp top-down DIB 를 만들어 <paramref name="draw"/> 에게 그리게 하고
        /// 그 픽셀을 BitmapSource 로 옮긴다.
        /// </summary>
        private static BitmapSource? RenderToBitmap(int w, int h, Func<IntPtr, bool> draw)
        {
            if (w <= 0 || h <= 0) return null;

            int stride = w * 4;
            var buffer = new byte[checked(stride * h)];
            if (!RenderToBuffer(w, h, draw, buffer)) return null;

            // Bgra32 가 아니라 Bgr32: GDI 가 채운 알파 바이트는 대개 0 이라
            // Bgra32 로 읽으면 전체가 투명해진다.
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// 32bpp top-down DIB 를 만들어 <paramref name="draw"/> 에게 그리게 하고
        /// 그 픽셀을 <paramref name="into"/> 로 복사한다. GDI 자원 정리를 한곳에 모아 두기 위한 것.
        /// </summary>
        private static bool RenderToBuffer(int w, int h, Func<IntPtr, bool> draw, byte[] into)
        {
            if (w <= 0 || h <= 0) return false;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;

            IntPtr memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return false;

                var bi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // 음수 = top-down. 안 그러면 상하가 뒤집힌다.
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0       // BI_RGB
                };

                hBmp = CreateDIBSection(screenDc, ref bi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
                if (hBmp == IntPtr.Zero || bits == IntPtr.Zero) return false;

                oldBmp = SelectObject(memDc, hBmp);

                if (!draw(memDc)) return false;
                GdiFlush();

                Marshal.Copy(bits, into, 0, checked(w * 4 * h));
                return true;
            }
            finally
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static void TryDrawCursor(IntPtr hdc, int originX, int originY)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
                return;

            IntPtr icon = CopyIcon(ci.hCursor);
            if (icon == IntPtr.Zero) return;
            try
            {
                int hotX = 0, hotY = 0;
                if (GetIconInfo(icon, out ICONINFO ii))
                {
                    hotX = ii.xHotspot;
                    hotY = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                DrawIconEx(hdc,
                    ci.ptScreenPos.X - originX - hotX,
                    ci.ptScreenPos.Y - originY - hotY,
                    icon, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally { DestroyIcon(icon); }
        }

        /// <summary>
        /// 현재 활성 창의 실제 보이는 영역. GetWindowRect 는 Win10 이후 보이지 않는
        /// 리사이즈 여백까지 포함하므로 DWM 확장 프레임을 우선 쓴다.
        /// </summary>
        internal static Int32Rect? ForegroundWindowRect()
            => WindowRect(GetForegroundWindow());

        internal static Int32Rect? WindowRect(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return null;

            RECT r;
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                                           out r, Marshal.SizeOf<RECT>());
            if (hr != 0 && !GetWindowRect(hwnd, out r))
                return null;

            var vs = VirtualScreen();
            int left = Math.Max(r.Left, vs.X);
            int top = Math.Max(r.Top, vs.Y);
            int right = Math.Min(r.Right, vs.X + vs.Width);
            int bottom = Math.Min(r.Bottom, vs.Y + vs.Height);
            if (right - left <= 0 || bottom - top <= 0) return null;

            return new Int32Rect(left, top, right - left, bottom - top);
        }

        /// <summary>어떤 방법으로 창을 떴는지.</summary>
        internal enum WindowCaptureMethod
        {
            /// <summary>Windows.Graphics.Capture — 겹친 창이 안 찍히는 정확한 방식.</summary>
            GraphicsCapture,
            /// <summary>PrintWindow(PW_RENDERFULLCONTENT) — 창에게 직접 그리게 시킨다.</summary>
            PrintWindow,
            /// <summary>화면에서 창 영역만 잘라내기 — 겹친 창이 같이 찍힌다.</summary>
            ScreenCrop
        }

        /// <summary>
        /// 창 하나를 가장 정확한 방법부터 차례로 시도해서 캡처한다.
        /// GraphicsCapture → PrintWindow → 화면 잘라내기 순.
        /// </summary>
        internal static BitmapSource? CaptureWindowSmart(IntPtr hwnd, bool includeCursor,
                                                         bool preferGraphicsCapture,
                                                         out WindowCaptureMethod used, out string note)
        {
            used = WindowCaptureMethod.ScreenCrop;
            note = "";

            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                note = "창이 보이지 않거나 최소화되어 있습니다.";
                return null;
            }

            if (preferGraphicsCapture && WindowsGraphicsCapture.IsSupported)
            {
                BitmapSource? wgc = WindowsGraphicsCapture.TryCaptureWindow(hwnd, includeCursor, out string err);
                if (wgc != null && !LooksBlank(wgc))
                {
                    used = WindowCaptureMethod.GraphicsCapture;
                    return wgc;
                }
                note = string.IsNullOrEmpty(err) ? "빈 화면이 와서 다른 방법으로 넘어갑니다." : err;
            }

            BitmapSource? printed = TryPrintWindow(hwnd);
            if (printed != null && !LooksBlank(printed))
            {
                used = WindowCaptureMethod.PrintWindow;
                return printed;
            }

            Int32Rect? r = WindowRect(hwnd);
            if (r == null) { note = "창 영역을 구하지 못했습니다."; return null; }

            used = WindowCaptureMethod.ScreenCrop;
            return CaptureRect(r.Value, includeCursor);
        }

        /// <summary>
        /// 창에게 자기 자신을 그리게 시킨다. 겹친 창이 안 찍히는 대신,
        /// 일부 앱(특히 보호된 창)은 검은 화면을 돌려준다.
        /// </summary>
        internal static BitmapSource? TryPrintWindow(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT wr)) return null;
            int fullW = wr.Width, fullH = wr.Height;
            if (fullW <= 0 || fullH <= 0) return null;

            // PrintWindow 는 GetWindowRect 기준으로 그린다. Win10 이후 그 사각형에는
            // 보이지 않는 리사이즈 여백이 들어 있으므로 DWM 프레임만큼 다시 잘라낸다.
            int cropX = 0, cropY = 0, cropW = fullW, cropH = fullH;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwm,
                                      Marshal.SizeOf<RECT>()) == 0 &&
                dwm.Width > 0 && dwm.Height > 0)
            {
                cropX = Math.Clamp(dwm.Left - wr.Left, 0, fullW - 1);
                cropY = Math.Clamp(dwm.Top - wr.Top, 0, fullH - 1);
                cropW = Math.Clamp(dwm.Width, 1, fullW - cropX);
                cropH = Math.Clamp(dwm.Height, 1, fullH - cropY);
            }

            BitmapSource? full = RenderToBitmap(fullW, fullH,
                memDc => PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT));
            if (full == null) return null;

            if (cropX == 0 && cropY == 0 && cropW == fullW && cropH == fullH) return full;

            var cropped = new CroppedBitmap(full, new Int32Rect(cropX, cropY, cropW, cropH));
            cropped.Freeze();
            return cropped;
        }

        /// <summary>모든 픽셀이 같은 색이면 캡처가 실패한 것으로 본다(대개 새까만 화면).</summary>
        private static bool LooksBlank(BitmapSource bmp)
        {
            try
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w <= 0 || h <= 0) return true;

                var conv = bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32
                    ? bmp
                    : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

                var px = new byte[4];
                uint first = 0;
                bool haveFirst = false;

                // 격자로 100 군데만 찍어 본다. 전부 같으면 빈 화면.
                for (int i = 0; i < 10; i++)
                {
                    for (int j = 0; j < 10; j++)
                    {
                        int x = Math.Min(w - 1, i * w / 10 + w / 20);
                        int y = Math.Min(h - 1, j * h / 10 + h / 20);
                        new CroppedBitmap(conv, new Int32Rect(x, y, 1, 1)).CopyPixels(px, 4, 0);
                        uint v = BitConverter.ToUInt32(px, 0);
                        if (!haveFirst) { first = v; haveFirst = true; }
                        else if (v != first) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>오버레이에서 "클릭 한 번으로 창 캡처" 후보가 되는 창.</summary>
        internal readonly struct CapturableWindow
        {
            internal CapturableWindow(IntPtr hwnd, Int32Rect rect) { Hwnd = hwnd; Rect = rect; }
            internal IntPtr Hwnd { get; }
            internal Int32Rect Rect { get; }
        }

        /// <summary>
        /// 화면에 실제로 보이는 최상위 창들을 z-order 위에서 아래 순서로 모은다.
        /// 오버레이를 띄우기 <b>전에</b> 호출해야 우리 창이 목록에 안 낀다.
        /// </summary>
        internal static System.Collections.Generic.List<CapturableWindow> EnumerateVisibleWindows()
        {
            var list = new System.Collections.Generic.List<CapturableWindow>();
            var vs = VirtualScreen();

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

                // 도구 창(툴팁·트레이 보조창 등)은 후보에서 뺀다.
                long ex = GetWindowLongSafe(hwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

                // 화면 밖에 숨겨 둔 UWP 창(cloaked)은 실제로는 안 보인다.
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                Int32Rect? r = WindowRect(hwnd);
                if (r == null) return true;
                if (r.Value.Width < 24 || r.Value.Height < 24) return true;

                // 가상 화면 전체를 덮는 바탕화면류는 선택 후보로 쓸모가 없다.
                if (r.Value.Width >= vs.Width && r.Value.Height >= vs.Height) return true;

                list.Add(new CapturableWindow(hwnd, r.Value));
                return true;
            }, IntPtr.Zero);

            return list;
        }

        /// <summary>커서 아래에 있는 최상위 창(자식 컨트롤이 아니라 창 전체).</summary>
        internal static IntPtr TopLevelWindowUnderCursor()
        {
            if (!GetCursorPos(out POINT p)) return IntPtr.Zero;
            IntPtr h = WindowFromPoint(p);
            return h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GA_ROOT);
        }
    }
}
