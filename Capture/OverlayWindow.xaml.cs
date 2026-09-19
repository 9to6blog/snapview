using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Threading.Tasks;
using SnapView.Core;
using SnapView.Native;
using static SnapView.Native.NativeMethods;

namespace SnapView.Capture
{
    /// <summary>사용자가 오버레이에서 고른 것.</summary>
    internal sealed class OverlayResult
    {
        /// <summary>선택 영역(얼린 비트맵의 픽셀 좌표).</summary>
        internal Int32Rect Region { get; init; }

        /// <summary>
        /// 창을 통째로 골랐고 그 뒤 크기를 손대지 않았을 때의 창 핸들.
        /// 이 값이 있으면 화면에서 잘라내는 대신 그 창만 다시 정확히 캡처할 수 있다.
        /// </summary>
        internal IntPtr WindowHandle { get; init; }

        /// <summary>확인 버튼 대신 무엇을 눌렀는지.</summary>
        internal OverlayAction Action { get; init; } = OverlayAction.Confirm;
    }

    /// <summary>
    /// 이 오버레이를 무엇 때문에 띄웠는가.
    ///
    /// 영역을 고르는 방법은 셋 다 같지만 <b>고른 뒤에 할 일이 다르다</b>. 녹화하려고
    /// 띄운 창에 "편집·복사·저장" 이 떠 있으면 무엇을 누르라는 건지 알 수가 없다.
    /// </summary>
    internal enum OverlayPurpose
    {
        /// <summary>화면을 찍는다.</summary>
        Capture,
        /// <summary>이 영역을 녹화한다.</summary>
        Record
    }

    internal enum OverlayAction
    {
        /// <summary>설정대로 처리(복사/저장/뷰어).</summary>
        Confirm,
        /// <summary>주석 편집기로 보낸다.</summary>
        Edit,
        /// <summary>클립보드에만 복사.</summary>
        CopyOnly,
        /// <summary>파일로만 저장.</summary>
        SaveOnly
    }

    /// <summary>
    /// 얼려 둔 화면 위에서 영역을 고르는 전체 화면 오버레이.
    ///
    /// 좌표계가 셋이라 헷갈리기 쉬운데 정리하면:
    ///   · 물리 픽셀   — 실제 화면. _virt 가 그 원점과 크기.
    ///   · 비트맵 픽셀 — 얼린 이미지 안의 좌표. (물리 − _virt 원점)
    ///   · DIP         — WPF 레이아웃 좌표. 배경을 Stretch=Fill 로 깔았으므로
    ///                   비트맵/DIP 비율만 곱하면 DPI 가 섞여 있어도 정확히 대응된다.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int LoupePixels = 11;       // 돋보기에 보여 줄 픽셀 수(가로=세로)
        private const double ClickThreshold = 4;  // 이보다 적게 움직이면 "드래그"가 아니라 "클릭"
        private const double HandleSize = 9;      // 조절점 한 변(DIP)
        private const double HandleGrab = 11;     // 조절점을 잡았다고 볼 반경(DIP)

        private enum Phase { Idle, Dragging, Adjusting, Moving, Resizing }

        private enum Handle { None, NW, N, NE, E, SE, S, SW, W, Inside }

        /// <summary>오버레이 좌표로 옮겨 둔 창 후보.</summary>
        private readonly struct WindowCandidate
        {
            internal WindowCandidate(IntPtr hwnd, Int32Rect rect) { Hwnd = hwnd; Rect = rect; }
            internal IntPtr Hwnd { get; }
            internal Int32Rect Rect { get; }
        }

        private readonly BitmapSource _frozen;
        private readonly Int32Rect _virt;
        private readonly bool _adjustBeforeCapture;
        private readonly bool _showCrosshair;
        private readonly OverlayPurpose _purpose;
        private readonly bool _startWithFullSelection;
        private readonly List<WindowCandidate> _windows;
        private readonly Rectangle[] _handles = new Rectangle[8];

        // 마우스가 움직일 때마다 부르는 것들은 만들어 두고 재사용한다.
        // 매번 새로 만들면 저사양에서 드래그가 눈에 띄게 버벅인다.
        private readonly System.Drawing.Rectangle[] _monitors =
            Array.ConvertAll(System.Windows.Forms.Screen.AllScreens, s => s.Bounds);
        private readonly ImageBrush _holeBrush;
        private readonly byte[] _loupeBuf = new byte[LoupePixels * LoupePixels * 4];
        private readonly SolidColorBrush _loupeBrush = new(Colors.Black);
        private WriteableBitmap? _loupeBitmap;
        private Size _loupeSize;
        private bool _selDashed;
        private static readonly DoubleCollection DashPattern = new() { 4, 3 };
        private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

        private Phase _phase = Phase.Idle;
        private Point _anchor;
        private Point _dragFrom;
        private Rect _selection = Rect.Empty;      // DIP
        private Rect _resizeStart = Rect.Empty;
        private Handle _activeHandle = Handle.None;
        private WindowCandidate? _hover;
        private IntPtr _pickedWindow = IntPtr.Zero;
        private volatile CropBoundaryAnalysis? _boundaryAnalysis;

        /// <summary>확정된 결과. 취소하면 null.</summary>
        internal OverlayResult? Result { get; private set; }

