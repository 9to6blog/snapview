using System;
using System.IO;

namespace SnapView.Core
{
    /// <summary>
    /// 영상 파일에서 <b>장 수와 초당 장수</b>를 읽어 온다.
    ///
    /// 재생기(MediaPlayer)는 이걸 안 알려 준다. 시간만 알 수 있어서 "지금 몇 번째 장인지"
    /// 를 못 보여 주고, 한 장씩 옮기지도 못한다. 그래서 파일을 직접 들여다본다.
    ///
    /// MP4 계열(mp4·m4v·mov)만 읽는다. 이 앱이 만드는 것도 그것이고, 나머지 형식까지
    /// 손대면 컨테이너 파서를 하나 더 짜야 한다. 못 읽으면 그냥 시간만 보여 준다.
    ///
    /// 박스 구조는 이렇게 생겼다:
    ///   moov / trak / mdia / mdhd     — 시간 단위와 길이
    ///                  / minf / stbl / stsd — 이 트랙이 영상인지 소리인지
    ///                                / stts — 장이 몇 개이고 각각 얼마나 머무는지
    /// </summary>
    internal sealed class VideoInfo
    {
        /// <summary>영상 장 수.</summary>
        internal int FrameCount { get; }

        /// <summary>영상 길이(초).</summary>
        internal double Seconds { get; }

        /// <summary>초당 장수. 길이가 0 이면 0.</summary>
        internal double Fps => Seconds > 0 ? FrameCount / Seconds : 0;

        private VideoInfo(int frames, double seconds)
        {
            FrameCount = frames;
            Seconds = seconds;
        }

        /// <summary>읽어 낸다. 못 읽거나 영상 트랙이 없으면 null.</summary>
        internal static VideoInfo? TryRead(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".mp4" or ".m4v" or ".mov")) return null;

                using FileStream fs = File.OpenRead(path);
                Box? moov = Find(fs, 0, fs.Length, "moov");
                if (moov == null) return null;

                // 트랙이 여럿이다(영상 · 소리). 영상 트랙을 찾을 때까지 훑는다.
                foreach (Box trak in Children(fs, moov.Value, "trak"))
                {
                    Box? mdia = Find(fs, trak.Start, trak.End, "mdia");
                    if (mdia == null) continue;

                    Box? mdhd = Find(fs, mdia.Value.Start, mdia.Value.End, "mdhd");
                    Box? minf = Find(fs, mdia.Value.Start, mdia.Value.End, "minf");
                    if (mdhd == null || minf == null) continue;

                    Box? stbl = Find(fs, minf.Value.Start, minf.Value.End, "stbl");
                    if (stbl == null) continue;

                    // vmhd 가 있으면 영상 트랙이다(소리 트랙은 smhd 를 갖는다).
                    if (Find(fs, minf.Value.Start, minf.Value.End, "vmhd") == null) continue;

                    (uint scale, ulong duration) = ReadMdhd(fs, mdhd.Value);
                    if (scale == 0) continue;

                    Box? stts = Find(fs, stbl.Value.Start, stbl.Value.End, "stts");
                    if (stts == null) continue;

                    int frames = ReadSttsCount(fs, stts.Value);
                    if (frames <= 0) continue;

                    return new VideoInfo(frames, (double)duration / scale);
                }
            }
            catch { }

            return null;
        }

        /// <summary>이 시각이 몇 번째 장인가(0부터). 초당 장수를 모르면 -1.</summary>
        internal int FrameAt(TimeSpan position)
        {
            double fps = Fps;
            if (fps <= 0) return -1;

            int i = (int)Math.Floor(position.TotalSeconds * fps + 1e-6);
            return Math.Clamp(i, 0, Math.Max(0, FrameCount - 1));
        }

        /// <summary>몇 번째 장이 시작되는 시각. 초당 장수를 모르면 그대로 돌려준다.</summary>
        internal TimeSpan TimeOfFrame(int index)
        {
            double fps = Fps;
            if (fps <= 0) return TimeSpan.Zero;

            index = Math.Clamp(index, 0, Math.Max(0, FrameCount - 1));

            // 장 한가운데를 짚는다. 경계에 딱 맞추면 반올림 때문에 앞뒤 장이 나올 수 있다.
            return TimeSpan.FromSeconds((index + 0.5) / fps);
        }

        // ================= MP4 박스 읽기 =================

        private readonly record struct Box(long Start, long End);

        /// <summary>[from, to) 안에서 이름이 같은 박스 하나를 찾는다. 내용 구간을 돌려준다.</summary>
        private static Box? Find(Stream s, long from, long to, string name)
        {
            foreach (Box b in Boxes(s, from, to, name)) return b;
            return null;
        }

        private static System.Collections.Generic.IEnumerable<Box> Children(Stream s, Box parent,
                                                                           string name)
            => Boxes(s, parent.Start, parent.End, name);

        private static System.Collections.Generic.IEnumerable<Box> Boxes(Stream s, long from, long to,
                                                                        string name)
        {
            long at = from;

            while (at + 8 <= to)
            {
                s.Position = at;
                var header = new byte[8];
                if (s.Read(header, 0, 8) != 8) yield break;

                long size = Be32(header, 0);
                string kind = Ascii(header, 4);
                long content = at + 8;

                if (size == 1)
                {
                    // 64비트 크기는 헤더 뒤에 8바이트로 더 붙는다.
                    var big = new byte[8];
                    if (s.Read(big, 0, 8) != 8) yield break;
                    size = (long)(((ulong)Be32(big, 0) << 32) | Be32(big, 4));
                    content += 8;
                }
                else if (size == 0)
                {
                    size = to - at;      // 0 은 "끝까지" 라는 뜻
                }

                if (size < 8 || at + size > to) yield break;

                if (kind == name) yield return new Box(content, at + size);
                at += size;
            }
        }

        private static (uint Scale, ulong Duration) ReadMdhd(Stream s, Box box)
        {
            s.Position = box.Start;
            var buf = new byte[32];
            int n = s.Read(buf, 0, buf.Length);
            if (n < 24) return (0, 0);

            int version = buf[0];
            if (version == 1)
            {
                if (n < 32) return (0, 0);
                uint scale = Be32(buf, 20);                                   // 만든 때·고친 때 8바이트씩
                ulong dur = ((ulong)Be32(buf, 24) << 32) | Be32(buf, 28);
                return (scale, dur);
            }

            return (Be32(buf, 12), Be32(buf, 16));                            // 만든 때·고친 때 4바이트씩
        }

        /// <summary>stts 의 (개수, 길이) 쌍을 모두 더해 장 수를 구한다.</summary>
        private static int ReadSttsCount(Stream s, Box box)
        {
            s.Position = box.Start;
            var head = new byte[8];
            if (s.Read(head, 0, 8) != 8) return 0;

            long entries = Be32(head, 4);                                     // 버전·플래그 4바이트 뒤
            if (entries <= 0 || entries > 1_000_000) return 0;

            var table = new byte[entries * 8];
            if (s.Read(table, 0, table.Length) != table.Length) return 0;

            long total = 0;
            for (long i = 0; i < entries; i++)
            {
                total += Be32(table, (int)(i * 8));
                if (total > int.MaxValue) return int.MaxValue;
            }
            return (int)total;
        }

        private static uint Be32(byte[] d, int i)
            => ((uint)d[i] << 24) | ((uint)d[i + 1] << 16) | ((uint)d[i + 2] << 8) | d[i + 3];

        private static string Ascii(byte[] d, int i)
            => "" + (char)d[i] + (char)d[i + 1] + (char)d[i + 2] + (char)d[i + 3];
    }
}
