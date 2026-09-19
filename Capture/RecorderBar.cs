using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SnapView.Capture
{
    /// <summary>
    /// 녹화하는 동안 화면 구석에 떠 있는 작은 조종 막대.
    /// 지금 찍고 있다는 걸 알려 주고, 멈추거나 잠깐 쉴 방법을 준다.
    ///
    /// 화면 캡처에서 <b>제외</b>된다(WDA_EXCLUDEFROMCAPTURE). 전체 화면을 찍어도 이 막대는 안 찍힌다.
    /// </summary>
    internal sealed class RecorderBar : Window
    {
        private readonly TextBlock _time = new();
        private readonly TextBlock _frames = new();
        private readonly Ellipse _dot;
        private readonly Button _pause;
        private readonly Button _stop;
        private readonly Button _cancel;
        private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(3) };

        private bool _saving;
        private bool _paused;
        private bool _armedCancel;

        /// <summary>멈추고 저장.</summary>
        internal event Action? Stopped;

        /// <summary>버리고 끝내기. 두 번 눌러야 온다(실수로 긴 녹화를 날리지 않게).</summary>
        internal event Action? Cancelled;

        /// <summary>잠깐 쉬기 / 다시 찍기 토글.</summary>
        internal event Action? PauseToggled;

        internal RecorderBar()
        {
            Title = "녹화 중 — SnapView";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = true;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            _dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            _time.Foreground = Res("Fg", Colors.White);
            _time.VerticalAlignment = VerticalAlignment.Center;
            _time.FontSize = 13;
            _time.MinWidth = 46;
            _time.Text = "0:00";

            _frames.Foreground = Res("FgDim", Colors.Gray);
            _frames.VerticalAlignment = VerticalAlignment.Center;
            _frames.FontSize = 11.5;
            _frames.Margin = new Thickness(8, 0, 0, 0);

            _pause = Button("❚❚", () => PauseToggled?.Invoke());
            _pause.ToolTip = "잠깐 쉬기 (쉬는 동안은 영상에 안 들어갑니다)";
            _stop = Button("■ 정지", () => Stopped?.Invoke());
            _cancel = Button("취소", RequestCancel);
            _cancel.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x73, 0x6B));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 7, 8, 7) };
            row.Children.Add(_dot);
            row.Children.Add(_time);
            row.Children.Add(_frames);
            row.Children.Add(new Rectangle
            {
                Width = 1, Margin = new Thickness(10, 3, 8, 3), Fill = Res("Divider", Colors.Gray)
            });
            row.Children.Add(_pause);
            row.Children.Add(_stop);
            row.Children.Add(_cancel);

            Content = new Border
            {
                Background = Res("BgChrome", Color.FromRgb(0x26, 0x26, 0x2B)),
                BorderBrush = Res("Divider", Color.FromRgb(0x3A, 0x3A, 0x42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = row
            };

            // 찍고 싶은 곳을 가리면 곤란하니 끌어서 옮길 수 있게 한다.
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            KeyDown += (_, e) =>
            {
                if (_saving) return;
                if (e.Key == Key.Escape) RequestCancel();
                else if (e.Key == Key.Enter) Stopped?.Invoke();
                else if (e.Key == Key.Space) PauseToggled?.Invoke();
            };

            _disarm.Tick += (_, _) => DisarmCancel();

            SourceInitialized += (_, _) => Native.WindowPlacement.ExcludeFromCapture(this);
        }

        /// <summary>
        /// 취소는 두 번 눌러야 한다. 정지 단추 바로 옆에 있어서 한 번에 30분짜리를 날리면 안 된다.
        /// 첫 번은 "정말 취소?" 로 바뀌고 3초 안에 다시 누르면 진짜로 버린다.
        /// </summary>
        private void RequestCancel()
        {
            if (_saving) return;
            if (_armedCancel) { DisarmCancel(); Cancelled?.Invoke(); return; }

            _armedCancel = true;
            _cancel.Content = "정말 취소?";
            _disarm.Start();
        }

        private void DisarmCancel()
        {
            _disarm.Stop();
            _armedCancel = false;
            _cancel.Content = "취소";
        }

        private Button Button(string text, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4) };
            if (Application.Current?.TryFindResource("ToolButton") is Style style) b.Style = style;
            b.Click += (_, _) => onClick();
            return b;
        }

        /// <summary>
        /// 찍고 있는 영역이 있는 모니터의 오른쪽 아래 구석에 붙인다.
        /// 주 모니터 기준으로 놓으면 보조 모니터를 찍을 때 엉뚱한 화면에 뜬다.
        /// </summary>
        internal void PlaceBottomRight(Int32Rect region)
        {
            UpdateLayout();
            Native.WindowPlacement.PlaceAtWorkAreaCorner(this, region, marginPx: 24);
        }

        /// <summary>파일을 마무리하는 중. 단추를 막고 저장 중이라고 알린다.</summary>
        internal void ShowSaving()
        {
            _saving = true;
            DisarmCancel();
            _time.Text = "저장 중…";
            _frames.Text = "";
            _dot.Opacity = 0.35;
            _pause.IsEnabled = _stop.IsEnabled = _cancel.IsEnabled = false;
        }

        /// <summary>쉬는 중인지 표시를 바꾼다.</summary>
        internal void SetPaused(bool paused)
        {
            _paused = paused;
            _pause.Content = paused ? "▶" : "❚❚";
            _pause.ToolTip = paused ? "다시 찍기" : "잠깐 쉬기 (쉬는 동안은 영상에 안 들어갑니다)";
            _dot.Opacity = paused ? 0.35 : 1.0;
            if (paused) _frames.Text = "쉬는 중";
        }

        /// <param name="actualFps">
        /// 실제로 담기고 있는 초당 장수. 고른 값보다 낮으면 기계가 못 따라가고 있는 것이다 —
        /// 그걸 녹화가 끝난 뒤에 알면 늦으므로 찍는 동안 보여 준다. 0 이면 아직 셀 게 없다.
        /// </param>
        internal void Update(TimeSpan elapsed, int frames, double actualFps = 0)
        {
            if (_saving) return;

            _time.Text = ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture)
                         + ":" + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
            if (_paused) return;

            _frames.Text = actualFps > 0
                ? frames + "장 · " + actualFps.ToString("0.#", CultureInfo.InvariantCulture) + "fps"
                : frames + "장";

            // 녹화 중이라는 게 눈에 띄도록 점을 깜빡인다.
            _dot.Opacity = elapsed.Seconds % 2 == 0 ? 1.0 : 0.35;
        }

        private static Brush Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }
    }
}
