using System;
using System.IO;
using System.Media;

namespace SnapView.Core
{
    /// <summary>
    /// 캡처했을 때 나는 짧은 알림음.
    ///
    /// 카메라 셔터("찰칵") 같은 소리는 쓰지 않는다. 하루에도 수십 번 나는 소리라
    /// 자동차 UI 처럼 <b>짧고 부드럽고 작은</b> 톤이어야 한다.
    /// 음원 파일을 두지 않고 그때그때 사인파를 합성한다 — 볼륨도 파형에 직접 녹여
    /// 넣으므로 SoundPlayer 에 볼륨 조절이 없어도 된다.
    /// </summary>
    internal static class CaptureSound
    {
        private const int SampleRate = 44100;

        /// <summary>전체 길이(초). 짧게 스치고 사라지는 정도.</summary>
        private const double Duration = 0.16;

        /// <summary>딱 소리가 나지 않게 아주 짧게 열어 준다.</summary>
        private const double AttackSeconds = 0.004;

        /// <summary>감쇠 시간 상수. 작을수록 빨리 사라진다.</summary>
        private const double DecayTau = 0.038;

        /// <summary>끝에서 뚝 끊겨 '툭' 소리가 나지 않도록 마지막을 부드럽게 내린다.</summary>
        private const double ReleaseSeconds = 0.010;

        /// <summary>볼륨 100%에서의 최대 진폭. 1.0 로 두면 너무 크다.</summary>
        private const double HeadRoom = 0.45;

        // 종소리처럼 들리도록 기본음 + 5도 + 옥타브를 아주 약하게 섞는다.
        private static readonly (double Freq, double Gain)[] Partials =
        {
            (880.0, 0.62),    // A5
            (1320.0, 0.28),   // E6
            (1760.0, 0.10)    // A6
        };

        private static SoundPlayer? _player;
        private static int _cachedVolume = -1;

        internal static void Play(int volumePercent)
        {
            try
            {
                volumePercent = Math.Clamp(volumePercent, 0, 100);
                if (volumePercent == 0) return;

                if (_player == null || _cachedVolume != volumePercent)
                {
                    _player?.Dispose();
                    _player = new SoundPlayer(new MemoryStream(BuildWav(volumePercent)));
                    _player.Load();
                    _cachedVolume = volumePercent;
                }
                _player.Play();
            }
            catch
            {
                // 소리 장치가 없거나 막혀 있어도 캡처 자체는 계속돼야 한다.
            }
        }

        /// <summary>16비트 모노 PCM WAV 한 개를 통째로 만든다.</summary>
        internal static byte[] BuildWav(int volumePercent)
        {
            volumePercent = Math.Clamp(volumePercent, 0, 100);
            double amplitude = HeadRoom * (volumePercent / 100.0);

            int sampleCount = (int)(SampleRate * Duration);
            var samples = new short[sampleCount];

            double releaseStart = Duration - ReleaseSeconds;

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / SampleRate;

                double wave = 0;
                foreach ((double freq, double gain) in Partials)
                    wave += gain * Math.Sin(2 * Math.PI * freq * t);

                double envelope = Math.Exp(-t / DecayTau);
                if (t < AttackSeconds) envelope *= t / AttackSeconds;
                if (t > releaseStart) envelope *= (Duration - t) / ReleaseSeconds;

                double value = wave * envelope * amplitude;
                samples[i] = (short)Math.Clamp(Math.Round(value * short.MaxValue),
                                               short.MinValue, short.MaxValue);
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
            w.Write(16);                                   // fmt 청크 크기
            w.Write((short)1);                             // PCM
            w.Write(channels);
            w.Write(SampleRate);
            w.Write(SampleRate * channels * bitsPerSample / 8);   // 초당 바이트
            w.Write((short)(channels * bitsPerSample / 8));       // 블록 정렬
            w.Write(bitsPerSample);

            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            foreach (short s in samples) w.Write(s);

            w.Flush();
            return ms.ToArray();
        }
    }
}
