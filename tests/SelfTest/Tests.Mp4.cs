using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Capture;

// H.264 MP4 인코딩. COM vtable 슬롯을 직접 부르므로 번호가 하나만 틀려도 죽는다.
// 그래서 매번 진짜 파일을 하나 만들어 본다.

internal static partial class SelfTest
{
    private static void TestMp4Writer()
    {
        Section("MP4 인코딩 (Media Foundation)");

        string path = Path.Combine(Path.GetTempPath(), "snapview_mp4_test.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            using (var mp4 = new Mp4Writer(path, 320, 240, fps: 10))
            {
                Check("가로세로가 짝수로 맞춰짐", mp4.Width % 2 == 0 && mp4.Height % 2 == 0,
                      $"{mp4.Width}x{mp4.Height}");

                for (int i = 0; i < 12; i++)
                {
                    byte level = (byte)(20 + i * 18);
                    mp4.Add(SolidGif(320, 240, Color.FromRgb(level, (byte)(255 - level), 90)));
                }

                Check("12장을 넣었다", mp4.FrameCount == 12, mp4.FrameCount.ToString());
            }

            Check("파일이 만들어짐", File.Exists(path));
            long size = new FileInfo(path).Length;
            Check("내용이 들어 있음", size > 2000, size + " 바이트");

            byte[] raw = File.ReadAllBytes(path);

            // MP4 는 맨 앞이 ftyp 박스다.
            Check("MP4(ftyp)로 시작", raw[4] == 'f' && raw[5] == 't' && raw[6] == 'y' && raw[7] == 'p');
            Check("moov(구성 정보)가 있음", HasBox(raw, "moov"));
            Check("mdat(실제 영상)이 있음", HasBox(raw, "mdat"));
            Check("H.264 트랙(avc1)이 있음", HasBox(raw, "avc1"));

            // 홀수 크기를 줘도 죽지 않아야 한다.
            string odd = Path.Combine(Path.GetTempPath(), "snapview_mp4_odd.mp4");
            try
            {
                using (var w = new Mp4Writer(odd, 101, 77, fps: 10))
                {
                    w.Add(SolidGif(101, 77, Colors.Red));
                    Check("홀수 크기도 처리됨", w.Width == 100 && w.Height == 76, $"{w.Width}x{w.Height}");
                }
                Check("홀수 크기 파일도 생김", File.Exists(odd) && new FileInfo(odd).Length > 0);
            }
            finally { try { if (File.Exists(odd)) File.Delete(odd); } catch { } }

            // 한 장도 안 넣으면 껍데기 파일을 남기지 않아야 한다.
            string empty = Path.Combine(Path.GetTempPath(), "snapview_mp4_empty.mp4");
            using (new Mp4Writer(empty, 64, 64, fps: 10)) { }
            Check("빈 녹화는 파일을 안 남긴다", !File.Exists(empty));

            TestMp4RealTime();
            TestMp4HighFps();
            TestAudioSilencePadding();
            TestAudioPlacement();
            TestMp4Finish();
            TestNv12Conversion();
            TestCaptureInto();
        }
        catch (Exception ex)
        {
            Check("MP4 인코딩", false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// RGB → YUV 변환. 여기가 틀리면 영상 색이 통째로 망가진다.
    /// 실제로 한 번 색이 네 배로 부풀어 하얗게 날아간 적이 있어서 값으로 묶어 둔다.
    /// </summary>
    private static void TestYuvConversion()
    {
        Section("영상 색 변환 (BT.601)");

        // 흰색: 밝기 최대(좁은 범위라 235), 색차는 가운데(128)
        Check("흰색 밝기 = 235", Mp4Writer.Luma(255, 255, 255) == 235,
              Mp4Writer.Luma(255, 255, 255).ToString());
        Check("흰색은 색이 없다", Mp4Writer.Cb(255, 255, 255) == 128 && Mp4Writer.Cr(255, 255, 255) == 128,
              $"{Mp4Writer.Cb(255, 255, 255)},{Mp4Writer.Cr(255, 255, 255)}");

        // 검정: 밝기 최소(16)
        Check("검정 밝기 = 16", Mp4Writer.Luma(0, 0, 0) == 16, Mp4Writer.Luma(0, 0, 0).ToString());
        Check("검정도 색은 가운데", Mp4Writer.Cb(0, 0, 0) == 128 && Mp4Writer.Cr(0, 0, 0) == 128);

        // 회색은 밝기만 중간, 색은 가운데
        byte grayY = Mp4Writer.Luma(128, 128, 128);
        Check("중간 회색 밝기는 16~235 사이", grayY > 110 && grayY < 140, grayY.ToString());
        Check("회색도 색은 가운데", Mp4Writer.Cb(128, 128, 128) == 128);

        // 빨강·초록·파랑의 자리 (BT.601 표준값)
        Check("빨강 Y≈81 Cr≈240", Near(Mp4Writer.Luma(255, 0, 0), 81) && Near(Mp4Writer.Cr(255, 0, 0), 240),
              $"Y={Mp4Writer.Luma(255, 0, 0)} Cr={Mp4Writer.Cr(255, 0, 0)}");
        Check("초록 Y≈145", Near(Mp4Writer.Luma(0, 255, 0), 145), Mp4Writer.Luma(0, 255, 0).ToString());
        Check("파랑 Y≈41 Cb≈240", Near(Mp4Writer.Luma(0, 0, 255), 41) && Near(Mp4Writer.Cb(0, 0, 255), 240),
              $"Y={Mp4Writer.Luma(0, 0, 255)} Cb={Mp4Writer.Cb(0, 0, 255)}");

        // 초록이 파랑보다 훨씬 밝아야 한다(사람 눈이 초록에 민감하다).
        Check("밝기 순서 초록 > 빨강 > 파랑",
              Mp4Writer.Luma(0, 255, 0) > Mp4Writer.Luma(255, 0, 0) &&
              Mp4Writer.Luma(255, 0, 0) > Mp4Writer.Luma(0, 0, 255));

        // 어떤 값을 넣어도 범위를 안 벗어나야 한다.
        int bad = 0;
        for (int v = 0; v <= 255; v += 5)
        {
            byte y = Mp4Writer.Luma((byte)v, (byte)(255 - v), (byte)(v / 2));
            if (y < 16 || y > 235) bad++;
        }
        Check("밝기가 16~235 를 안 벗어남", bad == 0, bad + "개 벗어남");
    }

    private static bool Near(byte actual, int expected) => Math.Abs(actual - expected) <= 2;

    /// <summary>
    /// 녹화 시작음과 종료음. 화면을 안 보고 단축키만 눌렀을 때
    /// 켠 건지 끈 건지 소리만으로 알 수 있어야 한다.
    /// </summary>
    private static void TestRecordSound()
    {
        Section("녹화 시작음 · 종료음");

        byte[] start = SnapView.Core.RecordSound.BuildWav(SnapView.Core.RecordSound.StartTones, 60);
        byte[] stop = SnapView.Core.RecordSound.BuildWav(SnapView.Core.RecordSound.StopTones, 60);

        Check("시작음이 WAV", start[0] == 'R' && start[1] == 'I' && start[2] == 'F' && start[3] == 'F');
        Check("종료음도 WAV", stop[0] == 'R' && stop[1] == 'I' && stop[2] == 'F' && stop[3] == 'F');
        Check("길이가 같다(같은 길이의 두 음)", start.Length == stop.Length);

        // 소리가 실제로 달라야 한다. 같은 파형이면 구분이 안 된다.
        Check("두 소리가 서로 다르다", !SameBytes(start, stop));

        // 시작은 올라가고 종료는 내려간다.
        Check("시작은 낮은 음 → 높은 음",
              SnapView.Core.RecordSound.StartTones[0] < SnapView.Core.RecordSound.StartTones[1]);
        Check("종료는 높은 음 → 낮은 음",
              SnapView.Core.RecordSound.StopTones[0] > SnapView.Core.RecordSound.StopTones[1]);
        Check("같은 음 두 개를 뒤집은 것",
              SnapView.Core.RecordSound.StartTones[0] == SnapView.Core.RecordSound.StopTones[1] &&
              SnapView.Core.RecordSound.StartTones[1] == SnapView.Core.RecordSound.StopTones[0]);

        // 볼륨 0 이면 아무 소리도 안 나야 한다.
        byte[] silent = SnapView.Core.RecordSound.BuildWav(SnapView.Core.RecordSound.StartTones, 0);
        Check("볼륨 0 은 완전 무음", MaxAmplitude(silent) == 0, MaxAmplitude(silent).ToString());

        // 클리핑이 없어야 한다.
        Check("클리핑 없음", MaxAmplitude(start) < short.MaxValue, MaxAmplitude(start).ToString());

        // 볼륨을 올리면 실제로 커진다.
        Check("볼륨을 올리면 커진다",
              MaxAmplitude(SnapView.Core.RecordSound.BuildWav(SnapView.Core.RecordSound.StartTones, 100)) >
              MaxAmplitude(SnapView.Core.RecordSound.BuildWav(SnapView.Core.RecordSound.StartTones, 30)));
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static int MaxAmplitude(byte[] wav)
    {
        int max = 0;
        for (int i = 44; i + 1 < wav.Length; i += 2)
        {
            short v = (short)(wav[i] | (wav[i + 1] << 8));
            max = Math.Max(max, Math.Abs((int)v));
        }
        return max;
    }

    /// <summary>
    /// 찍힌 시각을 그대로 적으면 영상 길이가 실제와 맞아야 한다.
    ///
    /// 예전에는 정해 둔 간격대로 차곡차곡 쌓았다. 그러면 기계가 못 따라가 장을 거를 때마다
    /// 영상이 빨리 감겼다 — 2초를 찍었는데 0.3초짜리가 나온다. 여기서 값으로 묶어 둔다.
    /// </summary>
    private static void TestMp4RealTime()
    {
        string path = Path.Combine(Path.GetTempPath(), "snapview_mp4_realtime.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            // 30fps 로 열었지만 실제로는 초당 5장밖에 못 찍은 상황.
            const int frames = 10;
            const double gap = 0.2;

            using (var w = new Mp4Writer(path, 160, 120, fps: 30))
            {
                for (int i = 0; i < frames; i++)
                {
                    byte level = (byte)(20 + i * 20);
                    w.Add(SolidGif(160, 120, Color.FromRgb(level, 90, (byte)(255 - level))),
                          TimeSpan.FromSeconds(i * gap));
                }
            }

            double seconds = Mp4Seconds(File.ReadAllBytes(path));
            double want = (frames - 1) * gap;          // 마지막 장의 머무는 시간은 덤

            Check("느리게 찍혀도 길이가 실제 시간과 맞는다", seconds >= want && seconds < want + 0.3,
                  $"{seconds:0.00}초 (기대 {want:0.00}초 남짓)");
            Check("정해진 간격대로 쌓았을 때보다 훨씬 길다", seconds > frames / 30.0 * 2,
                  $"{seconds:0.00}초 vs {frames / 30.0:0.00}초");
        }
        catch (Exception ex) { Check("실제 시각으로 담기", false, ex.Message); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    /// <summary>30 을 넘는 값도 인코더가 받아 줘야 한다. 예전에는 여기서 잘렸다.</summary>
    private static void TestMp4HighFps()
    {
        foreach (int fps in new[] { 1, 24, 60, Mp4Writer.MaxFps })
        {
            string path = Path.Combine(Path.GetTempPath(), $"snapview_mp4_{fps}fps.mp4");
            try { if (File.Exists(path)) File.Delete(path); } catch { }

            try
            {
                using (var w = new Mp4Writer(path, 160, 120, fps))
                {
                    for (int i = 0; i < 4; i++)
                        w.Add(SolidGif(160, 120, Colors.SteelBlue), TimeSpan.FromSeconds(i / (double)fps));
                }
                Check($"{fps}fps 로도 담긴다", File.Exists(path) && new FileInfo(path).Length > 1000);
            }
            catch (Exception ex) { Check($"{fps}fps 로도 담긴다", false, ex.Message); }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        Check("고를 수 있는 폭이 30 을 넘는다", Mp4Writer.MaxFps > 30, Mp4Writer.MaxFps.ToString());
        Check("녹화기와 인코더의 한계가 같다",
              SnapView.Capture.ScreenRecorder.MaxFps == Mp4Writer.MaxFps &&
              SnapView.Capture.ScreenRecorder.MinFps == Mp4Writer.MinFps);
    }

    /// <summary>
    /// 진짜로 화면을 잠깐 찍어 본다.
    ///
    /// 두 가지를 본다. (1) 30 을 넘는 값을 골랐을 때 실제로 30 을 넘게 담기는가 —
    /// 예전에는 30 에서 잘렸고, 잘리지 않게 고친 뒤에도 윈도우 타이머 눈금(15.6ms)에
    /// 걸려 64장 근처에서 막혔다. (2) 담긴 영상 길이가 <b>실제로 찍은 시간</b>과 맞는가.
    /// </summary>
    private static void TestRecorderTiming()
    {
        Section("녹화 속도와 길이");

        string path = Path.Combine(Path.GetTempPath(), "snapview_rec_test.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        var region = new System.Windows.Int32Rect(0, 0, 240, 180);
        var recorder = new ScreenRecorder(region, fps: 60, path, preferMp4: true, withAudio: false);

        try
        {
            recorder.Start();
            Check("MP4 로 담고 있다", recorder.IsMp4);

            // 2초 동안 타이머가 돌 수 있게 메시지를 흘려 준다.
            var frame = new System.Windows.Threading.DispatcherFrame();
            var stop = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
            stop.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            double seconds = recorder.Elapsed.TotalSeconds;
            double actual = recorder.ActualFps;
            int frames = recorder.FrameCount;

            bool wrote = recorder.Stop();

            Check("한 장이라도 담겼다", wrote && frames > 0, frames + "장");
            Console.WriteLine($"         (실측 {actual:0.#}fps, {frames}장 / {seconds:0.00}초)");

            // 240x180 짜리 작은 영역이면 초당 30장은 넘겨야 한다.
            // 여기서 막히면 타이머 눈금(timeBeginPeriod)이 안 먹고 있다는 뜻이다.
            Check("60 을 골랐을 때 30 을 넘긴다", actual > 30,
                  $"{actual:0.#}fps ({frames}장 / {seconds:0.00}초)");

            double length = Mp4Seconds(File.ReadAllBytes(path));
            Check("영상 길이가 실제로 찍은 시간과 맞는다",
                  Math.Abs(length - seconds) < 0.35, $"{length:0.00}초 vs {seconds:0.00}초");
        }
        catch (Exception ex) { Check("녹화", false, ex.Message); }
        finally
        {
            recorder.Dispose();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// 조용한 구간이 있어도 소리와 그림의 시계가 안 어긋나야 한다.
    ///
    /// WASAPI 루프백은 <b>아무 소리도 안 날 때 패킷을 아예 주지 않는다</b>. 그대로 두면
    /// 소리 트랙만 안 자라서, 조용한 구간이 끝난 뒤의 소리가 그만큼 앞당겨져 붙는다 —
    /// 5초 조용했으면 이후 소리가 그림보다 5초 빨라지고 소리 트랙도 5초 짧게 끝난다.
    /// </summary>
    /// <summary>
    /// 소리 덩어리를 <b>장치가 말한 시각</b>에 놓는다.
    /// 빈 곳이면 그 앞을 무음으로 메우고, 이미 채운 자리와 겹치면 겹친 앞부분을 버린다.
    /// 조용한 구간 뒤의 소리가 앞당겨지지 않게 하는 핵심이다.
    /// </summary>
    private static void TestAudioPlacement()
    {
        const int rate = 48000, ch = 2;
        int bytesPerSecond = rate * ch * 2;
        string path = Path.Combine(Path.GetTempPath(), "snapview_audio_place.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            using var w = new Mp4Writer(path, 160, 120, 10, 0, (ch, rate, 16));
            if (!w.HasAudio) { Check("소리 트랙을 붙일 수 있다", false, "AAC 인코더 없음"); return; }
            w.Add(SolidGif(160, 120, Colors.SlateGray), TimeSpan.Zero);

            var tenth = new byte[bytesPerSecond / 10];   // 0.1초짜리 덩어리

            // 1) 빈 곳에 놓으면 그 앞을 무음으로 채운다: 1.0초에 놓으면 1.1초까지 찬다.
            w.AddAudioAt(tenth, 0, tenth.Length, TimeSpan.FromSeconds(1.0));
            Check("빈 곳은 무음으로 채우고 그 뒤에 붙는다", Near(w.AudioFilledTo, 1.1),
                  w.AudioFilledTo.TotalSeconds.ToString("0.000"));

            // 2) 이어지는 덩어리는 무음을 끼우지 않고 그대로 붙는다.
            w.AddAudioAt(tenth, 0, tenth.Length, TimeSpan.FromSeconds(1.1));
            Check("이어지는 덩어리는 그대로 붙는다", Near(w.AudioFilledTo, 1.2),
                  w.AudioFilledTo.TotalSeconds.ToString("0.000"));

            // 3) 이미 채운 자리(1.2)보다 앞(1.15)에 놓으면 겹친 0.05초를 버리고 나머지만 붙는다.
            w.AddAudioAt(tenth, 0, tenth.Length, TimeSpan.FromSeconds(1.15));
            Check("겹친 앞부분은 버린다", Near(w.AudioFilledTo, 1.25),
                  w.AudioFilledTo.TotalSeconds.ToString("0.000"));

            // 4) 1ms 어긋난 것은 타임스탬프 잔떨림이다. 무음을 끼우지 않고 이어 붙인다.
            w.AddAudioAt(tenth, 0, tenth.Length, TimeSpan.FromSeconds(1.251));
            Check("작은 어긋남은 그대로 이어 붙인다", Near(w.AudioFilledTo, 1.35),
                  w.AudioFilledTo.TotalSeconds.ToString("0.000"));

            // 5) 시각이 없는 덩어리(AddAudio)는 예전처럼 채운 곳 뒤에 붙는다.
            w.AddAudio(tenth, tenth.Length);
            Check("시각 없는 덩어리는 뒤에 붙는다", Near(w.AudioFilledTo, 1.45),
                  w.AudioFilledTo.TotalSeconds.ToString("0.000"));
        }
        catch (Exception ex) { Check("소리 시각 배치", false, ex.Message); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    private static bool Near(TimeSpan actual, double seconds) => Math.Abs(actual.TotalSeconds - seconds) < 0.005;

    /// <summary>
    /// 마무리(moov 쓰기)가 성공했는지 알 수 있어야 한다. 예전엔 실패를 삼키고
    /// 찍은 장수만 보고 "녹화 완료" 라고 알렸다 — 열리지 않는 파일을 완료라고 한 것이다.
    /// </summary>
    private static void TestMp4Finish()
    {
        Section("MP4 마무리 결과");

        string path = Path.Combine(Path.GetTempPath(), "snapview_finish.mp4");
        string empty = Path.Combine(Path.GetTempPath(), "snapview_finish_empty.mp4");
        try { if (File.Exists(path)) File.Delete(path); if (File.Exists(empty)) File.Delete(empty); } catch { }

        try
        {
            using (var w = new Mp4Writer(path, 160, 120, 10))
            {
                w.Add(SolidGif(160, 120, Colors.Teal), TimeSpan.Zero);
                w.Add(SolidGif(160, 120, Colors.Teal), TimeSpan.FromSeconds(0.1));
                Check("마무리에 성공하면 true", w.Finish());
                Check("두 번 불러도 true 그대로", w.Finish());
                Check("오류 메시지는 없다", w.Error == null, w.Error);
            }

            byte[] raw = File.ReadAllBytes(path);
            bool hasMoov = false;
            foreach (var _ in Mp4Boxes(raw, 0, raw.Length, "moov")) hasMoov = true;
            Check("마무리한 파일에는 moov 가 있다", hasMoov);

            using (var e = new Mp4Writer(empty, 160, 120, 10))
                Check("한 장도 없으면 마무리는 false", !e.Finish());
            Check("빈 파일은 남기지 않는다", !File.Exists(empty));
        }
        catch (Exception ex) { Check("MP4 마무리", false, ex.Message); }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); if (File.Exists(empty)) File.Delete(empty); } catch { }
        }
    }

    /// <summary>
    /// BGRA → NV12 변환. 포인터로 두 줄씩 훑는 빠른 길이 느린 기준 계산과 같은 답을 내야 한다.
    /// </summary>
    private static void TestNv12Conversion()
    {
        Section("BGRA → NV12 변환");

        const int w = 6, h = 4;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4] = (byte)(i * 9);            // B
            bgra[i * 4 + 1] = (byte)(255 - i * 7);  // G
            bgra[i * 4 + 2] = (byte)(i * 13);       // R
            bgra[i * 4 + 3] = 255;
        }
        var nv12 = new byte[w * h * 3 / 2];
        Mp4Writer.ConvertBgraToNv12(bgra, w, h, nv12);

        int badY = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (nv12[y * w + x] != Mp4Writer.Luma(bgra[i + 2], bgra[i + 1], bgra[i])) badY++;
            }
        Check("밝기 면이 픽셀마다 맞는다", badY == 0, badY + "개 다름");

        int badUv = 0;
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = ((y + dy) * w + (x + dx)) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2];
                    }
                byte ar = (byte)(r / 4), ag = (byte)(g / 4), ab = (byte)(b / 4);
                int o = w * h + (y / 2) * w + x;
                if (nv12[o] != Mp4Writer.Cb(ar, ag, ab) || nv12[o + 1] != Mp4Writer.Cr(ar, ag, ab)) badUv++;
            }
        Check("색 면이 2×2 평균으로 맞는다", badUv == 0, badUv + "개 다름");

        bool threw = false;
        try { Mp4Writer.ConvertBgraToNv12(new byte[5 * 4 * 4], 5, 4, new byte[64]); }
        catch (ArgumentException) { threw = true; }
        Check("홀수 크기는 예외", threw);

        // 녹화가 쓰는 길: 픽셀 버퍼를 바로 넣는다. 영역이 홀수 크기면 한 줄·한 칸을 잘라 넣는다.
        string path = Path.Combine(Path.GetTempPath(), "snapview_addbgra.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try
        {
            using var writer = new Mp4Writer(path, 161, 121, 10);
            var pixels = new byte[161 * 121 * 4];
            writer.AddBgra(pixels, 161, 121, TimeSpan.Zero);
            writer.AddBgra(pixels, 161, 121, TimeSpan.FromSeconds(0.1));
            Check("홀수 크기 버퍼도 잘라 넣는다", writer.FrameCount == 2 && writer.Width == 160 && writer.Height == 120,
                  $"{writer.FrameCount}장 {writer.Width}×{writer.Height}");
            Check("그대로 마무리된다", writer.Finish());
        }
        catch (Exception ex) { Check("픽셀 버퍼 넣기", false, ex.Message); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    /// <summary>화면을 BitmapSource 없이 버퍼로 바로 뜬다. 녹화가 프레임마다 8MB 를 새로 잡지 않게.</summary>
    private static void TestCaptureInto()
    {
        Section("화면을 버퍼로 바로 뜨기");

        var r = new System.Windows.Int32Rect(0, 0, 16, 16);
        var buf = new byte[16 * 16 * 4];
        Check("떠 넣기 성공", SnapView.Native.ScreenCapture.CaptureRectInto(r, false, buf));

        BitmapSource shot = SnapView.Native.ScreenCapture.CaptureRect(r, false);
        var reference = new byte[16 * 16 * 4];
        shot.CopyPixels(reference, 64, 0);
        int diff = 0;
        for (int i = 0; i < buf.Length; i++)
            if (i % 4 != 3 && buf[i] != reference[i]) diff++;   // 알파 자리는 비교하지 않는다
        // 두 번 뜨는 사이에 화면이 조금 바뀔 수 있다(커서·깜빡임). 줄 간격이 틀렸다면 대부분이 다르다.
        Check("BitmapSource 로 뜬 것과 같은 픽셀", diff < buf.Length * 3 / 10, diff + "바이트 다름");

        bool threw = false;
        try { SnapView.Native.ScreenCapture.CaptureRectInto(r, false, new byte[10]); }
        catch (ArgumentException) { threw = true; }
        Check("작은 버퍼는 예외", threw);
    }

    /// <summary>
    /// 잠깐 쉬면 그동안은 영상에 안 들어가야 한다 — 시계가 멈추고, 다시 찍으면 이어진다.
    /// 0.6초 찍고 0.6초 쉬고 0.6초 더 찍으면 영상은 1.2초 안팎이어야 한다.
    /// </summary>
    private static void TestRecorderPause()
    {
        Section("녹화 잠깐 쉬기");

        string path = Path.Combine(Path.GetTempPath(), "snapview_pause_test.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        var region = new System.Windows.Int32Rect(0, 0, 160, 120);
        var recorder = new ScreenRecorder(region, fps: 30, path, preferMp4: true, withAudio: false);
        try
        {
            recorder.Start();
            PumpFor(0.6);
            recorder.Pause();
            Check("쉬는 중이라고 말한다", recorder.IsPaused);
            TimeSpan atPause = recorder.Elapsed;
            int framesAtPause = recorder.FrameCount;
            PumpFor(0.6);
            Check("쉬는 동안 시계가 안 간다", recorder.Elapsed == atPause,
                  $"{atPause.TotalSeconds:0.00} → {recorder.Elapsed.TotalSeconds:0.00}");
            // 누르는 순간 찍고 있던 한 장은 들어갈 수 있다(시각은 쉬기 전이라 영상은 멀쩡하다).
            Check("쉬는 동안 안 찍는다", recorder.FrameCount <= framesAtPause + 1,
                  $"{framesAtPause} → {recorder.FrameCount}");
            recorder.Resume();
            Check("다시 찍는다", !recorder.IsPaused);
            PumpFor(0.6);

            double seconds = recorder.Elapsed.TotalSeconds;
            bool wrote = recorder.Stop();
            Check("파일이 남았다", wrote, recorder.LastError);
            Check("길이가 쉰 시간을 뺀 1.2초 안팎", seconds > 0.95 && seconds < 1.5, seconds.ToString("0.00") + "초");

            byte[] raw = File.ReadAllBytes(path);
            double video = TrackSeconds(raw, "vmhd");
            Check("영상 트랙도 쉰 시간을 뺀 길이", video > 0.9 && video < 1.6, video.ToString("0.00") + "초");
        }
        catch (Exception ex) { Check("잠깐 쉬기", false, ex.Message); }
        finally
        {
            recorder.Dispose();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>화면 메시지를 흘리며 잠깐 기다린다(녹화 스레드가 Tick 을 보낼 수 있게).</summary>
    private static void PumpFor(double seconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void TestAudioSilencePadding()
    {
        const int rate = 48000, ch = 2, fps = 10, seconds = 6;
        string path = Path.Combine(Path.GetTempPath(), "snapview_audio_gap.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            using (var w = new Mp4Writer(path, 160, 120, fps, 0, (ch, rate, 16)))
            {
                if (!w.HasAudio) { Check("소리 트랙을 붙일 수 있다", false, "AAC 인코더 없음"); return; }

                for (int i = 0; i < fps * seconds; i++)
                {
                    double t = i / (double)fps;
                    w.Add(SolidGif(160, 120, Colors.SlateGray), TimeSpan.FromSeconds(t));

                    // 2~4초 구간은 장치가 아무것도 안 주는 상황을 흉내 낸다.
                    if (t < 2 || t >= 4)
                        w.AddAudio(new byte[rate / fps * ch * 2], rate / fps * ch * 2);

                    w.PadAudioTo(TimeSpan.FromSeconds(t), TimeSpan.FromMilliseconds(400));
                }
                w.PadAudioTo(TimeSpan.FromSeconds(seconds), TimeSpan.Zero);

                Check("소리가 영상 끝까지 채워졌다",
                      Math.Abs(w.AudioFilledTo.TotalSeconds - seconds) < 0.2,
                      $"{w.AudioFilledTo.TotalSeconds:0.00}초 / {seconds}초");
            }

            byte[] raw = File.ReadAllBytes(path);
            double video = TrackSeconds(raw, "vmhd");
            double audio = TrackSeconds(raw, "smhd");

            Check("영상 트랙 길이가 맞는다", Math.Abs(video - seconds) < 0.3, $"{video:0.00}초");
            Check("소리 트랙이 영상보다 짧게 끝나지 않는다", Math.Abs(audio - video) < 0.4,
                  $"소리 {audio:0.00}초 vs 영상 {video:0.00}초");
        }
        catch (Exception ex) { Check("조용한 구간 메우기", false, ex.Message); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    /// <summary>trak 하나의 길이(초). marker 는 vmhd(영상) 또는 smhd(소리).</summary>
    private static double TrackSeconds(byte[] d, string marker)
    {
        foreach ((long ms, long me) in Mp4Boxes(d, 0, d.Length, "moov"))
            foreach ((long ts, long te) in Mp4Boxes(d, ms, me, "trak"))
                foreach ((long ds, long de) in Mp4Boxes(d, ts, te, "mdia"))
                {
                    bool match = false;
                    foreach ((long ns, long ne) in Mp4Boxes(d, ds, de, "minf"))
                        foreach (var _ in Mp4Boxes(d, ns, ne, marker)) match = true;
                    if (!match) continue;

                    foreach ((long hs, long _) in Mp4Boxes(d, ds, de, "mdhd"))
                    {
                        uint scale = Be32(d, (int)hs + 12);
                        uint dur = Be32(d, (int)hs + 16);
                        if (scale > 0) return (double)dur / scale;
                    }
                }
        return 0;
    }

    private static System.Collections.Generic.IEnumerable<(long, long)> Mp4Boxes(
        byte[] d, long from, long to, string name)
    {
        long at = from;
        while (at + 8 <= to)
        {
            long size = Be32(d, (int)at);
            string kind = "" + (char)d[at + 4] + (char)d[at + 5] + (char)d[at + 6] + (char)d[at + 7];
            if (size == 0) size = to - at;
            if (size < 8 || at + size > to) yield break;
            if (kind == name) yield return (at + 8, at + size);
            at += size;
        }
    }

    /// <summary>mvhd 박스에서 영상 길이(초)를 읽는다.</summary>
    private static double Mp4Seconds(byte[] data)
    {
        for (int i = 0; i + 24 <= data.Length; i++)
        {
            if (data[i] != 'm' || data[i + 1] != 'v' || data[i + 2] != 'h' || data[i + 3] != 'd') continue;

            int p = i + 4;
            int version = data[p];
            p += 4;                       // version(1) + flags(3)

            if (version == 1)
            {
                p += 16;                  // 만든 때 · 고친 때 (8바이트씩)
                uint scale = Be32(data, p); p += 4;
                ulong dur = ((ulong)Be32(data, p) << 32) | Be32(data, p + 4);
                return scale == 0 ? 0 : (double)dur / scale;
            }
            else
            {
                p += 8;                   // 만든 때 · 고친 때 (4바이트씩)
                uint scale = Be32(data, p); p += 4;
                uint dur = Be32(data, p);
                return scale == 0 ? 0 : (double)dur / scale;
            }
        }
        return 0;
    }

    private static uint Be32(byte[] d, int i)
        => ((uint)d[i] << 24) | ((uint)d[i + 1] << 16) | ((uint)d[i + 2] << 8) | d[i + 3];

    /// <summary>MP4 박스 이름이 파일 어딘가에 있는지. 대충이지만 구조 확인에는 충분하다.</summary>
    private static bool HasBox(byte[] data, string name)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == name[0] && data[i + 1] == name[1] &&
                data[i + 2] == name[2] && data[i + 3] == name[3]) return true;
        }
        return false;
    }
}