        /// <param name="purpose">고른 뒤에 무엇을 할 것인가. 단추 구성이 여기 따라 달라진다.</param>
        /// <param name="confirmHint">
        /// "확인" 을 누르면 실제로 무슨 일이 일어나는지. 설정을 아는 쪽(컨트롤러)이 적어 준다 —
        /// "설정대로 처리" 라고만 하면 저장과 무엇이 다른지 알 수가 없다.
        /// </param>
        internal OverlayWindow(BitmapSource frozen, Int32Rect virtualScreen,
                               bool adjustBeforeCapture, bool showCrosshair,
                               OverlayPurpose purpose = OverlayPurpose.Capture,
                               string confirmHint = "", bool startWithFullSelection = false)
        {
            InitializeComponent();

            _frozen = frozen;
            _virt = virtualScreen;
            _adjustBeforeCapture = adjustBeforeCapture;
            _showCrosshair = showCrosshair;
            _purpose = purpose;
            _startWithFullSelection = startWithFullSelection;

            // 배경은 어둡게 구운 것을 깔고, 선택 영역(Hole)에만 원본을 보여 준다.
            Backdrop.Source = Darken(frozen, 0x8A);
            _holeBrush = new ImageBrush(frozen)
            {
                ViewboxUnits = BrushMappingMode.Absolute,   // 비트맵 픽셀 좌표로 지정
                Stretch = Stretch.Fill
            };
            Hole.Fill = _holeBrush;

            SetUpActionBar(confirmHint);

            // 오버레이를 띄우기 전에 잡아 둔 창 목록을 화면 좌표 → 비트맵 좌표로 옮겨 둔다.
            _windows = new List<WindowCandidate>();
            foreach (ScreenCapture.CapturableWindow c in ScreenCapture.EnumerateVisibleWindows())
            {
                _windows.Add(new WindowCandidate(c.Hwnd,
                    new Int32Rect(c.Rect.X - _virt.X, c.Rect.Y - _virt.Y, c.Rect.Width, c.Rect.Height)));
            }

            LoupeSwatch.Fill = _loupeBrush;
            CreateHandles();
            UpdateHint();
            BeginBoundaryAnalysis();
        }

        private void BeginBoundaryAnalysis()
        {
            BitmapSource source = _frozen;
            if (!source.IsFrozen)
            {
                source = new WriteableBitmap(source);
                source.Freeze();
            }

            _ = Task.Run(() =>
            {
                try { _boundaryAnalysis = CropBoundaryAnalysis.Analyze(source); }
                catch { /* 창·모니터 경계 자석은 분석 실패와 무관하게 계속 쓴다. */ }
            });
        }

