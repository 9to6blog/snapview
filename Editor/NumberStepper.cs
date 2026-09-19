using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SnapView.Editor
{
    /// <summary>
    /// 숫자 하나를 고르는 칸. <c>[−] [ 12 ] [+]</c> 꼴이다.
    ///
    /// 슬라이더를 대신한다. 좁은 슬라이더는 원하는 값을 짚기가 어렵다 —
    /// 1 을 고르려다 3 이 되고, 100% 를 되돌리려다 95% 가 된다.
    /// 여기서는 버튼으로 한 칸씩 확실히 움직이고, 숫자를 직접 쳐 넣을 수도 있고,
    /// 칸 위에서 휠을 굴려도 된다. 버튼은 누르고 있으면 계속 올라간다.
    /// </summary>
    public sealed class NumberStepper : UserControl
    {
        private readonly TextBox _box;
        private readonly TextBlock _suffixLabel;

        private double _value = 1;

        internal double Minimum { get; set; } = 1;
        internal double Maximum { get; set; } = 100;
        internal double Step { get; set; } = 1;

        /// <summary>숫자 뒤에 붙는 글자. 예: "%".</summary>
        internal string Suffix
        {
            get => _suffixLabel.Text;
            set
            {
                _suffixLabel.Text = value ?? "";
                _suffixLabel.Visibility = string.IsNullOrEmpty(value)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        internal double Value
        {
            get => _value;
            set => SetValue(value, raise: true);
        }

        internal event Action<double>? ValueChanged;

        public NumberStepper()
        {
            Focusable = false;

            _box = new TextBox
            {
                MinWidth = 34,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(2, 1, 0, 1)
            };
            _box.LostFocus += (_, _) => CommitText();
            _box.KeyDown += OnBoxKey;

            _suffixLabel = new TextBlock
            {
                Foreground = Brush("FgDim", Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
                Visibility = Visibility.Collapsed
            };

            RepeatButton minus = MakeButton("IconMinus", -1);
            RepeatButton plus = MakeButton("IconPlus", +1);
            DockPanel.SetDock(minus, Dock.Left);
            DockPanel.SetDock(plus, Dock.Right);

            var panel = new DockPanel { LastChildFill = true };
            panel.Children.Add(minus);
            panel.Children.Add(plus);

            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(_box);
            inner.Children.Add(_suffixLabel);
            panel.Children.Add(inner);

            Content = new Border
            {
                Background = Brush("Bg", Color.FromRgb(0x1B, 0x1B, 0x1F)),
                BorderBrush = Brush("Divider", Color.FromRgb(0x3A, 0x3A, 0x42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel
            };

            // 칸 위에서 휠을 굴리면 값이 움직인다. 슬라이더보다 훨씬 빠르다.
            PreviewMouseWheel += (s, e) =>
            {
                Nudge(e.Delta > 0 ? +1 : -1);
                e.Handled = true;
            };

            UpdateText();
        }

        /// <summary>
        /// 더하기·빼기 단추. 글자 대신 <b>직접 그린 아이콘</b>을 넣는다.
        /// "＋" 같은 특수문자는 글꼴마다 굵기·크기가 달라서 나란히 놓으면 어긋나 보인다.
        /// </summary>
        private RepeatButton MakeButton(string iconKey, int direction)
        {
            var icon = new System.Windows.Shapes.Path
            {
                Data = (Geometry)Application.Current.FindResource(iconKey),
                Style = (Style)Application.Current.FindResource("IconDim"),
                Width = 9,
                Height = 9,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            };

            var b = new RepeatButton
            {
                Content = icon,
                Width = 22,
                Delay = 350,
                Interval = 60,
                Style = (Style)Application.Current.FindResource("FlatRepeat")
            };
            b.Click += (_, _) => Nudge(direction);
            b.MouseEnter += (_, _) => icon.Stroke = Brush("Fg", Colors.White);
            b.MouseLeave += (_, _) => icon.Stroke = Brush("FgDim", Colors.Gray);
            return b;
        }

        private void Nudge(int direction) => Value = _value + direction * Step;

        private void OnBoxKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter: CommitText(); e.Handled = true; break;
                case Key.Up: Nudge(+1); e.Handled = true; break;
                case Key.Down: Nudge(-1); e.Handled = true; break;
                case Key.Escape: UpdateText(); e.Handled = true; break;
            }
        }

        private void CommitText()
        {
            if (double.TryParse(_box.Text.Replace("%", "").Trim(),
                                NumberStyles.Any, CultureInfo.CurrentCulture, out double v))
                Value = v;
            else
                UpdateText();   // 못 읽으면 원래 값으로 되돌린다
        }

        /// <summary>값을 넣되 알림은 내지 않는다. 창을 처음 채울 때 쓴다.</summary>
        internal void SetSilently(double value) => SetValue(value, raise: false);

        private void SetValue(double value, bool raise)
        {
            double clamped = Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);
            bool changed = Math.Abs(clamped - _value) > 1e-9;

            _value = clamped;
            UpdateText();

            if (changed && raise) ValueChanged?.Invoke(_value);
        }

        private void UpdateText()
        {
            _box.Text = _value.ToString(Step < 1 ? "0.##" : "0", CultureInfo.CurrentCulture);
        }

        private Brush Brush(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }
    }
}
