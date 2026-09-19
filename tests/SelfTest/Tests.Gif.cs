using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// 움직이는 GIF 를 실제로 써 보고, 다시 읽어서 맞는지 확인한다.

internal static partial class SelfTest
{
    private static void TestGifWriter()
    {
        Section("움직이는 GIF 쓰기");

        var frames = new List<BitmapSource>
        {
            SolidGif(40, 30, Colors.Red),
            SolidGif(40, 30, Colors.Lime),
            SolidGif(40, 30, Colors.Blue)
        };

        string path = Path.Combine(Path.GetTempPath(), "snapview_gif_test.gif");
        try
        {
            GifWriter.Save(frames, path, delayMs: 120);

            Check("파일이 만들어짐", File.Exists(path) && new FileInfo(path).Length > 0,
                  new FileInfo(path).Length + " 바이트");

            byte[] raw = File.ReadAllBytes(path);
            Check("GIF89a 로 시작", raw[0] == 'G' && raw[1] == 'I' && raw[2] == 'F' &&
                                 raw[3] == '8' && raw[4] == '9' && raw[5] == 'a');
            Check("끝 표시가 있음", raw[^1] == 0x3B);

            // 반복 설정(NETSCAPE2.0)이 들어갔는가. 이게 없으면 한 번 돌고 멈춘다.
            Check("끝없이 반복 설정이 들어감", Contains(raw, "NETSCAPE2.0"));

            // 실제로 되읽어 본다.
            using (FileStream fs = File.OpenRead(path))
            {
                var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                                                   BitmapCacheOption.OnLoad);

                Check("프레임 수가 맞음", decoder.Frames.Count == 3, decoder.Frames.Count + "장");
                Check("크기가 맞음",
                      decoder.Frames[0].PixelWidth == 40 && decoder.Frames[0].PixelHeight == 30,
                      $"{decoder.Frames[0].PixelWidth}x{decoder.Frames[0].PixelHeight}");

                Color c0 = PixelAt(decoder.Frames[0], 20, 15);
                Color c1 = PixelAt(decoder.Frames[1], 20, 15);
                Color c2 = PixelAt(decoder.Frames[2], 20, 15);

                Check("1번째 프레임이 빨강", c0.R > 200 && c0.G < 60, c0.ToString());
                Check("2번째 프레임이 초록", c1.G > 200 && c1.R < 60, c1.ToString());
                Check("3번째 프레임이 파랑", c2.B > 200 && c2.R < 60, c2.ToString());
            }

            // 간격이 실제로 박혔는지 — 그래픽 제어 블록의 값을 직접 읽는다.
            Check("프레임 간격이 12(=120ms)로 들어감", HasDelay(raw, 12), DelaysOf(raw));

            // 크기가 다른 장이 섞여도 첫 장 크기로 맞춰야 한다.
            var mixed = new List<BitmapSource> { SolidGif(40, 30, Colors.Red), SolidGif(20, 10, Colors.Lime) };
            using var ms = new MemoryStream();
            GifWriter.Save(mixed, ms, 100);
            ms.Position = 0;
            var d2 = new GifBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check("크기가 다른 장은 첫 장에 맞춰짐",
                  d2.Frames[1].PixelWidth == 40 && d2.Frames[1].PixelHeight == 30,
                  $"{d2.Frames[1].PixelWidth}x{d2.Frames[1].PixelHeight}");

            bool threw = false;
            try { GifWriter.Save(new List<BitmapSource>(), new MemoryStream(), 100); }
            catch (ArgumentException) { threw = true; }
            Check("빈 목록은 예외", threw);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>녹화가 쓰는 흘려보내기 방식. 다 모아 뒀다 저장하는 것과 결과가 같아야 한다.</summary>
    private static void TestGifStreaming()
    {
        Section("녹화용 GIF 흘려쓰기");

        string path = Path.Combine(Path.GetTempPath(), "snapview_gif_stream.gif");
        try
        {
            using (FileStream fs = File.Create(path))
            using (var gif = new GifWriter.Session(fs, 32, 24))
            {
                gif.Add(SolidGif(32, 24, Colors.Red), 100);
                gif.Add(SolidGif(32, 24, Colors.Lime), 100);
                gif.Add(SolidGif(64, 48, Colors.Blue), 100);   // 크기가 달라도 맞춰져야 한다
                Check("쓴 장수를 센다", gif.FrameCount == 3, gif.FrameCount.ToString());
            }

            using FileStream read = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(read, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            Check("흘려쓴 것도 3장", decoder.Frames.Count == 3, decoder.Frames.Count + "장");
            Check("모든 장이 첫 장 크기",
                  decoder.Frames[2].PixelWidth == 32 && decoder.Frames[2].PixelHeight == 24,
                  $"{decoder.Frames[2].PixelWidth}x{decoder.Frames[2].PixelHeight}");

            Color last = PixelAt(decoder.Frames[2], 16, 12);
            Check("마지막 장이 파랑", last.B > 200 && last.R < 60, last.ToString());

            // 닫은 뒤에 또 쓰면 막아야 한다.
            bool threw = false;
            var closed = new GifWriter.Session(new MemoryStream(), 8, 8);
            closed.Dispose();
            try { closed.Add(SolidGif(8, 8, Colors.Red), 100); }
            catch (ObjectDisposedException) { threw = true; }
            Check("닫은 뒤 쓰면 예외", threw);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// GIF 간격은 1/100초 단위라 30fps(33.3ms)를 장마다 반올림하면 30ms 가 되어 10% 빨리 감긴다.
    /// 누적 오차를 다음 장에 넘겨서 전체 길이를 맞춘다.
    /// </summary>
    private static void TestGifDelayRounding()
    {
        Section("GIF 간격 반올림 누적");

        using var ms = new MemoryStream();
        using (var gif = new GifWriter.Session(ms, 8, 8, leaveOpen: true))
        {
            for (int i = 0; i < 30; i++) gif.Add(SolidGif(8, 8, Colors.Red), 33);
            Check("30장×33ms ≈ 990ms (예전엔 900ms)", Math.Abs(gif.WrittenMs - 990) <= 10, gif.WrittenMs + "ms");
        }

        int total = 0;
        byte[] d = ms.ToArray();
        for (int i = 0; i + 7 < d.Length; i++)
            if (d[i] == 0x21 && d[i + 1] == 0xF9 && d[i + 2] == 4) total += (d[i + 4] | (d[i + 5] << 8)) * 10;
        Check("파일에 적힌 간격 합도 같다", Math.Abs(total - 990) <= 10, total + "ms");

        // 최소 간격은 그대로 2(20ms). 0·1 은 재생기마다 제멋대로 굴러서.
        using var ms2 = new MemoryStream();
        using (var fast = new GifWriter.Session(ms2, 8, 8, leaveOpen: true))
        {
            for (int i = 0; i < 10; i++) fast.Add(SolidGif(8, 8, Colors.Red), 5);
            Check("5ms 로 넣어도 장마다 20ms 는 준다", fast.WrittenMs == 200, fast.WrittenMs + "ms");
        }
    }

    private static void TestMediaKinds()
    {
        Section("뷰어가 여는 파일 종류");

        Check("mp4 는 동영상", ImageIO.IsVideo("a.mp4"));
        Check("MKV 도 대소문자 무시", ImageIO.IsVideo("A.MKV"));
        Check("png 은 동영상이 아님", !ImageIO.IsVideo("a.png"));
        Check("gif 는 그림 쪽", ImageIO.IsSupported("a.gif") && !ImageIO.IsVideo("a.gif"));
        Check("둘 다 뷰어가 연다", ImageIO.IsMedia("a.mp4") && ImageIO.IsMedia("a.png"));
        Check("모르는 확장자는 안 연다", !ImageIO.IsMedia("a.txt"));

        // 윈도우에 등록할 확장자에 동영상도 들어가야 파일 연결이 걸린다.
        string[] assoc = FileAssociation.AssociatedExtensions;
        Check("연결 목록에 그림이 들어감", assoc.Contains(".png"));
        Check("연결 목록에 동영상도 들어감", assoc.Contains(".mp4") && assoc.Contains(".mkv"));
        Check("연결 목록에 중복 없음", assoc.Distinct().Count() == assoc.Length);
        Check("연결 목록이 전부 점으로 시작", assoc.All(e => e.StartsWith(".", StringComparison.Ordinal)));
    }

    private static bool Contains(byte[] data, string text)
    {
        for (int i = 0; i + text.Length <= data.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < text.Length; j++)
            {
                if (data[i + j] != text[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }

    /// <summary>그래픽 제어 확장(21 F9 04) 안의 간격 값을 모은다.</summary>
    private static List<int> Delays(byte[] gif)
    {
        var found = new List<int>();
        for (int i = 0; i + 6 < gif.Length; i++)
        {
            if (gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 2] == 0x04)
                found.Add(gif[i + 4] | (gif[i + 5] << 8));
        }
        return found;
    }

    private static bool HasDelay(byte[] gif, int expected)
    {
        List<int> d = Delays(gif);
        return d.Count == 3 && d.TrueForAll(v => v == expected);
    }

    private static string DelaysOf(byte[] gif) => string.Join(",", Delays(gif));

    private static BitmapSource SolidGif(int w, int h, Color color)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = color.B; px[i + 1] = color.G; px[i + 2] = color.R; px[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
