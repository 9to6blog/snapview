using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Native
{
    /// <summary>
    /// Windows.Graphics.Capture 로 창 하나만 정확히 떠 온다.
    ///
    /// 화면에서 잘라내는 방식과 달리 <b>위에 겹친 창이 같이 찍히지 않고</b>,
    /// 하드웨어 가속으로 그리는 창(크롬·전자 앱·게임)도 검게 나오지 않는다.
    ///
    /// WinRT 를 C# 프로젝션(Microsoft.Windows.SDK.NET.dll) 대신 COM 으로 직접 부른다.
    /// 그 프로젝션 하나가 25MB 라서, 이 API 하나 쓰자고 실행 파일이 1.5MB → 27MB 가 되기 때문.
    /// IID 와 메서드 순서는 실제 프로젝션 어셈블리에서 확인해 옮긴 값이고,
    /// tests/SelfTest 가 매번 진짜 창을 한 번 떠 보며 검증한다.
    /// </summary>
    internal static unsafe class WindowsGraphicsCapture
    {
        // ---- 런타임 클래스 ----
        private const string ClassCaptureItem = "Windows.Graphics.Capture.GraphicsCaptureItem";
        private const string ClassCaptureSession = "Windows.Graphics.Capture.GraphicsCaptureSession";
        private const string ClassFramePool = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

        // ---- IID ----
        private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static readonly Guid IID_IGraphicsCaptureSessionStatics = new("2224A540-5974-49AA-B232-0882536F4CB5");
        private static readonly Guid IID_IGraphicsCaptureSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
        private static readonly Guid IID_IGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
        private static readonly Guid IID_IFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
        private static readonly Guid IID_IDirect3DDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
        private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

        // ---- vtable 슬롯 (IUnknown 0~2, IInspectable 3~5, 실제 메서드는 6부터) ----
        private const int SlotItemGetSize = 7;                 // IGraphicsCaptureItem
        private const int SlotSessionStartCapture = 6;         // IGraphicsCaptureSession
        private const int SlotSessionPutCursorEnabled = 7;     // IGraphicsCaptureSession2
        private const int SlotSessionPutBorderRequired = 7;    // IGraphicsCaptureSession3
        private const int SlotStaticsIsSupported = 6;          // IGraphicsCaptureSessionStatics
        private const int SlotPoolTryGetNextFrame = 7;         // IDirect3D11CaptureFramePool
        private const int SlotPoolCreateCaptureSession = 10;
        private const int SlotPoolStaticsCreateFreeThreaded = 6;   // IDirect3D11CaptureFramePoolStatics2
        private const int SlotFrameGetSurface = 6;             // IDirect3D11CaptureFrame
        private const int SlotFrameGetContentSize = 8;
        private const int SlotClosableClose = 6;               // IClosable
        private const int SlotInteropCreateForWindow = 3;      // IGraphicsCaptureItemInterop (IUnknown 파생)

        private const int DxgiFormatB8G8R8A8UIntNormalized = 87;

        [StructLayout(LayoutKind.Sequential)]
        private struct SizeInt32
        {
            public int Width;
            public int Height;
        }

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

        private static bool? _supported;

        internal static bool IsSupported
        {
            get
            {
                _supported ??= ProbeSupported();
                return _supported.Value;
            }
        }

        private static bool ProbeSupported()
        {
            IntPtr statics = IntPtr.Zero;
            try
            {
                if (GetActivationFactory(ClassCaptureSession, IID_IGraphicsCaptureSessionStatics, out statics) != 0)
                    return false;

                byte ok;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)
                          Slot(statics, SlotStaticsIsSupported))(statics, &ok);
                return hr == 0 && ok != 0;
            }
            catch { return false; }
            finally { Release(statics); }
        }

        /// <summary>
        /// 창 하나를 캡처한다. 실패하면 null 을 돌려주고 <paramref name="error"/> 에 이유를 담는다.
        /// (예외를 밖으로 던지지 않는다 — 호출하는 쪽이 조용히 대체 수단으로 넘어가야 하므로)
        /// </summary>
        internal static BitmapSource? TryCaptureWindow(IntPtr hwnd, bool includeCursor, out string error)
        {
            error = "";
            if (hwnd == IntPtr.Zero) { error = "창 핸들이 없습니다."; return null; }
            if (!IsSupported) { error = "이 윈도우 버전은 Graphics Capture 를 지원하지 않습니다."; return null; }

            IntPtr item = IntPtr.Zero, device = IntPtr.Zero, context = IntPtr.Zero;
            IntPtr winrtDevice = IntPtr.Zero, pool = IntPtr.Zero, session = IntPtr.Zero, frame = IntPtr.Zero;

            try
            {
                int hr = CreateItemForWindow(hwnd, out item);
                if (hr != 0 || item == IntPtr.Zero)
                { error = $"이 창은 캡처할 수 없습니다 (0x{hr:X8})"; return null; }

                SizeInt32 size;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)
                      Slot(item, SlotItemGetSize))(item, &size);
                if (hr != 0 || size.Width <= 0 || size.Height <= 0)
                { error = "창 크기를 알 수 없습니다."; return null; }

                hr = D3D11.D3D11CreateDevice(IntPtr.Zero, D3D11.DriverTypeHardware, IntPtr.Zero,
                    D3D11.CreateDeviceBgraSupport, IntPtr.Zero, 0, D3D11.SdkVersion,
                    out device, out _, out context);
                if (hr != 0)
                {
                    // 하드웨어 장치가 안 되면 소프트웨어 래스터라이저로 한 번 더
                    hr = D3D11.D3D11CreateDevice(IntPtr.Zero, D3D11.DriverTypeWarp, IntPtr.Zero,
                        D3D11.CreateDeviceBgraSupport, IntPtr.Zero, 0, D3D11.SdkVersion,
                        out device, out _, out context);
                }
                if (hr != 0) { error = $"D3D11 장치 생성 실패 (0x{hr:X8})"; return null; }

                if (D3D11.QueryInterface(device, D3D11.IID_IDXGIDevice, out IntPtr dxgi) != 0)
                { error = "IDXGIDevice 조회 실패"; return null; }

                IntPtr inspectable;
                try { hr = D3D11.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable); }
                finally { D3D11.Release(dxgi); }
                if (hr != 0) { error = $"WinRT 장치 변환 실패 (0x{hr:X8})"; return null; }

                try { hr = D3D11.QueryInterface(inspectable, IID_IDirect3DDevice, out winrtDevice); }
                finally { Release(inspectable); }
                if (hr != 0) { error = "IDirect3DDevice 조회 실패"; return null; }

                hr = CreateFramePool(winrtDevice, size, out pool);
                if (hr != 0 || pool == IntPtr.Zero)
                { error = $"프레임 풀 생성 실패 (0x{hr:X8})"; return null; }

                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)
                      Slot(pool, SlotPoolCreateCaptureSession))(pool, item, &session);
                if (hr != 0 || session == IntPtr.Zero)
                { error = $"캡처 세션 생성 실패 (0x{hr:X8})"; return null; }

                SetBool(session, IID_IGraphicsCaptureSession2, SlotSessionPutCursorEnabled, includeCursor);
                // 캡처 중임을 알리는 노란 테두리 제거 (Win11 / Win10 20348+)
                SetBool(session, IID_IGraphicsCaptureSession3, SlotSessionPutBorderRequired, false);

                hr = ((delegate* unmanaged[Stdcall]<IntPtr, int>)
                      Slot(session, SlotSessionStartCapture))(session);
                if (hr != 0) { error = $"캡처 시작 실패 (0x{hr:X8})"; return null; }

                frame = WaitForFrame(pool, TimeSpan.FromSeconds(2));
                if (frame == IntPtr.Zero) { error = "프레임이 도착하지 않았습니다."; return null; }

                return ToBitmap(frame, device, context, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                CloseAndRelease(frame);
                CloseAndRelease(session);
                CloseAndRelease(pool);
                CloseAndRelease(winrtDevice);
                Release(item);
                D3D11.Release(context);
                D3D11.Release(device);
            }
        }

        // ================================================= COM 잡일

        private static void* Slot(IntPtr obj, int index)
        {
            void** vtbl = *(void***)obj;
            return vtbl[index];
        }

        private static void Release(IntPtr obj) => D3D11.Release(obj);

        /// <summary>WinRT 객체는 Close 로 자원을 먼저 놓아 준 뒤 해제한다.</summary>
        private static void CloseAndRelease(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            try
            {
                if (D3D11.QueryInterface(obj, IID_IClosable, out IntPtr closable) == 0)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, SlotClosableClose))(closable);
                    D3D11.Release(closable);
                }
            }
            catch { }
            D3D11.Release(obj);
        }

        private static int GetActivationFactory(string className, Guid iid, out IntPtr factory)
        {
            factory = IntPtr.Zero;
            int hr = WindowsCreateString(className, className.Length, out IntPtr hstr);
            if (hr != 0) return hr;
            try
            {
                Guid id = iid;
                return RoGetActivationFactory(hstr, ref id, out factory);
            }
            finally { WindowsDeleteString(hstr); }
        }

        private static int CreateItemForWindow(IntPtr hwnd, out IntPtr item)
        {
            item = IntPtr.Zero;

            int hr = GetActivationFactory(ClassCaptureItem, IID_IGraphicsCaptureItemInterop, out IntPtr interop);
            if (hr != 0 || interop == IntPtr.Zero) return hr == 0 ? -1 : hr;

            try
            {
                Guid iid = IID_IGraphicsCaptureItem;
                IntPtr result;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                      Slot(interop, SlotInteropCreateForWindow))(interop, hwnd, &iid, &result);
                item = hr == 0 ? result : IntPtr.Zero;
                return hr;
            }
            finally { Release(interop); }
        }

        private static int CreateFramePool(IntPtr winrtDevice, SizeInt32 size, out IntPtr pool)
        {
            pool = IntPtr.Zero;

            int hr = GetActivationFactory(ClassFramePool, IID_IFramePoolStatics2, out IntPtr statics);
            if (hr != 0 || statics == IntPtr.Zero) return hr == 0 ? -1 : hr;

            try
            {
                IntPtr result;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)
                      Slot(statics, SlotPoolStaticsCreateFreeThreaded))(
                          statics, winrtDevice, DxgiFormatB8G8R8A8UIntNormalized, 2, size, &result);
                pool = hr == 0 ? result : IntPtr.Zero;
                return hr;
            }
            finally { Release(statics); }
        }

        /// <summary>선택 인터페이스의 bool 프로퍼티를 설정한다. 없으면 조용히 넘어간다.</summary>
        private static void SetBool(IntPtr obj, Guid iid, int slot, bool value)
        {
            try
            {
                if (D3D11.QueryInterface(obj, iid, out IntPtr iface) != 0) return;
                try
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(iface, slot))(
                        iface, value ? (byte)1 : (byte)0);
                }
                finally { Release(iface); }
            }
            catch { }
        }

        /// <summary>
        /// 첫 프레임을 기다린다. FrameArrived 이벤트 대신 짧게 폴링하는 이유는
        /// 자유 스레드 풀의 콜백을 COM 으로 직접 다루면 훨씬 복잡해지기 때문.
        /// </summary>
        private static IntPtr WaitForFrame(IntPtr pool, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                IntPtr frame;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)
                          Slot(pool, SlotPoolTryGetNextFrame))(pool, &frame);
                if (hr == 0 && frame != IntPtr.Zero) return frame;
                Thread.Sleep(4);
            }
            return IntPtr.Zero;
        }

        // ================================================= 픽셀 내려받기

        private static BitmapSource? ToBitmap(IntPtr frame, IntPtr device, IntPtr context, out string error)
        {
            error = "";
            IntPtr surface = IntPtr.Zero, access = IntPtr.Zero;
            IntPtr texture = IntPtr.Zero, staging = IntPtr.Zero;
            bool mapped = false;

            try
            {
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)
                          Slot(frame, SlotFrameGetSurface))(frame, &surface);
                if (hr != 0 || surface == IntPtr.Zero) { error = "서피스를 얻지 못했습니다."; return null; }

                SizeInt32 content;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)
                      Slot(frame, SlotFrameGetContentSize))(frame, &content);
                if (hr != 0) { error = "프레임 크기를 얻지 못했습니다."; return null; }

                if (D3D11.QueryInterface(surface, IID_IDirect3DDxgiInterfaceAccess, out access) != 0)
                { error = "DXGI 인터페이스 통로를 얻지 못했습니다."; return null; }

                if (D3D11.DxgiAccessGetInterface(access, D3D11.IID_ID3D11Texture2D, out texture) != 0 ||
                    texture == IntPtr.Zero)
                { error = "텍스처를 얻지 못했습니다."; return null; }

                D3D11.Texture2DDesc desc = D3D11.GetTextureDesc(texture);

                // GPU 텍스처는 CPU 가 직접 못 읽으므로 STAGING 사본을 만들어 복사한다.
                var stagingDesc = new D3D11.Texture2DDesc
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Usage = D3D11.UsageStaging,
                    BindFlags = 0,
                    CpuAccessFlags = D3D11.CpuAccessRead,
                    MiscFlags = 0
                };
                hr = D3D11.CreateTexture2D(device, stagingDesc, out staging);
                if (hr != 0) { error = $"스테이징 텍스처 생성 실패 (0x{hr:X8})"; return null; }

                D3D11.CopyResource(context, staging, texture);

                hr = D3D11.Map(context, staging, out D3D11.MappedSubresource map);
                if (hr != 0) { error = $"텍스처 매핑 실패 (0x{hr:X8})"; return null; }
                mapped = true;

                // 프레임 풀 텍스처가 창보다 클 수 있으므로 실제 내용 크기로 자른다.
                int w = Math.Clamp(content.Width, 1, (int)desc.Width);
                int h = Math.Clamp(content.Height, 1, (int)desc.Height);

                int stride = w * 4;
                var buffer = new byte[checked(stride * h)];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(map.Data + y * (int)map.RowPitch, buffer, y * stride, stride);

                D3D11.Unmap(context, staging);
                mapped = false;

                EnsureVisibleAlpha(buffer);

                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (mapped) D3D11.Unmap(context, staging);
                D3D11.Release(staging);
                D3D11.Release(texture);
                Release(access);
                Release(surface);
            }
        }

        /// <summary>
        /// 알파가 전부 0 으로 오는 창이 있다. 그대로 두면 이미지가 통째로 투명해지므로
        /// 그런 경우에만 불투명하게 돌린다(둥근 모서리가 있는 창은 알파를 살린다).
        /// </summary>
        private static void EnsureVisibleAlpha(byte[] bgra)
        {
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] != 0) return;
            }
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        }
    }
}
