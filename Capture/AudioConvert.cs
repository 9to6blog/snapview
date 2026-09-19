using System;

namespace SnapView.Capture
{
    /// <summary>
    /// 소리 장치가 주는 형식을 AAC 인코더가 받는 형식으로 옮긴다.
    ///
    /// 윈도우는 보통 <b>32비트 실수 · 2채널 · 48kHz</b> 로 준다.
    /// AAC 인코더는 <b>16비트 정수 · 1~2채널 · 44.1kHz 또는 48kHz</b> 만 받는다.
    /// 그래서 실수를 정수로 바꾸고, 채널 수가 넘치면 배치에 맞게 섞어서 줄인다.
    /// 표본 속도가 다르면 <see cref="AudioResampler"/> 가 따로 맞춘다.
    /// </summary>
    internal static class AudioConvert
    {
        /// <summary>AAC 인코더가 받아 주는 표본 속도인가.</summary>
        internal static bool IsSupportedRate(int rate) => rate == 44100 || rate == 48000;

        /// <summary>AAC 로 못 담는 속도의 장치는 이 속도로 바꿔서 담는다.</summary>
        internal const int FallbackRate = 48000;

        /// <summary>센터·뒤·옆 채널을 양쪽에 나눠 줄 때의 크기(-3dB).</summary>
        private const double Side = 0.7071;

        /// <summary>
        /// 장치가 준 덩어리를 16비트 PCM 으로 바꾼다.
        /// 결과 채널 수는 <paramref name="outChannels"/>(1 또는 2).
        /// </summary>
        internal static byte[] ToPcm16(byte[] source, int length, int inChannels,
                                       bool isFloat, int inBits, int outChannels)
        {
            outChannels = Math.Clamp(outChannels, 1, 2);
            if (length <= 0 || inChannels <= 0) return Array.Empty<byte>();

            int inBytesPerSample = Math.Max(1, inBits / 8);
            int inFrameBytes = inBytesPerSample * inChannels;
            int frames = length / inFrameBytes;
            if (frames <= 0) return Array.Empty<byte>();

            (double[] leftGain, double[] rightGain) = StereoGains(inChannels);
            var inFrame = new double[inChannels];
            var outBytes = new byte[frames * outChannels * 2];
            int at = 0;

            for (int f = 0; f < frames; f++)
            {
                int baseIndex = f * inFrameBytes;
                for (int c = 0; c < inChannels; c++)
                    inFrame[c] = ReadSample(source, baseIndex + c * inBytesPerSample, isFloat, inBits);

                double left = 0, right = 0;
                for (int c = 0; c < inChannels; c++)
                {
                    left += inFrame[c] * leftGain[c];
                    right += inFrame[c] * rightGain[c];
                }

                if (outChannels == 1) Write(outBytes, ref at, (left + right) / 2);
                else { Write(outBytes, ref at, left); Write(outBytes, ref at, right); }
            }

            return outBytes;
        }

        /// <summary>
        /// 채널 수별 표준 배치(WAVEFORMATEXTENSIBLE 기본 순서)를 스테레오로 줄이는 계수.
        /// 앞 왼·오는 그대로, 센터·뒤·옆은 -3dB 로 양쪽에 나누고, 저음(LFE)은 뺀다.
        /// 계수 합으로 나눠서 전 채널이 최대여도 넘치지 않게 한다.
        ///
        /// 예전엔 채널을 번갈아 묶어서 6채널이면 센터(대사)가 왼쪽에만, 저음이 오른쪽에만
        /// 실렸다. 좌우가 기울고 소리가 먹먹해지는 원인이었다.
        /// </summary>
        private static (double[] Left, double[] Right) StereoGains(int channels)
        {
            var l = new double[channels];
            var r = new double[channels];

            switch (channels)
            {
                case 1: l[0] = 1; r[0] = 1; break;
                case 2: l[0] = 1; r[1] = 1; break;
                case 3: l[0] = 1; r[1] = 1; l[2] = r[2] = Side; break;                              // FL FR FC
                case 4: l[0] = 1; r[1] = 1; l[2] = Side; r[3] = Side; break;                        // FL FR BL BR
                case 5: l[0] = 1; r[1] = 1; l[2] = r[2] = Side; l[3] = Side; r[4] = Side; break;    // FL FR FC BL BR
                case 6: l[0] = 1; r[1] = 1; l[2] = r[2] = Side; l[4] = Side; r[5] = Side; break;    // FL FR FC LFE BL BR
                case 7: l[0] = 1; r[1] = 1; l[2] = r[2] = Side; l[4] = r[4] = Side;
                        l[5] = Side; r[6] = Side; break;                                            // FL FR FC LFE BC SL SR
                case 8: l[0] = 1; r[1] = 1; l[2] = r[2] = Side; l[4] = Side; r[5] = Side;
                        l[6] = Side; r[7] = Side; break;                                            // FL FR FC LFE BL BR SL SR
                default:
                    // 모르는 배치: 짝수 번째는 왼쪽, 홀수 번째는 오른쪽. 없는 것보단 낫다.
                    for (int c = 0; c < channels; c++) { if (c % 2 == 0) l[c] = 1; else r[c] = 1; }
                    break;
            }

            Normalize(l);
            Normalize(r);
            return (l, r);
        }

        private static void Normalize(double[] gains)
        {
            double sum = 0;
            foreach (double g in gains) sum += g;
            if (sum <= 1) return;
            for (int i = 0; i < gains.Length; i++) gains[i] /= sum;
        }

        private static void Write(byte[] dst, ref int at, double value)
        {
            short pcm = (short)Math.Clamp(Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
            dst[at++] = (byte)(pcm & 0xFF);
            dst[at++] = (byte)((pcm >> 8) & 0xFF);
        }

        /// <summary>표본 하나를 -1 ~ 1 사이 값으로 읽는다.</summary>
        private static double ReadSample(byte[] data, int offset, bool isFloat, int bits)
        {
            if (offset < 0 || offset + bits / 8 > data.Length) return 0;

            if (isFloat && bits == 32)
                return BitConverter.ToSingle(data, offset);

            return bits switch
            {
                16 => BitConverter.ToInt16(data, offset) / 32768.0,
                32 => BitConverter.ToInt32(data, offset) / 2147483648.0,
                24 => ((data[offset + 2] << 16 | data[offset + 1] << 8 | data[offset]) << 8 >> 8) / 8388608.0,
                8 => (data[offset] - 128) / 128.0,
                _ => 0
            };
        }
    }
}
