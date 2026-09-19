using System;
using System.Runtime.InteropServices;

namespace SnapView.Native
{
    /// <summary>
    /// Windows.Graphics.Capture 가 돌려주는 GPU 텍스처를 CPU 메모리로 내리기 위한
    /// 최소한의 D3D11 인터롭.
    ///
    /// SharpDX/Vortice 같은 외부 라이브러리를 끌어오지 않으려고 필요한 vtable
    /// 슬롯만 직접 호출한다. <b>슬롯 번호를 틀리면 프로세스가 즉사하므로</b>
    /// tests/SelfTest 에서 실제로 창을 캡처해 보는 검사를 반드시 함께 돌릴 것.
    /// </summary>
    internal static unsafe class D3D11
    {
        internal const int DriverTypeHardware = 1;
        internal const int DriverTypeWarp = 5;
        internal const uint CreateDeviceBgraSupport = 0x20;
        internal const uint SdkVersion = 7;

        internal const uint UsageStaging = 3;
        internal const uint CpuAccessRead = 0x20000;
        internal const uint MapRead = 1;
        internal const uint FormatB8G8R8A8Unorm = 87;

        internal static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        // ---- vtable 슬롯 (IUnknown 이 0~2) ----
        private const int SlotQueryInterface = 0;
        private const int SlotRelease = 2;
        private const int SlotDeviceCreateTexture2D = 5;    // ID3D11Device
        private const int SlotTextureGetDesc = 10;          // ID3D11Texture2D (ID3D11Resource 가 7~9)
        private const int SlotContextMap = 14;              // ID3D11DeviceContext
        private const int SlotContextUnmap = 15;
        private const int SlotContextCopyResource = 47;
        private const int SlotDxgiAccessGetInterface = 3;   // IDirect3DDxgiInterfaceAccess

        [StructLayout(LayoutKind.Sequential)]
        internal struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format;
            public uint SampleCount, SampleQuality;      // DXGI_SAMPLE_DESC
            public uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MappedSubresource
        {
            public IntPtr Data;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [DllImport("d3d11.dll")]
        internal static extern int D3D11CreateDevice(
            IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);

        [DllImport("d3d11.dll")]
        internal static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static void* Slot(IntPtr obj, int index)
        {
            void** vtbl = *(void***)obj;
            return vtbl[index];
        }

        internal static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
        {
            IntPtr r;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                      Slot(obj, SlotQueryInterface))(obj, &iid, &r);
            result = hr == 0 ? r : IntPtr.Zero;
            return hr;
        }

        internal static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, SlotRelease))(obj);
        }

        internal static int CreateTexture2D(IntPtr device, Texture2DDesc desc, out IntPtr texture)
        {
            IntPtr tex;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)
                      Slot(device, SlotDeviceCreateTexture2D))(device, &desc, null, &tex);
            texture = hr == 0 ? tex : IntPtr.Zero;
            return hr;
        }

        internal static Texture2DDesc GetTextureDesc(IntPtr texture)
        {
            Texture2DDesc d;
            ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)
             Slot(texture, SlotTextureGetDesc))(texture, &d);
            return d;
        }

        internal static void CopyResource(IntPtr context, IntPtr dst, IntPtr src)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)
             Slot(context, SlotContextCopyResource))(context, dst, src);
        }

        internal static int Map(IntPtr context, IntPtr resource, out MappedSubresource mapped)
        {
            MappedSubresource m;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)
                      Slot(context, SlotContextMap))(context, resource, 0, MapRead, 0, &m);
            mapped = m;
            return hr;
        }

        internal static void Unmap(IntPtr context, IntPtr resource)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
             Slot(context, SlotContextUnmap))(context, resource, 0);
        }

        internal static int DxgiAccessGetInterface(IntPtr access, Guid iid, out IntPtr result)
        {
            IntPtr r;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                      Slot(access, SlotDxgiAccessGetInterface))(access, &iid, &r);
            result = hr == 0 ? r : IntPtr.Zero;
            return hr;
        }
    }
}
