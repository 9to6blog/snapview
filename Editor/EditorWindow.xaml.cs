using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;
using SnapView.Native;

namespace SnapView.Editor
{
    /// <summary>
    /// 캡처한 그림에 화살표·도형·글자·모자이크를 얹고, 자르고, 뒤집는 편집기.
    ///
    /// 모든 주석은 원본 이미지 픽셀 좌표로 들고 있다가 마지막에 원본 해상도로
    /// 한 장으로 합친다. 창을 줄여서 편집해도 결과물 화질이 떨어지지 않는다.
    ///
    /// 도형을 그리면 바로 확정되지 않고 <b>조절점이 달린 채로 남는다</b>.
    /// 끌어서 위치·크기를 맞춘 뒤 Enter 를 눌러야 확정된다.
    /// </summary>
    public partial class EditorWindow : Window
    {
        private static readonly string[] Palette =
        {
            "#FFE23B3B", "#FFFF8A00", "#FFFFD400", "#FF37C871",
            "#FF3DA9FC", "#FFB56BFF", "#FF101014", "#FFFFFFFF"
        };

        internal const string DefaultFontFamily = "Malgun Gothic";

        private const double MinZoom = 0.05;
        private const double MaxZoom = 8.0;
        private const double ZoomStep = 1.25;

        private readonly Settings _settings;
        private readonly UndoStack _undoStack = new();

        // 닫기 확인·제목의 * 표시·임시 저장이 보는 "바뀌었는가".
        private bool _dirty;
        private long _changeStamp;
        private long _savedStamp;
        private bool _completed;
        private string? _projectPath;

        // 몇 초마다 몰래 저장해 두는 복구 파일. 앱이 죽어도 다음에 이어서 할 수 있다.
        private readonly RecoveryStore _recovery = new();
        private readonly System.Windows.Threading.DispatcherTimer _autosave = new()
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        private bool _autosaving;

        // 선택한 주석의 값을 리본에 되비칠 때 되받아치기를 끊는 잠금.
        private bool _syncingUi;

        // 새 스타일 값들(2026-09 개선): 촉 · 점선 무늬 · 글자 정렬 · 가리개 모양 · 돋보기 배율 · 자르기 비율
        private ArrowHead _head = ArrowHead.Filled;
        private DashPattern _dashPattern = DashPattern.Dash;
        private TextAlign _textAlign = TextAlign.Left;
        private ToolKind _maskShape = ToolKind.Rectangle;
        private double _magnifierZoom = 2;
        private double _cropAspect;   // 0 = 자유
        private readonly List<Color> _recentColors = new();

        // 화면 이동(가운데 버튼 · Space+끌기)
        private bool _panning;
        private Point _panStart;
        private Vector _panOffset;

        // 여럿을 골랐을 때 무리 전체 크기 조절
        private bool _groupResizing;
        private int _groupHandle = -1;

        private BitmapSource _image;
        private ToolKind _tool = ToolKind.Arrow;
        private Color _color = Colors.Red;
        private double _thickness = 3;
        private double _opacity = 1.0;
        private bool _filled;
        private int _maskStrength = 12;
        private ToolKind _toolBeforePicker = ToolKind.Arrow;
        private bool _erasing;
        private EraseAnnotation? _eraseStroke;
        private RadioButton? _customSwatch;
        private Popup? _colorPopup;
        private Popup? _shapePopup;
        private bool _syncingLayers;
        private bool _bold = true;
        private bool _italic;
        private WriteableBitmap? _eraseBuffer;   // 픽셀 지우개가 쓰는 동안의 그림
        private Rect _region = Rect.Empty;       // 영역 선택 결과(이미지 픽셀)
        private bool _draggingRegion;
        private Point _regionStart;
        private string _fontFamily = DefaultFontFamily;
        private int _counter = 1;
        private double _zoom = 1;
        private bool _fitToWindow = true;

        private bool _drawing;
        private Point _startImage;
        private int _handleIndex = -1;
        private bool _movingActive;
        private Annotation? _draggingSelected;
        private Annotation? _resizingSelected;   // 확정한 주석의 조절점을 끄는 중
        private Point _dragLast;
        private TextAnnotation? _editingText;

