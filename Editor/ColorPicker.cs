using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SnapView.Editor
{
    /// <summary>
    /// 팔레트에 없는 색을 직접 고르는 판.
    ///
    /// 왼쪽의 네모에서 <b>선명함(가로) · 밝기(세로)</b> 를 찍고, 오른쪽 띠에서 색상을 고른다.
    /// 정확한 값이 필요하면 아래 칸에 <c>#RRGGBB</c> 를 그대로 쳐 넣으면 된다.
    /// </summary>
    public sealed class ColorPicker : UserControl
    {
        private const double FieldSize = 150;
        private const double HueWidth = 20;

        private readonly Canvas _field = new() { Width = FieldSize, Height = FieldSize };
        private readonly Canvas _hue = new() { Width = HueWidth, Height = FieldSize };
        private readonly Rectangle _fieldBase = new() { Width = FieldSize, Height = FieldSize };
        private readonly Ellipse _fieldDot = new()
        {
            Width = 12, Height = 12, StrokeThickness = 2, Stroke = Brushes.White,
            IsHitTestVisible = false
        };
        private readonly Rectangle _hueMark = new()
        {
            Width = HueWidth, Height = 3, Fill = Brushes.White, IsHitTestVisible = false
        };
        private readonly TextBox _hex = new()
        {
            Width = 90, TextAlignment = TextAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(1)
        };
        private readonly Border _preview = new()
        {
            Width = 34, Height = 24, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1)
        };

        private double _h;          // 0~360
        private double _s = 1;      // 0~1
        private double _v = 1;      // 0~1
        private bool _updatingHex;

        /// <summary>고른 색이 바뀔 때마다. 확정이 아니라 미리보기 수준으로 계속 온다.</summary>
        internal event Action<Color>? ColorChanged;

        internal Color Color
        {
            get => FromHsv(_h, _s, _v);
            set
            {
                (_h, _s, _v) = ToHsv(value);
                Refresh(raise: false);
            }
        }

        public ColorPicker()
        {
            _hex.Foreground = Res("Fg", Colors.White);
            _hex.CaretBrush = _hex.Foreground;
            _hex.BorderBrush = Res("Divider", Color.FromRgb(0x3A, 0x3A, 0x42));
            _preview.BorderBrush = _hex.BorderBrush;

            BuildField();
            BuildHueStrip();

            _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
            _hex.LostFocus += (_, _) => CommitHex();

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_field);
            row.Children.Add(new Border { Width = 8 });
            row.Children.Add(_hue);

            var bottom = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };
            bottom.Children.Add(_preview);
            bottom.Children.Add(new Border { Width = 8 });
            bottom.Children.Add(_hex);

            var root = new StackPanel();
            root.Children.Add(row);
            root.Children.Add(bottom);
            Content = root;

            Refresh(raise: false);
        }

        // ---------------------------------------------------------------- 판 만들기

        private void BuildField()
        {
            // 바탕은 순색, 그 위에 흰색(왼→오른)과 검정(위→아래)을 겹쳐서
            // 왼쪽 위는 흰색, 오른쪽 위는 순색, 아래는 검정이 되게 한다.
            var toWhite = new Rectangle
            {
                Width = FieldSize, Height = FieldSize,
                Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255),
                                               new Point(0, 0), new Point(1, 0))
            };
            var toBlack = new Rectangle
            {
                Width = FieldSize, Height = FieldSize,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black,
                                               new Point(0, 0), new Point(0, 1))
            };

            _field.Children.Add(_fieldBase);
            _field.Children.Add(toWhite);
            _field.Children.Add(toBlack);
            _field.Children.Add(_fieldDot);

            _field.Cursor = Cursors.Cross;
            _field.MouseLeftButtonDown += (_, e) => { _field.CaptureMouse(); PickField(e); };
            _field.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) PickField(e);
            };
            _field.MouseLeftButtonUp += (_, _) => _field.ReleaseMouseCapture();
        }

        private void BuildHueStrip()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            for (int i = 0; i <= 6; i++)
                brush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));

            _hue.Children.Add(new Rectangle { Width = HueWidth, Height = FieldSize, Fill = brush });
            _hue.Children.Add(_hueMark);

            _hue.Cursor = Cursors.Cross;
            _hue.MouseLeftButtonDown += (_, e) => { _hue.CaptureMouse(); PickHue(e); };
            _hue.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) PickHue(e);
            };
            _hue.MouseLeftButtonUp += (_, _) => _hue.ReleaseMouseCapture();
        }

        // ---------------------------------------------------------------- 입력

        private void PickField(MouseEventArgs e)
        {
            Point p = e.GetPosition(_field);
            _s = Math.Clamp(p.X / FieldSize, 0, 1);
            _v = Math.Clamp(1 - p.Y / FieldSize, 0, 1);
            Refresh(raise: true);
        }

        private void PickHue(MouseEventArgs e)
        {
            Point p = e.GetPosition(_hue);
            _h = Math.Clamp(p.Y / FieldSize, 0, 1) * 360;
            Refresh(raise: true);
        }

        private void CommitHex()
        {
            if (_updatingHex) return;

            string text = _hex.Text.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;

            try
            {
                var c = (Color)ColorConverter.ConvertFromString(text);
                (_h, _s, _v) = ToHsv(c);
                Refresh(raise: true);
            }
            catch { Refresh(raise: false); }   // 못 읽으면 원래 값으로 되돌린다
        }

        private void Refresh(bool raise)
        {
            Color pure = FromHsv(_h, 1, 1);
            var baseBrush = new SolidColorBrush(pure);
            baseBrush.Freeze();
            _fieldBase.Fill = baseBrush;

            Color current = Color;
            var currentBrush = new SolidColorBrush(current);
            currentBrush.Freeze();
            _preview.Background = currentBrush;

            Canvas.SetLeft(_fieldDot, _s * FieldSize - 6);
            Canvas.SetTop(_fieldDot, (1 - _v) * FieldSize - 6);
            Canvas.SetTop(_hueMark, _h / 360 * FieldSize - 1.5);

            _updatingHex = true;
            _hex.Text = $"#{current.R:X2}{current.G:X2}{current.B:X2}";
            _updatingHex = false;

            if (raise) ColorChanged?.Invoke(current);
        }

        // ---------------------------------------------------------------- 색 변환

        internal static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;

            (double r, double g, double b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };

            return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
        }

        internal static (double H, double S, double V) ToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            double h;
            if (d < 1e-9) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);

            return (((h % 360) + 360) % 360, max <= 0 ? 0 : d / max, max);
        }

        private static byte Byte(double v)
            => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

        private static Brush Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }

        /// <summary>"#RRGGBB" 로 적는다.</summary>
        internal static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
