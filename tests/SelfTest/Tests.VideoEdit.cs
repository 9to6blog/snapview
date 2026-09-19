using System;
using System.IO;
using System.Windows.Media;
using SnapView.Capture;
using SnapView.Core;
using SnapView.Native;

// 컷 편집 · 배속. SourceReader(디코더)도 COM vtable 직접 호출이라,
// 진짜 MP4 를 만들어 되읽고 → 잘라 내보내고 → 길이와 색까지 확인한다.

internal static partial class SelfTest
{
    private static void TestVideoEdit(string tmp)
    {
        Section("영상 컷 편집 · 배속");

        // 3초짜리 시험 영상: 0~1초 빨강, 1~2초 초록, 2~3초 파랑 (10fps, 160x120)
        string src = Path.Combine(tmp, "edit_src.mp4");
        using (var w = new Mp4Writer(src, 160, 120, fps: 10))
        {
            for (int i = 0; i < 30; i++)
            {
                Color c = i < 10 ? Colors.Red : i < 20 ? Color.FromRgb(0, 200, 0) : Colors.Blue;
                w.Add(SolidGif(160, 120, c), TimeSpan.FromMilliseconds(i * 100));
            }
        }
        Check("시험 영상이 만들어짐", File.Exists(src) && new FileInfo(src).Length > 1000);

        // ---- 되읽기 (SourceReader 슬롯 검증) ----
        int frames = 0;
        bool ordered = true, firstIsRed = false;
        TimeSpan last = TimeSpan.MinValue;
        using (var r = new VideoReader(src))
        {
            Check("되읽은 크기가 원본과 같다", r.Width == 160 && r.Height == 120,
                  $"{r.Width}x{r.Height}");

            VideoReader.Sample? s;
            while ((s = r.Read()) != null)
            {
                if (!s.Value.IsVideo) continue;
                if (frames == 0) firstIsRed = Nv12CenterIs(s.Value.Data, r.Width, r.Height, "red");
                if (s.Value.Time <= last) ordered = false;
                last = s.Value.Time;
                frames++;
            }
        }
        Check("장 수가 그대로 나온다", frames == 30, frames + "장");
        Check("시각이 단조롭게 늘어난다", ordered);
        Check("첫 장이 빨강으로 디코딩됨", firstIsRed);

        // ---- 컷: 가운데 초록 구간만 남기기 ----
        string cut = Path.Combine(tmp, "edit_cut.mp4");
        int kept = VideoEditor.Export(src, cut,
            TimeSpan.FromSeconds(1.05), TimeSpan.FromSeconds(1.95), speed: 1.0);
        Check("잘라낸 파일이 생김", File.Exists(cut) && new FileInfo(cut).Length > 500);
        Check("구간만큼만 담김", kept >= 7 && kept <= 11, kept + "장");

        VideoInfo? cutInfo = VideoInfo.TryRead(cut);
        Check("잘라낸 길이가 구간과 비슷", cutInfo != null &&
              Math.Abs(cutInfo.Seconds - 0.9) < 0.35, cutInfo?.Seconds.ToString("0.00") + "초");

        using (var r = new VideoReader(cut))
        {
            VideoReader.Sample? s;
            bool green = false;
            while ((s = r.Read()) != null)
            {
                if (!s.Value.IsVideo) continue;
                green = Nv12CenterIs(s.Value.Data, r.Width, r.Height, "green");
                break;
            }
            Check("잘라낸 영상의 첫 장은 초록", green);
        }

        // ---- 배속 ----
        string slow = Path.Combine(tmp, "edit_slow.mp4");
        VideoEditor.Export(src, slow, TimeSpan.FromSeconds(1.05), TimeSpan.FromSeconds(1.95), 0.5);
        VideoInfo? slowInfo = VideoInfo.TryRead(slow);
        Check("0.5×(슬로우)는 길이가 두 배쯤", slowInfo != null &&
              Math.Abs(slowInfo.Seconds - 1.8) < 0.5, slowInfo?.Seconds.ToString("0.00") + "초");

        string fast = Path.Combine(tmp, "edit_fast.mp4");
        VideoEditor.Export(src, fast, TimeSpan.FromSeconds(1.05), TimeSpan.FromSeconds(1.95), 2.0);
        VideoInfo? fastInfo = VideoInfo.TryRead(fast);
        Check("2×(빨리감기)는 길이가 절반쯤", fastInfo != null &&
              fastInfo.Seconds < 0.75 && fastInfo.Seconds > 0.2,
              fastInfo?.Seconds.ToString("0.00") + "초");

        // ---- 소리: 1× 컷에서는 남고, 배속에서는 뺀다 ----
        string srcA = Path.Combine(tmp, "edit_src_audio.mp4");
        using (var w = new Mp4Writer(srcA, 160, 120, fps: 10, 0, (2, 44100, 16)))
        {
            var pcm = new byte[44100 * 4];   // 1초 분량, 낮은 사인파
            for (int i = 0; i < 44100; i++)
            {
                short v = (short)(Math.Sin(i * 0.05) * 8000);
                pcm[i * 4] = pcm[i * 4 + 2] = (byte)(v & 0xFF);
                pcm[i * 4 + 1] = pcm[i * 4 + 3] = (byte)(v >> 8);
            }
            for (int i = 0; i < 20; i++)
                w.Add(SolidGif(160, 120, Colors.Orange), TimeSpan.FromMilliseconds(i * 100));
            w.AddAudio(pcm, pcm.Length);
            w.PadAudioTo(TimeSpan.FromSeconds(2), TimeSpan.Zero);
        }

        string cutA = Path.Combine(tmp, "edit_cut_audio.mp4");
        VideoEditor.Export(srcA, cutA, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), 1.0);
        Check("1× 컷에는 소리 트랙이 남는다", HasBox(File.ReadAllBytes(cutA), "mp4a"));