        // 선택 도구의 올가미(빈 곳을 끌어 여럿 담기)
        private bool _banding;
        private bool _bandAdditive;
        private Point _bandStart;

        // 끌기 스냅(스마트 가이드): 시작 시점의 커서와 무리 전체 테두리를 기억해 두고
        // 매번 절대 위치로 계산한다 — 스냅으로 붙였다 떼었다 해도 커서와 어긋나지 않는다.
        private Point _dragAnchor;
        private Rect _dragUnionStart;

        private bool _shadow;
        private bool _dashed;
        private bool _bothArrows;
        private bool _gradient;
        private BlendMode _blend = BlendMode.Normal;

        // 자동 선택(마술봉)이 고른 영역
        private bool[]? _wandMask;
        private int _wandCount;
        private bool _textHalo = true;
        private bool _textBg;

        // 주석 복사(Ctrl+C) 결과. 시스템 클립보드에는 표식 글만 남긴다 —
        // 밖에서 그림을 복사해 오면 표식이 지워져 Ctrl+V 가 자연히 그림 붙여넣기로 돌아간다.
        private List<Annotation>? _annClipboard;
        private const string AnnClipboardMarker = "SnapView.Annotations";

        // 필터 미리보기: 거는 도중에는 원본을 들고 있다가 취소하면 되돌린다.
        private ImageEffects.ImageFilter _pendingFilter;
        private BitmapSource? _filterBase;
        private Popup? _filterPopup;

        /// <summary>"완료"를 눌렀을 때 합쳐진 결과.</summary>
        internal event Action<BitmapSource>? Completed;

        /// <summary>"글자" 를 눌렀을 때. 컨트롤러가 읽어서 창을 띄운다.</summary>

