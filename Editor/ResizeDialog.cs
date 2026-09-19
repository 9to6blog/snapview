using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnapView.Editor
{
    /// <summary>
    /// 그림 크기를 새로 정하는 작은 창 — 작은 그림을 크게 늘려 바꿔치기(확대 교체)하거나
    /// 큰 그림을 줄일 때 쓴다. 기본은 비율 유지 — 한쪽을 고치면 다른 쪽이 따라온다.
    /// </summary>
    public sealed class ResizeDialog : Window
    {
        private readonly int _originalWidth;
        private readonly int _originalHeight;

        private readonly NumberStepper _width = new() { Width = 110, Height = 26 };
        private readonly NumberStepper _height = new() { Width = 110, Height = 26 };
        private readonly NumberStepper _percent = new() { Width = 110, Height = 26, Suffix = "%" };
        private readonly CheckBox _keepRatio = new() { Content = " 비율 유지", IsChecked = true };
        private readonly TextBlock _summary = new();

        private bool _syncing;

        internal int ResultWidth { get; private set; }
        internal int ResultHeight { get; private set; }

        internal ResizeDialog(int width, int height)
        {
            _originalWidth = Math.Max(1, width);
            _originalHeight = Math.Max(1, height);
            ResultWidth = _originalWidth;
            ResultHeight = _originalHeight;

            Title = "이미지 크기 조절";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Res("Bg", Color.FromRgb(0x1B, 0x1B, 0x1F));

            _width.Minimum = 1; _width.Maximum = 20000; _width.Step = 1;
            _height.Minimum = 1; _height.Maximum = 20000; _height.Step = 1;
            _percent.Minimum = 1; _percent.Maximum = 800; _percent.Step = 5;

            _width.SetSilently(_originalWidth);
            _height.SetSilently(_originalHeight);
            _percent.SetSilently(100);

            _width.ValueChanged += v => OnWidth((int)v);
            _height.ValueChanged += v => OnHeight((int)v);
            _percent.ValueChanged += OnPercent;

            Content = BuildBody();
            UpdateSummary();
        }

        private UIElement BuildBody()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < 6; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddRow(grid, 0, "가로", _width);
            AddRow(grid, 1, "세로", _height);
            AddRow(grid, 2, "비율", _percent);

            _keepRatio.Foreground = Res("Fg", Colors.White);
            _keepRatio.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_keepRatio, 3);
            Grid.SetColumnSpan(_keepRatio, 2);
            grid.Children.Add(_keepRatio);

            _summary.Foreground = Res("FgDim", Colors.Gray);
            _summary.FontSize = 11.5;
            _summary.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_summary, 4);
            Grid.SetColumnSpan(_summary, 2);
            grid.Children.Add(_summary);

            var ok = new Button { Content = "적용", MinWidth = 62, IsDefault = true };
            var cancel = new Button
            {
                Content = "취소", MinWidth = 62, IsCancel = true, Margin = new Thickness(6, 0, 0, 0)
            };
            ok.Style = (Style)TryFindResource("ToolButton");
            cancel.Style = ok.Style;
            ok.Click += (_, _) => { DialogResult = true; Close(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 5);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            return grid;
        }

        private void AddRow(Grid grid, int row, string label, UIElement field)
        {
            var text = new TextBlock
            {
                Text = label,
                Foreground = Res("FgDim", Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 10, 3)
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }

        // ---------------------------------------------------------------- 값 맞물리기

        private void OnWidth(int w)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                ResultWidth = w;
                if (_keepRatio.IsChecked == true)
                {
                    ResultHeight = Math.Max(1, (int)Math.Round(w * (double)_originalHeight / _originalWidth));
                    _height.SetSilently(ResultHeight);
                }
                _percent.SetSilently(Math.Round(w * 100.0 / _originalWidth));
            }
            finally { _syncing = false; }
            UpdateSummary();
        }

        private void OnHeight(int h)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                ResultHeight = h;
                if (_keepRatio.IsChecked == true)
                {
                    ResultWidth = Math.Max(1, (int)Math.Round(h * (double)_originalWidth / _originalHeight));
                    _width.SetSilently(ResultWidth);
                }
                _percent.SetSilently(Math.Round(h * 100.0 / _originalHeight));
            }
            finally { _syncing = false; }
            UpdateSummary();
        }

        private void OnPercent(double percent)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                ResultWidth = Math.Max(1, (int)Math.Round(_originalWidth * percent / 100));
                ResultHeight = Math.Max(1, (int)Math.Round(_originalHeight * percent / 100));
                _width.SetSilently(ResultWidth);
                _height.SetSilently(ResultHeight);
            }
            finally { _syncing = false; }
            UpdateSummary();
        }

        private void UpdateSummary()
            => _summary.Text = string.Format(CultureInfo.CurrentCulture,
                "{0}×{1} → {2}×{3}", _originalWidth, _originalHeight, ResultWidth, ResultHeight);

        private static Brush Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }
    }
}