        string fastA = Path.Combine(tmp, "edit_fast_audio.mp4");
        VideoEditor.Export(srcA, fastA, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), 2.0);
        Check("배속에서는 소리를 뺀다(음정이 틀어지므로)", !HasBox(File.ReadAllBytes(fastA), "mp4a"));

        // ---- 말이 안 되는 구간 ----
        bool threw = false;
        try { VideoEditor.Export(src, Path.Combine(tmp, "x.mp4"), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 1.0); }
        catch (ArgumentException) { threw = true; }
        Check("끝이 시작보다 앞이면 예외", threw);

        TestVideoEditPadding(tmp);
    }

    /// <summary>
    /// 코딩 크기 ≠ 표시 크기인 해상도에서의 되읽기.
    ///
    /// H.264 는 16의 배수로 부호화한다 — 548×466 은 표면이 560×480 이고 피치는 더 클 수
    /// 있다. 처음 구현이 이걸 몰라서 실제 녹화물이 줄무늬로 깨졌는데, 단색 프레임 검사는
    /// 어느 픽셀을 읽어도 같은 색이라 못 잡았다. 그래서 <b>위치마다 색이 다른 패턴</b>으로
    /// 네 모서리 사분면을 전부 확인한다.
    /// </summary>
    private static void TestVideoEditPadding(string tmp)
    {
        const int W = 548, H = 466;   // 실제로 깨졌던 그 크기
        string src = Path.Combine(tmp, "edit_pad.mp4");

        using (var w = new Mp4Writer(src, W, H, fps: 10))
        {
            var img = QuadrantPattern(W, H);
            for (int i = 0; i < 10; i++) w.Add(img, TimeSpan.FromMilliseconds(i * 100));
        }

        using (var r = new VideoReader(src))
        {
            Check("코딩 크기가 다른 영상도 표시 크기로 읽힘", r.Width == W && r.Height == H,
                  $"{r.Width}x{r.Height}");

            VideoReader.Sample? s;
            while ((s = r.Read()) != null)
            {
                if (!s.Value.IsVideo) continue;
                byte[] d = s.Value.Data;
                Check("좌상단 사분면이 빨강", Nv12At(d, W, H, W / 4, H / 4, "red"));
                Check("우상단 사분면이 초록", Nv12At(d, W, H, 3 * W / 4, H / 4, "green"));
                Check("좌하단 사분면이 파랑", Nv12At(d, W, H, W / 4, 3 * H / 4, "blue"));
                Check("우하단 사분면이 흰색", Nv12At(d, W, H, 3 * W / 4, 3 * H / 4, "white"));
                // 줄 밀림·UV 어긋남은 가장자리에서 제일 먼저 드러난다.
                Check("오른쪽 끝도 제 색(초록)", Nv12At(d, W, H, W - 4, H / 4, "green"));
                Check("아래쪽 끝도 제 색(파랑)", Nv12At(d, W, H, W / 4, H - 4, "blue"));
                break;
            }
        }

        // 이 크기로 컷 내보내기까지 왕복해도 패턴이 살아 있어야 한다.
        string cut = Path.Combine(tmp, "edit_pad_cut.mp4");
        VideoEditor.Export(src, cut, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.75), 1.0);
        using (var r = new VideoReader(cut))
        {
            VideoReader.Sample? s;
            while ((s = r.Read()) != null)
            {
                if (!s.Value.IsVideo) continue;
                byte[] d = s.Value.Data;
                bool all = Nv12At(d, r.Width, r.Height, r.Width / 4, r.Height / 4, "red") &&
                           Nv12At(d, r.Width, r.Height, 3 * r.Width / 4, r.Height / 4, "green") &&
                           Nv12At(d, r.Width, r.Height, r.Width / 4, 3 * r.Height / 4, "blue") &&
                           Nv12At(d, r.Width, r.Height, 3 * r.Width / 4, 3 * r.Height / 4, "white");
                Check("컷 왕복 후에도 사분면 패턴 유지", all);
                break;
            }
        }
    }

    /// <summary>사분면마다 색이 다른 시험 이미지: 좌상 빨강 · 우상 초록 · 좌하 파랑 · 우하 흰색.</summary>
    private static System.Windows.Media.Imaging.BitmapSource QuadrantPattern(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            bool right = x >= w / 2, bottom = y >= h / 2;
            (byte r, byte g, byte b) = (right, bottom) switch
            {
                (false, false) => ((byte)220, (byte)30, (byte)30),
                (true, false) => ((byte)25, (byte)200, (byte)40),
                (false, true) => ((byte)30, (byte)40, (byte)220),
                _ => ((byte)235, (byte)235, (byte)235)
            };
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static bool Nv12CenterIs(byte[] nv12, int w, int h, string color)
        => Nv12At(nv12, w, h, w / 2, h / 2, color);

    /// <summary>NV12 프레임의 (x,y)가 그 색인지 — 인코딩·디코딩 왕복 후라 넉넉히 본다.</summary>
    private static bool Nv12At(byte[] nv12, int w, int h, int x, int y, string color)
    {
        byte luma = nv12[y * w + x];
        int uvRow = w * h + (y / 2) * w;
        byte u = nv12[uvRow + (x / 2) * 2];
        byte v = nv12[uvRow + (x / 2) * 2 + 1];

        return color switch
        {
            "red" => v > 170 && u < 130,                 // Cr 높음
            "green" => u < 115 && v < 115 && luma > 100, // 둘 다 낮고 밝음
            "blue" => u > 170 && v < 130,                // Cb 높음
            "white" => luma > 190 && Math.Abs(u - 128) < 22 && Math.Abs(v - 128) < 22,
            _ => false
        };
    }
}
