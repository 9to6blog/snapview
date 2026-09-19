using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnapView.Capture
{
    /// <summary>
    /// 녹화 시작 전 "3, 2, 1" 을 찍을 영역 한가운데에 크게 보여 준다.
    ///
    /// 단축키를 누른 손이나 트레이 메뉴가 첫 장에 찍히는 게 싫은 사람을 위한 것이라
    /// 기본은 꺼져 있다(설정에서 초를 고른다). 세는 동안 녹화 키를 다시 누르면 취소된다.
    /// </summary>
    internal sealed class CountdownWindow : Window
    {
        private readonly TextBlock _number = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Int32Rect _region;
        private int _left;
        private bool _done;

        /// <summary>다 셌다. 이제 찍기 시작하면 된다.</summary>
        internal event Action? Finished;

        internal CountdownWindow(Int32Rect region, int seconds)
        {
            _region = region;
            _left = Math.Clamp(seconds, 1, 10);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            IsHitTestVisible = false;
            Width = 160;
            Height = 160;

            _number.FontSize = 96;
            _number.FontWeight = FontWeights.Bold;
            _number.Foreground = Brushes.White;
            _number.HorizontalAlignment = HorizontalAlignment.Center;
            _number.VerticalAlignment = VerticalAlignment.Center;
            _number.Text = _left.ToString();

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x24)),
                CornerRadius = new CornerRadius(24),
                Child = _number
            };

            SourceInitialized += (_, _) =>
            {
                // 영역 한가운데(물리 픽셀). 배율이 달라도 어긋나지 않게 물리 좌표로 놓는다.
                double scale = Native.WindowPlacement.DpiScaleOf(this);
                int size = (int)Math.Round(160 * scale);
                Native.WindowPlacement.PlacePhysical(this,
                    _region.X + (_region.Width - size) / 2, _region.Y + (_region.Height - size) / 2, size, size);
            };

            _timer.Tick += (_, _) => Step();
            Loaded += (_, _) => _timer.Start();
        }

        private void Step()
        {
            _left--;
            if (_left > 0) { _number.Text = _left.ToString(); return; }

            _timer.Stop();
            if (_done) return;
            _done = true;
            Close();
            Finished?.Invoke();
        }

        /// <summary>세는 걸 그만두고 닫는다. Finished 는 안 온다.</summary>
        internal void Cancel()
        {
            _timer.Stop();
            _done = true;
            Close();
        }
    }
}
