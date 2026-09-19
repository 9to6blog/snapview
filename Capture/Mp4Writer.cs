using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Native;

namespace SnapView.Capture
{
    /// <summary>
    /// 화면 한 장 한 장을 H.264 MP4 로 인코딩한다.
    ///
    /// GIF 와 달리 색이 안 뭉개지고 파일도 훨씬 작다. 대신 윈도우의 H.264 인코더에
    /// 기대므로, 그 인코더가 없는 환경에서는 만들다가 실패할 수 있다.
    /// 그때는 부르는 쪽이 GIF 로 물러선다.
    ///
    /// 인코더에 <b>NV12</b> 로 넣는다. 윈도우 H.264 인코더가 원래 받는 형식이라
    /// 중간 변환기가 끼지 않고, RGB 로 넣을 때 따라붙는 줄 간격·상하 반전 문제도 없다.
    /// 색 변환(BT.601)은 여기서 직접 한다.
    /// </summary>
    internal sealed class Mp4Writer : IDisposable
    {
        private const long TicksPerSecond = 10_000_000;   // 100ns 단위

        /// <summary>
        /// 담을 수 있는 초당 장수.
        ///
        /// 위쪽 한계는 윈도우 H.264 인코더가 받아 주는 범위에서 정했다. 이 기계에서는
        /// 160 까지 받고 180 부터 거절했다(0xC00D36B4). 기계마다 다를 수 있어 여유를
        /// 두고 120 으로 둔다 — 화면 녹화에는 차고 넘친다. 그래도 거절당하면
        /// <see cref="ScreenRecorder"/> 가 흔한 값으로 한 번 더 시도한다.
        /// </summary>
        internal const int MinFps = 1;
        internal const int MaxFps = 120;

        private readonly long _frameDuration;
        private readonly byte[] _bgra;      // 한 장치의 원본 픽셀
        private readonly byte[] _nv12;      // 변환 결과 (Y 면 + UV 면)

        private IntPtr _writer;
        private int _stream;
        private long _time;
        private bool _closed;

        // 마지막 한 장은 손에 쥐고 있는다. 다음 장이 와야 이 장이 화면에
        // 얼마나 오래 머물렀는지 알 수 있기 때문이다. (Flush 참고)
        private IntPtr _pending;
        private long _pendingTime;

        // ---- 소리 ----
        private int _audioStream = -1;
        private long _audioTime;
        private long _audioBytes;
        private int _audioBlockAlign;
        private int _audioBytesPerSecond;

        /// <summary>소리 트랙이 붙어 있는가.</summary>
        internal bool HasAudio => _audioStream >= 0;

