using System;
using System.Threading;
using SnapView.Native;

namespace SnapView.Capture
{
    /// <summary>
    /// 가벼운 영상 편집 — 구간 잘라내기(컷)와 배속(슬로우/빨리감기).
    ///
    /// 원본은 절대 건드리지 않고 <b>항상 새 파일로 내보낸다</b>. 방식은 다시 인코딩:
    /// <see cref="VideoReader"/> 로 NV12 를 되읽어 시각만 고쳐 <see cref="Mp4Writer"/> 에
    /// 도로 넣는다. 압축을 안 풀고 이어 붙이는 방식(무손실 컷)은 키프레임 자리에서만
    /// 자를 수 있어 "여기서부터 여기까지" 가 안 맞는다 — 화면 녹화물처럼 짧은 영상에는
    /// 정확한 가위질이 더 중요하다.
    ///
    /// 소리는 <b>배속이 1×일 때만</b> 담는다. 느리게/빠르게 한 소리는 음정이 틀어져서
    /// 쓸 수 없고, 음정을 지키는 시간 늘이기는 이 앱의 몫이 아니다.
    /// </summary>
    internal static class VideoEditor
    {
        /// <summary>
        /// <paramref name="src"/> 의 <paramref name="start"/>~<paramref name="end"/> 구간을
        /// <paramref name="speed"/> 배속으로 <paramref name="dst"/> 에 내보낸다.
        /// speed 0.5 = 절반 속도(길이 두 배), 2.0 = 두 배 속도(길이 절반).
        /// </summary>
        /// <param name="progress">0~1. 진행 표시용(없으면 null).</param>
        /// <returns>내보낸 장 수.</returns>
        internal static int Export(string src, string dst, TimeSpan start, TimeSpan end,
                                   double speed, Action<double>? progress = null,
                                   CancellationToken cancel = default)
        {
            if (end <= start) throw new ArgumentException("구간이 비어 있습니다");
            speed = Math.Clamp(speed, 0.1, 8.0);

            using var reader = new VideoReader(src);

            // H.264 는 짝수 크기만 받는다. 홀수면 인코더 쪽에서 한 줄을 자르므로
            // 되읽은 NV12 도 거기에 맞춰 줄여 넣는다.
            int w = Math.Max(2, reader.Width - (reader.Width % 2));
            int h = Math.Max(2, reader.Height - (reader.Height % 2));

            // 원본의 초당 장수를 알면 그대로 쓴다. 헤더용 값이라 정확하지 않아도 되지만
            // (실제 시간은 장마다 적는다) 마지막 장의 길이가 이 값을 따른다.
            int fps = 30;
            Core.VideoInfo? info = Core.VideoInfo.TryRead(src);
            if (info != null && info.Fps > 0) fps = (int)Math.Clamp(Math.Round(info.Fps), 1, Mp4Writer.MaxFps);

            bool keepAudio = Math.Abs(speed - 1.0) < 0.001 && reader.HasAudio &&
                             (reader.AudioRate == 44100 || reader.AudioRate == 48000);

            double span = (end - start).TotalSeconds;
            int frames = 0;

            using var writer = new Mp4Writer(dst, w, h, fps, 0,
                keepAudio ? (Math.Clamp(reader.AudioChannels, 1, 2), reader.AudioRate, 16) : null);

            bool audioStarted = false;

            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                VideoReader.Sample? next = reader.Read();
                if (next == null) break;
                VideoReader.Sample s = next.Value;

                if (s.IsVideo)
                {
                    if (s.Time < start) continue;
                    if (s.Time > end)
                    {
                        // 소리도 구간이 끝났으면 더 읽을 이유가 없다.
                        if (!keepAudio) break;
                        continue;
                    }

                    var when = TimeSpan.FromTicks((long)((s.Time - start).Ticks / speed));
                    writer.AddNv12(CropEven(s.Data, reader.Width, reader.Height, w, h), when);
                    frames++;

                    progress?.Invoke(Math.Clamp((s.Time - start).TotalSeconds / span, 0, 1));
                }
                else if (keepAudio && writer.HasAudio)
                {
                    if (s.Time < start || s.Time > end) continue;

                    // 소리가 구간 시작보다 늦게 시작하면 그만큼 무음으로 메워 박자를 맞춘다.
                    if (!audioStarted)
                    {
                        writer.PadAudioTo(s.Time - start, TimeSpan.Zero);
                        audioStarted = true;
                    }

                    var chunk = new byte[s.Length];
                    Array.Copy(s.Data, chunk, s.Length);
                    writer.AddAudio(chunk, s.Length);
                }
            }

            if (frames == 0) throw new InvalidOperationException("이 구간에는 장이 하나도 없습니다");
            progress?.Invoke(1);
            return frames;
        }

        /// <summary>홀수 크기 영상의 NV12 에서 마지막 한 줄/칸을 걷어 내 짝수 크기로 만든다.</summary>
        private static byte[] CropEven(byte[] nv12, int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW == dstW && srcH == dstH) return nv12;

            var outBuf = new byte[dstW * dstH * 3 / 2];
            for (int y = 0; y < dstH; y++)
                Array.Copy(nv12, y * srcW, outBuf, y * dstW, dstW);

            int srcUv = srcW * srcH, dstUv = dstW * dstH;
            for (int y = 0; y < dstH / 2; y++)
                Array.Copy(nv12, srcUv + y * srcW, outBuf, dstUv + y * dstW, dstW);

            return outBuf;
        }
    }
}
