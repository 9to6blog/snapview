using System;
using System.Runtime.InteropServices;

namespace SnapView.Native
{
    /// <summary>
    /// Media Foundation SourceReader 로 영상 파일을 되읽는다 — 컷 편집·배속 내보내기용.
    ///
    /// 영상은 <b>NV12</b> 로 받아서 <see cref="Capture.Mp4Writer"/> 에 그대로 넘길 수 있게 한다
    /// (RGB 를 거치면 색 변환을 두 번 해 화질이 한 번 더 깎인다). 소리는 16비트 PCM 으로 받는다.
    /// 슬롯 번호 검증은 늘 그랬듯 자체 검사가 한다 — 진짜 MP4 를 만들어 되읽어 색까지 본다.
    /// </summary>
    internal sealed class VideoReader : IDisposable
    {
        /// <summary>읽어 낸 표본 하나. Data 는 다음 Read 까지만 유효하다(버퍼 재사용).</summary>
        internal readonly struct Sample
        {
            internal Sample(bool isVideo, TimeSpan time, byte[] data, int length)
            { IsVideo = isVideo; Time = time; Data = data; Length = length; }

            internal bool IsVideo { get; }
            internal TimeSpan Time { get; }
            internal byte[] Data { get; }
            internal int Length { get; }
        }

        private IntPtr _reader;
        private bool _videoDone, _audioDone;
        private uint _videoIndex = uint.MaxValue, _audioIndex = uint.MaxValue;
        private byte[] _videoBuf = Array.Empty<byte>();
        private byte[] _audioBuf = Array.Empty<byte>();

        /// <summary>영상 크기(짝수로 맞춘 값 아님 — 원본 그대로).</summary>
        internal int Width { get; }
        internal int Height { get; }

        internal bool HasAudio { get; private set; }
        internal int AudioRate { get; private set; }
        internal int AudioChannels { get; private set; }

        internal VideoReader(string path)
        {
            MediaFoundation.Startup();
            try
            {
                MediaFoundation.Check(
                    MediaFoundation.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out _reader),
                    "MFCreateSourceReaderFromURL");

                // 영상·소리 첫 스트림만 켠다. (자막 등 다른 스트림은 안 읽는다)
                MediaFoundation.ReaderSelect(_reader, MediaFoundation.AllStreams, false);
                MediaFoundation.ReaderSelect(_reader, MediaFoundation.FirstVideoStream, true);

                // 출력을 NV12 로. 부분 형식만 주면 리더가 디코더를 알아서 끼운다.
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out IntPtr vt), "MFCreateMediaType");
                try
                {
                    MediaFoundation.SetGuid(vt, MediaFoundation.MF_MT_MAJOR_TYPE,
                                            MediaFoundation.MFMediaType_Video);
                    MediaFoundation.SetGuid(vt, MediaFoundation.MF_MT_SUBTYPE,
                                            MediaFoundation.MFVideoFormat_NV12);
                    MediaFoundation.Check(
                        MediaFoundation.ReaderSetType(_reader, MediaFoundation.FirstVideoStream, vt),
                        "SetCurrentMediaType(video)");
                }
                finally { MediaFoundation.Release(vt); }

                IntPtr cur = MediaFoundation.ReaderCurrentType(_reader, MediaFoundation.FirstVideoStream);
                try
                {
                    ulong size = MediaFoundation.GetUInt64(cur, MediaFoundation.MF_MT_FRAME_SIZE);
                    Width = (int)(size >> 32);
                    Height = (int)(size & 0xFFFFFFFF);
                }
                finally { MediaFoundation.Release(cur); }

                if (Width <= 0 || Height <= 0)
                    throw new InvalidOperationException("영상 크기를 읽지 못했습니다");

