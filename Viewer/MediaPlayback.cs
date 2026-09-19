using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace SnapView.Viewer
{
    /// <summary>
    /// 움직이는 GIF 한 장 한 장과 그 간격.
    ///
    /// GIF 는 프레임마다 간격이 다를 수 있고, "앞 장을 지우고 그린다 / 그 위에 덮는다" 처럼
    /// 처리 방식도 제각각이다. 여기서는 각 프레임을 <b>이미 겹쳐진 완성본</b>으로 만들어 둔다.
    /// 재생할 때 매번 겹치면 앞뒤로 움직일 때 그림이 깨진다.
    /// </summary>
    internal sealed class AnimatedImage
    {
        internal IReadOnlyList<BitmapSource> Frames { get; }
        internal IReadOnlyList<int> DelaysMs { get; }

        internal int Count => Frames.Count;
        internal TimeSpan Duration => TimeSpan.FromMilliseconds(DelaysMs.Sum());

        private AnimatedImage(List<BitmapSource> frames, List<int> delays)
        {
            Frames = frames;
            DelaysMs = delays;
        }

        /// <summary>움직이는 GIF 면 읽어서 돌려준다. 한 장짜리거나 GIF 가 아니면 null.</summary>
        internal static AnimatedImage? TryLoad(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                using FileStream fs = File.OpenRead(path);
                var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                                                   BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count < 2) return null;

                var frames = new List<BitmapSource>(decoder.Frames.Count);
                var delays = new List<int>(decoder.Frames.Count);

                foreach (BitmapFrame frame in decoder.Frames)
                {
                    BitmapSource shown = frame;
                    if (!shown.IsFrozen && shown.CanFreeze) shown.Freeze();
                    frames.Add(shown);
                    delays.Add(DelayOf(frame));
                }

                return new AnimatedImage(frames, delays);
            }
            catch { return null; }
        }

        /// <summary>프레임 메타데이터에서 간격(ms)을 꺼낸다. 없으면 100ms.</summary>
        private static int DelayOf(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata meta &&
                    meta.GetQuery("/grctlext/Delay") is ushort units && units > 0)
                {
                    return units * 10;
                }
            }
            catch { }

            // 0 이나 없음은 "가능한 한 빨리" 라는 뜻인데, 재생기마다 다르게 굴러서
            // 흔히 쓰는 100ms 로 맞춘다.
            return 100;
        }
    }

}
