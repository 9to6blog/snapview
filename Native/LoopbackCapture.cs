using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SnapView.Native
{
    /// <summary>
    /// 지금 스피커로 나가고 있는 소리를 그대로 받아 온다(WASAPI 루프백).
    ///
    /// 마이크가 아니라 <b>시스템 소리</b>다. 화면 녹화에 필요한 건 보통 이쪽 —
    /// 영상·게임·회의 소리가 화면과 같이 담겨야 한다.
    ///
    /// COM 인터페이스를 vtable 슬롯으로 직접 부른다. WinRT 프로젝션을 끌어오면
    /// 실행 파일이 25MB 불어나기 때문에 이 프로젝트는 전부 이 방식을 쓴다.
    /// </summary>
    internal sealed unsafe class LoopbackCapture : IDisposable
    {
        // ---- COM ----
        private static readonly Guid CLSID_MMDeviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
        private static readonly Guid IID_IMMDeviceEnumerator = new("a95664d2-9614-4f35-a746-de8db63617e6");
        private static readonly Guid IID_IAudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
        private static readonly Guid IID_IAudioCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");

        private const int eRender = 0;          // 재생 장치
        private const int eConsole = 0;         // 일반 용도

        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;

        // ---- vtable 슬롯 (IUnknown 0~2) ----
        private const int SlotRelease = 2;

        // IMMDeviceEnumerator
        private const int EnumGetDefaultEndpoint = 4;

        // IMMDevice
        private const int DeviceActivate = 3;

        // IAudioClient
        private const int ClientInitialize = 3;
        private const int ClientGetBufferSize = 4;
        private const int ClientGetMixFormat = 8;
        private const int ClientStart = 10;
        private const int ClientStop = 11;
        private const int ClientGetService = 14;

        // IAudioCaptureClient
        private const int CaptureGetBuffer = 3;
        private const int CaptureReleaseBuffer = 4;
        private const int CaptureGetNextPacketSize = 5;

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context,
                                                   ref Guid iid, out IntPtr obj);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        /// <summary>WAVEFORMATEX 앞부분. 확장 형식이어도 이 만큼은 같은 자리에 있다.</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSecond;
            public uint AvgBytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        private IntPtr _client;
        private IntPtr _capture;
        private IntPtr _formatPtr;
        private Thread? _pump;
        private volatile bool _running;
        private bool _disposed;

        private readonly object _gate = new();
        private readonly Queue<Block> _blocks = new();
        private long _queuedBytes;

        /// <summary>
        /// 가져가는 쪽이 멈췄을 때 쌓아 둘 최대 길이(초). 넘치면 오래된 것부터 버린다.
        /// 소리를 안 쓰게 됐는데 장치는 계속 주는 상황에서 메모리가 끝없이 자라면 안 된다.
        /// </summary>
        private const int MaxQueuedSeconds = 5;

        /// <summary>장치에서 받은 한 덩어리. 시각은 QPC 기준 100ns 단위(장치가 못 주면 HasTime=false).</summary>
        internal readonly struct Block
        {
            internal readonly byte[] Data;
            internal readonly long Qpc100ns;
            internal readonly bool HasTime;
            internal Block(byte[] data, long qpc, bool hasTime) { Data = data; Qpc100ns = qpc; HasTime = hasTime; }
        }

        /// <summary>장치가 끊겼거나(헤드폰을 뽑는 등) 오류로 받기가 멈췄다.</summary>
        internal bool Failed { get; private set; }

        /// <summary>넘쳐서 버린 덩어리 수.</summary>
        internal int DroppedBlocks { get; private set; }

        internal int Channels { get; private set; }
        internal int SampleRate { get; private set; }
        internal int BitsPerSample { get; private set; }
        internal int BlockAlign => Channels * BitsPerSample / 8;

        /// <summary>소리가 실수(float)로 오는가. AAC 로 넘기려면 16비트 정수로 바꿔야 한다.</summary>
        internal bool IsFloat { get; private set; }

        internal bool Started { get; private set; }

        /// <summary>기본 재생 장치에 붙는다. 실패하면 예외.</summary>
        internal void Start()
        {
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* INPROC_SERVER */, ref iid, out IntPtr enumerator),
                  "MMDeviceEnumerator");

            IntPtr device = IntPtr.Zero;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)
                       Slot(enumerator, EnumGetDefaultEndpoint))(enumerator, eRender, eConsole, &device),
                      "GetDefaultAudioEndpoint");

                Guid clientId = IID_IAudioClient;
                IntPtr client;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)
                       Slot(device, DeviceActivate))(device, &clientId, 1, IntPtr.Zero, &client),
                      "IAudioClient 활성화");
                _client = client;

                IntPtr format;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)
                       Slot(_client, ClientGetMixFormat))(_client, &format), "GetMixFormat");
                _formatPtr = format;

                ReadFormat();

                // 1초짜리 버퍼. 넉넉히 잡아야 잠깐 밀려도 소리가 안 끊긴다.
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, IntPtr, int>)
                       Slot(_client, ClientInitialize))(_client, AUDCLNT_SHAREMODE_SHARED,
                                                        AUDCLNT_STREAMFLAGS_LOOPBACK,
                                                        10_000_000, 0, _formatPtr, IntPtr.Zero),
                      "IAudioClient 초기화");

                Guid captureId = IID_IAudioCaptureClient;
                IntPtr capture;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                       Slot(_client, ClientGetService))(_client, &captureId, &capture), "GetService");
                _capture = capture;

                Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_client, ClientStart))(_client), "Start");

                _running = true;
                Started = true;

                _pump = new Thread(Pump) { IsBackground = true, Name = "SnapView 소리 받기" };
                _pump.Start();
            }
            finally
            {
                Release(device);
                Release(enumerator);
            }
        }

        private void ReadFormat()
        {
            var wf = Marshal.PtrToStructure<WaveFormat>(_formatPtr);

            Channels = wf.Channels;
            SampleRate = (int)wf.SamplesPerSecond;
            BitsPerSample = wf.BitsPerSample;

            // 0xFFFE 는 확장 형식. 요즘 장치는 대부분 32비트 실수로 준다.
            const ushort WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
            IsFloat = wf.FormatTag == WAVE_FORMAT_IEEE_FLOAT ||
                      (wf.FormatTag == WAVE_FORMAT_EXTENSIBLE && wf.BitsPerSample == 32);
        }

        /// <summary>장치에서 계속 소리를 퍼 온다. 별도 스레드에서 돈다.</summary>
        private void Pump()
        {
            while (_running)
            {
                try
                {
                    uint packet;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)
                              Slot(_capture, CaptureGetNextPacketSize))(_capture, &packet);
                    if (hr < 0) { Fail(hr, "GetNextPacketSize"); break; }

                    if (packet == 0) { Thread.Sleep(10); continue; }

                    IntPtr data;
                    uint frames, flags;
                    long pos, qpc;

                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, long*, long*, int>)
                          Slot(_capture, CaptureGetBuffer))(_capture, &data, &frames, &flags, &pos, &qpc);
                    if (hr < 0) { Fail(hr, "GetBuffer"); break; }

                    int bytes = (int)frames * BlockAlign;
                    var block = new byte[bytes];

                    // 무음 표시가 오면 데이터를 안 읽고 0 으로 채운다(규격이 그렇게 하라고 한다).
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero && bytes > 0)
                        Marshal.Copy(data, block, 0, bytes);

                    ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)
                     Slot(_capture, CaptureReleaseBuffer))(_capture, frames);

                    if (bytes > 0)
                        Enqueue(new Block(block, qpc, (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0));
                }
                catch (Exception ex) { Fail(ex.HResult, ex.GetType().Name); break; }
            }
        }

        private void Fail(int hr, string what)
        {
            if (!_running) return;          // 우리가 멈춘 것이다. 오류가 아니다.
            Failed = true;
            Core.Log.Write($"소리 받기가 멈췄습니다 ({what} 0x{hr:X8}). 재생 장치가 바뀌었을 수 있습니다.");
        }

        private void Enqueue(Block block)
        {
            long cap = (long)SampleRate * BlockAlign * MaxQueuedSeconds;
            lock (_gate)
            {
                _blocks.Enqueue(block);
                _queuedBytes += block.Data.Length;
                while (_queuedBytes > cap && _blocks.Count > 1)
                {
                    _queuedBytes -= _blocks.Dequeue().Data.Length;
                    DroppedBlocks++;
                }
            }
        }

        /// <summary>모아 둔 덩어리를 시각과 함께 전부 가져간다. 없으면 빈 목록.</summary>
        internal List<Block> DrainBlocks()
        {
            lock (_gate)
            {
                var list = new List<Block>(_blocks);
                _blocks.Clear();
                _queuedBytes = 0;
                return list;
            }
        }

        /// <summary>모아 둔 소리를 한 덩어리로 이어 가져간다(시각은 버린다). 없으면 빈 배열.</summary>
        internal byte[] Drain()
        {
            List<Block> blocks = DrainBlocks();
            if (blocks.Count == 0) return Array.Empty<byte>();

            int total = 0;
            foreach (Block b in blocks) total += b.Data.Length;

            var all = new byte[total];
            int at = 0;
            foreach (Block b in blocks)
            {
                Buffer.BlockCopy(b.Data, 0, all, at, b.Data.Length);
                at += b.Data.Length;
            }
            return all;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;

            try { _pump?.Join(300); } catch { }

            if (_client != IntPtr.Zero)
            {
                try { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_client, ClientStop))(_client); }
                catch { }
            }

            Release(_capture);
            Release(_client);
            _capture = _client = IntPtr.Zero;

            if (_formatPtr != IntPtr.Zero) { CoTaskMemFree(_formatPtr); _formatPtr = IntPtr.Zero; }
        }

        private static void* Slot(IntPtr obj, int index)
        {
            void** vtbl = *(void***)obj;
            return vtbl[index];
        }

        private static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, SlotRelease))(obj);
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException($"{what} 실패 (0x{hr:X8})");
        }
    }
}