        private void CreateHandles()
        {
            for (int i = 0; i < _handles.Length; i++)
            {
                var r = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)),
                    StrokeThickness = 1,
                    Visibility = Visibility.Collapsed
                };
                _handles[i] = r;
                Layer.Children.Add(r);
            }
        }

        /// <summary>
        /// 얼린 화면에 어둡게 덮개를 미리 구워 넣는다. 반투명 덮개를 실시간으로 얹으면
        /// 선택이 바뀔 때마다 화면 전체를 다시 그려야 해서 저사양에서 드래그가 버벅인다.
        /// 검은 반투명(#8A000000)을 얹은 것과 같은 값: 채널 × (255-alpha) / 255.
        /// </summary>
        private static BitmapSource Darken(BitmapSource src, byte alpha)
        {
            var table = new byte[256];
            int keep = 255 - alpha;
            for (int i = 0; i < 256; i++) table[i] = (byte)(i * keep / 255);

            int w = src.PixelWidth, h = src.PixelHeight;
            int stride = w * 4;
            var buf = new byte[(long)stride * h];
            src.CopyPixels(buf, stride, 0);

            for (int i = 0; i < buf.Length; i += 4)
            {
                buf[i] = table[buf[i]];
                buf[i + 1] = table[buf[i + 1]];
                buf[i + 2] = table[buf[i + 2]];
            }

            var dark = BitmapSource.Create(w, h, 96, 96, src.Format, null, buf, stride);
            dark.Freeze();
            return dark;
        }

        // ---------------- 좌표 변환 ----------------

        private double PxPerDipX => _frozen.PixelWidth / Math.Max(1.0, Root.ActualWidth);
        private double PxPerDipY => _frozen.PixelHeight / Math.Max(1.0, Root.ActualHeight);

        private Rect ToDip(Int32Rect r) => new(
            r.X / PxPerDipX, r.Y / PxPerDipY, r.Width / PxPerDipX, r.Height / PxPerDipY);

        private Int32Rect ToPixels(Rect r) => ViewMath.ToPixels(
            r, PxPerDipX, PxPerDipY, _frozen.PixelWidth, _frozen.PixelHeight);

        /// <summary>
        /// 화면 픽셀에서 검출한 긴 콘텐츠 경계와 창·모니터 테두리 중 가장 가까운 곳에 붙인다.
        /// 분석은 뒤에서 준비하므로 오버레이가 뜨는 첫 순간에도 창/모니터 자석은 즉시 동작한다.
        /// </summary>
        private Point SnapOverlayPoint(Point dip, bool snapX, bool snapY)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return dip;

            double px = dip.X * PxPerDipX;
            double py = dip.Y * PxPerDipY;
            double tolX = 12 * PxPerDipX;
            double tolY = 12 * PxPerDipY;
            double bestX = px, bestY = py;
            double distX = tolX + 1, distY = tolY + 1;

            CropSnapResult content = _boundaryAnalysis?.Snap(new Point(px, py), Math.Max(tolX, tolY)) ?? default;
            if (snapX && content.X.HasValue) Consider(content.X.Value, px, tolX, ref bestX, ref distX);
            if (snapY && content.Y.HasValue) Consider(content.Y.Value, py, tolY, ref bestY, ref distY);

            foreach (WindowCandidate window in _windows)
            {
                Int32Rect r = window.Rect;
                if (snapX)
                {
                    Consider(r.X, px, tolX, ref bestX, ref distX);
                    Consider(r.X + r.Width, px, tolX, ref bestX, ref distX);
                }
                if (snapY)
                {
                    Consider(r.Y, py, tolY, ref bestY, ref distY);
                    Consider(r.Y + r.Height, py, tolY, ref bestY, ref distY);
                }
            }

            foreach (System.Drawing.Rectangle monitor in _monitors)
            {
                double l = monitor.Left - _virt.X, t = monitor.Top - _virt.Y;
                double r = monitor.Right - _virt.X, b = monitor.Bottom - _virt.Y;
                if (snapX) { Consider(l, px, tolX, ref bestX, ref distX); Consider(r, px, tolX, ref bestX, ref distX); }
                if (snapY) { Consider(t, py, tolY, ref bestY, ref distY); Consider(b, py, tolY, ref bestY, ref distY); }
            }

            return new Point(snapX ? bestX / PxPerDipX : dip.X,
                             snapY ? bestY / PxPerDipY : dip.Y);
        }

        private static void Consider(double candidate, double value, double tolerance,
                                     ref double best, ref double bestDistance)
        {
            double distance = Math.Abs(candidate - value);
            if (distance <= tolerance && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        // ---------------- 창 배치 ----------------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // WPF 의 DIP 배치를 거치지 않고 물리 픽셀로 직접 가상 화면 전체를 덮는다.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, _virt.X, _virt.Y, _virt.Width, _virt.Height, SWP_SHOWWINDOW);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Activate();
            Focus();

            if (_startWithFullSelection)
            {
                _selection = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
                _phase = Phase.Adjusting;
                HintBar.Visibility = Visibility.Collapsed;
            }
            UpdateVisuals();
            PlaceHintBar();

            // 마우스를 움직이기 전에도 안내선이 보이도록 지금 커서 자리에 한 번 그려 둔다.
            UpdateCrosshair(Mouse.GetPosition(Root));
        }

        private void UpdateHint()
        {
            string what = _purpose switch
            {
                OverlayPurpose.Record => "녹화할 영역",
                _ => "영역"
            };

            HintText.Text = _adjustBeforeCapture
                ? $"드래그 {what} 선택   ·   자석 맞춤 M   ·   Alt 드래그 스냅 해제   ·   클릭 창 단위   ·   놓으면 조절 가능   ·   ESC 취소"
                : $"드래그 {what} 선택   ·   자석 맞춤 M   ·   Alt 드래그 스냅 해제   ·   클릭 창 단위   ·   ESC 취소";
        }

        /// <summary>
        /// 용도에 맞게 단추를 고른다.
        ///
        /// 녹화하려고 띄운 창에 편집·복사·저장이 떠 있으면 무엇을 누르라는 건지 알 수 없다.
        /// 그때 할 수 있는 일은 <b>시작하거나 그만두거나</b> 둘뿐이다.
        /// </summary>
        private void SetUpActionBar(string confirmHint)
        {
            bool forCapture = _purpose == OverlayPurpose.Capture;

            // 찍을 때만 뜻이 있는 것들
            BtnEdit.Visibility = BtnCopy.Visibility = BtnSave.Visibility =
                forCapture ? Visibility.Visible : Visibility.Collapsed;

            // 왼쪽이 통째로 비면 구분선만 덩그러니 남는다.
            ActionSeparator.Visibility = forCapture ? Visibility.Visible : Visibility.Collapsed;

            // 녹화에 체크 표시는 어울리지 않는다. 녹화를 뜻하는 건 빨간 점이다.
            bool forRecord = _purpose == OverlayPurpose.Record;
            RecordDot.Visibility = forRecord ? Visibility.Visible : Visibility.Collapsed;
            ConfirmIcon.Visibility = forRecord ? Visibility.Collapsed : Visibility.Visible;

            ConfirmLabel.Text = _purpose switch
            {
                OverlayPurpose.Record => "녹화 시작",
                _ => "확인"
            };

            BtnOk.ToolTip = _purpose switch
            {
                OverlayPurpose.Record => "이 영역을 녹화하기 시작한다 (Enter · Space · 더블클릭)",
                _ => (confirmHint.Length > 0 ? confirmHint : "설정대로 처리") +
                     "  (Enter · Space · 더블클릭)"
            };

            BtnCancel.ToolTip = _purpose == OverlayPurpose.Record ? "녹화하지 않는다 (ESC)" : "취소 (ESC)";
        }

        private bool _hintAtBottom;

        private void PlaceHintBar()
        {
            try
            {
                System.Drawing.Rectangle mon =
                    System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;

                HintBar.Visibility = Visibility.Visible;
                HintBar.UpdateLayout();

                double left = (mon.X - _virt.X + mon.Width / 2.0) / PxPerDipX - HintBar.ActualWidth / 2.0;
                double frac = _hintAtBottom ? 0.91 : 0.09;
                double top = (mon.Y - _virt.Y + mon.Height * frac) / PxPerDipY
                             - (_hintAtBottom ? HintBar.ActualHeight : 0);
                Canvas.SetLeft(HintBar, Math.Max(0, left));
                Canvas.SetTop(HintBar, Math.Max(0, top));
            }
            catch { HintBar.Visibility = Visibility.Collapsed; }
        }

        /// <summary>
        /// 안내 막대가 잡으려는 자리를 가리면 안 된다. 커서가 가까이 오면 화면 반대쪽
        /// (위 ↔ 아래)으로 비켜난다. 아예 숨기지 않는 이유: 처음 쓰는 사람에게는
        /// 안내가 필요하고, 비켜나기만 하면 두 요구가 부딪히지 않는다.
        /// </summary>
        private void DodgeHintBar(Point p)
        {
            if (HintBar.Visibility != Visibility.Visible) return;

            double x = Canvas.GetLeft(HintBar), y = Canvas.GetTop(HintBar);
            if (double.IsNaN(x) || double.IsNaN(y)) return;

            var near = new Rect(x, y, HintBar.ActualWidth, HintBar.ActualHeight);
            near.Inflate(36, 36);
            if (!near.Contains(p)) return;

            _hintAtBottom = !_hintAtBottom;
            PlaceHintBar();
        }

        // ---------------- 입력 ----------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Point raw = e.GetPosition(Root);
            Point p = raw;
            BtnMagnet.Content = "자석 맞춤";

            if (_phase == Phase.Adjusting)
            {
                if (e.ClickCount == 2 && _selection.Contains(p)) { Finish(OverlayAction.Confirm); return; }

                Handle h = HitTest(p);
                if (h is not Handle.None)
                {
                    _activeHandle = h;
                    _resizeStart = _selection;
                    _dragFrom = p;
                    _phase = h == Handle.Inside ? Phase.Moving : Phase.Resizing;
                    CaptureMouse();
                    return;
                }
                // 선택 밖을 누르면 새로 잡기 시작
            }

            p = SnapOverlayPoint(raw, snapX: true, snapY: true);
            // 클릭 여부와 매 프레임 네 변 재계산에는 자석 적용 전의 시작점이 필요하다.
            // 자석 적용된 점을 고정해 버리면 비동기 경계 분석이 늦게 끝났을 때
            // 왼쪽·위쪽은 다시 검사되지 않고 움직이는 오른쪽·아래쪽만 붙는다.
            _anchor = raw;
            _phase = Phase.Dragging;
            _pickedWindow = IntPtr.Zero;
            _selection = new Rect(p, new Size(0, 0));
            HintBar.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
            CaptureMouse();
            UpdateVisuals();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point raw = e.GetPosition(Root);
            Point p = raw;

            switch (_phase)
            {
                case Phase.Dragging:
                    p = SnapOverlayPoint(raw, snapX: true, snapY: true);
                    _selection = ViewMath.SnapRectEdges(Normalize(_anchor, raw), SnapOverlayPoint);
                    break;

                case Phase.Moving:
                {
                    double dx = p.X - _dragFrom.X, dy = p.Y - _dragFrom.Y;
                    double nx = Math.Clamp(_resizeStart.X + dx, 0, Math.Max(0, Root.ActualWidth - _resizeStart.Width));
                    double ny = Math.Clamp(_resizeStart.Y + dy, 0, Math.Max(0, Root.ActualHeight - _resizeStart.Height));
                    _selection = new Rect(nx, ny, _resizeStart.Width, _resizeStart.Height);
                    _pickedWindow = IntPtr.Zero;   // 손댔으니 더는 "그 창"이 아니다
                    break;
                }

                case Phase.Resizing:
                    bool snapX = _activeHandle is Handle.NW or Handle.W or Handle.SW or
                                                   Handle.NE or Handle.E or Handle.SE;
                    bool snapY = _activeHandle is Handle.NW or Handle.N or Handle.NE or
                                                   Handle.SW or Handle.S or Handle.SE;
                    p = SnapOverlayPoint(raw, snapX, snapY);
                    _selection = Resize(_resizeStart, _activeHandle, p);
                    _pickedWindow = IntPtr.Zero;
                    break;

                case Phase.Idle:
                    _hover = FindWindowAt(p);
                    break;

                case Phase.Adjusting:
                    Cursor = CursorFor(HitTest(p));
                    break;
            }

            UpdateVisuals();
            UpdateCrosshair(p);
            UpdateLoupe(raw);
            DodgeHintBar(raw);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            SetCrosshairVisible(false);
            Loupe.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 커서를 지나는 가로·세로 안내선을 화면 끝까지 긋는다.
        /// 잡고 싶은 대상의 모서리에 선을 맞춰 놓고 드래그를 시작하면 딱 떨어진다.
        /// </summary>
        private void UpdateCrosshair(Point p)
        {
            if (!_showCrosshair) return;

            // 액션 바 위에서는 거슬리므로 감춘다(돋보기와 같은 규칙).
            if (ActionBar.Visibility == Visibility.Visible)
            {
                var bar = new Rect(Canvas.GetLeft(ActionBar), Canvas.GetTop(ActionBar),
                                   ActionBar.ActualWidth, ActionBar.ActualHeight);
                if (bar.Contains(p)) { SetCrosshairVisible(false); return; }
            }

            double w = Root.ActualWidth, h = Root.ActualHeight;

            // 0.5 를 더해야 1px 선이 두 픽셀에 걸쳐 흐려지지 않는다.
            double x = Math.Floor(p.X) + 0.5;
            double y = Math.Floor(p.Y) + 0.5;

            CrossH.X1 = 0; CrossH.X2 = w; CrossH.Y1 = y; CrossH.Y2 = y;
            CrossHBase.X1 = 0; CrossHBase.X2 = w; CrossHBase.Y1 = y; CrossHBase.Y2 = y;
            CrossV.Y1 = 0; CrossV.Y2 = h; CrossV.X1 = x; CrossV.X2 = x;
            CrossVBase.Y1 = 0; CrossVBase.Y2 = h; CrossVBase.X1 = x; CrossVBase.X2 = x;

            SetCrosshairVisible(true);
        }

        private void SetCrosshairVisible(bool show)
        {
            Visibility v = show && _showCrosshair ? Visibility.Visible : Visibility.Collapsed;
            CrossH.Visibility = CrossV.Visibility = v;
            CrossHBase.Visibility = CrossVBase.Visibility = v;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Point p = e.GetPosition(Root);

            if (_phase is Phase.Moving or Phase.Resizing)
            {
                ReleaseMouseCapture();
                _phase = Phase.Adjusting;
                _activeHandle = Handle.None;
                UpdateVisuals();
                return;
            }

            if (_phase != Phase.Dragging) return;
            ReleaseMouseCapture();

            bool wasClick = Math.Abs(p.X - _anchor.X) < ClickThreshold &&
                            Math.Abs(p.Y - _anchor.Y) < ClickThreshold;

            if (wasClick)
            {
                // 드래그가 아니라 클릭 = 커서 아래 창을 통째로.
                WindowCandidate? win = FindWindowAt(p);
                if (win == null) { Cancel(); return; }

                _selection = ToDip(win.Value.Rect);
                _pickedWindow = win.Value.Hwnd;
            }
            else if (ToPixels(_selection) is { Width: < 2 } or { Height: < 2 })
            {
                Cancel();
                return;
            }

            if (_adjustBeforeCapture)
            {
                _phase = Phase.Adjusting;
                UpdateVisuals();
            }
            else
            {
                Finish(OverlayAction.Confirm);
            }
        }

        /// <summary>
        /// 우클릭 = 커서 아래 색을 <b>#RRGGBB 로 클립보드에 복사</b> (사용자 지정).
        /// 예전에는 취소였는데, 취소는 ESC 가 이미 하고 있고 돋보기가 색을 보여 주는
        /// 마당에 집어 갈 방법이 없는 게 더 아까웠다.
        /// </summary>
        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);

            Point p = e.GetPosition(Root);
            Color c = ColorAt(p);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

            // 클립보드는 다른 프로세스가 잠글 수 있어서 짧게 재시도한다.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { Clipboard.SetText(hex); break; }
                catch { System.Threading.Thread.Sleep(40); }
            }

            // 복사됐다는 표시는 돋보기 줄에. 마우스를 움직이면 원래 표시로 돌아간다.
            UpdateLoupe(p);
            LoupeText.Text = hex + "  복사됨";
            _loupeBrush.Color = c;
        }

        private Color ColorAt(Point dip)
        {
            int px = Math.Clamp((int)(dip.X * PxPerDipX), 0, _frozen.PixelWidth - 1);
            int py = Math.Clamp((int)(dip.Y * PxPerDipY), 0, _frozen.PixelHeight - 1);
            var buf = new byte[4];
            try { _frozen.CopyPixels(new Int32Rect(px, py, 1, 1), buf, 4, 0); }
            catch { return Colors.Black; }
            return Color.FromRgb(buf[2], buf[1], buf[0]);   // Bgr32
        }

        // 이 오버레이 안에서 눌린 적이 있는 키만 뗌을 인정한다. 전역 단축키(Alt+Shift+S)의
        // S 처럼 오버레이가 열리기 전부터 눌려 있던 키의 뗌이 "파일로만 저장" 이 되면 곤란하다.
        private readonly HashSet<Key> _seenDown = new();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            _seenDown.Add(e.Key);

            // 방향키만 누름에서 처리한다 — 꾹 누르고 있으면 반복돼야 하는 키라서.
            if (_phase == Phase.Adjusting)
            {
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

                switch (e.Key)
                {
                    // Ctrl 을 누르면 이동 대신 오른쪽/아래 모서리를 늘린다
                    case Key.Left: Nudge(ctrl ? -step : 0, 0, ctrl ? 0 : -step, 0); return;
                    case Key.Right: Nudge(ctrl ? step : 0, 0, ctrl ? 0 : step, 0); return;
                    case Key.Up: Nudge(0, ctrl ? -step : 0, 0, ctrl ? 0 : -step); return;
                    case Key.Down: Nudge(0, ctrl ? step : 0, 0, ctrl ? 0 : step); return;
                }
            }
        }

        /// <summary>
        /// 확인·취소처럼 <b>오버레이를 닫는 키는 뗄 때 처리한다.</b>
        ///
        /// 누를 때 처리하면 닫힌 직후의 키 뗌이 원래 보고 있던 앱으로 들어간다 —
        /// 영상을 틀어 놓고 스페이스로 캡처를 확정하면 그 뗌이 플레이어에 닿아
        /// 재생이 멈췄다(스페이스는 대부분 재생/일시정지 키다). 뗌까지 여기서 삼키고
        /// 닫으면 밖으로 새는 게 없다.
        /// </summary>
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (!_seenDown.Remove(e.Key)) return;

            if (e.Key == Key.M)
            {
                AutoFitSelection();
                return;
            }

            if (_phase == Phase.Adjusting)
            {
                switch (e.Key)
                {
                    // 스페이스도 확인으로 친다. 영역을 잡은 손 그대로 누를 수 있는 자리다.
                    case Key.Enter:
                    case Key.Space: Finish(OverlayAction.Confirm); return;
                    // 이 키들은 "찍을 때" 만 뜻이 있다. 녹화 중에 S 를 눌러 저장이 되면 이상하다.
                    case Key.E when _purpose == OverlayPurpose.Capture:
                        Finish(OverlayAction.Edit); return;
                    case Key.C when _purpose == OverlayPurpose.Capture:
                        Finish(OverlayAction.CopyOnly); return;
                    case Key.S when _purpose == OverlayPurpose.Capture:
                        Finish(OverlayAction.SaveOnly); return;
                    case Key.Escape: ResetToIdle(); return;
                }
            }

            if (e.Key == Key.Escape) Cancel();
        }

        /// <summary>선택을 옮기거나(dx,dy) 크기를 바꾼다(dw,dh).</summary>
        private void Nudge(double dw, double dh, double dx, double dy)
        {
            double x = _selection.X + dx;
            double y = _selection.Y + dy;
            double w = Math.Max(1, _selection.Width + dw);
            double h = Math.Max(1, _selection.Height + dh);

            x = Math.Clamp(x, 0, Math.Max(0, Root.ActualWidth - w));
            y = Math.Clamp(y, 0, Math.Max(0, Root.ActualHeight - h));

            _selection = new Rect(x, y, w, h);
            _pickedWindow = IntPtr.Zero;
            UpdateVisuals();
        }

        private void ResetToIdle()
        {
            _phase = Phase.Idle;
            _selection = Rect.Empty;
            _pickedWindow = IntPtr.Zero;
            _hover = null;
            Cursor = Cursors.Cross;
            ActionBar.Visibility = Visibility.Collapsed;
            HintBar.Visibility = Visibility.Visible;
            UpdateVisuals();
        }

        private void OnAutoFitSelection(object sender, RoutedEventArgs e) => AutoFitSelection();

        /// <summary>
        /// 현재 선택을 확정하기 전에 그 안의 실제 콘텐츠 경계로 캡처 사각형 자체를 줄인다.
        /// 편집기의 자동 여백을 나중에 적용하는 것이 아니라, 이 결과가 그대로 캡처 영역이 된다.
        /// </summary>
        private void AutoFitSelection()
        {
            if (_phase != Phase.Adjusting || _selection.IsEmpty) return;

            Int32Rect current = ToPixels(_selection);
            if (current.Width < 8 || current.Height < 8) return;

            Cursor old = Cursor;
            Cursor = Cursors.Wait;
            try
            {
                Int32Rect? target = null;
                string description = "";
                IntPtr targetWindow = IntPtr.Zero;

                var cropped = new CroppedBitmap(_frozen, current);
                cropped.Freeze();
                AutoCropResult? fitted = CropBoundaryAnalysis.Analyze(cropped).MagneticCrop;
                if (fitted is { } found)
                {
                    Int32Rect local = found.Region;
                    var proposed = new Int32Rect(current.X + local.X, current.Y + local.Y,
                                                 local.Width, local.Height);
                    if (!SameRect(proposed, current))
                    {
                        target = proposed;
                        description = found.Description;
                    }
                }

                // 픽셀 경계가 애매하면, 대충 둘러 잡은 선택 안에 거의 온전히 들어온
                // 맨 앞 창을 정확한 창 테두리로 맞춘다. 전체 화면 선택에서도 이 경로가 유용하다.
                if (!target.HasValue && FindContainedWindow(current) is { } window)
                {
                    target = window.Rect;
                    targetWindow = window.Hwnd;
                    description = "창 경계 자동 맞춤";
                }

                if (!target.HasValue)
                {
                    BtnMagnet.Content = "경계 없음";
                    BtnMagnet.ToolTip = "현재 선택 안에서 자동으로 맞출 콘텐츠 경계를 찾지 못했습니다";
                    return;
                }

                _selection = ToDip(target.Value);
                _pickedWindow = targetWindow;
                _phase = Phase.Adjusting;
                BtnMagnet.Content = "맞춤 ✓";
                BtnMagnet.ToolTip = description + " — 이 영역이 그대로 캡처됩니다 (M으로 다시 분석)";
                UpdateVisuals();
            }
            catch (Exception ex)
            {
                BtnMagnet.Content = "맞춤 실패";
                BtnMagnet.ToolTip = "자석 맞춤 실패: " + ex.Message;
            }
            finally { Cursor = old; }
        }

        private WindowCandidate? FindContainedWindow(Int32Rect selection)
        {
            long selectionArea = (long)selection.Width * selection.Height;
            foreach (WindowCandidate window in _windows)
            {
                Int32Rect r = window.Rect;
                int l = Math.Max(selection.X, r.X), t = Math.Max(selection.Y, r.Y);
                int rr = Math.Min(selection.X + selection.Width, r.X + r.Width);
                int bb = Math.Min(selection.Y + selection.Height, r.Y + r.Height);
                if (rr <= l || bb <= t) continue;

                long area = (long)r.Width * r.Height;
                long overlap = (long)(rr - l) * (bb - t);
                if (area < selectionArea * 0.02 || overlap < area * 0.98) continue;
                if (r.X < selection.X - 2 || r.Y < selection.Y - 2 ||
                    r.X + r.Width > selection.X + selection.Width + 2 ||
                    r.Y + r.Height > selection.Y + selection.Height + 2) continue;
                if (SameRect(r, selection)) continue;
                return window; // 열거가 z-order 순서라 첫 후보가 화면에서 제일 앞이다.
            }
            return null;
        }

        private static bool SameRect(Int32Rect a, Int32Rect b)
            => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

        private static Rect Normalize(Point a, Point b) => new(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        private Rect Resize(Rect start, Handle h, Point p)
        {
            double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;

            if (h is Handle.NW or Handle.W or Handle.SW) l = p.X;
            if (h is Handle.NE or Handle.E or Handle.SE) r = p.X;
            if (h is Handle.NW or Handle.N or Handle.NE) t = p.Y;
            if (h is Handle.SW or Handle.S or Handle.SE) b = p.Y;

            double x = Math.Clamp(Math.Min(l, r), 0, Root.ActualWidth);
            double y = Math.Clamp(Math.Min(t, b), 0, Root.ActualHeight);
            double w = Math.Clamp(Math.Abs(r - l), 1, Root.ActualWidth - x);
            double hh = Math.Clamp(Math.Abs(b - t), 1, Root.ActualHeight - y);
            return new Rect(x, y, w, hh);
        }

        private Handle HitTest(Point p)
        {
            if (_selection.IsEmpty || _selection.Width <= 0) return Handle.None;

            Point[] pts = HandlePoints();
            var ids = new[] { Handle.NW, Handle.N, Handle.NE, Handle.E, Handle.SE, Handle.S, Handle.SW, Handle.W };
            for (int i = 0; i < pts.Length; i++)
            {
                if (Math.Abs(p.X - pts[i].X) <= HandleGrab && Math.Abs(p.Y - pts[i].Y) <= HandleGrab)
                    return ids[i];
            }
            return _selection.Contains(p) ? Handle.Inside : Handle.None;
        }

        private Point[] HandlePoints()
        {
            double l = _selection.Left, t = _selection.Top, r = _selection.Right, b = _selection.Bottom;
            double cx = (l + r) / 2, cy = (t + b) / 2;
            return new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy),
                new Point(r, b), new Point(cx, b), new Point(l, b), new Point(l, cy)
            };
        }

        private static Cursor CursorFor(Handle h) => h switch
        {
            Handle.NW or Handle.SE => Cursors.SizeNWSE,
            Handle.NE or Handle.SW => Cursors.SizeNESW,
            Handle.N or Handle.S => Cursors.SizeNS,
            Handle.E or Handle.W => Cursors.SizeWE,
            Handle.Inside => Cursors.SizeAll,
            _ => Cursors.Cross
        };

        private WindowCandidate? FindWindowAt(Point dip)
        {
            int bx = (int)Math.Round(dip.X * PxPerDipX);
            int by = (int)Math.Round(dip.Y * PxPerDipY);

            // z-order 위→아래 순서라 처음 걸리는 게 제일 위에 있는 창이다.
            foreach (WindowCandidate c in _windows)
            {
                Int32Rect r = c.Rect;
                if (bx >= r.X && bx < r.X + r.Width && by >= r.Y && by < r.Y + r.Height)
                    return c;
            }
            return null;
        }

        // ---------------- 확정 ----------------

        private void Finish(OverlayAction action)
        {
            Int32Rect px = ToPixels(_selection);
            if (px.Width < 2 || px.Height < 2) { Cancel(); return; }

            Result = new OverlayResult
            {
                Region = px,
                WindowHandle = _pickedWindow,
                Action = action
            };
            DialogResult = true;
            Close();
        }

        private void Cancel()
        {
            Result = null;
            DialogResult = false;
            Close();
        }

        private void OnActionConfirm(object sender, RoutedEventArgs e) => Finish(OverlayAction.Confirm);
        private void OnActionEdit(object sender, RoutedEventArgs e) => Finish(OverlayAction.Edit);
        private void OnActionCopy(object sender, RoutedEventArgs e) => Finish(OverlayAction.CopyOnly);
        private void OnActionSave(object sender, RoutedEventArgs e) => Finish(OverlayAction.SaveOnly);
        private void OnActionCancel(object sender, RoutedEventArgs e) => Cancel();

        // ---------------- 그리기 ----------------

        private void UpdateVisuals()
        {
            Rect clear;
            bool dashed = false;
            bool showHandles = false;

            if (_phase == Phase.Idle)
            {
                if (_hover.HasValue) { clear = ToDip(_hover.Value.Rect); dashed = true; }
                else
                {
                    Hole.Visibility = Visibility.Collapsed;
                    SelBorder.Visibility = Visibility.Collapsed;
                    SizeTip.Visibility = Visibility.Collapsed;
                    ShowHandles(false);
                    return;
                }
            }
            else
            {
                clear = _selection;
                showHandles = _phase is Phase.Adjusting or Phase.Moving or Phase.Resizing;
            }

            // 선택 영역만 원본 밝기로. 배경은 어둡게 구워 둔 것이라 여기만 다시 그려진다.
            Hole.Visibility = Visibility.Visible;
            Hole.Width = Math.Max(0, clear.Width);
            Hole.Height = Math.Max(0, clear.Height);
            Canvas.SetLeft(Hole, clear.X);
            Canvas.SetTop(Hole, clear.Y);
            _holeBrush.Viewbox = new Rect(clear.X * PxPerDipX, clear.Y * PxPerDipY,
                                          Math.Max(0.001, clear.Width * PxPerDipX),
                                          Math.Max(0.001, clear.Height * PxPerDipY));

            SelBorder.Visibility = Visibility.Visible;
            SelBorder.Width = Math.Max(0, clear.Width);
            SelBorder.Height = Math.Max(0, clear.Height);
            if (dashed != _selDashed)
            {
                SelBorder.StrokeDashArray = dashed ? DashPattern : null;
                _selDashed = dashed;
            }
            Canvas.SetLeft(SelBorder, clear.X);
            Canvas.SetTop(SelBorder, clear.Y);

            Int32Rect px = _phase == Phase.Idle && _hover.HasValue ? _hover.Value.Rect : ToPixels(clear);
            SizeText.Text = px.Width.ToString(CultureInfo.InvariantCulture) + " × " +
                            px.Height.ToString(CultureInfo.InvariantCulture);
            SizeTip.Visibility = Visibility.Visible;

            // UpdateLayout 은 창 전체 레이아웃을 강제하므로 이 요소만 잰다.
            SizeTip.Measure(Unbounded);

            // 크기 표시도 막대와 같은 이유로 가상 화면이 아니라 모니터 안에 가둔다.
            Rect tipMon = MonitorRectAt(new Point(clear.X, clear.Y));
            double tipW = SizeTip.DesiredSize.Width, tipH = SizeTip.DesiredSize.Height;

            double tipTop = clear.Y - tipH - 6;
            if (tipTop < tipMon.Top) tipTop = clear.Y + 6;

            (double tipLeft, tipTop) = ViewMath.ClampInto(tipMon.Left, tipMon.Top, tipMon.Width,
                                                          tipMon.Height, clear.X, tipTop, tipW, tipH);
            Canvas.SetLeft(SizeTip, tipLeft);
            Canvas.SetTop(SizeTip, tipTop);

            ShowHandles(showHandles);
            PlaceActionBar(_phase == Phase.Adjusting, clear);
        }

        private void ShowHandles(bool show)
        {
            if (!show)
            {
                foreach (Rectangle r in _handles) r.Visibility = Visibility.Collapsed;
                return;
            }

            Point[] pts = HandlePoints();
            for (int i = 0; i < _handles.Length; i++)
            {
                _handles[i].Visibility = Visibility.Visible;
                Canvas.SetLeft(_handles[i], pts[i].X - HandleSize / 2);
                Canvas.SetTop(_handles[i], pts[i].Y - HandleSize / 2);
            }
        }

        /// <summary>
        /// 어떤 지점이 올라앉은 <b>모니터</b>의 사각형(오버레이 DIP 좌표).
        ///
        /// 오버레이는 가상 화면 전체를 덮는다. 그런데 모니터 크기가 서로 다르면 가상 화면에는
        /// <b>어느 모니터에도 속하지 않는 빈 구역</b>이 생긴다(2560×1440 옆에 1920×1080 을
        /// 붙이면 오른쪽 아래 360줄이 그렇다). 가상 화면만 보고 자리를 잡으면 그 빈 구역에
        /// 놓여서 아무 데도 안 보인다. 그래서 "가상 화면 안" 이 아니라 "모니터 안" 으로 가둔다.
        /// </summary>
        private Rect MonitorRectAt(Point dip)
        {
            if (_monitors.Length == 0) return new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);

            int x = _virt.X + (int)Math.Round(dip.X * PxPerDipX);
            int y = _virt.Y + (int)Math.Round(dip.Y * PxPerDipY);

            // 마우스가 움직일 때마다 오므로 Screen.FromPoint(모니터 열거)를 매번 부르지 않고,
            // 열어 둘 때 받아 둔 목록에서 찾는다. 어느 모니터에도 안 속하는 빈 구역이면
            // 제일 가까운 모니터로 (FromPoint 와 같은 결과).
            System.Drawing.Rectangle b = _monitors[0];
            long best = long.MaxValue;
            foreach (System.Drawing.Rectangle m in _monitors)
            {
                long dx = x < m.Left ? m.Left - x : x >= m.Right ? x - m.Right + 1 : 0;
                long dy = y < m.Top ? m.Top - y : y >= m.Bottom ? y - m.Bottom + 1 : 0;
                long d = dx * dx + dy * dy;
                if (d == 0) { b = m; break; }
                if (d < best) { best = d; b = m; }
            }

            return new Rect((b.X - _virt.X) / PxPerDipX, (b.Y - _virt.Y) / PxPerDipY,
                            b.Width / PxPerDipX, b.Height / PxPerDipY);
        }

        private void PlaceActionBar(bool show, Rect sel)
        {
            if (!show) { ActionBar.Visibility = Visibility.Collapsed; return; }

            ActionBar.Visibility = Visibility.Visible;
            ActionBar.Measure(Unbounded);

            double w = ActionBar.DesiredSize.Width, h = ActionBar.DesiredSize.Height;

            // 선택 영역의 아래쪽 모서리가 어느 모니터에 있는지로 잡는다.
            // 영역이 모니터에 걸쳐 있을 때 막대가 딸려 가면 안 된다.
            Rect mon = MonitorRectAt(new Point(Math.Clamp(sel.Right - w / 2, sel.Left, sel.Right),
                                               Math.Max(sel.Top, sel.Bottom - 1)));

            double left = sel.Right - w;
            double top = sel.Bottom + 10;

            // 아래가 좁으면 위로, 위도 좁으면 선택 영역 안 아래쪽에 얹는다.
            if (top + h > mon.Bottom) top = sel.Top - h - 10;
            if (top < mon.Top) top = sel.Bottom - h - 6;

            (left, top) = ViewMath.ClampInto(mon.Left, mon.Top, mon.Width, mon.Height, left, top, w, h);

            Canvas.SetLeft(ActionBar, left);
            Canvas.SetTop(ActionBar, top);
        }

        private void UpdateLoupe(Point dip)
        {
            if (_frozen.PixelWidth < LoupePixels || _frozen.PixelHeight < LoupePixels) return;

            // 액션 바 위에서는 돋보기가 거슬리므로 숨긴다.
            if (ActionBar.Visibility == Visibility.Visible)
            {
                var bar = new Rect(Canvas.GetLeft(ActionBar), Canvas.GetTop(ActionBar),
                                   ActionBar.ActualWidth, ActionBar.ActualHeight);
                if (bar.Contains(dip)) { Loupe.Visibility = Visibility.Collapsed; return; }
            }

            int px = Math.Clamp((int)(dip.X * PxPerDipX), 0, _frozen.PixelWidth - 1);
            int py = Math.Clamp((int)(dip.Y * PxPerDipY), 0, _frozen.PixelHeight - 1);

            int half = LoupePixels / 2;
            int ox = Math.Clamp(px - half, 0, _frozen.PixelWidth - LoupePixels);
            int oy = Math.Clamp(py - half, 0, _frozen.PixelHeight - LoupePixels);

            // 매번 CroppedBitmap 을 두 개 만들지 않고, 한 번 복사해서 재사용 비트맵에 쓴다.
            // 커서 아래 색도 같은 버퍼에서 꺼낸다.
            try
            {
                _frozen.CopyPixels(new Int32Rect(ox, oy, LoupePixels, LoupePixels),
                                   _loupeBuf, LoupePixels * 4, 0);
            }
            catch { return; }

            _loupeBitmap ??= new WriteableBitmap(LoupePixels, LoupePixels, 96, 96, PixelFormats.Bgr32, null);
            _loupeBitmap.WritePixels(new Int32Rect(0, 0, LoupePixels, LoupePixels),
                                     _loupeBuf, LoupePixels * 4, 0);
            if (!ReferenceEquals(LoupeImage.Source, _loupeBitmap)) LoupeImage.Source = _loupeBitmap;

            int ci = ((py - oy) * LoupePixels + (px - ox)) * 4;
            var c = Color.FromRgb(_loupeBuf[ci + 2], _loupeBuf[ci + 1], _loupeBuf[ci]);   // Bgr32
            _loupeBrush.Color = c;
            LoupeText.Text = string.Format(CultureInfo.InvariantCulture, "{0},{1}  #{2:X2}{3:X2}{4:X2}",
                                           px + _virt.X, py + _virt.Y, c.R, c.G, c.B);

            Loupe.Visibility = Visibility.Visible;

            // 돋보기 크기는 사실상 일정하다. 창 전체 레이아웃을 강제하는 대신 처음 한 번만 잰다.
            if (_loupeSize.Width <= 0)
            {
                Loupe.UpdateLayout();
                _loupeSize = new Size(Loupe.ActualWidth, Loupe.ActualHeight);
            }

            double w = _loupeSize.Width, h = _loupeSize.Height;
            double left = dip.X + 22, top = dip.Y + 22;
            if (left + w > Root.ActualWidth) left = dip.X - w - 22;
            if (top + h > Root.ActualHeight) top = dip.Y - h - 22;
            Canvas.SetLeft(Loupe, Math.Max(0, left));
            Canvas.SetTop(Loupe, Math.Max(0, top));
        }

    }
}
