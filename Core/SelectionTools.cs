using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>
    /// 자동 선택(마술봉): 클릭한 자리와 비슷한 색으로 <b>이어진</b> 영역을 고른다.
    /// 고른 영역은 투명하게 지우거나(배경 제거) 색으로 채운다.
    /// </summary>
    internal static class SelectionTools
    {
        /// <summary>
        /// (x,y)에서 시작해 색이 <paramref name="tolerance"/> 안으로 비슷한 이웃을
        /// 물들여 간다. 채널 차의 최댓값으로 잰다(사람 눈에 단순하고 예측 가능).
        /// </summary>
        internal static bool[] FloodSelect(BitmapSource source, int x, int y,
                                           int tolerance, out int count)
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            count = 0;
            var mask = new bool[w * h];
            if (x < 0 || y < 0 || x >= w || y >= h) return mask;

            BitmapSource src = source.Format == PixelFormats.Bgra32
                ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var px = new byte[w * 4 * h];
            src.CopyPixels(px, w * 4, 0);

            int seed = (y * w + x) * 4;
            byte sb = px[seed], sg = px[seed + 1], sr = px[seed + 2], sa = px[seed + 3];

            bool Similar(int i)
            {
                int d = Math.Abs(px[i] - sb);
                d = Math.Max(d, Math.Abs(px[i + 1] - sg));
                d = Math.Max(d, Math.Abs(px[i + 2] - sr));
                d = Math.Max(d, Math.Abs(px[i + 3] - sa));
                return d <= tolerance;
            }

            var queue = new Queue<int>();
            queue.Enqueue(y * w + x);
            mask[y * w + x] = true;

            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                count++;
                int cx = p % w, cy = p / w;

                void Try(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                    int n = ny * w + nx;
                    if (mask[n] || !Similar(n * 4)) return;
                    mask[n] = true;
                    queue.Enqueue(n);
                }
                Try(cx - 1, cy); Try(cx + 1, cy); Try(cx, cy - 1); Try(cx, cy + 1);
            }
            return mask;
        }

        /// <summary>
        /// 고른 영역을 손본 새 그림. <paramref name="fill"/> 이 null 이면 투명하게 판다
        /// (PNG 로 저장하면 뚫린 채로 남는다 — 배경 제거). 색이면 그 색으로 칠한다.
        /// </summary>
        internal static BitmapSource ApplyMask(BitmapSource source, bool[] mask, Color? fill)
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            BitmapSource src = source.Format == PixelFormats.Bgra32
                ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var px = new byte[w * 4 * h];
            src.CopyPixels(px, w * 4, 0);

            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                int o = i * 4;
                if (fill == null)
                {
                    px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0;
                }
                else
                {
                    px[o] = fill.Value.B; px[o + 1] = fill.Value.G;
                    px[o + 2] = fill.Value.R; px[o + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>화면에 씌울 표시용 물들임(파란 반투명). 고른 데가 어디인지 보여 준다.</summary>
        internal static BitmapSource MaskOverlay(bool[] mask, int w, int h)
        {
            var px = new byte[w * 4 * h];
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                int o = i * 4;
                px[o] = 255; px[o + 1] = 160; px[o + 2] = 70; px[o + 3] = 95;   // 파랑, 약하게
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Shift+클릭으로 넓힐 때: 두 마스크를 합친다.</summary>
        internal static int Union(bool[] into, bool[] add)
        {
            int count = 0;
            for (int i = 0; i < into.Length && i < add.Length; i++)
            {
                into[i] |= add[i];
                if (into[i]) count++;
            }
            return count;
        }
    }
}
