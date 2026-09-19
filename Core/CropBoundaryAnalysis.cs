using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    internal enum BorderTone
    {
        None,
        Black,
        White,
        Transparent
    }

    internal readonly record struct AutoCropResult(Int32Rect Region, string Description);

    internal readonly record struct CropSnapResult(double? X, double? Y)
    {
        internal Point Apply(Point p, bool snapX, bool snapY)
            => new(snapX && X.HasValue ? X.Value : p.X,
                   snapY && Y.HasValue ? Y.Value : p.Y);
    }

    /// <summary>
    /// 흰색·검은색·투명 레터박스를 찾고, 자르기 손잡이가 붙을 만한 긴 이미지 경계를 준비한다.
    /// 한 번 픽셀을 읽어 두 결과를 함께 만들어 편집 중 반복 분석을 피한다.
    /// </summary>
    internal sealed class CropBoundaryAnalysis
    {
        private const int ColorTolerance = 22;
        private const double BorderCoverage = 0.965;
        private const int MinBorderPixels = 2;
        private const int MaxLineSamples = 2048;
        private const int MaxEdgeSamples = 640;

        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;
        private readonly byte[] _pixels;
        private readonly List<SnapLine> _vertical = new();
        private readonly List<SnapLine> _horizontal = new();

        internal AutoCropResult? AutoCrop { get; }
        /// <summary>
        /// 캡처 전에 누르는 "자석 맞춤" 결과. 흰색·검은색·투명 여백뿐 아니라
        /// 사용자가 대충 둘러 잡은 단색 바깥 배경과 긴 사각 경계도 찾는다.
        /// </summary>
        internal AutoCropResult? MagneticCrop { get; }

        private readonly record struct SnapLine(int Position, double Score);
        private readonly record struct SideTrim(int Pixels, BorderTone Tone);

        private CropBoundaryAnalysis(BitmapSource source)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            _width = bgra.PixelWidth;
            _height = bgra.PixelHeight;
            _stride = _width * 4;
            _pixels = new byte[_stride * _height];
            bgra.CopyPixels(_pixels, _stride, 0);

            AutoCrop = FindAutoCrop();
            BuildSnapLines();
            // 캡처 화면에서는 색 여백보다 "미디어 카드의 사각 테두리"가 더 정확하다.
            // SNS 화면의 흰 바깥을 먼저 자르면 프로필 머리글·하트/댓글 줄이 남을 수 있으므로,
            // 위·아래 UI 사이를 길게 잇는 카드 경계를 최우선으로 선택한다.
            MagneticCrop = FindEdgeRectangle() ?? AutoCrop ?? FindFlatBackgroundCrop();
            AddAutoCropSnapLines();

            // 이후 Snap 은 선 목록만 쓴다. 전체 화면 분석에서 수십 MB가 될 수 있는 원본
            // 픽셀 복사본을 오버레이가 닫힐 때까지 붙들고 있을 이유가 없다.
            _pixels = Array.Empty<byte>();
        }

        internal static CropBoundaryAnalysis Analyze(BitmapSource source)
        {
            if (source.PixelWidth < 1 || source.PixelHeight < 1)
                throw new ArgumentException("크기가 없는 이미지는 분석할 수 없습니다.", nameof(source));
            return new CropBoundaryAnalysis(source);
        }

        internal CropSnapResult Snap(Point p, double tolerance)
        {
            if (tolerance <= 0) return default;
            double? x = Nearest(_vertical, p.X, tolerance);
            double? y = Nearest(_horizontal, p.Y, tolerance);
            return new CropSnapResult(x, y);
        }

        private AutoCropResult? FindAutoCrop()
        {
            if (_width < 8 || _height < 8) return null;

            SideTrim top = ScanSide(Side.Top);
            SideTrim bottom = ScanSide(Side.Bottom);
            SideTrim left = ScanSide(Side.Left);
            SideTrim right = ScanSide(Side.Right);

            int x = left.Pixels;
            int y = top.Pixels;
            int w = _width - left.Pixels - right.Pixels;
            int h = _height - top.Pixels - bottom.Pixels;

            if (x == 0 && y == 0 && w == _width && h == _height) return null;

            // 잘못 판정해 거의 전부를 날리는 후보는 내놓지 않는다. 애매한 이미지는 수동 자르기가 낫다.
            if (w < 8 || h < 8 || w < _width * 0.08 || h < _height * 0.08) return null;

            var tones = new[] { top, bottom, left, right }
                .Where(s => s.Pixels > 0)
                .Select(s => s.Tone)
                .Distinct()
                .ToArray();

            string tone = tones.Length == 1 ? ToneName(tones[0]) : "단색";
            string sides = SideDescription(top.Pixels, bottom.Pixels, left.Pixels, right.Pixels);
            return new AutoCropResult(new Int32Rect(x, y, w, h), $"{tone} 여백 {sides}");
        }

        private enum Side { Top, Bottom, Left, Right }

        private readonly record struct PixelColor(byte B, byte G, byte R, byte A);

        private SideTrim ScanSide(Side side)
        {
            int lineCount = side is Side.Top or Side.Bottom ? _height : _width;
            int lineLength = side is Side.Top or Side.Bottom ? _width : _height;
            if (lineCount < MinBorderPixels + 2) return default;

            BorderTone tone = BestTone(side, 0, lineLength);
            if (tone == BorderTone.None) return default;

            int lastGood = -1;
            int misses = 0;
            int maxScan = Math.Max(1, lineCount - 4);
            for (int distance = 0; distance < maxScan; distance++)
            {
                double coverage = Coverage(side, distance, lineLength, tone);
                if (coverage >= BorderCoverage)
                {
                    lastGood = distance;
                    misses = 0;
                }
                else if (++misses >= 2)
                {
                    break;
                }
            }

            int trim = lastGood + 1;
            return trim >= MinBorderPixels ? new SideTrim(trim, tone) : default;
        }

        private BorderTone BestTone(Side side, int distance, int lineLength)
        {
            BorderTone best = BorderTone.None;
            double bestCoverage = 0;
            foreach (BorderTone tone in new[] { BorderTone.Black, BorderTone.White, BorderTone.Transparent })
            {
                double coverage = Coverage(side, distance, lineLength, tone);
                if (coverage > bestCoverage)
                {
                    bestCoverage = coverage;
                    best = tone;
                }
            }
            return bestCoverage >= BorderCoverage ? best : BorderTone.None;
        }

        private double Coverage(Side side, int distance, int lineLength, BorderTone tone)
        {
            int step = Math.Max(1, lineLength / MaxLineSamples);
            int matches = 0, samples = 0;
            for (int along = 0; along < lineLength; along += step)
            {
                int x, y;
                switch (side)
                {
                    case Side.Top: x = along; y = distance; break;
                    case Side.Bottom: x = along; y = _height - 1 - distance; break;
                    case Side.Left: x = distance; y = along; break;
                    default: x = _width - 1 - distance; y = along; break;
                }

                if (Matches(x, y, tone)) matches++;
                samples++;
            }
            return samples == 0 ? 0 : (double)matches / samples;
        }

        private bool Matches(int x, int y, BorderTone tone)
        {
            int i = y * _stride + x * 4;
            byte b = _pixels[i], g = _pixels[i + 1], r = _pixels[i + 2], a = _pixels[i + 3];
            return tone switch
            {
                BorderTone.Transparent => a <= 12,
                BorderTone.Black => a > 12 && Math.Max(r, Math.Max(g, b)) <= ColorTolerance,
                BorderTone.White => a > 12 && Math.Min(r, Math.Min(g, b)) >= 255 - ColorTolerance,
                _ => false
            };
        }

        /// <summary>
        /// 선택 영역 네 귀퉁이 가운데 셋 이상이 같은 색이면 그 색을 "선택 바깥"으로 보고
        /// 사방에서 안쪽으로 훑는다. 검정/흰색에 한정하지 않아 웹 카드나 영상 프레임 주변의
        /// 회색·남색 배경도 한 번의 자석 버튼으로 걷어 낼 수 있다.
        /// </summary>
        private AutoCropResult? FindFlatBackgroundCrop()
        {
            if (_width < 8 || _height < 8) return null;

            PixelColor[] corners =
            {
                ColorAt(0, 0), ColorAt(_width - 1, 0),
                ColorAt(0, _height - 1), ColorAt(_width - 1, _height - 1)
            };

            PixelColor? background = null;
            foreach (PixelColor candidate in corners)
            {
                int alike = corners.Count(c => Similar(c, candidate, 18));
                if (alike >= 3) { background = candidate; break; }
            }
            if (!background.HasValue) return null;

            int top = ScanSolidSide(Side.Top, background.Value);
            int bottom = ScanSolidSide(Side.Bottom, background.Value);
            int left = ScanSolidSide(Side.Left, background.Value);
            int right = ScanSolidSide(Side.Right, background.Value);

            if (top == 0 && bottom == 0 && left == 0 && right == 0) return null;

            int w = _width - left - right;
            int h = _height - top - bottom;
            if (w < 8 || h < 8 || w < _width * 0.08 || h < _height * 0.08) return null;

            return new AutoCropResult(new Int32Rect(left, top, w, h),
                "선택 바깥 배경 " + SideDescription(top, bottom, left, right));
        }

        private int ScanSolidSide(Side side, PixelColor background)
        {
            int lineCount = side is Side.Top or Side.Bottom ? _height : _width;
            int lineLength = side is Side.Top or Side.Bottom ? _width : _height;
            int lastGood = -1;
            int misses = 0;
            int step = Math.Max(1, lineLength / MaxLineSamples);

            for (int distance = 0; distance < Math.Max(1, lineCount - 4); distance++)
            {
                int matches = 0, samples = 0;
                for (int along = 0; along < lineLength; along += step)
                {
                    int x, y;
                    switch (side)
                    {
                        case Side.Top: x = along; y = distance; break;
                        case Side.Bottom: x = along; y = _height - 1 - distance; break;
                        case Side.Left: x = distance; y = along; break;
                        default: x = _width - 1 - distance; y = along; break;
                    }
                    if (Similar(ColorAt(x, y), background, 22)) matches++;
                    samples++;
                }

                if (samples > 0 && (double)matches / samples >= BorderCoverage)
                {
                    lastGood = distance;
                    misses = 0;
                }
                else if (++misses >= 2) break;
            }

            int trim = lastGood + 1;
            return trim >= MinBorderPixels ? trim : 0;
        }

        private PixelColor ColorAt(int x, int y)
        {
            int i = y * _stride + x * 4;
            return new PixelColor(_pixels[i], _pixels[i + 1], _pixels[i + 2], _pixels[i + 3]);
        }

        private static bool Similar(PixelColor a, PixelColor b, int tolerance)
            => Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.G - b.G) <= tolerance &&
               Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.A - b.A) <= tolerance;

        /// <summary>
        /// 단색 바깥이 아니어도 선택 가장자리 쪽에 사각형을 이루는 긴 경계 네 개가 있으면
        /// 그 안쪽을 콘텐츠로 본다. 사진 속 짧은 선보다 화면 카드·창 테두리를 우선한다.
        /// </summary>
        private AutoCropResult? FindEdgeRectangle()
        {
            int? left = PickOuterLine(_vertical, _width, fromStart: true);
            int? right = PickOuterLine(_vertical, _width, fromStart: false);
            int? top = PickOuterLine(_horizontal, _height, fromStart: true);
            int? bottom = PickOuterLine(_horizontal, _height, fromStart: false);

            // 사용자가 대충 잡은 선택이 이미 카드 한쪽 끝에서 시작하는 경우에는 그 변의
            // 색 변화가 이미지 바깥에 있어 검출할 수 없다. 나머지 세 변이 충분히 길고
            // 강하면, 없는 한 변만 현재 선택 경계로 보완한다. SNS 미디어가 화면 맨 위에
            // 붙어 있고 아래에 좋아요/본문 UI만 남은 캡처가 대표적인 경우다.
            int detected = (left.HasValue ? 1 : 0) + (right.HasValue ? 1 : 0) +
                           (top.HasValue ? 1 : 0) + (bottom.HasValue ? 1 : 0);
            if (detected < 3) return null;

            int l = left ?? 0;
            int r = right ?? _width;
            int t = top ?? 0;
            int b = bottom ?? _height;
            if (r - l < 8 || b - t < 8) return null;

            return new AutoCropResult(
                new Int32Rect(l, t, r - l, b - t),
                detected == 4 ? "긴 콘텐츠 경계 자동 맞춤" : "선택 끝에 닿은 콘텐츠 자동 맞춤");
        }

        private static int? PickOuterLine(List<SnapLine> lines, int length, bool fromStart)
        {
            double maxInset = length * 0.42;
            SnapLine? best = null;
            double bestRank = double.MinValue;
            foreach (SnapLine line in lines)
            {
                double inset = fromStart ? line.Position : length - line.Position;
                // 1px 카드 테두리는 실제 웹 캡처에서 흔하다. 0은 선택 경계 자체이므로
                // 후보에서 빼고, 1px 경계부터는 유효한 콘텐츠 선으로 인정한다.
                if (inset < 1 || inset > maxInset) continue;

                // 강한 경계를 우선하되, 비슷한 경계라면 선택 바깥쪽에 가까운 것을 택한다.
                double rank = line.Score - inset / Math.Max(1, maxInset) * 65;
                if (rank > bestRank) { best = line; bestRank = rank; }
            }
            return best?.Position;
        }

        private void BuildSnapLines()
        {
            if (_width < 3 || _height < 3) return;

            double[] verticalScores = new double[_width];
            double[] horizontalScores = new double[_height];

            int yStep = Math.Max(1, _height / MaxEdgeSamples);
            for (int x = 1; x < _width; x++)
                verticalScores[x] = EdgeScoreVertical(x, yStep);

            int xStep = Math.Max(1, _width / MaxEdgeSamples);
            for (int y = 1; y < _height; y++)
                horizontalScores[y] = EdgeScoreHorizontal(y, xStep);

            AddLocalPeaks(verticalScores, _vertical);
            AddLocalPeaks(horizontalScores, _horizontal);

        }

        private void AddAutoCropSnapLines()
        {
            // 편집기에서 손잡이를 끌 때는 레터박스 경계도 반드시 자석 후보가 되어야 한다.
            // 캡처의 사각 카드 탐색이 끝난 뒤에 넣는다. 먼저 넣으면 흰 UI 여백의 경계가
            // 점수 1000으로 카드 테두리를 이겨 SNS 머리글·하단 버튼 줄이 남는다.
            if (AutoCrop is { } auto)
            {
                AddOrRaise(_vertical, auto.Region.X, 1000);
                AddOrRaise(_vertical, auto.Region.X + auto.Region.Width, 1000);
                AddOrRaise(_horizontal, auto.Region.Y, 1000);
                AddOrRaise(_horizontal, auto.Region.Y + auto.Region.Height, 1000);
            }
        }

        private double EdgeScoreVertical(int x, int step)
        {
            int samples = 0, strong = 0, veryStrong = 0;
            long total = 0;
            for (int y = 0; y < _height; y += step)
            {
                int d = PixelDifference(x - 1, y, x, y);
                total += d;
                if (d >= 28) strong++;
                if (d >= 70) veryStrong++;
                samples++;
            }
            return Score(samples, strong, veryStrong, total);
        }

        private double EdgeScoreHorizontal(int y, int step)
        {
            int samples = 0, strong = 0, veryStrong = 0;
            long total = 0;
            for (int x = 0; x < _width; x += step)
            {
                int d = PixelDifference(x, y - 1, x, y);
                total += d;
                if (d >= 28) strong++;
                if (d >= 70) veryStrong++;
                samples++;
            }
            return Score(samples, strong, veryStrong, total);
        }

        private static double Score(int samples, int strong, int veryStrong, long total)
        {
            if (samples == 0) return 0;
            double strongCoverage = (double)strong / samples;
            double veryCoverage = (double)veryStrong / samples;
            if (strongCoverage < 0.60 && veryCoverage < 0.35) return 0;
            return total / (double)samples + strongCoverage * 100 + veryCoverage * 80;
        }

        private int PixelDifference(int x1, int y1, int x2, int y2)
        {
            int a = y1 * _stride + x1 * 4;
            int b = y2 * _stride + x2 * 4;
            int db = Math.Abs(_pixels[a] - _pixels[b]);
            int dg = Math.Abs(_pixels[a + 1] - _pixels[b + 1]);
            int dr = Math.Abs(_pixels[a + 2] - _pixels[b + 2]);
            int da = Math.Abs(_pixels[a + 3] - _pixels[b + 3]);
            return Math.Max(Math.Max(db, dg), Math.Max(dr, da));
        }

        private static void AddLocalPeaks(double[] scores, List<SnapLine> lines)
        {
            // 마지막 픽셀에서 시작하는 경계도 검사한다. 카드가 선택 오른쪽/아래쪽 끝에서
            // 1px 안쪽에 있을 때 scores.Length - 1이 실제 바깥 경계가 된다.
            for (int i = 1; i < scores.Length; i++)
            {
                double score = scores[i];
                if (score <= 0) continue;
                bool peak = true;
                for (int d = 1; d <= 2; d++)
                {
                    if (i - d >= 0 && scores[i - d] > score) peak = false;
                    if (i + d < scores.Length && scores[i + d] > score) peak = false;
                }
                if (peak) lines.Add(new SnapLine(i, score));
            }
        }

        private static void AddOrRaise(List<SnapLine> lines, int position, double score)
        {
            if (position <= 0) return;
            int at = lines.FindIndex(x => x.Position == position);
            if (at >= 0)
            {
                if (lines[at].Score < score) lines[at] = new SnapLine(position, score);
            }
            else lines.Add(new SnapLine(position, score));
        }

        private static double? Nearest(List<SnapLine> lines, double value, double tolerance)
        {
            SnapLine? best = null;
            double bestDistance = double.MaxValue;
            foreach (SnapLine line in lines)
            {
                double distance = Math.Abs(line.Position - value);
                if (distance > tolerance) continue;
                if (distance < bestDistance - 0.001 ||
                    (Math.Abs(distance - bestDistance) <= 0.001 && (!best.HasValue || line.Score > best.Value.Score)))
                {
                    best = line;
                    bestDistance = distance;
                }
            }
            return best?.Position;
        }

        private static string ToneName(BorderTone tone) => tone switch
        {
            BorderTone.Black => "검은색",
            BorderTone.White => "흰색",
            BorderTone.Transparent => "투명",
            _ => "단색"
        };

        private static string SideDescription(int top, int bottom, int left, int right)
        {
            var sides = new List<string>();
            if (top > 0) sides.Add($"위 {top}px");
            if (bottom > 0) sides.Add($"아래 {bottom}px");
            if (left > 0) sides.Add($"왼쪽 {left}px");
            if (right > 0) sides.Add($"오른쪽 {right}px");
            return string.Join(" · ", sides);
        }
    }
}
