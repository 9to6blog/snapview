using System;
using System.Runtime.InteropServices;

namespace SnapView.Native
{
    /// <summary>
    /// Media Foundation 의 SinkWriter 로 H.264 MP4 를 쓴다.
    ///
    /// WinRT C# 프로젝션(Microsoft.Windows.SDK.NET.dll)을 쓰면 이 기능 하나에
    /// 실행 파일이 25MB 불어난다. 그래서 <see cref="WindowsGraphicsCapture"/> 와 같은 방식으로
    /// COM 인터페이스를 vtable 슬롯으로 직접 부른다.
    ///
    /// <b>슬롯 번호를 틀리면 엉뚱한 함수가 불려 프로세스가 죽는다.</b>
    /// 번호는 mfobjects.h · mfreadwrite.h 의 선언 순서 그대로이고,
    /// tests/SelfTest 가 매번 진짜 MP4 를 만들어 보며 검증한다.
    /// </summary>
    internal static unsafe class MediaFoundation
    {
        // MF_SDK_VERSION(0x0002) << 16 | MF_API_VERSION(0x0070)
        private const uint Version = 0x00020070;

        // ---- 특성 GUID ----
        internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

        /// <summary>
        /// 줄 걸러 그리는 옛 방식인지. 요즘 화면은 전부 Progressive(2).
        /// <b>이게 없으면 인코더가 형식을 통째로 거절한다</b>(MF_E_INVALIDMEDIATYPE).
        /// </summary>
        internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        internal const uint InterlaceProgressive = 2;

        // ---- 소리 ----
        internal static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        internal static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
        internal static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");

        internal static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        internal static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        internal static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        internal static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        internal static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        internal static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        internal static readonly Guid MF_MT_AAC_PROFILE_LEVEL = new("7632f0e6-9538-4d61-acda-ea29c8c14456");

        // ---- 미디어 종류 ----
        internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");

        /// <summary>'NV12'. 윈도우 H.264 인코더가 그대로 받아 주는 형식.</summary>
        internal static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

        // ---- SourceReader (영상 되읽기: 컷 편집·배속용) ----
        // 스트림 번호 대신 쓰는 약속된 값들 (mfreadwrite.h)
        internal const uint FirstVideoStream = 0xFFFFFFFC;
        internal const uint FirstAudioStream = 0xFFFFFFFD;
        internal const uint AnyStream = 0xFFFFFFFE;
        internal const uint AllStreams = 0xFFFFFFFE;

        // ReadSample 이 돌려주는 깃발 (MF_SOURCE_READER_FLAG)
        // 0x1 은 ERROR 다 — 끝(0x2)과 헷갈리면 끝을 영영 못 알아보고 무한 루프에 빠진다.
        internal const uint ReadFlagError = 0x1;
        internal const uint ReadFlagEndOfStream = 0x2;
        internal const uint ReadFlagStreamTick = 0x100;

        // ---- vtable 슬롯 ----
        // IUnknown 이 0~2. IMFAttributes 가 3~32, 그 뒤로 파생 인터페이스가 이어진다.
        private const int SlotRelease = 2;

        private const int AttrGetUINT32 = 7;
        private const int AttrGetUINT64 = 8;
        private const int AttrGetGUID = 10;
        private const int AttrSetUINT32 = 21;
        private const int AttrSetUINT64 = 22;
        private const int AttrSetGUID = 24;
        private const int AttrGetCount = 30;

        // IMFSample (IMFAttributes 다음)
        private const int SampleGetSampleTime = 35;
        private const int SampleSetSampleTime = 36;
        private const int SampleSetSampleDuration = 38;
        private const int SampleConvertToContiguous = 41;
        private const int SampleAddBuffer = 42;

        // IMFMediaBuffer
        private const int BufferLock = 3;
        private const int BufferUnlock = 4;
        private const int BufferGetCurrentLength = 5;
        private const int BufferSetCurrentLength = 6;

        // IMFSinkWriter
        private const int WriterAddStream = 3;
        private const int WriterSetInputMediaType = 4;
        private const int WriterBeginWriting = 5;
        private const int WriterWriteSample = 6;
        private const int WriterFinalize = 11;

        // IMFSourceReader (mfreadwrite.h 선언 순서, IUnknown 다음부터)
        private const int ReaderSetStreamSelection = 4;
        private const int ReaderGetCurrentMediaType = 6;
        private const int ReaderSetCurrentMediaType = 7;
        private const int ReaderReadSample = 9;

        // ---- 함수 ----
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateMediaType(out IntPtr type);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateMemoryBuffer(int maxLength, out IntPtr buffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateSample(out IntPtr sample);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream,
                                                             IntPtr attributes, out IntPtr writer);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes,
                                                               out IntPtr reader);

