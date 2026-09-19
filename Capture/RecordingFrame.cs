using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SnapView.Native;
using static SnapView.Native.NativeMethods;

namespace SnapView.Capture
{
    /// <summary>
    /// 녹화 중인 영역을 <b>깜빡이는 빨간 점선</b>으로 감싸는 테두리 창.
    ///
    /// 녹화가 돌고 있다는 걸 화면만 봐도 알 수 있어야 하고, 무엇보다
    /// <b>어디를 찍고 있는지</b>가 보여야 한다. 단축키로 시작하면 영역이 눈에 안 보여서
    /// 엉뚱한 데를 찍고 있어도 모른다.
    ///
    /// 가운데는 뚫려 있어서(클릭이 통과한다) 녹화하는 동안 그 안에서 하던 일을 계속할 수 있다.
    /// 테두리 자체는 찍히지 않도록 <b>영역 바깥쪽</b>에 그린다.
    /// </summary>
    internal sealed class RecordingFrame : Window
    {
        private const double Thickness = 3;

        private readonly Rectangle _border;

        internal RecordingFrame(Int32Rect region)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;

            // 처음 자리는 대략 잡아 두고(창이 어느 모니터에 생길지 정한다), 핸들이 생기면
            // 물리 픽셀로 다시 맞춘다. WPF 의 Left/Width 는 DIP 라서 배율이 100% 가 아닌
            // 모니터에서는 물리 좌표를 그대로 넣으면 테두리가 영역보다 크게 그려진다.
            Left = region.X - Thickness;
            Top = region.Y - Thickness;
            Width = region.Width + Thickness * 2;
            Height = region.Height + Thickness * 2;

            SourceInitialized += (_, _) =>
            {
                // 전체 화면·모니터 녹화에서는 테두리가 영역 안에 놓인다. 캡처에서 빼 둔다.
                WindowPlacement.ExcludeFromCapture(this);

                // 테두리를 영역 바깥에 두르려고 사방으로 넓힌다. 두께는 그 모니터 배율만큼 키운다.
                // 두 번 놓는 이유: 다른 배율의 모니터로 옮겨지면 WPF 가 창 크기를 다시 잡기 때문이다.
                for (int pass = 0; pass < 2; pass++)
                {
                    int t = (int)Math.Ceiling(Thickness * WindowPlacement.DpiScaleOf(this));
                    WindowPlacement.PlacePhysical(this, region.X - t, region.Y - t,
                                                  region.Width + t * 2, region.Height + t * 2);
                }
            };

            _border = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A)),
                StrokeThickness = Thickness,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = Brushes.Transparent,
                RadiusX = 2,
                RadiusY = 2
            };

            Content = new Grid { Children = { _border } };

            Loaded += (_, _) =>
            {
                MakeClickThrough();
                StartBlinking();
            };
        }

        /// <summary>
        /// 마우스가 이 창을 통과하게 만든다. 안 그러면 녹화하는 동안 그 영역을 못 만진다.
        /// (WS_EX_TRANSPARENT — 클릭이 아래 창으로 그대로 내려간다)
        /// </summary>
        private void MakeClickThrough()
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            long style = GetWindowLongSafe(hwnd, GWL_EXSTYLE);
            SetWindowLongSafe(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        }

        /// <summary>
        /// 테두리를 깜빡이고 점선을 흐르게 한다.
        /// 깜빡이기만 하면 정지 화면과 구분이 잘 안 돼서, 점선도 같이 움직인다.
        /// </summary>
        private void StartBlinking()
        {
            var blink = new DoubleAnimation
            {
                From = 1.0,
                To = 0.25,
                Duration = TimeSpan.FromSeconds(0.6),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            _border.BeginAnimation(OpacityProperty, blink);

            var march = new DoubleAnimation
            {
                From = 0,
                To = 7,                                   // 점 + 틈 한 벌
                Duration = TimeSpan.FromSeconds(0.5),
                RepeatBehavior = RepeatBehavior.Forever
            };
            _border.BeginAnimation(Shape.StrokeDashOffsetProperty, march);
        }

        /// <summary>애니메이션을 멈추고 닫는다.</summary>
        internal void Finish()
        {
            _border.BeginAnimation(OpacityProperty, null);
            _border.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            Close();
        }
    }
}