        internal EditorWindow(BitmapSource image, Settings settings)
        {
            _settings = settings;
            _image = image;

            InitializeComponent();

            Canvas1.Source = image;
            _thickness = Math.Clamp(settings.AnnotationThickness, 1, 16);
            _color = ParseColor(settings.AnnotationColor);

            _opacity = Math.Clamp(settings.AnnotationOpacity, 0.1, 1.0);
            _filled = settings.AnnotationFilled;
            _fontFamily = string.IsNullOrWhiteSpace(settings.AnnotationFontFamily)
                ? DefaultFontFamily : settings.AnnotationFontFamily;

            _maskStrength = Math.Clamp(settings.MosaicBlockSize, 2, 64);

            SetUpSteppers(Math.Clamp(settings.AnnotationFontSize, 10, 80));

            foreach (BlendMode m in Enum.GetValues<BlendMode>())
                BlendBox.Items.Add(BlendComposite.NameOf(m));
            BlendBox.SelectedIndex = 0;
            TbFill.IsChecked = _filled;
            TbBold.IsChecked = _bold;
            TbItalic.IsChecked = _italic;

            BuildSwatches();
            BuildFontList();
            BuildShapeGallery();
            BuildMaskShapeList();
            HeadBox.SelectedIndex = 0;
            DashPatternBox.SelectedIndex = 0;
            MagnifierZoomBox.SelectedIndex = 1;
            CropAspectBox.SelectedIndex = 0;
            SetAlignButtons();
            SelectTool(LastTool());

            Stage.MouseLeftButtonDown += OnStageDown;
            Stage.MouseMove += OnStageMove;
            Stage.MouseLeftButtonUp += OnStageUp;
            Stage.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) BeginPan(e); };
            Stage.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle && _panning) EndPan(); };
            Stage.MouseLeave += (_, _) =>
            {
                if (Canvas1.Hover == null) return;
                Canvas1.Hover = null;
                Canvas1.InvalidateVisual();
            };
            Scroller.SizeChanged += (_, _) => { if (_fitToWindow) Relayout(); };

            TextEntry.PreviewKeyDown += OnTextEntryKey;
            TextEntry.LostKeyboardFocus += (_, _) => CommitText();

            Loaded += (_, _) => { Relayout(); UpdateStatus(); OfferRecovery(); };
            InitializeSession();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private static Color ParseColor(string s)
        {
            try { return (Color)ColorConverter.ConvertFromString(s); }
            catch { return Colors.Red; }
        }

        private void BuildSwatches()
        {
            foreach (string hex in Palette)
            {
                Color c = ParseColor(hex);
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush(c),
                    GroupName = "swatch",
                    Tag = c,
                    IsChecked = c == _color,
                    ToolTip = hex
                };
                rb.Checked += (s, _) =>
                {
                    if (_syncingUi) return;
                    _color = (Color)((RadioButton)s!).Tag;
                    ApplyStyleToEditable(a => a.Color = _color);
                };
                SwatchPanel.Children.Add(rb);
            }
            // 팔레트 밖의 색을 담아 두는 칸. 스포이드로 집거나 직접 고른 색이 여기 앉는다.
            _customSwatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(_color),
                GroupName = "swatch",
                Tag = _color,
                ToolTip = "직접 고른 색"
            };
            _customSwatch.Checked += (sender, _) =>
            {
                if (_syncingUi) return;
                _color = (Color)((RadioButton)sender!).Tag;
                ApplyStyleToEditable(a => a.Color = _color);
            };
            SwatchPanel.Children.Add(_customSwatch);

            // 색상판 단추. 무지개 원을 직접 그린다 — 그림 문자는 PC 마다 다르게 나온다.
            var wheel = new System.Windows.Shapes.Ellipse
            {
                Width = 15,
                Height = 15,
                Stroke = (Brush)FindResource("Divider"),
                StrokeThickness = 1,
                Fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0xE2, 0x3B, 0x3B), 0.00),
                    new GradientStop(Color.FromRgb(0xE8, 0x8B, 0x2E), 0.20),
                    new GradientStop(Color.FromRgb(0xE6, 0xC7, 0x2E), 0.38),
                    new GradientStop(Color.FromRgb(0x3F, 0xB9, 0x50), 0.56),
                    new GradientStop(Color.FromRgb(0x3D, 0x8B, 0xE8), 0.76),
                    new GradientStop(Color.FromRgb(0x9B, 0x59, 0xD6), 1.00)
                }, 45)
            };

            var more = new Button
            {
                Style = (Style)FindResource("ToolButton"),
                Content = wheel,
                Padding = new Thickness(4),
                ToolTip = "색 직접 고르기"
            };
            more.Click += OnPickCustomColor;
            SwatchPanel.Children.Add(more);

            SetColor(_color);
        }

        /// <summary>
        /// 지금 색을 바꾸고 색칸 표시도 맞춘다.
        /// 팔레트에 없는 색이면 "직접 고른 색" 칸에 앉힌다.
        /// </summary>
        private void SetColor(Color c)
        {
            _color = c;
            ApplyStyleToEditable(a => a.Color = _color);
            if (!ShowColorInSwatches(c)) PushRecentColor(c);
        }

        /// <summary>색칸 표시만 맞춘다. 팔레트에 있으면 그 칸, 없으면 "직접 고른 색" 칸. 팔레트에 있었으면 true.</summary>
        private bool ShowColorInSwatches(Color c)
        {
            bool was = _syncingUi;
            _syncingUi = true;
            try
            {
                foreach (object child in SwatchPanel.Children)
                {
                    if (child is not RadioButton rb || rb == _customSwatch) continue;
                    if (rb.Tag is Color pc && pc == c) { rb.IsChecked = true; return true; }
                }

                if (_customSwatch == null) return false;
                _customSwatch.Tag = c;
                _customSwatch.Background = new SolidColorBrush(c);
                _customSwatch.IsChecked = true;
                return false;
            }
            finally { _syncingUi = was; }
        }

        /// <summary>색상판을 띄운다. 고르는 동안 화면에 바로 반영된다.</summary>
        private void OnPickCustomColor(object sender, RoutedEventArgs e)
        {
            if (_colorPopup != null) { _colorPopup.IsOpen = false; _colorPopup = null; }

            var picker = new ColorPicker { Color = _color, Margin = new Thickness(10) };
            picker.ColorChanged += SetColor;

            var close = new Button
            {
                Style = (Style)FindResource("ToolButton"),
                Content = "닫기",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 10, 10)
            };
            close.Click += (_, _) => { if (_colorPopup != null) _colorPopup.IsOpen = false; };

            // 색상판에서 바로 스포이드로. 화면의 색을 집어 오고 싶은 경우가 잦다.
            var pick = new Button
            {
                Style = (Style)FindResource("ToolButton"),
                Content = "그림에서 집기 (I)",
                Margin = new Thickness(10, 0, 0, 10)
            };
            pick.Click += (_, _) =>
            {
                if (_colorPopup != null) _colorPopup.IsOpen = false;
                if (_tool != ToolKind.Picker) _toolBeforePicker = _tool;
                SelectTool(ToolKind.Picker);
            };
            var buttons = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(pick, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            buttons.Children.Add(pick);
            buttons.Children.Add(close);

            var body = new StackPanel();
            body.Children.Add(picker);
            body.Children.Add(buttons);

            _colorPopup = new Popup
            {
                PlacementTarget = (UIElement)sender,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = (Brush)FindResource("BgChrome"),
                    BorderBrush = (Brush)FindResource("Divider"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = body
                }
            };
            _colorPopup.Closed += (_, _) => _colorPopup = null;
            _colorPopup.IsOpen = true;
        }

        /// <summary>
        /// 숫자 칸들을 준비한다. 슬라이더 대신 쓰는 이유는 좁은 슬라이더로는
        /// 원하는 값을 정확히 짚기가 어렵기 때문이다.
        /// </summary>
        private void SetUpSteppers(double fontSize)
        {
            ThicknessBox.Minimum = 1;
            ThicknessBox.Maximum = 64;
            ThicknessBox.Step = 1;
            ThicknessBox.SetSilently(_thickness);
            ThicknessBox.ValueChanged += OnThicknessChanged;

            OpacityBox.Minimum = 5;
            OpacityBox.Maximum = 100;
            OpacityBox.Step = 5;
            OpacityBox.Suffix = "%";
            OpacityBox.SetSilently(Math.Round(_opacity * 100));
            OpacityBox.ValueChanged += OnOpacityChanged;

            FontSizeBox.Minimum = 8;
            FontSizeBox.Maximum = 200;
            FontSizeBox.Step = 1;
            FontSizeBox.SetSilently(fontSize);
            FontSizeBox.ValueChanged += OnFontSizeChanged;

            MaskStrengthBox.Minimum = 2;
            MaskStrengthBox.Maximum = 64;
            MaskStrengthBox.Step = 1;
            MaskStrengthBox.SetSilently(_maskStrength);
            MaskStrengthBox.ValueChanged += OnMaskStrengthChanged;

            NextNumberBox.Minimum = 1;
            NextNumberBox.Maximum = 999;
            NextNumberBox.Step = 1;
            NextNumberBox.SetSilently(_counter);
            NextNumberBox.ValueChanged += OnNextNumberChanged;

            foreach (NumberStepper box in new[] { CropWidthBox, CropHeightBox })
            {
                box.Minimum = 8;
                box.Maximum = 20000;
                box.Step = 1;
                box.Suffix = "px";
            }
            CropWidthBox.ValueChanged += v => OnCropSizeChanged(v, null);
            CropHeightBox.ValueChanged += v => OnCropSizeChanged(null, v);
        }

        /// <summary>
        /// 리본에는 자주 쓰는 도형만 늘어놓고, 나머지는 "더보기" 안에 넣는다.
        /// 전부 펼치면 리본이 넘쳐서 정작 어디에 뭐가 있는지 못 찾는다.
        /// </summary>
        private void BuildShapeGallery()
        {
            foreach (ToolKind kind in ShapeGeometry.Common)
                ShapeGallery.Children.Add(MakeShapeButton(kind));

            var more = new Button
            {
                Style = (Style)FindResource("ToolButton"),
                Content = MoreGlyph(),
                Padding = new Thickness(6, 4, 6, 4),
                ToolTip = $"도형 더보기 (전체 {ShapeGeometry.All.Length}개)"
            };
            more.Click += OnMoreShapes;
            ShapeGallery.Children.Add(more);
        }

        private ToggleButton MakeShapeButton(ToolKind kind)
        {
            var tb = new ToggleButton
            {
                Style = (Style)FindResource("ToolToggle"),
                Content = ShapeIcon(kind),
                Tag = kind.ToString(),
                ToolTip = ShapeGeometry.NameOf(kind),
                MinWidth = 30,
                Padding = new Thickness(4)
            };
            tb.Click += OnToolClick;
            return tb;
        }

        /// <summary>"더보기" 단추의 점 세 개.</summary>
        private static UIElement MoreGlyph()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < 3; i++)
            {
                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = (Brush)Application.Current.FindResource("Fg")
                });
            }
            return row;
        }

        /// <summary>도형 전체를 격자로 펼친다. 고르면 곧바로 그 도구가 된다.</summary>
        private void OnMoreShapes(object sender, RoutedEventArgs e)
        {
            if (_shapePopup != null) { _shapePopup.IsOpen = false; _shapePopup = null; }

            var grid = new WrapPanel { Width = 8 * 34, Margin = new Thickness(6) };

            foreach (ToolKind kind in ShapeGeometry.All)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("ToolButton"),
                    Content = ShapeIcon(kind),
                    Padding = new Thickness(5),
                    Width = 32,
                    Height = 32,
                    ToolTip = ShapeGeometry.NameOf(kind)
                };
                ToolKind picked = kind;
                b.Click += (_, _) =>
                {
                    SelectTool(picked);
                    if (_shapePopup != null) _shapePopup.IsOpen = false;
                };
                grid.Children.Add(b);
            }

            _shapePopup = new Popup
            {
                PlacementTarget = (UIElement)sender,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = (Brush)FindResource("BgChrome"),
                    BorderBrush = (Brush)FindResource("Divider"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = grid
                }
            };
            _shapePopup.Closed += (_, _) => _shapePopup = null;
            _shapePopup.IsOpen = true;
        }

        /// <summary>
        /// 도형 단추의 그림은 <b>그 도형을 실제로 그려서</b> 만든다.
        /// 비슷하게 생긴 특수문자를 빌려 쓰면 글꼴에 따라 없거나 네모로 나온다.
        /// </summary>
        private static System.Windows.Shapes.Path ShapeIcon(ToolKind kind)
        {
            Geometry geo = kind switch
            {
                ToolKind.Rectangle => new RectangleGeometry(new Rect(0, 1, 14, 12), 1.5, 1.5),
                ToolKind.Ellipse => new EllipseGeometry(new Point(7, 7), 7, 6),
                _ => ShapeGeometry.Build(kind, new Rect(0, 0, 14, 14)) ?? Geometry.Empty
            };

            return new System.Windows.Shapes.Path
            {
                Data = geo,
                Stretch = Stretch.Uniform,
                Width = 15,
                Height = 15,
                Stroke = (Brush)Application.Current.FindResource("Fg"),
                StrokeThickness = 1.3,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true
            };
        }

        // 시스템 글꼴 열거는 첫 회가 특히 느려서(글꼴 캐시 서비스 왕복) 편집기를 열 때마다
        // 하면 캡처 → 편집기 흐름이 그만큼 늦게 뜬다. 글꼴은 실행 중에 바뀌는 일이 드무니
        // 한 번 만든 목록을 프로세스가 사는 동안 재사용한다. FontFamily 는 불변이라 안전하다.
        private static volatile List<FontFamily>? _fontListCache;

        internal static List<FontFamily> GetFontList() => _fontListCache ??=
            Fonts.SystemFontFamilies
                 .OrderBy(f => f.Source, StringComparer.CurrentCultureIgnoreCase)
                 .ToList();

        /// <summary>시스템에 깔린 글꼴을 이름순으로 채운다. 목록에는 그 글꼴로 보여 준다.</summary>
        private void BuildFontList()
        {
            List<FontFamily> families = GetFontList();

            FontCombo.ItemsSource = families;
            FontCombo.SelectedItem =
                families.FirstOrDefault(f => string.Equals(f.Source, _fontFamily,
                                                           StringComparison.OrdinalIgnoreCase))
                ?? families.FirstOrDefault(f => string.Equals(f.Source, DefaultFontFamily,
                                                              StringComparison.OrdinalIgnoreCase))
                ?? families.FirstOrDefault();
        }

        /// <summary>
        /// 지금 만지고 있는 주석(확정 전 또는 선택된 것들)에 스타일을 반영한다.
        /// 골라 둔 것을 바꿀 때는 실행취소에 남긴다(연타는 한 칸으로 묶인다).
        /// </summary>
        private void ApplyStyleToEditable(Action<Annotation> apply, string undoKey = "style")
        {
            if (Canvas1 == null || _syncingUi) return;

            if (Canvas1.Active is { } active)
            {
                apply(active);
                Canvas1.InvalidateVisual();
                return;
            }

            if (Canvas1.SelectedMany.Count == 0) return;
            PushUndo(undoKey);
            foreach (Annotation s in Canvas1.SelectedMany)
                if (!s.Locked) apply(s);
            Canvas1.InvalidateVisual();
        }

        // ================================================= 선택 (하나 · 여럿)

        /// <summary>선택을 이것 하나로 바꾼다. null 이면 전부 푼다.</summary>
        private void SetSelection(Annotation? primary)
        {
            Canvas1.SelectedMany.Clear();
            Canvas1.Selected = primary;
            if (primary != null) Canvas1.SelectedMany.Add(primary);
        }

        /// <summary>Shift+클릭: 선택에 넣었다 뺐다 한다.</summary>
        private void ToggleSelection(Annotation a)
        {
            if (Canvas1.SelectedMany.Remove(a))
            {
                if (ReferenceEquals(Canvas1.Selected, a))
                    Canvas1.Selected = Canvas1.SelectedMany.Count > 0 ? Canvas1.SelectedMany[^1] : null;
            }
            else
            {
                Canvas1.SelectedMany.Add(a);
                Canvas1.Selected = a;
            }
        }

        // ================================================= 배치 · 배율

        private void Relayout()
        {
            if (Canvas1.Source == null || Canvas1.ImageWidth <= 0) return;

            if (_fitToWindow)
            {
                double availW = Math.Max(1, Scroller.ViewportWidth - 24);
                double availH = Math.Max(1, Scroller.ViewportHeight - 24);
                if (availW <= 1 || availH <= 1)
                {
                    availW = Math.Max(1, Scroller.ActualWidth - 24);
                    availH = Math.Max(1, Scroller.ActualHeight - 24);
                }
                // 작은 그림은 키우고 큰 그림은 줄인다. 예전엔 100% 를 못 넘겨 아이콘 캡처가 우표만 했다.
                _zoom = EditorMath.FitZoom(Canvas1.ImageWidth, Canvas1.ImageHeight, availW, availH, MaxZoom);
            }

            Canvas1.Scale = _zoom;
            Canvas1.Width = Canvas1.ImageWidth * _zoom;
            Canvas1.Height = Canvas1.ImageHeight * _zoom;

            // 200% 부터는 픽셀을 또렷하게. 보간하면 확대해도 흐릿해서 픽셀 단위 작업이 안 된다.
            RenderOptions.SetBitmapScalingMode(Canvas1,
                _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            if (_editingText != null) StyleTextEntry(_editingText);
            UpdateStatus();
        }

        /// <summary>배율을 바꾼다. <paramref name="anchor"/>(뷰포트 좌표) 아래의 그림 점이 그 자리에 남는다.</summary>
        private void SetZoom(double zoom, Point? anchor = null)
        {
            double old = _zoom;
            _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            _fitToWindow = false;
            Relayout();

            if (!anchor.HasValue || Math.Abs(old - _zoom) < 1e-9) return;

            // 그림은 Holder 여백(12) 안쪽에 놓이므로 그만큼 뺀 자리를 기준점으로 삼는다.
            var offset = new Vector(Scroller.HorizontalOffset, Scroller.VerticalOffset);
            var cursor = new Point(anchor.Value.X - 12, anchor.Value.Y - 12);
            Vector want = EditorMath.AnchoredOffset(old, _zoom, cursor, offset);
            Scroller.UpdateLayout();
            Scroller.ScrollToHorizontalOffset(Math.Max(0, want.X));
            Scroller.ScrollToVerticalOffset(Math.Max(0, want.Y));
        }

        private Point ViewportCenter() => new(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);

        private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_zoom * ZoomStep, ViewportCenter());
        private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_zoom / ZoomStep, ViewportCenter());

        private void OnZoomFit(object sender, RoutedEventArgs e)
        {
            _fitToWindow = true;
            Relayout();
        }

        private void OnStageWheel(object sender, MouseWheelEventArgs e)
        {
            // 편집기에서는 그냥 휠은 스크롤(기본 동작), Ctrl+휠 이 확대·축소.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            e.Handled = true;
            SetZoom(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), e.GetPosition(Scroller));
        }

        private void UpdateStatus()
        {
            StInfo.Text = string.Format(CultureInfo.InvariantCulture, "{0} × {1}",
                (int)Canvas1.ImageWidth, (int)Canvas1.ImageHeight);

            int shown = Canvas1.Items.Count(a => a is not EraseAnnotation);

            StHint.Text = Canvas1.Active != null
                ? (Canvas1.Active is CropAnnotation
                    ? "자를 영역을 맞춘 뒤 Enter 또는 안쪽 더블클릭 (ESC 취소)"
                    : "끌어서 위치·크기를 맞춘 뒤 Enter 로 확정 (ESC 취소)")
                : Canvas1.SelectedMany.Count > 1
                    ? $"주석 {Canvas1.SelectedMany.Count}개 선택 — 함께 끌어 옮기고 모서리로 함께 크기 조절 · Ctrl+D 복제 · Delete 삭제"
                    : shown == 0
                        ? "도구를 고르고 그림 위에 끌어 보세요 (F1 단축키)"
                        : $"주석 {shown}개";

            Annotation? sized = Canvas1.Active ?? (Canvas1.SelectedMany.Count == 1 ? Canvas1.Selected : null);
            StSize.Text = sized != null && !sized.Bounds.IsEmpty
                ? $"{(int)Math.Round(sized.Bounds.Width)} × {(int)Math.Round(sized.Bounds.Height)}" : "";

            RefreshLayers();
            SyncUiFromEditable();
            ApplyPanelRules();

            BtnZoom.Content = Math.Round(_zoom * 100).ToString(CultureInfo.InvariantCulture) + "%";
            BtnUndo.IsEnabled = _undoStack.CanUndo;
            BtnRedo.IsEnabled = _undoStack.CanRedo;

            LayerTitle.Text = shown == 0 ? "레이어" : $"레이어 ({shown})";
            LayerEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 3줄(세부 설정)의 보임과 2줄 묶음의 활성을 도구와 고른 주석에 맞춘다.
        /// 예전엔 도구만 봐서 선택 도구로 글자를 골라도 글꼴을 못 바꿨다.
        /// </summary>
        private void ApplyPanelRules()
        {
            IEnumerable<Annotation> editable = Canvas1.Active != null
                ? new[] { Canvas1.Active } : Canvas1.SelectedMany;
            EditorPanel p = EditorRules.Panels(_tool, editable);

            static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            FontPanel.Visibility = V(p.HasFlag(EditorPanel.Font));
            MaskPanel.Visibility = V(p.HasFlag(EditorPanel.Mask) || p.HasFlag(EditorPanel.MaskShape));
            MaskStrengthBox.Visibility = V(p.HasFlag(EditorPanel.Mask));
            MaskLabel.Visibility = MaskStrengthBox.Visibility;
            MaskShapePanel.Visibility = V(p.HasFlag(EditorPanel.MaskShape));
            MaskLabel.Text = _tool == ToolKind.Wand ? "색 허용치" : "가리기 세기";
            MagnifierPanel.Visibility = V(p.HasFlag(EditorPanel.Magnifier));
            CounterPanel.Visibility = V(p.HasFlag(EditorPanel.Counter));
            CropPanel.Visibility = V(p.HasFlag(EditorPanel.Crop));
            RegionPanel.Visibility = V(p.HasFlag(EditorPanel.Region));
            Row3Hint.Visibility = V(p == EditorPanel.None);

            // 2줄 가운데는 자리가 흔들리면 눈이 피곤하니 흐리게만 하고, 줄 끝의 묶음은 접는다.
            LineGroup.IsEnabled = p.HasFlag(EditorPanel.Line) || p.HasFlag(EditorPanel.Font);
            FillGroup.Visibility = V(p.HasFlag(EditorPanel.Fill));
            ArrowGroup.Visibility = V(p.HasFlag(EditorPanel.ArrowHead));
        }

        // ================================================= 도구 선택

        private void OnToolClick(object sender, RoutedEventArgs e)
        {
            var tb = (ToggleButton)sender;
            if (Enum.TryParse(tb.Tag?.ToString(), out ToolKind kind)) SelectTool(kind);
        }

        private void SelectTool(ToolKind kind)
        {
            CommitText();
            ConfirmActive();          // 도구를 바꾸면 만들던 것을 확정한다

            // 자동 선택 영역은 마술봉과 함께 산다. 다른 도구로 가면 치운다.
            if (kind != ToolKind.Wand && _wandMask != null)
            {
                WandClear();
                Canvas1.InvalidateVisual();
            }

            // 스포이드는 한 번 쓰고 원래 도구로 돌아간다. 그래서 직전 것을 기억해 둔다.
            if (kind == ToolKind.Picker && _tool != ToolKind.Picker) _toolBeforePicker = _tool;

            _tool = kind;

            // "더보기" 에서 고른 도형은 리본에 단추가 없다. 그러면 아무것도 안 눌린 것처럼
            // 보여서 지금 무슨 도구인지 알 수가 없다. 그 도형 단추를 앞에 끼워 넣는다.
            if (ShapeGeometry.IsBoxShape(kind) &&
                !ShapeGallery.Children.OfType<ToggleButton>().Any(t => t.Tag?.ToString() == kind.ToString()))
            {
                ShapeGallery.Children.Insert(0, MakeShapeButton(kind));
            }

            foreach (ToggleButton tb in ToolButtons())
                tb.IsChecked = tb.Tag?.ToString() == kind.ToString();

            if (kind != ToolKind.Select) SetSelection(null);
            Stage.Cursor = ToolCursor();
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private Cursor ToolCursor() => _tool switch
        {
            ToolKind.Select => Cursors.Arrow,
            ToolKind.Text => Cursors.IBeam,
            ToolKind.Picker => Cursors.UpArrow,
            ToolKind.Eraser => Cursors.Hand,
            ToolKind.PixelEraser => Cursors.Hand,
            _ => Cursors.Cross
        };

        private IEnumerable<ToggleButton> ToolButtons()
        {
            ToggleButton[] fixedOnes =
            {
                TbSelect, TbArrow, TbLine, TbPen, TbHighlighter, TbText,
                TbCounter, TbNumArrow, TbPicker, TbEraser, TbPixelEraser, TbWand, TbRegion,
                TbMosaic, TbBlur, TbCrop, TbSpotlight, TbMagnifier
            };

            // 도형 갤러리는 코드로 만들어 붙이므로 여기서 같이 훑는다.
            return fixedOnes.Concat(ShapeGallery.Children.OfType<ToggleButton>());
        }

        private void OnThicknessChanged(double value)
        {
            _thickness = value;
            ApplyStyleToEditable(a => a.Thickness = _thickness);
        }

        private void OnOpacityChanged(double percent)
        {
            _opacity = Math.Clamp(percent / 100.0, 0.05, 1.0);

            // 모자이크·흐림은 건드리지 않는다. 비쳐 보이면 가리는 의미가 없다.
            ApplyStyleToEditable(a => { if (a.SupportsOpacity) a.Opacity = _opacity; });
        }

        /// <summary>모자이크 블록 크기 · 흐림 반경.</summary>
        private void OnMaskStrengthChanged(double value)
        {
            _maskStrength = (int)Math.Round(value);
            ApplyStyleToEditable(a => { if (a is PixelateAnnotation p) p.Strength = _maskStrength; });
        }

        private void OnFillToggled(object sender, RoutedEventArgs e)
        {
            _filled = TbFill.IsChecked == true;
            ApplyStyleToEditable(a =>
            {
                if (a is ShapeAnnotation s && CanFill(s.Kind)) s.Filled = _filled;
            });
        }

        /// <summary>안쪽이 있는 도형만 채울 수 있다. 선·화살표는 채울 면이 없다.</summary>
        private static bool CanFill(ToolKind kind)
            => ShapeGeometry.IsBoxShape(kind);

        private void OnShadowToggled(object sender, RoutedEventArgs e)
        {
            _shadow = TbShadow.IsChecked == true;
            ApplyStyleToEditable(a => { if (a.SupportsShadow) a.Shadow = _shadow; });
        }

        private void OnBlendChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi) return;
            _blend = (BlendMode)Math.Clamp(BlendBox.SelectedIndex, 0, 3);
            ApplyStyleToEditable(a => { if (a.SupportsBlend) a.Blend = _blend; });
        }

    }
}