                TrySetUpAudio();
            }
            catch
            {
                MediaFoundation.Release(_reader);
                _reader = IntPtr.Zero;
                MediaFoundation.Shutdown();
                throw;
            }
        }

        /// <summary>소리는 있으면 좋은 것. 없거나 못 읽는 형식이면 영상만 읽는다.</summary>
        private void TrySetUpAudio()
        {
            try
            {
                MediaFoundation.ReaderSelect(_reader, MediaFoundation.FirstAudioStream, true);

                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out IntPtr at), "MFCreateMediaType");
                try
                {
                    MediaFoundation.SetGuid(at, MediaFoundation.MF_MT_MAJOR_TYPE,
                                            MediaFoundation.MFMediaType_Audio);
                    MediaFoundation.SetGuid(at, MediaFoundation.MF_MT_SUBTYPE,
                                            MediaFoundation.MFAudioFormat_PCM);
                    MediaFoundation.SetUInt32(at, MediaFoundation.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    MediaFoundation.Check(
                        MediaFoundation.ReaderSetType(_reader, MediaFoundation.FirstAudioStream, at),
                        "SetCurrentMediaType(audio)");
                }
                finally { MediaFoundation.Release(at); }

                IntPtr cur = MediaFoundation.ReaderCurrentType(_reader, MediaFoundation.FirstAudioStream);
                try
                {
                    AudioRate = (int)MediaFoundation.GetUInt32(
                        cur, MediaFoundation.MF_MT_AUDIO_SAMPLES_PER_SECOND);
                    AudioChannels = (int)MediaFoundation.GetUInt32(
                        cur, MediaFoundation.MF_MT_AUDIO_NUM_CHANNELS);
                }
                finally { MediaFoundation.Release(cur); }

                HasAudio = AudioRate > 0 && AudioChannels > 0;
            }
            catch
            {
                // 소리 스트림이 없는 파일이 흔하다(우리 화면 녹화도 소리 끄면 그렇다).
                HasAudio = false;
                _audioDone = true;
                try { MediaFoundation.ReaderSelect(_reader, MediaFoundation.FirstAudioStream, false); }
                catch { }
            }
        }

        /// <summary>
        /// 다음 표본을 읽는다. 영상·소리가 파일에 담긴 순서대로 섞여 나온다.
        /// 다 읽었으면 null. 영상 Data 는 <b>딱 Width×Height×3/2 크기의 NV12</b> 로 줄 맞춰 준다.
        /// </summary>
        internal Sample? Read()
        {
            while (!(_videoDone && _audioDone))
            {
                MediaFoundation.ReaderRead(_reader, MediaFoundation.AnyStream,
                                           out uint stream, out uint flags,
                                           out long ts, out IntPtr sample);

                if ((flags & MediaFoundation.ReadFlagError) != 0)
                {
                    MediaFoundation.Release(sample);
                    throw new InvalidOperationException("영상을 읽는 중 오류가 났습니다");
                }

                bool isVideo = ResolveStream(stream);

                if ((flags & MediaFoundation.ReadFlagEndOfStream) != 0)
                {
                    if (isVideo) _videoDone = true; else _audioDone = true;
                    MediaFoundation.Release(sample);
                    continue;
                }
                if (sample == IntPtr.Zero) continue;   // 스트림 틱 등, 표본 없는 알림

                try
                {
                    IntPtr buffer = MediaFoundation.SampleContiguousBuffer(sample);
                    try
                    {
                        if (isVideo)
                        {
                            int packed = CopyVideo(buffer);
                            return new Sample(true, TimeSpan.FromTicks(ts), _videoBuf, packed);
                        }

                        int len = MediaFoundation.BufferGetLength(buffer);
                        if (_audioBuf.Length < len) _audioBuf = new byte[len];
                        IntPtr data = MediaFoundation.BufferLockPtr(buffer, out _);
                        try { Marshal.Copy(data, _audioBuf, 0, len); }
                        finally { MediaFoundation.BufferUnlockPtr(buffer); }
                        return new Sample(false, TimeSpan.FromTicks(ts), _audioBuf, len);
                    }
                    finally { MediaFoundation.Release(buffer); }
                }
                finally { MediaFoundation.Release(sample); }
            }
            return null;
        }

        /// <summary>
        /// AnyStream 으로 읽으면 실제 스트림 번호가 온다. 어느 쪽이 영상인지는
        /// 그 스트림의 현재 형식을 한 번 물어봐서 기억해 둔다.
        /// </summary>
        private bool ResolveStream(uint stream)
        {
            if (stream == _videoIndex) return true;
            if (stream == _audioIndex) return false;

            IntPtr type = MediaFoundation.ReaderCurrentType(_reader, stream);
            try
            {
                Guid major = MediaFoundation.GetGuidAttr(type, MediaFoundation.MF_MT_MAJOR_TYPE);
                bool isVideo = major == MediaFoundation.MFMediaType_Video;
                if (isVideo) _videoIndex = stream; else _audioIndex = stream;
                return isVideo;
            }
            finally { MediaFoundation.Release(type); }
        }

        /// <summary>
        /// 프레임을 표준 배치(NV12 = 딱 Width×Height×3/2)로 복사한다.
        ///
        /// <b>디코더 프레임은 표시 크기가 아니라 코딩 크기다.</b> H.264 는 16의 배수로
        /// 부호화하므로 548×466 영상의 표면은 560×480 이고, 줄 간격(피치)은 다시 576 처럼
        /// 더 클 수 있다. ContiguousCopyTo 는 피치 여유만 벗기고 <b>코딩 크기는 그대로</b>
        /// 돌려주기 때문에 그걸 표시 크기로 읽으면 줄이 밀린 줄무늬가 된다(실제로 그랬다).
        /// Lock2D 로 피치를 직접 받아 표시 크기만큼만 걷어 낸다. UV 면은 표시 높이가 아니라
        /// <b>코딩 높이</b> 뒤에서 시작한다 — 코딩 높이는 전체 길이 ÷ 피치 ÷ 1.5 로 나온다.
        /// </summary>
        private int CopyVideo(IntPtr buffer)
        {
            int want = Width * Height * 3 / 2;
            if (_videoBuf.Length < want) _videoBuf = new byte[want];

            int total = MediaFoundation.BufferGetLength(buffer);

            IntPtr b2d = MediaFoundation.TryQueryInterface(buffer, MediaFoundation.IID_IMF2DBuffer);
            if (b2d != IntPtr.Zero)
            {
                try
                {
                    IntPtr scan0 = MediaFoundation.Buffer2DLock(b2d, out int pitch);
                    try
                    {
                        if (pitch < Width)
                            throw new InvalidOperationException($"피치가 폭보다 작습니다 ({pitch} < {Width})");

                        int codedH = (int)((long)total * 2 / (3 * pitch));
                        if (codedH < Height) codedH = Height;

                        CopyPlanes(scan0, pitch, codedH);
                        return want;
                    }
                    finally { MediaFoundation.Buffer2DUnlock(b2d); }
                }
                finally { MediaFoundation.Release(b2d); }
            }

            // 2D 버퍼가 아니면(시스템 메모리) 피치 여유는 없지만 코딩 크기 패딩은 있을 수 있다.
            IntPtr data = MediaFoundation.BufferLockPtr(buffer, out _);
            try
            {
                if (total == want)
                {
                    Marshal.Copy(data, _videoBuf, 0, want);
                    return want;
                }

                int codedW = (Width + 15) / 16 * 16;
                int codedH2 = (Height + 15) / 16 * 16;
                if ((long)codedW * codedH2 * 3 / 2 == total)
                {
                    CopyPlanes(data, codedW, codedH2);
                    return want;
                }

                throw new InvalidOperationException(
                    $"NV12 배치를 모르겠습니다 (len={total}, {Width}x{Height})");
            }
            finally { MediaFoundation.BufferUnlockPtr(buffer); }
        }

        /// <summary>피치·코딩 크기가 있는 표면에서 표시 크기(Width×Height)만 걷어 낸다.</summary>
        private unsafe void CopyPlanes(IntPtr surface, int pitch, int codedH)
        {
            byte* src = (byte*)surface;
            fixed (byte* dst = _videoBuf)
            {
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(src + (long)y * pitch, dst + (long)y * Width, Width, Width);

                byte* srcUv = src + (long)pitch * codedH;   // UV 는 코딩 높이 뒤!
                byte* dstUv = dst + (long)Width * Height;
                for (int y = 0; y < Height / 2; y++)
                    Buffer.MemoryCopy(srcUv + (long)y * pitch, dstUv + (long)y * Width, Width, Width);
            }
        }

        public void Dispose()
        {
            if (_reader == IntPtr.Zero) return;
            MediaFoundation.Release(_reader);
            _reader = IntPtr.Zero;
            MediaFoundation.Shutdown();
        }
    }
}
