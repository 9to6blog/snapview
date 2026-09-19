using System;

namespace SnapView.Capture
{
    /// <summary>
    /// 16비트 PCM 의 표본 속도를 바꾼다(직선 보간).
    ///
    /// AAC 는 44.1/48kHz 만 받는데 고급 DAC 는 96/192kHz 로 준다. 그런 장치에서도
    /// 소리를 포기하지 않으려는 것이다. 화면 녹화 소리에는 직선 보간이면 충분하다.
    /// 덩어리 단위로 불러도 이어지도록 마지막 프레임과 자리를 기억한다.
    /// </summary>
    internal sealed class AudioResampler
    {
        private readonly int _channels;
        private readonly double _step;      // 출력 한 표본이 입력 몇 프레임에 해당하는가
        private readonly short[] _prev;     // 앞 덩어리의 마지막 프레임
        private bool _hasPrev;
        private double _pos;                // 다음 출력 표본의 자리(현재 덩어리 프레임 기준, -1 보다 큼)

        internal AudioResampler(int channels, int fromRate, int toRate)
        {
            _channels = Math.Max(1, channels);
            _step = (double)Math.Max(1, fromRate) / Math.Max(1, toRate);
            _prev = new short[_channels];
        }

        /// <summary>한 덩어리를 바꾼다. 결과 길이는 속도 비율만큼 달라진다.</summary>
        internal byte[] Process(byte[] pcm, int length)
        {
            int frameBytes = _channels * 2;
            int frames = Math.Min(length, pcm.Length) / frameBytes;
            if (frames <= 0) return Array.Empty<byte>();

            // 보간에는 다음 프레임이 필요하다. 마지막 프레임까지만 내고 그 뒤는 다음 덩어리에서 잇는다.
            int count = (int)Math.Floor((frames - 1 - _pos) / _step) + 1;
            if (count < 0) count = 0;

            var output = new byte[count * frameBytes];
            int at = 0;
            for (int k = 0; k < count; k++)
            {
                double p = _pos + k * _step;
                int i = (int)Math.Floor(p);
                double frac = p - i;

                for (int c = 0; c < _channels; c++)
                {
                    double a = SampleAt(pcm, i, c);
                    double b = frac > 0 ? SampleAt(pcm, i + 1, c) : a;
                    short v = (short)Math.Clamp(Math.Round(a + (b - a) * frac), short.MinValue, short.MaxValue);
                    output[at++] = (byte)(v & 0xFF);
                    output[at++] = (byte)((v >> 8) & 0xFF);
                }
            }

            _pos = _pos + count * _step - frames;
            for (int c = 0; c < _channels; c++) _prev[c] = ReadShort(pcm, (frames - 1) * _channels + c);
            _hasPrev = true;
            return output;
        }

        private double SampleAt(byte[] pcm, int frame, int channel)
        {
            if (frame < 0) return _hasPrev ? _prev[channel] : ReadShort(pcm, channel);
            return ReadShort(pcm, frame * _channels + channel);
        }

        private static short ReadShort(byte[] pcm, int index)
        {
            int o = index * 2;
            return (short)(pcm[o] | (pcm[o + 1] << 8));
        }
    }
}
