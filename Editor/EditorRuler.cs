using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SnapView.Editor
{
    /// <summary>Ticks use source-image pixels; origin and spacing follow scrolling and zoom.</summary>
    public sealed class EditorRuler : FrameworkElement
    {
        public bool Vertical { get; set; }
        private double _origin;
        private double _scale = 1;
        internal void SetMetrics(double origin, double scale)
        {
            if (Math.Abs(_origin - origin) < 0.01 && Math.Abs(_scale - scale) < 0.0001) return;
            _origin = origin; _scale = Math.Max(0.01, scale); InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double length = Vertical ? ActualHeight : ActualWidth;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(38, 39, 44)), null, new Rect(RenderSize));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(126, 133, 145)), 1);
            double rawStep = 70 / _scale;
            double power = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double major = (rawStep / power <= 2 ? 2 : rawStep / power <= 5 ? 5 : 10) * power;
            double minor = major / 5;
            int first = (int)Math.Floor(-_origin / (_scale * minor));
            int last = (int)Math.Ceiling((length - _origin) / (_scale * minor));
            dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            for (int i = first; i <= last; i++)
            {
                double pos = Math.Round(_origin + i * minor * _scale) + 0.5;
                bool big = i % 5 == 0;
                double start = big ? 14 : 19;
                dc.DrawLine(pen, Vertical ? new Point(start, pos) : new Point(pos, start),
                                 Vertical ? new Point(24, pos) : new Point(pos, 24));
                if (!big) continue;
                var label = new FormattedText((i * minor).ToString("0.##", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9,
                    pen.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                if (Vertical)
                {
                    dc.PushTransform(new TranslateTransform(2, pos - 2));
                    dc.PushTransform(new RotateTransform(-90));
                    dc.DrawText(label, new Point(0, 0)); dc.Pop(); dc.Pop();
                }
                else dc.DrawText(label, new Point(pos + 3, 1));
            }
            dc.Pop();
        }
    }

}
