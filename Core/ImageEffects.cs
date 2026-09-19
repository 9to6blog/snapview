using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>모자이크·흐림처럼 원본 픽셀을 읽어야 하는 효과들.</summary>
    internal static class ImageEffects
    {
        /// <summary>이미지의 한 영역을 블록 단위 평균색으로 뭉갠다(모자이크).</summary>
        internal static BitmapSource? Pixelate(BitmapSource source, Int32Rect region, int block)
        {
            if (!TryReadRegion(source, region, out byte[] px, out int w, out int h)) return null;
            block = Math.Clamp(block, 2, Math.Max(2, Math.Min(w, h)));

            for (int by = 0; by < h; by += block)
            {
                for (int bx = 0; bx < w; bx += block)
                {
                    int x2 = Math.Min(bx + block, w);
                    int y2 = Math.Min(by + block, h);
                    long b = 0, g = 0, r = 0, a = 0;
                    int n = 0;

                    for (int y = by; y < y2; y++)
                    {
                        int row = y * w * 4;
                        for (int x = bx; x < x2; x++)
                        {
                            int i = row + x * 4;
                            b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3];
                            n++;
                        }
                    }
                    if (n == 0) continue;

                    byte bb = (byte)(b / n), gg = (byte)(g / n), rr = (byte)(r / n), aa = (byte)(a / n);
                    for (int y = by; y < y2; y++)
                    {
                        int row = y * w * 4;
                        for (int x = bx; x < x2; x++)
                        {
                            int i = row + x * 4;
                            px[i] = bb; px[i + 1] = gg; px[i + 2] = rr; px[i + 3] = aa;
                        }
                    }
                }
            }
            return Build(px, w, h);
        }

        /// <summary>이미지의 한 영역을 흐리게 한다(박스 블러 3회 ≈ 가우시안).</summary>
        internal static BitmapSource? Blur(BitmapSource source, Int32Rect region, int radius)
        {
            if (!TryReadRegion(source, region, out byte[] px, out int w, out int h)) return null;
            radius = Math.Clamp(radius, 1, 64);

            var tmp = new byte[px.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurHorizontal(px, tmp, w, h, radius);
                BoxBlurVertical(tmp, px, w, h, radius);
            }
            return Build(px, w, h);
        }

        private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int h, int r)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int x1 = Math.Max(0, x - r), x2 = Math.Min(w - 1, x + r);
                    int n = x2 - x1 + 1;
                    int b = 0, g = 0, rr = 0, a = 0;
                    for (int k = x1; k <= x2; k++)
                    {
                        int i = row + k * 4;
                        b += src[i]; g += src[i + 1]; rr += src[i + 2]; a += src[i + 3];
                    }
                    int o = row + x * 4;
                    dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n);
                    dst[o + 2] = (byte)(rr / n); dst[o + 3] = (byte)(a / n);
                }
            }
        }

        private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int r)
        {
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int y1 = Math.Max(0, y - r), y2 = Math.Min(h - 1, y + r);
                    int n = y2 - y1 + 1;
                    int b = 0, g = 0, rr = 0, a = 0;
                    for (int k = y1; k <= y2; k++)
                    {
                        int i = (k * w + x) * 4;
                        b += src[i]; g += src[i + 1]; rr += src[i + 2]; a += src[i + 3];
                    }
                    int o = (y * w + x) * 4;
                    dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n);
                    dst[o + 2] = (byte)(rr / n); dst[o + 3] = (byte)(a / n);
                }
            }
        }

        // ============================================================ 이미지 전체 필터

        /// <summary>이미지 한 장에 통째로 거는 효과.</summary>
        internal enum ImageFilter
        {
            Grayscale, Sepia, Invert, Noise, Brighten, Darken, Contrast, Sharpen, Soften
        }

        /// <summary>사람이 읽는 이름. 메뉴에 그대로 쓴다.</summary>
        internal static string NameOf(ImageFilter f) => f switch
        {
            ImageFilter.Grayscale => "흑백",
            ImageFilter.Sepia => "세피아",
            ImageFilter.Invert => "색 반전",
            ImageFilter.Noise => "노이즈(거친 입자)",
            ImageFilter.Brighten => "밝게",
            ImageFilter.Darken => "어둡게",
            ImageFilter.Contrast => "대비 높이기",
            ImageFilter.Sharpen => "선명하게",
            ImageFilter.Soften => "부드럽게",
            _ => f.ToString()
        };

        /// <summary>
        /// 이미지 전체에 필터를 건다. 실패하면 null 을 돌려주고 원본을 그대로 두게 한다.
        /// <paramref name="strength"/> 는 0~1 이고 필터마다 뜻이 조금씩 다르다.
        /// </summary>
        internal static BitmapSource? Apply(BitmapSource source, ImageFilter filter, double strength = 0.5)
        {
            var whole = new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight);
            if (!TryReadRegion(source, whole, out byte[] px, out int w, out int h)) return null;

            strength = Math.Clamp(strength, 0, 1);

            // 효과는 언제나 최대 세기로 만든 다음, 원본과 섞어서 세기를 맞춘다.
            // 이렇게 해야 흑백·반전처럼 "세기" 개념이 없던 필터에도 강도가 생긴다.
            byte[] original = (byte[])px.Clone();

            switch (filter)
            {
                case ImageFilter.Sharpen: Convolve(px, w, h, SharpenKernel); break;
                case ImageFilter.Soften: Convolve(px, w, h, SoftenKernel); break;
                case ImageFilter.Noise: AddNoise(px, MaxNoise); break;
                default: PerPixel(px, filter); break;
            }

            if (strength < 0.999) Blend(px, original, strength);

            return Build(px, w, h);
        }

        /// <summary>결과를 원본 쪽으로 되돌린다. amount 1 이면 결과 그대로, 0 이면 원본.</summary>
        private static void Blend(byte[] result, byte[] original, double amount)
        {
            for (int i = 0; i < result.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int o = original[i + c];
                    result[i + c] = Clamp8((int)Math.Round(o + (result[i + c] - o) * amount));
                }
            }
        }

        private static readonly double[] SharpenKernel = { 0, -1, 0, -1, 5, -1, 0, -1, 0 };
        private static readonly double[] SoftenKernel =
        {
            1 / 9.0, 1 / 9.0, 1 / 9.0,
            1 / 9.0, 1 / 9.0, 1 / 9.0,
            1 / 9.0, 1 / 9.0, 1 / 9.0
        };

        private const int MaxNoise = 70;
        private const int MaxShift = 90;
        private const double MaxContrast = 2.2;

        private static void PerPixel(byte[] px, ImageFilter filter)
        {
            // 대비는 표준 공식. 128 을 축으로 벌린다.
            const double contrast = MaxContrast;
            const int shift = MaxShift;

            for (int i = 0; i < px.Length; i += 4)
            {
                int b = px[i], g = px[i + 1], r = px[i + 2];

                switch (filter)
                {
                    case ImageFilter.Grayscale:
                    {
                        // 사람 눈이 느끼는 밝기 비중(BT.601)
                        int y = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                        b = g = r = y;
                        break;
                    }
                    case ImageFilter.Sepia:
                    {
                        int y = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                        r = y + 40; g = y + 20; b = y - 20;
                        break;
                    }
                    case ImageFilter.Invert:
                        r = 255 - r; g = 255 - g; b = 255 - b;
                        break;

                    case ImageFilter.Brighten:
                        r += shift; g += shift; b += shift;
                        break;

                    case ImageFilter.Darken:
                        r -= shift; g -= shift; b -= shift;
                        break;

                    case ImageFilter.Contrast:
                        r = (int)((r - 128) * contrast + 128);
                        g = (int)((g - 128) * contrast + 128);
                        b = (int)((b - 128) * contrast + 128);
                        break;
                }

                px[i] = Clamp8(b); px[i + 1] = Clamp8(g); px[i + 2] = Clamp8(r);
            }
        }

        /// <summary>필름 입자처럼 보이도록 세 채널에 같은 값을 더한다(색이 아니라 밝기가 흔들린다).</summary>
        private static void AddNoise(byte[] px, int amount)
        {
            var rng = new Random(12345);   // 같은 그림에 같은 결과가 나오도록 씨앗을 고정한다
            for (int i = 0; i < px.Length; i += 4)
            {
                int d = rng.Next(-amount, amount + 1);
                px[i] = Clamp8(px[i] + d);
                px[i + 1] = Clamp8(px[i + 1] + d);
                px[i + 2] = Clamp8(px[i + 2] + d);
            }
        }

        /// <summary>3×3 커널을 씌운다. 가장자리는 있는 픽셀로 붙든다.</summary>
        private static void Convolve(byte[] px, int w, int h, double[] k)
        {
            byte[] src = (byte[])px.Clone();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double b = 0, g = 0, r = 0;

                    for (int ky = -1; ky <= 1; ky++)
                    {
                        int sy = Math.Clamp(y + ky, 0, h - 1);
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int sx = Math.Clamp(x + kx, 0, w - 1);
                            double f = k[(ky + 1) * 3 + (kx + 1)];
                            if (f == 0) continue;

                            int i = (sy * w + sx) * 4;
                            b += src[i] * f; g += src[i + 1] * f; r += src[i + 2] * f;
                        }
                    }

                    int o = (y * w + x) * 4;
                    px[o] = Clamp8((int)Math.Round(b));
                    px[o + 1] = Clamp8((int)Math.Round(g));
                    px[o + 2] = Clamp8((int)Math.Round(r));
                }
            }
        }

        private static byte Clamp8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        private static bool TryReadRegion(BitmapSource source, Int32Rect region,
                                          out byte[] pixels, out int w, out int h)
        {
            pixels = Array.Empty<byte>();
            w = h = 0;
            try
            {
                int x = Math.Clamp(region.X, 0, Math.Max(0, source.PixelWidth - 1));
                int y = Math.Clamp(region.Y, 0, Math.Max(0, source.PixelHeight - 1));
                int rw = Math.Clamp(region.Width, 1, source.PixelWidth - x);
                int rh = Math.Clamp(region.Height, 1, source.PixelHeight - y);

                BitmapSource src = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                var crop = new CroppedBitmap(src, new Int32Rect(x, y, rw, rh));
                int stride = rw * 4;
                pixels = new byte[checked(stride * rh)];
                crop.CopyPixels(pixels, stride, 0);
                w = rw; h = rh;
                return true;
            }
            catch { return false; }
        }

        private static BitmapSource Build(byte[] pixels, int w, int h)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bmp.Freeze();
            return bmp;
        }
    }
}
