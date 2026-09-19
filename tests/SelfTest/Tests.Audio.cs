using System;
using System.IO;
using System.Windows.Media;
using SnapView.Capture;

// 소리 변환과 소리 트랙이 붙은 MP4.

internal static partial class SelfTest
{
    private static void TestAudioConvert()
    {
        Section("소리 형식 바꾸기");

        Check("44.1kHz 는 담을 수 있다", AudioConvert.IsSupportedRate(44100));
        Check("48kHz 도 담을 수 있다", AudioConvert.IsSupportedRate(48000));
        Check("96kHz 는 못 담는다", !AudioConvert.IsSupportedRate(96000));

        // 32비트 실수 스테레오 → 16비트 스테레오
        var floats = new float[] { 0f, 0.5f, -0.5f, 1f, -1f, 0.25f };
        byte[] raw = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, raw, 0, raw.Length);

        byte[] pcm = AudioConvert.ToPcm16(raw, raw.Length, inChannels: 2, isFloat: true,
                                          inBits: 32, outChannels: 2);

        Check("표본 수가 유지된다", pcm.Length == floats.Length * 2, pcm.Length + "바이트");

        Check("0 은 0 으로", Sample(pcm, 0) == 0, Sample(pcm, 0).ToString());
        Check("0.5 는 절반쯤", Math.Abs(Sample(pcm, 1) - 16384) <= 2, Sample(pcm, 1).ToString());
        Check("-0.5 도 절반쯤", Math.Abs(Sample(pcm, 2) + 16384) <= 2, Sample(pcm, 2).ToString());
        Check("1.0 은 최대에서 안 넘친다", Sample(pcm, 3) == short.MaxValue, Sample(pcm, 3).ToString());
        Check("-1.0 도 안 넘친다", Sample(pcm, 4) <= -32767, Sample(pcm, 4).ToString());

        // 넘치는 값도 잘라 내야 한다.
        var loud = new float[] { 3f, -3f };
        byte[] loudRaw = new byte[loud.Length * 4];
        Buffer.BlockCopy(loud, 0, loudRaw, 0, loudRaw.Length);
        byte[] clipped = AudioConvert.ToPcm16(loudRaw, loudRaw.Length, 2, true, 32, 2);
        Check("넘치는 소리는 잘린다",
              Sample(clipped, 0) == short.MaxValue && Sample(clipped, 1) <= -32767);

        // 스테레오를 모노로 줄이면 절반 길이
        byte[] mono = AudioConvert.ToPcm16(raw, raw.Length, inChannels: 2, isFloat: true,
                                           inBits: 32, outChannels: 1);
        Check("스테레오 → 모노는 절반 길이", mono.Length == pcm.Length / 2,
              $"{mono.Length} vs {pcm.Length}");