        /// <summary>소리가 어디까지 채워져 있는가. 넣은 바이트 수로 계산한다.</summary>
        internal TimeSpan AudioFilledTo => _audioBytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)_audioBytes / _audioBytesPerSecond)
            : TimeSpan.Zero;

        internal int Width { get; }
        internal int Height { get; }
        internal int FrameCount { get; private set; }
        internal string Path { get; }

        /// <param name="audio">
        /// 소리를 같이 담을 형식. null 이면 영상만 담는다.
        /// (채널 수 · 초당 표본 수 · 표본 비트 수)
        /// </param>
        internal Mp4Writer(string path, int width, int height, int fps, int bitrate = 0,
                           (int Channels, int SampleRate, int Bits)? audio = null)
        {
            // H.264 는 가로·세로가 짝수여야 한다. 홀수면 인코더가 거절하거나 한 줄이 깨진다.
            Width = Math.Max(2, width - (width % 2));
            Height = Math.Max(2, height - (height % 2));
            Path = path;

            fps = Math.Clamp(fps, MinFps, MaxFps);
            _frameDuration = TicksPerSecond / fps;

            _bgra = new byte[Width * Height * 4];
            _nv12 = new byte[Width * Height * 3 / 2];

            if (bitrate <= 0)
            {
                // 화면 녹화는 움직임이 적어서 이 정도면 충분히 깨끗하다.
                bitrate = (int)Math.Clamp((long)Width * Height * fps / 8, 800_000, 20_000_000);
            }

            MediaFoundation.Startup();
            try { Open(fps, bitrate, audio); }
            catch { MediaFoundation.Shutdown(); throw; }
        }

        private void Open(int fps, int bitrate, (int Channels, int SampleRate, int Bits)? audio)
        {
            MediaFoundation.Check(
                MediaFoundation.MFCreateSinkWriterFromURL(Path, IntPtr.Zero, IntPtr.Zero, out _writer),
                "MFCreateSinkWriterFromURL");

            IntPtr outType = IntPtr.Zero, inType = IntPtr.Zero;
            try
            {
                // 나갈 형식: H.264
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out outType), "MFCreateMediaType");
                MediaFoundation.SetGuid(outType, MediaFoundation.MF_MT_MAJOR_TYPE,
                                        MediaFoundation.MFMediaType_Video);
                MediaFoundation.SetGuid(outType, MediaFoundation.MF_MT_SUBTYPE,
                                        MediaFoundation.MFVideoFormat_H264);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_INTERLACE_MODE,
                                          MediaFoundation.InterlaceProgressive);
                MediaFoundation.SetPair(outType, MediaFoundation.MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
                MediaFoundation.SetPair(outType, MediaFoundation.MF_MT_FRAME_RATE, (uint)fps, 1);
                MediaFoundation.SetPair(outType, MediaFoundation.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AVG_BITRATE, (uint)bitrate);

                _stream = MediaFoundation.WriterAdd(_writer, outType);

                // 들어갈 형식: NV12
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out inType), "MFCreateMediaType");
                MediaFoundation.SetGuid(inType, MediaFoundation.MF_MT_MAJOR_TYPE,
                                        MediaFoundation.MFMediaType_Video);
                MediaFoundation.SetGuid(inType, MediaFoundation.MF_MT_SUBTYPE,
                                        MediaFoundation.MFVideoFormat_NV12);
                MediaFoundation.SetUInt32(inType, MediaFoundation.MF_MT_INTERLACE_MODE,
                                          MediaFoundation.InterlaceProgressive);
                MediaFoundation.SetPair(inType, MediaFoundation.MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
                MediaFoundation.SetPair(inType, MediaFoundation.MF_MT_FRAME_RATE, (uint)fps, 1);
                MediaFoundation.SetPair(inType, MediaFoundation.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

                MediaFoundation.WriterSetInput(_writer, _stream, inType);

                // 소리는 있으면 좋은 것이지 없으면 녹화를 접을 일은 아니다.
                // 붙이다 실패해도 영상만으로 계속 간다.
                if (audio.HasValue) TryAddAudio(audio.Value);

                MediaFoundation.WriterBegin(_writer);
            }
            finally
            {
                MediaFoundation.Release(outType);
                MediaFoundation.Release(inType);
            }
        }

        /// <summary>
        /// 소리 트랙을 붙인다. 나갈 형식은 AAC, 들어갈 형식은 16비트 PCM.
        /// AAC 인코더는 <b>44.1kHz / 48kHz 에 1~2 채널, 16비트</b>만 받는다.
        /// 장치가 다른 형식으로 주면 부르는 쪽에서 미리 맞춰 넣어야 한다.
        /// </summary>
        private void TryAddAudio((int Channels, int SampleRate, int Bits) audio)
        {
            IntPtr outType = IntPtr.Zero, inType = IntPtr.Zero;
            try
            {
                int channels = Math.Clamp(audio.Channels, 1, 2);
                int rate = audio.SampleRate;
                const int bits = 16;

                _audioBlockAlign = channels * bits / 8;
                _audioBytesPerSecond = rate * _audioBlockAlign;

                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out outType), "MFCreateMediaType");
                MediaFoundation.SetGuid(outType, MediaFoundation.MF_MT_MAJOR_TYPE,
                                        MediaFoundation.MFMediaType_Audio);
                MediaFoundation.SetGuid(outType, MediaFoundation.MF_MT_SUBTYPE,
                                        MediaFoundation.MFAudioFormat_AAC);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)rate);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AAC_PAYLOAD_TYPE, 0);
                MediaFoundation.SetUInt32(outType, MediaFoundation.MF_MT_AAC_PROFILE_LEVEL, 0x29);

                int stream = MediaFoundation.WriterAdd(_writer, outType);

                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out inType), "MFCreateMediaType");
                MediaFoundation.SetGuid(inType, MediaFoundation.MF_MT_MAJOR_TYPE,
                                        MediaFoundation.MFMediaType_Audio);
                MediaFoundation.SetGuid(inType, MediaFoundation.MF_MT_SUBTYPE,
                                        MediaFoundation.MFAudioFormat_PCM);
                MediaFoundation.SetUInt32(inType, MediaFoundation.MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
                MediaFoundation.SetUInt32(inType, MediaFoundation.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)rate);
                MediaFoundation.SetUInt32(inType, MediaFoundation.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
                MediaFoundation.SetUInt32(inType, MediaFoundation.MF_MT_AUDIO_BLOCK_ALIGNMENT,
                                          (uint)_audioBlockAlign);
                MediaFoundation.SetUInt32(inType, MediaFoundation.MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
                                          (uint)_audioBytesPerSecond);

                MediaFoundation.WriterSetInput(_writer, stream, inType);
                _audioStream = stream;
            }
            catch (Exception ex)
            {
                Core.Log.Write("소리 트랙을 못 붙였습니다(영상만 담습니다): " + ex.Message);
                _audioStream = -1;
            }
            finally
            {
                MediaFoundation.Release(outType);
                MediaFoundation.Release(inType);
            }
        }

        /// <summary>16비트 PCM 덩어리를 소리 트랙 끝에 이어 붙인다(시각을 모를 때).</summary>
        internal void AddAudio(byte[] pcm, int length) => AddAudio(pcm, 0, length);

        /// <summary>시각 어긋남을 이만큼까지는 그냥 이어 붙인다. 타임스탬프의 잔떨림 크기.</summary>
        private static readonly TimeSpan PlacementTolerance = TimeSpan.FromMilliseconds(2);

        /// <summary>
        /// 16비트 PCM 덩어리를 <b>장치가 말한 시각</b>에 놓는다.
        ///
        /// 지금까지 채운 곳보다 뒤면 그 사이를 무음으로 메우고, 앞이면 겹치는 앞부분을 버린다.
        /// 루프백은 조용할 때 패킷을 안 주므로, 시각을 안 보고 뒤에 이어 붙이기만 하면
        /// 조용한 구간 뒤의 소리가 그만큼 앞당겨진다. 시각대로 놓으면 그런 일이 없다.
        /// </summary>
        internal void AddAudioAt(byte[] pcm, int offset, int length, TimeSpan at)
        {
            if (_closed || _audioStream < 0 || length <= 0 || _audioBytesPerSecond <= 0) return;

            long wantBytes = (long)(at.TotalSeconds * _audioBytesPerSecond);
            wantBytes -= wantBytes % _audioBlockAlign;
            long tolerance = (long)(PlacementTolerance.TotalSeconds * _audioBytesPerSecond);

            long diff = wantBytes - _audioBytes;
            if (diff > tolerance)
            {
                PadAudioTo(at, TimeSpan.Zero);
            }
            else if (diff < -tolerance)
            {
                long skip = Math.Min(length, -diff);
                skip -= skip % _audioBlockAlign;
                offset += (int)skip;
                length -= (int)skip;
                if (length <= 0) return;
            }

            AddAudio(pcm, offset, length);
        }

        private void AddAudio(byte[] pcm, int offset, int length)
        {
            if (_closed || _audioStream < 0 || length <= 0) return;

            MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer(length, out IntPtr buffer),
                                  "MFCreateMemoryBuffer");
            IntPtr sample = IntPtr.Zero;
            try
            {
                IntPtr data = MediaFoundation.BufferLockPtr(buffer, out _);
                try { System.Runtime.InteropServices.Marshal.Copy(pcm, offset, data, length); }
                finally { MediaFoundation.BufferUnlockPtr(buffer); }

                MediaFoundation.BufferSetLength(buffer, length);

                MediaFoundation.Check(MediaFoundation.MFCreateSample(out sample), "MFCreateSample");
                MediaFoundation.SampleAdd(sample, buffer);
                MediaFoundation.SampleSetTime(sample, _audioTime);

                // 담긴 바이트 수로 길이를 계산한다. 영상과 시간이 어긋나면 소리가 밀린다.
                long duration = _audioBytesPerSecond > 0
                    ? (long)length * TicksPerSecond / _audioBytesPerSecond : 0;
                MediaFoundation.SampleSetDuration(sample, duration);

                MediaFoundation.WriterWrite(_writer, _audioStream, sample);
                _audioTime += duration;
                _audioBytes += length;
            }
            catch (Exception ex)
            {
                Core.Log.Write("소리 한 덩어리를 못 넣었습니다: " + ex.Message);
            }
            finally
            {
                MediaFoundation.Release(sample);
                MediaFoundation.Release(buffer);
            }
        }

        /// <summary>
        /// 소리를 <paramref name="when"/> 까지 무음으로 메운다.
        ///
        /// WASAPI 루프백은 <b>아무 소리도 안 날 때 패킷을 아예 주지 않는다</b>. 그동안
        /// 소리 트랙은 안 자라는데 영상은 계속 자라므로, 조용한 구간이 끝나면 그다음 소리가
        /// 그만큼 앞당겨져 붙는다 — 5초 조용했으면 이후 소리가 영상보다 5초 빨라지고,
        /// 소리 트랙 자체도 그만큼 짧게 끝난다. 빈 만큼 무음을 넣어 시계를 맞춘다.
        ///
        /// <paramref name="tolerance"/> 만큼은 봐 준다. 소리는 원래 조금 늦게 도착하는데
        /// 그걸 매번 무음으로 메우면, 곧 도착할 진짜 소리가 그만큼 뒤로 밀린다.
        /// </summary>
        internal void PadAudioTo(TimeSpan when, TimeSpan tolerance)
        {
            if (_closed || _audioStream < 0 || _audioBytesPerSecond <= 0) return;

            double target = when.TotalSeconds - tolerance.TotalSeconds;
            long wantBytes = (long)(target * _audioBytesPerSecond);
            long missing = wantBytes - _audioBytes;
            if (missing < _audioBlockAlign) return;

            // 표본 경계에 맞춘다. 어긋나면 좌우 채널이 뒤바뀐 채로 이어진다.
            missing -= missing % _audioBlockAlign;

            var silence = new byte[Math.Min(missing, 64 * 1024)];
            while (missing > 0)
            {
                int chunk = (int)Math.Min(missing, silence.Length);
                chunk -= chunk % _audioBlockAlign;
                if (chunk <= 0) break;

                AddAudio(silence, chunk);
                missing -= chunk;
            }
        }

        /// <summary>한 장을 이어 붙인다. 크기가 다르면 맞춰서 넣는다.</summary>
        /// <param name="when">
        /// 이 장을 <b>실제로 찍은 시각</b>(녹화 시작 기준). 넣어 주면 그 시각을 그대로 쓴다.
        ///
        /// 이게 중요하다. 정해 둔 간격대로 차곡차곡 쌓으면, 기계가 못 따라와서 장을
        /// 몇 개 거를 때마다 영상이 실제보다 빨리 감긴다 — 10초를 찍었는데 6초짜리가 나온다.
        /// 찍힌 시각을 그대로 적어 두면 몇 장을 걸렀든 길이가 맞는다.
        ///
        /// null 이면 정해진 간격대로 쌓는다(검사용).
        /// </param>
        internal void Add(BitmapSource frame, TimeSpan? when = null)
        {
            if (_closed) throw new ObjectDisposedException(nameof(Mp4Writer));

            Normalize(frame).CopyPixels(_bgra, Width * 4, 0);
            ConvertBgraToNv12(_bgra, Width, Height, _nv12);
            PushNv12(_nv12, _nv12.Length, when);
        }

        /// <summary>
        /// 이미 BGRA(알파를 안 쓰는 BGRX 도 된다)로 들어 있는 한 장을 넣는다. 녹화가 이걸 쓴다 —
        /// BitmapSource 를 만들고 다시 픽셀을 뽑는 왕복(1080p 한 장에 8MB 복사 두 번)을 건너뛴다.
        /// 원본이 인코더 크기와 같으면 복사 없이 바로 변환하고, 홀수 크기라 한 줄·한 칸이 남으면 잘라 넣는다.
        /// </summary>
        internal void AddBgra(byte[] bgra, int srcWidth, int srcHeight, TimeSpan? when = null)
        {
            if (_closed) throw new ObjectDisposedException(nameof(Mp4Writer));
            if (srcWidth < Width || srcHeight < Height || bgra.Length < srcWidth * srcHeight * 4)
                throw new ArgumentException($"프레임 크기가 맞지 않습니다 ({srcWidth}×{srcHeight}, 필요 {Width}×{Height})");

            byte[] source = bgra;
            if (srcWidth != Width || srcHeight != Height)
            {
                int srcStride = srcWidth * 4, dstStride = Width * 4;
                for (int y = 0; y < Height; y++)
                    Buffer.BlockCopy(bgra, y * srcStride, _bgra, y * dstStride, dstStride);
                source = _bgra;
            }

            ConvertBgraToNv12(source, Width, Height, _nv12);
            PushNv12(_nv12, _nv12.Length, when);
        }

        /// <summary>
        /// 이미 NV12 인 한 장을 그대로 넣는다 — 컷 편집·배속처럼 <b>디코딩한 것을 도로
        /// 인코딩</b>할 때 쓴다. RGB 를 거치면 색 변환을 두 번 해 화질이 한 번 더 깎인다.
        /// 크기는 이 인코더의 Width×Height×3/2 와 정확히 같아야 한다.
        /// </summary>
        internal void AddNv12(byte[] nv12, TimeSpan? when = null)
        {
            if (_closed) throw new ObjectDisposedException(nameof(Mp4Writer));
            if (nv12.Length < _nv12.Length)
                throw new ArgumentException($"NV12 크기가 다릅니다 ({nv12.Length} < {_nv12.Length})");
            PushNv12(nv12, _nv12.Length, when);
        }

        private void PushNv12(byte[] data, int length, TimeSpan? when)
        {
            MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer(length, out IntPtr buffer),
                                  "MFCreateMemoryBuffer");
            IntPtr sample = IntPtr.Zero;
            bool handedOver = false;
            try
            {
                IntPtr dst = MediaFoundation.BufferLockPtr(buffer, out _);
                try { System.Runtime.InteropServices.Marshal.Copy(data, 0, dst, length); }
                finally { MediaFoundation.BufferUnlockPtr(buffer); }

                MediaFoundation.BufferSetLength(buffer, length);

                MediaFoundation.Check(MediaFoundation.MFCreateSample(out sample), "MFCreateSample");
                MediaFoundation.SampleAdd(sample, buffer);

                long time = when.HasValue
                    ? (long)Math.Round(when.Value.TotalSeconds * TicksPerSecond)
                    : _time;

                // 시간은 뒤로 갈 수 없다. 같은 시각에 두 장이 겹쳐도 안 된다.
                if (FrameCount > 0 && time <= _pendingTime) time = _pendingTime + 1;
                if (time < 0) time = 0;

                // 앞 장의 길이가 이제야 정해졌다. 그 장을 내보내고 이 장을 손에 쥔다.
                Flush(time);

                _pending = sample;
                _pendingTime = time;
                handedOver = true;

                _time = time + _frameDuration;
                FrameCount++;
            }
            finally
            {
                if (!handedOver) MediaFoundation.Release(sample);
                MediaFoundation.Release(buffer);
            }
        }

        /// <summary>쥐고 있던 장의 길이를 <paramref name="nextTime"/> 로 확정해서 내보낸다.</summary>
        private void Flush(long nextTime)
        {
            if (_pending == IntPtr.Zero) return;

            long duration = nextTime > _pendingTime ? nextTime - _pendingTime : _frameDuration;

            try
            {
                MediaFoundation.SampleSetTime(_pending, _pendingTime);
                MediaFoundation.SampleSetDuration(_pending, duration);
                MediaFoundation.WriterWrite(_writer, _stream, _pending);
            }
            finally
            {
                MediaFoundation.Release(_pending);
                _pending = IntPtr.Zero;
            }
        }

        /// <summary>
        /// BGRA 를 NV12 로 옮긴다.
        /// 밝기(Y)는 픽셀마다, 색(UV)은 2×2 묶음마다 하나씩 — 사람 눈이 색보다 밝기에
        /// 훨씬 민감해서 영상 형식은 다들 이렇게 아낀다.
        /// </summary>
        internal static unsafe void ConvertBgraToNv12(byte[] bgra, int width, int height, byte[] nv12)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentException($"NV12 는 짝수 크기여야 합니다 ({width}×{height})");
            if (bgra.Length < width * height * 4) throw new ArgumentException("BGRA 버퍼가 작습니다");
            if (nv12.Length < width * height * 3 / 2) throw new ArgumentException("NV12 버퍼가 작습니다");

            // 두 줄씩 한 번에 훑는다. 예전엔 Y 한 번, UV 한 번 두 번 훑고 배열 경계 검사가
            // 픽셀마다 붙어서 넓은 영역에서 초당 장수를 갉아먹는 원인 중 하나였다.
            fixed (byte* src = bgra)
            fixed (byte* dst = nv12)
            {
                byte* uvPlane = dst + width * height;
                for (int y = 0; y < height; y += 2)
                {
                    byte* row0 = src + (long)y * width * 4;
                    byte* row1 = row0 + width * 4;
                    byte* y0 = dst + (long)y * width;
                    byte* y1 = y0 + width;
                    byte* uv = uvPlane + (long)(y >> 1) * width;

                    for (int x = 0; x < width; x += 2)
                    {
                        byte* p00 = row0 + x * 4;
                        byte* p01 = p00 + 4;
                        byte* p10 = row1 + x * 4;
                        byte* p11 = p10 + 4;

                        y0[x] = Luma(p00[2], p00[1], p00[0]);
                        y0[x + 1] = Luma(p01[2], p01[1], p01[0]);
                        y1[x] = Luma(p10[2], p10[1], p10[0]);
                        y1[x + 1] = Luma(p11[2], p11[1], p11[0]);

                        // 2×2 네 픽셀의 평균색으로 U·V 를 하나씩 만든다.
                        int r = p00[2] + p01[2] + p10[2] + p11[2];
                        int g = p00[1] + p01[1] + p10[1] + p11[1];
                        int b = p00[0] + p01[0] + p10[0] + p11[0];
                        byte ar = (byte)(r >> 2), ag = (byte)(g >> 2), ab = (byte)(b >> 2);
                        uv[x] = Cb(ar, ag, ab);
                        uv[x + 1] = Cr(ar, ag, ab);
                    }
                }
            }
        }

        // BT.601, 영상에서 쓰는 좁은 범위(밝기 16~235 · 색 16~240).
        //
        // 계수를 8비트 자리로 맞춰 뒀다. 예전에 16비트짜리 표를 >>16 으로 쓰는 바람에
        // 값이 네 배로 부풀어 화면이 전부 하얗게 날아갔다. 검사로 묶어 둔다.
        internal static byte Luma(byte r, byte g, byte b)
            => Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

        internal static byte Cb(byte r, byte g, byte b)
            => Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);

        internal static byte Cr(byte r, byte g, byte b)
            => Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);

        private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        /// <summary>크기와 픽셀 형식을 인코더가 기대하는 대로 맞춘다.</summary>
        private BitmapSource Normalize(BitmapSource frame)
        {
            BitmapSource sized = frame;

            if (sized.PixelWidth != Width || sized.PixelHeight != Height)
            {
                var scaled = new TransformedBitmap(sized,
                    new ScaleTransform((double)Width / sized.PixelWidth,
                                       (double)Height / sized.PixelHeight));
                scaled.Freeze();
                sized = scaled;
            }

            // Bgr32 는 Bgra32 와 메모리 배치가 같다(알파 자리를 안 쓸 뿐). 변환하면 복사만 한 번 더 한다.
            if (sized.Format != PixelFormats.Bgra32 && sized.Format != PixelFormats.Bgr32)
            {
                var converted = new FormatConvertedBitmap(sized, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                sized = converted;
            }

            return sized;
        }

        private bool _finished;
        private bool _finishOk;

        /// <summary>마무리가 실패했을 때의 이유. 성공했거나 아직 안 했으면 null.</summary>
        internal string? Error { get; private set; }

        /// <summary>
        /// 파일을 마무리한다(moov 기록). <b>이게 실패하면 파일은 어느 재생기로도 못 연다.</b>
        /// 디스크가 꽉 찼거나 저장 폴더가 사라진 경우가 그렇다. 그래서 결과를 돌려준다 —
        /// 부르는 쪽이 이 값을 보고 "완료" 인지 "실패" 인지 알려야 한다.
        /// 한 장도 안 넣었으면 쓸 파일이 없으므로 false.
        /// </summary>
        internal bool Finish()
        {
            if (_finished) return _finishOk;
            _finished = true;

            try
            {
                if (_writer == IntPtr.Zero || FrameCount == 0) { _finishOk = false; return false; }

                // 마지막 한 장은 다음 장이 없으니 정해진 간격만큼 머물다 끝나는 것으로 한다.
                Flush(_pendingTime + _frameDuration);
                MediaFoundation.WriterFinish(_writer);
                _finishOk = true;
            }
            catch (Exception ex)
            {
                Error = "파일을 마무리하지 못했습니다: " + ex.Message;
                Core.Log.Write(Error + " (" + Path + ")");
                _finishOk = false;
            }
            finally { MediaFoundation.Release(_pending); _pending = IntPtr.Zero; }

            return _finishOk;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            Finish();

            MediaFoundation.Release(_writer);
            _writer = IntPtr.Zero;
            MediaFoundation.Shutdown();

            // 한 장도 못 넣었으면 껍데기 파일만 남는다.
            if (FrameCount == 0)
            {
                try { if (File.Exists(Path)) File.Delete(Path); } catch { }
            }
        }
    }
}
