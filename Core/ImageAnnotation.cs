using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>
    /// 그림 위에 얹는 또 다른 그림. 붙여넣기가 이걸로 들어온다.
    ///
    /// 바로 합쳐 버리지 않고 주석으로 두는 이유는, 붙여넣고 나서 위치와 크기를
    /// 끌어서 맞출 수 있어야 하기 때문이다. 확정은 다른 주석과 똑같이 Enter.
    /// </summary>
    internal sealed class ImageAnnotation : Annotation
    {
        internal BitmapSource? Image { get; set; }
        internal Point Start { get; set; }
        internal Point End { get; set; }

        internal override Rect Bounds => new(
            Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

        /// <summary>왼쪽 위 한 점에 원본 크기로 놓는다.</summary>
        internal void PlaceAt(Point topLeft)
        {
            Start = topLeft;
            End = new Point(topLeft.X + (Image?.PixelWidth ?? 0),
                            topLeft.Y + (Image?.PixelHeight ?? 0));
        }

        internal override void Move(Vector d)
        {
            Start += d;
            End += d;
        }

        internal override void Flip(bool horizontal, double w, double h)
        {
            Start = MapFlip(Start, horizontal, w, h);
            End = MapFlip(End, horizontal, w, h);
        }

        protected override void MapPoints(Func<Point, Point> map)
        {
            Start = map(Start);
            End = map(End);
        }

        internal override IReadOnlyList<Point> Handles() => RectHandles(Bounds);

        internal override void DragHandle(int index, Point p)
            => (Start, End) = ResizeRect(Start, End, index, p);

        internal override bool CanRotate => true;

        protected override Annotation CloneCore() => new ImageAnnotation
        {
            Image = Image, Start = Start, End = End,
            Color = Color, Thickness = Thickness, Opacity = Opacity, Shadow = Shadow
        };

        /// <summary>복제해 검게 그려도 그림은 검어지지 않는다. 네모 그림자를 대신 깐다.</summary>
        internal override void RenderShadow(DrawingContext dc, BitmapSource source)
        {
            Rect b = Bounds;
            if (b.Width < 1 || b.Height < 1) return;

            double off = Math.Max(3, Math.Min(b.Width, b.Height) * 0.025);
            b.Offset(off, off);

            var brush = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, b, 4, 4);
        }

        internal override bool SupportsBlend => true;

        private BitmapSource? _blendCache;
        private string? _blendKey;
        private Rect _blendRect = Rect.Empty;

        internal override void Render(DrawingContext dc, BitmapSource source)
        {
            Rect b = Bounds;
            if (Image == null || b.Width < 1 || b.Height < 1) return;

            if (Blend == BlendMode.Normal)
            {
                dc.DrawImage(Image, b);
                return;
            }

            // 혼합: 얹은 그림을 바탕 픽셀과 직접 섞는다(질감·워터마크용).
            string key = $"{Start}|{End}|{Blend}|{Image.GetHashCode()}|{source.GetHashCode()}";
            if (_blendCache == null || _blendKey != key)
            {
                _blendCache = BlendComposite.Compose(source, b, Blend,
                    dcc => dcc.DrawImage(Image, b), out _blendRect);
                _blendKey = key;
            }
            if (_blendCache != null) dc.DrawImage(_blendCache, _blendRect);
        }
    }
}