        // 16비트 정수 입력도 다룬다.
        var shorts = new short[] { 0, 16384, -16384, short.MaxValue };
        byte[] intRaw = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, intRaw, 0, intRaw.Length);
        byte[] fromInt = AudioConvert.ToPcm16(intRaw, intRaw.Length, 1, false, 16, 1);
        Check("16비트 입력은 그대로 통과", Math.Abs(Sample(fromInt, 1) - 16384) <= 2,
              Sample(fromInt, 1).ToString());

        Check("빈 입력은 빈 결과", AudioConvert.ToPcm16(Array.Empty<byte>(), 0, 2, true, 32, 2).Length == 0);
    }

    /// <summary>
    /// 5.1·7.1 을 스테레오로 줄일 때 채널 배치를 지켜야 한다.
    /// 예전엔 채널을 번갈아 묶어서 센터(대사)가 왼쪽에만, 저음이 오른쪽에만 실렸다.
    /// </summary>
    private static void TestAudioDownmix()
    {
        Section("다채널 소리 줄이기");

        // 6채널 순서: FL FR FC LFE BL BR / 8채널: FL FR FC LFE BL BR SL SR
        short[] center = Mix(0, 0, 0.5f, 0, 0, 0);
        Check("센터는 양쪽에 똑같이", center[0] == center[1] && center[0] > 3000, $"{center[0]},{center[1]}");

        short[] lfe = Mix(0, 0, 0, 1f, 0, 0);
        Check("저음(LFE)은 섞지 않는다", lfe[0] == 0 && lfe[1] == 0, $"{lfe[0]},{lfe[1]}");

        short[] left = Mix(1f, 0, 0, 0, 0, 0);
        Check("앞왼쪽은 왼쪽에만", left[0] > 10000 && left[1] == 0, $"{left[0]},{left[1]}");

        short[] backRight = Mix(0, 0, 0, 0, 0, 1f);
        Check("뒤오른쪽은 오른쪽에만, 앞보다 작게",
              backRight[1] > 5000 && backRight[1] < left[0] && backRight[0] == 0, $"{backRight[0]},{backRight[1]}");

        short[] full = Mix(1f, 1f, 1f, 1f, 1f, 1f);
        Check("전부 최대여도 안 넘친다", full[0] <= short.MaxValue && full[0] > 20000 && full[0] == full[1],
              $"{full[0]},{full[1]}");

        short[] sideLeft = Mix(0, 0, 0, 0, 0, 0, 1f, 0);
        Check("7.1 옆왼쪽은 왼쪽에만", sideLeft[0] > 5000 && sideLeft[1] == 0, $"{sideLeft[0]},{sideLeft[1]}");

        // 스테레오 → 모노는 평균, 모노 → 스테레오는 복사.
        byte[] st = FloatFrame(0.5f, -0.5f);
        byte[] mono = AudioConvert.ToPcm16(st, st.Length, 2, true, 32, 1);
        Check("스테레오 → 모노는 평균", Sample(mono, 0) == 0, Sample(mono, 0).ToString());
        byte[] mo = FloatFrame(0.25f);
        byte[] wide = AudioConvert.ToPcm16(mo, mo.Length, 1, true, 32, 2);
        Check("모노 → 스테레오는 복사", Sample(wide, 0) == Sample(wide, 1) && Sample(wide, 0) > 8000,
              $"{Sample(wide, 0)},{Sample(wide, 1)}");
    }

    private static byte[] FloatFrame(params float[] frame)
    {
        var raw = new byte[frame.Length * 4];
        Buffer.BlockCopy(frame, 0, raw, 0, raw.Length);
        return raw;
    }

    /// <summary>실수 한 프레임을 스테레오 16비트로 줄인 (왼쪽, 오른쪽).</summary>
    private static short[] Mix(params float[] frame)
    {
        byte[] raw = FloatFrame(frame);
        byte[] pcm = AudioConvert.ToPcm16(raw, raw.Length, frame.Length, true, 32, 2);
        return new[] { Sample(pcm, 0), Sample(pcm, 1) };
    }

    /// <summary>
    /// 96kHz·192kHz 장치는 AAC 가 못 받는다. 48kHz 로 바꿔서라도 소리를 담는다.
    /// 덩어리 경계에서 끊기거나 겹치면 안 된다.
    /// </summary>
    private static void TestAudioResample()
    {
        Section("소리 표본 속도 바꾸기");

        // 96kHz 톱니(0,10,20,...) 모노 → 48kHz: 길이 절반, 값은 두 칸씩.
        var ramp = new short[960];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = (short)(i * 10);
        byte[] raw = new byte[ramp.Length * 2];
        Buffer.BlockCopy(ramp, 0, raw, 0, raw.Length);

        var down = new AudioResampler(1, 96000, 48000);
        byte[] a = down.Process(raw, raw.Length);
        Check("절반 길이", Math.Abs(a.Length - raw.Length / 2) <= 2, a.Length + "바이트");
        Check("값이 두 칸씩 간다", Math.Abs(Sample(a, 10) - 200) <= 10, Sample(a, 10).ToString());

        // 이어지는 블록: 첫 표본이 앞 블록 마지막 + 20 이어야 매끈하다.
        var ramp2 = new short[960];
        for (int i = 0; i < ramp2.Length; i++) ramp2[i] = (short)((960 + i) * 10);
        byte[] raw2 = new byte[ramp2.Length * 2];
        Buffer.BlockCopy(ramp2, 0, raw2, 0, raw2.Length);
        byte[] b = down.Process(raw2, raw2.Length);
        short lastA = Sample(a, a.Length / 2 - 1);
        Check("블록 경계가 매끈하다", Math.Abs(Sample(b, 0) - (lastA + 20)) <= 10,
              $"{lastA} → {Sample(b, 0)}");

        // 44.1kHz → 48kHz 스테레오는 길어진다: 441 프레임 → 480 안팎.
        var up = new AudioResampler(2, 44100, 48000);
        byte[] st = new byte[441 * 4];
        byte[] c = up.Process(st, st.Length);
        Check("44.1k → 48k 는 길어진다", Math.Abs(c.Length / 4 - 480) <= 1, (c.Length / 4) + "프레임");

        Check("빈 입력은 빈 결과", up.Process(Array.Empty<byte>(), 0).Length == 0);
    }

    private static short Sample(byte[] pcm, int index)
        => (short)(pcm[index * 2] | (pcm[index * 2 + 1] << 8));

    /// <summary>
    /// 시스템 소리 받기. COM vtable 을 직접 부르므로 슬롯이 틀리면 죽는다.
    /// 실제 장치에 한 번 붙어 본다.
    /// </summary>
    private static void TestLoopbackCapture()
    {
        Section("시스템 소리 받기 (WASAPI 루프백)");

        SnapView.Native.LoopbackCapture? capture = null;
        try
        {
            capture = new SnapView.Native.LoopbackCapture();
            capture.Start();

            Check("기본 재생 장치에 붙었다", capture.Started);
            Check("채널 수가 1 이상", capture.Channels >= 1, capture.Channels + "채널");
            Check("표본 속도가 그럴듯함", capture.SampleRate >= 8000 && capture.SampleRate <= 192000,
                  capture.SampleRate + "Hz");
            Check("표본 비트 수가 그럴듯함", capture.BitsPerSample is 16 or 24 or 32,
                  capture.BitsPerSample + "비트");

            Console.WriteLine($"         (장치 형식: {capture.Channels}채널 {capture.SampleRate}Hz " +
                              $"{capture.BitsPerSample}비트 {(capture.IsFloat ? "실수" : "정수")})");

            Check("AAC 로 담을 수 있는 속도", AudioConvert.IsSupportedRate(capture.SampleRate),
                  AudioConvert.IsSupportedRate(capture.SampleRate) ? "" : "이 장치로는 소리가 안 담깁니다");

            // 잠깐 받아 본다. 무음이어도 예외 없이 돌아야 한다.
            System.Threading.Thread.Sleep(300);
            byte[] block = capture.Drain();
            Check("소리를 예외 없이 받아 온다", true, block.Length + "바이트");

            // 받은 것을 그대로 변환해 본다.
            if (block.Length > 0)
            {
                byte[] pcm = AudioConvert.ToPcm16(block, block.Length, capture.Channels,
                                                  capture.IsFloat, capture.BitsPerSample, 2);
                Check("받은 소리를 16비트로 바꿀 수 있다", pcm.Length > 0, pcm.Length + "바이트");
            }
        }
        catch (Exception ex)
        {
            Check("시스템 소리 받기", false, ex.Message);
        }
        finally { capture?.Dispose(); }
    }

    /// <summary>
    /// 녹화 시작음은 <b>다 난 뒤에</b> 돌아와야 한다.
    ///
    /// 녹화기는 스피커로 나가는 소리를 통째로 담는다. 시작음이 나는 도중에 돌아오면
    /// 컨트롤러가 그 사이에 녹화기를 켜고, 시작음이 영상 맨 앞에 그대로 들어간다.
    /// 실제 장치로 확인한다: 시작음을 내고 돌아온 직후 루프백을 켰을 때 시작음이
    /// 안 잡혀야 한다. 대조군으로 루프백이 켜진 채 종료음을 내면 잡혀야 한다.
    /// (스피커로 짧은 소리가 두 번 난다. 음소거 상태면 판단하지 않고 넘어간다.)
    /// </summary>
    private static void TestRecordSoundBeforeCapture()
    {
        Section("녹화 시작음은 녹음이 켜지기 전에 끝난다");

        SnapView.Native.LoopbackCapture? capture = null;
        try
        {
            // 1. 시작음을 내고, 돌아온 직후 루프백을 켠다. 컨트롤러가 하는 순서 그대로.
            SnapView.Core.RecordSound.PlayStart(60);
            capture = new SnapView.Native.LoopbackCapture();
            capture.Start();
            System.Threading.Thread.Sleep(400);
            double after = TonePower(capture);

            // 2. 대조군: 루프백이 켜진 채로 종료음을 내면 당연히 잡혀야 한다.
            SnapView.Core.RecordSound.PlayStop(60);
            System.Threading.Thread.Sleep(400);
            double during = TonePower(capture);

            const double floor = 0.002;   // 이보다 작으면 소리가 아예 안 잡히는 환경
            if (during < floor)
            {
                Console.WriteLine($"         (루프백에 소리가 안 잡혀 판단하지 않습니다 — 음소거? 대조 {during:0.0000})");
                return;
            }

            Check("녹음 중에 낸 소리는 잡힌다(대조군)", during >= floor, $"{during:0.0000}");
            Check("시작음이 끝난 뒤에 녹음이 시작된다", after < during * 0.05,
                  $"앞부분 {after:0.0000} vs 대조 {during:0.0000}");
        }
        catch (Exception ex)
        {
            Check("녹화 시작음 순서", false, ex.Message);
        }
        finally { capture?.Dispose(); }
    }

    /// <summary>모인 소리에서 시작·종료음 두 주파수(660·990Hz)의 진폭 합. 0~1.</summary>
    private static double TonePower(SnapView.Native.LoopbackCapture capture)
    {
        byte[] raw = capture.Drain();
        if (raw.Length == 0) return 0;

        byte[] pcm = AudioConvert.ToPcm16(raw, raw.Length, capture.Channels,
                                          capture.IsFloat, capture.BitsPerSample, 1);
        var mono = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, mono, 0, mono.Length * 2);

        double sum = 0;
        foreach (double f in SnapView.Core.RecordSound.StartTones)
            sum += Goertzel(mono, capture.SampleRate, f);
        return sum;
    }

    /// <summary>한 주파수 성분의 진폭(0~1). 다른 소리가 섞여 있어도 그 음만 본다.</summary>
    private static double Goertzel(short[] x, int rate, double freq)
    {
        if (x.Length == 0) return 0;
        double k = 2 * Math.Cos(2 * Math.PI * freq / rate);
        double s1 = 0, s2 = 0;
        foreach (short v in x)
        {
            double s0 = v / 32768.0 + k * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        double power = s1 * s1 + s2 * s2 - k * s1 * s2;
        return Math.Sqrt(Math.Max(0, power)) * 2 / x.Length;
    }

    /// <summary>소리 트랙까지 붙인 MP4 가 실제로 만들어지는지.</summary>
    private static void TestMp4WithAudio()
    {
        Section("소리 담긴 MP4");

        string path = Path.Combine(Path.GetTempPath(), "snapview_mp4_audio.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            const int rate = 48000, channels = 2;
            bool hadAudio;

            using (var mp4 = new Mp4Writer(path, 320, 240, 10, 0, (channels, rate, 16)))
            {
                hadAudio = mp4.HasAudio;
                Check("소리 트랙이 붙는다", hadAudio);

                // 0.1초씩 10번 — 영상과 소리를 번갈아 넣는다.
                int bytesPerTenth = rate / 10 * channels * 2;
                var tone = new byte[bytesPerTenth];

                for (int i = 0; i < 10; i++)
                {
                    mp4.Add(SolidGif(320, 240, Color.FromRgb((byte)(20 + i * 20), 80, 140)));

                    // 440Hz 짜리 소리를 채운다.
                    for (int f = 0; f < bytesPerTenth / (channels * 2); f++)
                    {
                        double t = (i * (rate / 10.0) + f) / rate;
                        short v = (short)(Math.Sin(2 * Math.PI * 440 * t) * 8000);
                        for (int c = 0; c < channels; c++)
                        {
                            int o = (f * channels + c) * 2;
                            tone[o] = (byte)(v & 0xFF);
                            tone[o + 1] = (byte)((v >> 8) & 0xFF);
                        }
                    }
                    mp4.AddAudio(tone, tone.Length);
                }
            }

            Check("파일이 만들어짐", File.Exists(path));

            byte[] raw = File.ReadAllBytes(path);
            Check("MP4 로 시작", raw[4] == 'f' && raw[5] == 't' && raw[6] == 'y' && raw[7] == 'p');
            Check("H.264 트랙이 있음", HasBox(raw, "avc1"));
            Check("AAC 트랙이 있음", HasBox(raw, "mp4a"), hadAudio ? "" : "(소리 트랙이 안 붙었음)");
            Check("소리를 넣으면 파일이 더 커진다", new FileInfo(path).Length > 8000,
                  new FileInfo(path).Length + " 바이트");
        }
        catch (Exception ex)
        {
            Check("소리 담긴 MP4", false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