        // ---- 시작/종료 ----
        private static int _started;
        private static readonly object Gate = new();

        /// <summary>MF 를 켠다. 여러 번 불러도 안전하다.</summary>
        internal static void Startup()
        {
            lock (Gate)
            {
                if (_started++ > 0) return;
                Check(MFStartup(Version, 0), "MFStartup");
            }
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                if (_started == 0 || --_started > 0) return;
                MFShutdown();
            }
        }

        // ---- vtable 호출 ----
        private static void* Slot(IntPtr obj, int index)
        {
            void** vtbl = *(void***)obj;
            return vtbl[index];
        }

        internal static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, SlotRelease))(obj);
        }

        internal static void SetGuid(IntPtr attributes, Guid key, Guid value)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)
                      Slot(attributes, AttrSetGUID))(attributes, &key, &value), "SetGUID");

        /// <summary>검사용: 넣은 값이 실제로 들어갔는지 되읽는다.</summary>
        internal static ulong GetUInt64(IntPtr attributes, Guid key)
        {
            ulong v;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)
                   Slot(attributes, AttrGetUINT64))(attributes, &key, &v), "GetUINT64");
            return v;
        }

        internal static uint GetCount(IntPtr attributes)
        {
            uint n;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)
                   Slot(attributes, AttrGetCount))(attributes, &n), "GetCount");
            return n;
        }

        internal static void SetUInt32(IntPtr attributes, Guid key, uint value)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)
                      Slot(attributes, AttrSetUINT32))(attributes, &key, value), "SetUINT32");

        internal static void SetUInt64(IntPtr attributes, Guid key, ulong value)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)
                      Slot(attributes, AttrSetUINT64))(attributes, &key, value), "SetUINT64");

        /// <summary>가로·세로처럼 두 값을 하나에 담는 특성(위 32비트 · 아래 32비트).</summary>
        internal static void SetPair(IntPtr attributes, Guid key, uint high, uint low)
            => SetUInt64(attributes, key, ((ulong)high << 32) | low);

        internal static void SampleSetTime(IntPtr sample, long ticks100ns)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)
                      Slot(sample, SampleSetSampleTime))(sample, ticks100ns), "SetSampleTime");

        internal static void SampleSetDuration(IntPtr sample, long ticks100ns)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)
                      Slot(sample, SampleSetSampleDuration))(sample, ticks100ns), "SetSampleDuration");

        internal static void SampleAdd(IntPtr sample, IntPtr buffer)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)
                      Slot(sample, SampleAddBuffer))(sample, buffer), "AddBuffer");

        internal static IntPtr BufferLockPtr(IntPtr buffer, out int maxLength)
        {
            IntPtr data;
            int max, current;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int*, int*, int>)
                   Slot(buffer, BufferLock))(buffer, &data, &max, &current), "Lock");
            maxLength = max;
            return data;
        }

        internal static void BufferUnlockPtr(IntPtr buffer)
            => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, BufferUnlock))(buffer);

        internal static void BufferSetLength(IntPtr buffer, int length)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)
                      Slot(buffer, BufferSetCurrentLength))(buffer, length), "SetCurrentLength");

        internal static int WriterAdd(IntPtr writer, IntPtr mediaType)
        {
            int index;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)
                   Slot(writer, WriterAddStream))(writer, mediaType, &index), "AddStream");
            return index;
        }

        internal static void WriterSetInput(IntPtr writer, int stream, IntPtr mediaType)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int>)
                      Slot(writer, WriterSetInputMediaType))(writer, stream, mediaType, IntPtr.Zero),
                     "SetInputMediaType");

        internal static void WriterBegin(IntPtr writer)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)
                      Slot(writer, WriterBeginWriting))(writer), "BeginWriting");

        internal static void WriterWrite(IntPtr writer, int stream, IntPtr sample)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)
                      Slot(writer, WriterWriteSample))(writer, stream, sample), "WriteSample");

        internal static void WriterFinish(IntPtr writer)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)
                      Slot(writer, WriterFinalize))(writer), "Finalize");

        // ---- SourceReader (되읽기) ----

        /// <summary>
        /// 디코더가 주는 프레임 버퍼는 정렬 여유(피치·높이 패딩)가 붙은 GPU 표면일 수 있다.
        /// 이 인터페이스의 ContiguousCopyTo 가 표준 배치(NV12 = 딱 W×H×3/2)로 복사해 준다.
        /// ConvertToContiguousBuffer 는 버퍼가 하나면 <b>변환 없이 그대로</b> 돌려주므로 못 믿는다.
        /// </summary>
        internal static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

        // IMF2DBuffer vtable (IUnknown 다음): Lock2D=3, Unlock2D=4, GetScanline0AndPitch=5,
        // IsContiguousFormat=6, GetContiguousLength=7, ContiguousCopyTo=8
        private const int Buffer2DLock2D = 3;
        private const int Buffer2DUnlock2D = 4;
        private const int Buffer2DGetContiguousLength = 7;
        private const int Buffer2DContiguousCopyTo = 8;

        /// <summary>표면의 첫 줄 주소와 줄 간격(피치)을 얻는다. 다 쓰면 Buffer2DUnlock.</summary>
        internal static IntPtr Buffer2DLock(IntPtr buffer2d, out int pitch)
        {
            IntPtr scan0; int p;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int*, int>)
                   Slot(buffer2d, Buffer2DLock2D))(buffer2d, &scan0, &p), "Lock2D");
            pitch = p;
            return scan0;
        }

        internal static void Buffer2DUnlock(IntPtr buffer2d)
            => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer2d, Buffer2DUnlock2D))(buffer2d);

        internal static IntPtr TryQueryInterface(IntPtr obj, Guid iid)
        {
            IntPtr p;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                      Slot(obj, 0))(obj, &iid, &p);
            return hr >= 0 ? p : IntPtr.Zero;
        }

        internal static int Buffer2DLength(IntPtr buffer2d)
        {
            uint len;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)
                   Slot(buffer2d, Buffer2DGetContiguousLength))(buffer2d, &len), "GetContiguousLength");
            return (int)len;
        }

        internal static void Buffer2DCopyTo(IntPtr buffer2d, byte[] dest, int length)
        {
            fixed (byte* p = dest)
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, int>)
                       Slot(buffer2d, Buffer2DContiguousCopyTo))(buffer2d, p, (uint)length),
                      "ContiguousCopyTo");
            }
        }

        internal static uint GetUInt32(IntPtr attributes, Guid key)
        {
            uint v;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)
                   Slot(attributes, AttrGetUINT32))(attributes, &key, &v), "GetUINT32");
            return v;
        }

        internal static Guid GetGuidAttr(IntPtr attributes, Guid key)
        {
            Guid v;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)
                   Slot(attributes, AttrGetGUID))(attributes, &key, &v), "GetGUID");
            return v;
        }

        internal static long SampleGetTime(IntPtr sample)
        {
            long t;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)
                   Slot(sample, SampleGetSampleTime))(sample, &t), "GetSampleTime");
            return t;
        }

        /// <summary>버퍼가 여러 개여도 하나로 이어 붙여 돌려준다. 돌려받은 버퍼는 Release 할 것.</summary>
        internal static IntPtr SampleContiguousBuffer(IntPtr sample)
        {
            IntPtr buffer;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)
                   Slot(sample, SampleConvertToContiguous))(sample, &buffer), "ConvertToContiguousBuffer");
            return buffer;
        }

        internal static int BufferGetLength(IntPtr buffer)
        {
            int len;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)
                   Slot(buffer, BufferGetCurrentLength))(buffer, &len), "GetCurrentLength");
            return len;
        }

        internal static void ReaderSelect(IntPtr reader, uint stream, bool selected)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)
                      Slot(reader, ReaderSetStreamSelection))(reader, stream, selected ? 1 : 0),
                     "SetStreamSelection");

        /// <summary>이 스트림이 지금 내놓기로 한 형식. 돌려받은 것은 Release 할 것.</summary>
        internal static IntPtr ReaderCurrentType(IntPtr reader, uint stream)
        {
            IntPtr type;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
                   Slot(reader, ReaderGetCurrentMediaType))(reader, stream, &type), "GetCurrentMediaType");
            return type;
        }

        /// <summary>원하는 출력 형식을 알린다. 리더가 디코더·변환기를 알아서 끼운다.</summary>
        internal static int ReaderSetType(IntPtr reader, uint stream, IntPtr mediaType)
            => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)
                Slot(reader, ReaderSetCurrentMediaType))(reader, stream, IntPtr.Zero, mediaType);

        /// <summary>
        /// 다음 표본 하나를 동기로 읽는다. 표본 없이 깃발만 올 수 있다(스트림 끝 등).
        /// 돌려받은 sample 은 Release 할 것.
        /// </summary>
        internal static void ReaderRead(IntPtr reader, uint stream,
                                        out uint actualStream, out uint flags,
                                        out long timestamp, out IntPtr sample)
        {
            uint act, fl; long ts; IntPtr sm;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)
                   Slot(reader, ReaderReadSample))(reader, stream, 0, &act, &fl, &ts, &sm), "ReadSample");
            actualStream = act; flags = fl; timestamp = ts; sample = sm;
        }

        /// <summary>HRESULT 를 사람이 읽을 수 있는 예외로 바꾼다.</summary>
        internal static void Check(int hr, string what)
        {
            if (hr >= 0) return;
            throw new InvalidOperationException($"{what} 실패 (0x{hr:X8})");
        }
    }
}
