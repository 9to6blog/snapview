using System;
using System.IO;
using System.Media;
using System.Threading;

namespace SnapView.Core
{
    /// <summary>
    /// 녹화 시작·종료 알림음.
    ///
    /// 캡처음과 달리 <b>둘이 확실히 달라야</b> 한다. 화면을 안 보고 단축키만 눌렀을 때
    /// 지금 켠 건지 끈 건지 소리만으로 알 수 있어야 하기 때문이다.
    /// 시작은 <b>낮은 음에서 높은 음으로</b>, 종료는 그 반대로 간다.
    ///
    /// 시작음은 <b>다 날 때까지 기다렸다가</b> 돌아온다(약 0.2초). 녹화기는 스피커로 나가는
    /// 소리를 통째로 담으므로, 시작음이 나는 도중에 녹화기를 켜면 시작음이 영상 맨 앞에
    /// 들어간다. 부르는 쪽은 이 호출이 끝난 뒤에 녹화기를 켠다.
    /// 종료음은 파일을 닫은 뒤에 나므로 기다리지 않고 바로 돌아온다.
    /// </summary>
    internal static class RecordSound
    {
        private const int SampleRate = 44100;
        private const double ToneSeconds = 0.09;     // 음 하나 길이
        private const double GapSeconds = 0.02;      // 음 사이 틈
        private const double HeadRoom = 0.22;        // 너무 크지 않게

        // 5도 간격 두 음. 시작은 올라가고 종료는 내려간다.
        private static readonly double[] Rising = { 660, 990 };
        private static readonly double[] Falling = { 990, 660 };

        private static readonly SoundPlayer Player = new();
        private static readonly object Gate = new();

        /// <summary>동기 재생이 돌아온 뒤 장치가 꼬리를 다 내보낼 때까지 더 기다리는 시간.</summary>
        private const int EndpointDrainMs = 120;

        internal static void PlayStart(int volumePercent) => Play(Rising, volumePercent, wait: true);
        internal static void PlayStop(int volumePercent) => Play(Falling, volumePercent, wait: false);

        private static void Play(double[] tones, int volumePercent, bool wait)
        {
            if (volumePercent <= 0) return;

            try
            {
                lock (Gate)
                {
                    Player.Stream = new MemoryStream(BuildWav(tones, volumePercent));
                    if (wait)
                    {
                        // PlaySync 는 소리 장치에 마지막 조각을 넘긴 순간 돌아온다. 장치가 그걸
                        // 실제로 내보내기까지 수십 ms 가 더 걸려서, 그 꼬리가 녹음 앞머리에 잡혔다.
                        Player.PlaySync();
                        Thread.Sleep(EndpointDrainMs);
                    }
                    else Player.Play();
                }
            }
            catch
            {
                // 소리 장치가 없어도 녹화 자체는 계속돼야 한다.
            }
        }

        /// <summary>두 음을 이어 붙인 16비트 모노 WAV.</summary>
        internal static byte[] BuildWav(double[] tones, int volumePercent)
        {
            volumePercent = Math.Clamp(volumePercent, 0, 100);
            double amplitude = HeadRoom * (volumePercent / 100.0);

            int toneSamples = (int)(SampleRate * ToneSeconds);
            int gapSamples = (int)(SampleRate * GapSeconds);
            var samples = new short[tones.Length * toneSamples + (tones.Length - 1) * gapSamples];

            int at = 0;
            for (int t = 0; t < tones.Length; t++)
            {
                for (int i = 0; i < toneSamples; i++)
                {
                    double time = (double)i / SampleRate;

                    // 앞뒤를 부드럽게 깎는다. 안 그러면 딱딱 소리가 난다.
                    double envelope = Math.Min(1.0, Math.Min(i, toneSamples - i) / (SampleRate * 0.008));

                    double wave = Math.Sin(2 * Math.PI * tones[t] * time)
                                  + 0.3 * Math.Sin(4 * Math.PI * tones[t] * time);

                    double value = wave / 1.3 * envelope * amplitude;
                    samples[at++] = (short)Math.Clamp(Math.Round(value * short.MaxValue),
                                                      short.MinValue, short.MaxValue);
                }

                if (t < tones.Length - 1) at += gapSamples;   // 틈은 무음 그대로
            }

            return WrapAsWav(samples);
        }

        private static byte[] WrapAsWav(short[] samples)
        {
            const short channels = 1;
            const short bitsPerSample = 16;
            int dataBytes = samples.Length * 2;

            using var ms = new MemoryStream(44 + dataBytes);
            using var w = new BinaryWriter(ms);

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { 'W', 'A', 'V', 'E' });

            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write(channels);
            w.Write(SampleRate);
            w.Write(SampleRate * channels * bitsPerSample / 8);
            w.Write((short)(channels * bitsPerSample / 8));
            w.Write(bitsPerSample);

            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            foreach (short s in samples) w.Write(s);

            w.Flush();
            return ms.ToArray();
        }

        /// <summary>검사용: 시작음과 종료음이 실제로 다른지 확인할 때 쓴다.</summary>
        internal static double[] StartTones => Rising;
        internal static double[] StopTones => Falling;
    }
}
