using System;
using System.IO;
using System.Windows.Media;
using SnapView.Capture;
using SnapView.Core;

// 영상 파일에서 장 수·초당 장수를 읽어 낸다.
// 재생기가 안 알려 주는 값이라 파일을 직접 뜯는다 — 박스 오프셋이 하나만 틀려도
// 엉뚱한 숫자가 나오므로, 우리가 만든 파일로 왕복 검사한다.

internal static partial class SelfTest
{
    private static void TestVideoInfo()
    {
        Section("영상 장 수 읽기 (MP4 박스)");

        string path = Path.Combine(Path.GetTempPath(), "snapview_info_test.mp4");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        try
        {
            const int fps = 20, frames = 40;          // 2초짜리
            using (var w = new Mp4Writer(path, 160, 120, fps))
            {
                for (int i = 0; i < frames; i++)
                {
                    byte level = (byte)(10 + i * 5);
                    w.Add(SolidGif(160, 120, Color.FromRgb(level, 80, 200)),
                          TimeSpan.FromSeconds(i / (double)fps));
                }
            }

            VideoInfo? info = VideoInfo.TryRead(path);
            Check("MP4 를 읽어 냈다", info != null);
            if (info == null) return;

            Check("장 수가 맞는다", info.FrameCount == frames,
                  $"{info.FrameCount} / {frames}");
            Check("길이가 맞는다", Math.Abs(info.Seconds - frames / (double)fps) < 0.15,
                  $"{info.Seconds:0.000}초");
            Check("초당 장수가 맞는다", Math.Abs(info.Fps - fps) < 1.0, $"{info.Fps:0.00}fps");

            // 시각 ↔ 장 번호가 서로 되돌아와야 한다.
            Check("0초는 첫 장", info.FrameAt(TimeSpan.Zero) == 0);
            Check("1초는 절반쯤", info.FrameAt(TimeSpan.FromSeconds(1)) == fps,
                  info.FrameAt(TimeSpan.FromSeconds(1)).ToString());
            Check("끝을 넘겨도 마지막 장", info.FrameAt(TimeSpan.FromSeconds(99)) == frames - 1);

            bool roundTrips = true;
            for (int i = 0; i < frames; i++)
                if (info.FrameAt(info.TimeOfFrame(i)) != i) roundTrips = false;
            Check("장 번호 → 시각 → 장 번호 가 그대로", roundTrips);

            Check("음수 장은 첫 장으로", info.FrameAt(TimeSpan.FromSeconds(-5)) == 0);
        }
        catch (Exception ex) { Check("영상 장 수 읽기", false, ex.Message); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        // 영상이 아닌 것에 물어봐도 죽지 않아야 한다.
        Check("PNG 에 물어보면 null", VideoInfo.TryRead("nope.png") == null);
        Check("없는 파일도 null", VideoInfo.TryRead(
            Path.Combine(Path.GetTempPath(), "snapview_없는파일.mp4")) == null);

        string junk = Path.Combine(Path.GetTempPath(), "snapview_junk.mp4");
        try
        {
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            Check("망가진 파일도 null", VideoInfo.TryRead(junk) == null);
        }
        finally { try { if (File.Exists(junk)) File.Delete(junk); } catch { } }
    }
}
