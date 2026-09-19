using System;
using System.Text;
using SnapView.Core;

// 캡처 알림음 검사. 소리를 실제로 내지는 않고 만들어진 WAV 를 뜯어본다.

internal static partial class SelfTest
{
    private static void TestCaptureSound()
    {
        Section("캡처 알림음");

        byte[] wav = CaptureSound.BuildWav(100);

        // --- 헤더가 제대로 된 WAV 인가 ---
        Check("RIFF 로 시작", Ascii(wav, 0, 4) == "RIFF", Ascii(wav, 0, 4));
        Check("WAVE 형식", Ascii(wav, 8, 4) == "WAVE", Ascii(wav, 8, 4));
        Check("fmt 청크가 있음", Ascii(wav, 12, 4) == "fmt ", Ascii(wav, 12, 4));
        Check("data 청크가 있음", Ascii(wav, 36, 4) == "data", Ascii(wav, 36, 4));

        int riffSize = BitConverter.ToInt32(wav, 4);
        Check("RIFF 크기가 파일 크기와 맞음", riffSize == wav.Length - 8,
              $"{riffSize} vs {wav.Length - 8}");

        int dataBytes = BitConverter.ToInt32(wav, 40);
        Check("data 크기가 실제 데이터와 맞음", dataBytes == wav.Length - 44,
              $"{dataBytes} vs {wav.Length - 44}");

        short format = BitConverter.ToInt16(wav, 20);
        short channels = BitConverter.ToInt16(wav, 22);
        int rate = BitConverter.ToInt32(wav, 24);
        short bits = BitConverter.ToInt16(wav, 34);
        Check("16비트 모노 PCM 44.1kHz",
              format == 1 && channels == 1 && rate == 44100 && bits == 16,
              $"fmt={format} ch={channels} rate={rate} bits={bits}");

        int blockAlign = BitConverter.ToInt16(wav, 32);
        int byteRate = BitConverter.ToInt32(wav, 28);
        Check("블록 정렬·초당 바이트가 맞음",
              blockAlign == 2 && byteRate == 44100 * 2, $"{blockAlign} / {byteRate}");

        // --- 소리의 성질 ---
        short[] pcm = ReadSamples(wav);
        double seconds = pcm.Length / 44100.0;
        Check("길이가 0.1~0.25초 (스치고 사라지는 정도)", seconds is > 0.1 and < 0.25,
              seconds.ToString("0.###") + "초");

        Check("맨 앞은 무음에서 시작(딱 소리 방지)", Math.Abs(pcm[0]) < 1200, pcm[0].ToString());
        Check("맨 뒤도 무음으로 끝(툭 소리 방지)", Math.Abs(pcm[^1]) < 1200, pcm[^1].ToString());

        int peak = 0, peakAt = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            if (Math.Abs((int)pcm[i]) > peak) { peak = Math.Abs(pcm[i]); peakAt = i; }
        }
        Check("클리핑 없음(최대 진폭이 한계 미만)", peak < short.MaxValue,
              peak + " / " + short.MaxValue);
        Check("볼륨 100%에서도 여유가 있음(너무 크지 않음)", peak < short.MaxValue * 0.6,
              (peak / (double)short.MaxValue).ToString("0.##"));
        Check("앞쪽에서 가장 크고 뒤로 갈수록 잦아듦", peakAt < pcm.Length / 3,
              $"{peakAt} / {pcm.Length}");

        // 뒤쪽 1/4 은 앞쪽보다 훨씬 조용해야 한다(감쇠가 도는지)
        double head = Rms(pcm, 0, pcm.Length / 4);
        double tail = Rms(pcm, pcm.Length * 3 / 4, pcm.Length);
        Check("끝부분이 시작보다 훨씬 조용함", tail < head * 0.2,
              $"{head:0} -> {tail:0}");

        // --- 볼륨 ---
        short[] quiet = ReadSamples(CaptureSound.BuildWav(20));
        short[] loud = ReadSamples(CaptureSound.BuildWav(80));
        Check("볼륨을 올리면 실제로 커진다", Peak(loud) > Peak(quiet) * 3,
              $"{Peak(quiet)} -> {Peak(loud)}");
        Check("길이는 볼륨과 무관", quiet.Length == loud.Length);

        short[] silent = ReadSamples(CaptureSound.BuildWav(0));
        Check("볼륨 0 이면 완전 무음", Peak(silent) == 0, Peak(silent).ToString());

        // 범위를 벗어난 값이 들어와도 죽지 않아야 한다
        Check("볼륨 음수도 안전", CaptureSound.BuildWav(-50).Length > 44);
        Check("볼륨 100 초과도 안전", Peak(ReadSamples(CaptureSound.BuildWav(500))) < short.MaxValue);
    }

    private static string Ascii(byte[] data, int offset, int length)
        => Encoding.ASCII.GetString(data, offset, length);

    private static short[] ReadSamples(byte[] wav)
    {
        int count = (wav.Length - 44) / 2;
        var pcm = new short[count];
        for (int i = 0; i < count; i++) pcm[i] = BitConverter.ToInt16(wav, 44 + i * 2);
        return pcm;
    }

    private static int Peak(short[] pcm)
    {
        int peak = 0;
        foreach (short s in pcm) peak = Math.Max(peak, Math.Abs((int)s));
        return peak;
    }

    private static double Rms(short[] pcm, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)pcm[i] * pcm[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }
}
