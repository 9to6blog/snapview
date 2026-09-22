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
    /// <summary>스타일 토글·정렬 메뉴·글꼴 조작 — 자동 선택(마술봉)도 여기.</summary>
    public partial class EditorWindow
    {
        // ================================================= 자동 선택 (마술봉)

        private void WandClick(Point p, bool additive)
        {
            int x = (int)Math.Clamp(Math.Floor(p.X), 0, Canvas1.ImageWidth - 1);
            int y = (int)Math.Clamp(Math.Floor(p.Y), 0, Canvas1.ImageHeight - 1);
            int tol = Math.Clamp((int)MaskStrengthBox.Value, 0, 255);

            bool[] mask = SelectionTools.FloodSelect(_image, x, y, tol, out int count);

            if (additive && _wandMask != null && _wandMask.Length == mask.Length)
            {
                _wandCount = SelectionTools.Union(mask, _wandMask);
                _wandMask = mask;
            }
            else
            {
                _wandMask = mask;
                _wandCount = count;
            }

            Canvas1.SelectionTint = SelectionTools.MaskOverlay(
                _wandMask, _image.PixelWidth, _image.PixelHeight);
            Canvas1.InvalidateVisual();
            StHint.Text = $"{_wandCount:N0}픽셀 선택 — Delete 투명하게(배경 제거) · " +
                          "Enter 지금 색 채우기 · Shift+클릭 추가 · ESC 해제";
        }

        private void WandClear()
        {
            if (_wandMask == null && Canvas1.SelectionTint == null) return;
            _wandMask = null;
            _wandCount = 0;
            Canvas1.SelectionTint = null;
        }

        /// <summary>고른 영역을 투명하게 판다 — 배경 제거. PNG 로 저장하면 뚫린 채 남는다.</summary>
        private void WandErase() => WandApply(null, "투명하게 지웠습니다 (PNG 로 저장하면 뚫린 채 남습니다)");

        private void WandFill() => WandApply(_color, "지금 색으로 채웠습니다");

        private void WandApply(Color? fill, string done)
        {
            if (_wandMask == null) return;

            PushUndo();
            _image = SelectionTools.ApplyMask(_image, _wandMask, fill);
            Canvas1.Source = _image;
            WandClear();
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = done;
        }

        private void OnGradientToggled(object sender, RoutedEventArgs e)
        {
            _gradient = TbGradient.IsChecked == true;
            if (_gradient) { _filled = true; TbFill.IsChecked = true; }
            ApplyStyleToEditable(a =>
            {
                if (a is ShapeAnnotation s && s.CanFill) { s.GradientFill = _gradient; if (_gradient) s.Filled = true; }
            });
        }

        private void OnBothArrowsToggled(object sender, RoutedEventArgs e)
        {
            _bothArrows = TbBothArrows.IsChecked == true;
            ApplyStyleToEditable(a =>
            {
                if (a is ShapeAnnotation { Kind: ToolKind.Arrow } s) s.BothArrows = _bothArrows;
            });
        }

        /// <summary>정렬·균등 간격 메뉴. 둘(배분은 셋) 이상 골라야 뜻이 있다.</summary>
        private void OnArrangeMenu(object sender, RoutedEventArgs e)
        {
            if (Canvas1.SelectedMany.Count == 0)
            {
                StHint.Text = "정렬할 레이어나 그룹을 고르세요 — 하나도 배경 이미지 기준으로 맞출 수 있습니다";
                return;
            }

            var menu = new ContextMenu
            {
                PlacementTarget = BtnArrange,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };

            void Add(string header, Action action, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (_, _) =>
                {
                    action();
                    Canvas1.InvalidateVisual();
                    UpdateStatus();
                };
                menu.Items.Add(item);
            }

            var selection = new MenuItem { Header = "선택 항목 기준", IsCheckable = true, IsChecked = !_alignToBackground, StaysOpenOnClick = true };
            var background = new MenuItem { Header = "배경 이미지 기준 (레이어 0)", IsCheckable = true, IsChecked = _alignToBackground, StaysOpenOnClick = true };
            selection.Click += (_, _) => { _alignToBackground = false; selection.IsChecked = true; background.IsChecked = false; };
            background.Click += (_, _) => { _alignToBackground = true; background.IsChecked = true; selection.IsChecked = false; };
            var keepGroups = new MenuItem { Header = "그룹을 하나의 항목으로 정렬", IsCheckable = true, IsChecked = _alignKeepGroups, StaysOpenOnClick = true };
            keepGroups.Click += (_, _) => _alignKeepGroups = keepGroups.IsChecked;
            menu.Items.Add(selection); menu.Items.Add(background); menu.Items.Add(keepGroups); menu.Items.Add(new Separator());
            Add("왼쪽 맞춤", () => AlignSelected(AlignMode.Left));
            Add("가로 가운데 맞춤", () => AlignSelected(AlignMode.CenterH));
            Add("오른쪽 맞춤", () => AlignSelected(AlignMode.Right));
            menu.Items.Add(new Separator());
            Add("위 맞춤", () => AlignSelected(AlignMode.Top));
            Add("세로 가운데 맞춤", () => AlignSelected(AlignMode.CenterV));
            Add("아래 맞춤", () => AlignSelected(AlignMode.Bottom));
            menu.Items.Add(new Separator());
            void Distribute(bool horizontal)
            {
                var units = LayerGroups.Units(Canvas1.Items, Canvas1.SelectedMany, _alignKeepGroups);
                if (units.Count < 3) { StHint.Text = "간격을 맞출 레이어 또는 그룹이 세 개 이상 필요합니다"; return; }
                PushUndo(); LayerGroups.Distribute(units, horizontal);
            }
            Add("가로 간격 고르게", () => Distribute(true));
            Add("세로 간격 고르게", () => Distribute(false));

            menu.IsOpen = true;
        }

        private void OnTextHaloToggled(object sender, RoutedEventArgs e)
        {
            _textHalo = TbTextHalo.IsChecked == true;
            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.OutlineHalo = _textHalo; });
        }

        private void OnTextBgToggled(object sender, RoutedEventArgs e)
        {
            _textBg = TbTextBg.IsChecked == true;
            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.Background = _textBg; });
        }

        private void OnFontSizeChanged(double size)
        {
            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.FontSize = size; });

            if (_editingText != null)
                TextEntry.FontSize = Math.Max(10, size * Canvas1.Scale);
        }

        private void OnBoldToggled(object sender, RoutedEventArgs e)
        {
            _bold = TbBold.IsChecked == true;
            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.Bold = _bold; });
            if (_editingText != null) TextEntry.FontWeight = _bold ? FontWeights.Bold : FontWeights.Normal;
        }

        private void OnItalicToggled(object sender, RoutedEventArgs e)
        {
            _italic = TbItalic.IsChecked == true;
            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.Italic = _italic; });
            if (_editingText != null) TextEntry.FontStyle = _italic ? FontStyles.Italic : FontStyles.Normal;
        }

        private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || FontCombo.SelectedItem is not FontFamily family) return;
            _fontFamily = family.Source;

            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.FontFamilyName = _fontFamily; });

            if (_editingText != null) TextEntry.FontFamily = family;
        }

        // ================================================= 새 스타일(촉 · 점선 무늬 · 정렬 · 가리개 모양 · 배율 · 번호)

        private void OnHeadChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || HeadBox.SelectedIndex < 0) return;
            _head = (ArrowHead)Math.Clamp(HeadBox.SelectedIndex, 0, 2);
            ApplyStyleToEditable(a => { if (a is ShapeAnnotation { Kind: ToolKind.Arrow } s) s.Head = _head; });
        }

        private void OnDashPatternChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || DashPatternBox.SelectedIndex < 0) return;
            _dashed = DashPatternBox.SelectedIndex > 0;
            _dashPattern = (DashPattern)Math.Clamp(DashPatternBox.SelectedIndex - 1, 0, 2);
            ApplyStyleToEditable(a =>
            {
                if (a is ShapeAnnotation or PathAnnotation) { a.Dashed = _dashed; a.DashPattern = _dashPattern; }
            });
        }

        private void OnAlignClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && Enum.TryParse(tb.Tag?.ToString(), out TextAlign align)) _textAlign = align;
            SetAlignButtons();
            ApplyStyleToEditable(a => { if (a is TextAnnotation t) t.Align = _textAlign; });
            if (_editingText != null) { _editingText.Align = _textAlign; StyleTextEntry(_editingText); }
        }

        private void SetAlignButtons()
        {
            TbAlignLeft.IsChecked = _textAlign == TextAlign.Left;
            TbAlignCenter.IsChecked = _textAlign == TextAlign.Center;
            TbAlignRight.IsChecked = _textAlign == TextAlign.Right;
        }

        private readonly List<ToolKind> _maskShapes = new();

        /// <summary>가리개·강조 모양 목록: 사각형·타원 + 갤러리 전부.</summary>
        private void BuildMaskShapeList()
        {
            _maskShapes.Clear();
            _maskShapes.Add(ToolKind.Rectangle);
            _maskShapes.Add(ToolKind.Ellipse);
            foreach (ToolKind k in ShapeGeometry.All)
                if (!_maskShapes.Contains(k) && MaskShapes.IsMaskShape(k)) _maskShapes.Add(k);

            MaskShapeBox.Items.Clear();
            foreach (ToolKind k in _maskShapes)
                MaskShapeBox.Items.Add(k == ToolKind.Rectangle ? "사각형" : k == ToolKind.Ellipse ? "타원" : ShapeGeometry.NameOf(k));
            MaskShapeBox.SelectedIndex = 0;
        }

        private void OnMaskShapeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || MaskShapeBox.SelectedIndex < 0 || MaskShapeBox.SelectedIndex >= _maskShapes.Count) return;
            _maskShape = _maskShapes[MaskShapeBox.SelectedIndex];
            ApplyStyleToEditable(a =>
            {
                if (a is PixelateAnnotation x) x.Shape = _maskShape;
                else if (a is SpotlightAnnotation sp) sp.Shape = _maskShape;
            });
        }

        private static readonly double[] MagnifierZooms = { 1.5, 2, 3, 4 };

        private void OnMagnifierZoomChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingUi || MagnifierZoomBox.SelectedIndex < 0) return;
            _magnifierZoom = MagnifierZooms[Math.Clamp(MagnifierZoomBox.SelectedIndex, 0, MagnifierZooms.Length - 1)];
            _magnifierDisplayRadius = _magnifierSourceRadius * _magnifierZoom;
            ApplyStyleToEditable(a => { if (a is MagnifierAnnotation m) m.DisplayRadius = _magnifierDisplayRadius; });
            MagnifierDisplayBox.SetSilently(_magnifierDisplayRadius * 2);
        }

        /// <summary>다음 번호. 번호 하나를 골라 둔 상태면 그 번호를 바꾼다.</summary>
        private void OnNextNumberChanged(double value)
        {
            if (_syncingUi) return;
            int n = Math.Max(1, (int)Math.Round(value));
            Annotation? one = Canvas1.Active ?? (Canvas1.SelectedMany.Count == 1 ? Canvas1.Selected : null);
            if (one is CounterAnnotation or NumberArrowAnnotation)
            {
                ApplyStyleToEditable(a =>
                {
                    if (a is CounterAnnotation c) c.Number = n;
                    else if (a is NumberArrowAnnotation na) na.Number = n;
                });
                return;
            }
            _counter = n;
        }

        private static readonly double[] CropAspects = { 0, 1, 4.0 / 3, 3.0 / 2, 16.0 / 9, 9.0 / 16, -1 };

        private void OnCropAspectChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CropAspectBox.SelectedIndex < 0 || Canvas1 == null) return;
            double a = CropAspects[Math.Clamp(CropAspectBox.SelectedIndex, 0, CropAspects.Length - 1)];
            _cropAspect = a < 0 ? Canvas1.ImageWidth / Math.Max(1, Canvas1.ImageHeight) : a;
            if (Canvas1.Active is CropAnnotation crop && _cropAspect > 0)
            {
                crop.End = EditorMath.FitAspect(crop.Start, crop.End, _cropAspect);
                Canvas1.InvalidateVisual();
                UpdateStatus();
            }
        }

        private void OnCropSizeChanged(double? width, double? height)
        {
            if (_syncingUi || Canvas1.Active is not CropAnnotation crop) return;
            Rect b = crop.Bounds;
            double w = width ?? b.Width, h = height ?? b.Height;
            crop.Start = b.TopLeft;
            crop.End = new Point(Math.Min(Canvas1.ImageWidth, b.X + Math.Max(8, w)),
                                 Math.Min(Canvas1.ImageHeight, b.Y + Math.Max(8, h)));
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void OnCropConfirm(object sender, RoutedEventArgs e) => ConfirmActive();
        private void OnRegionCopy(object sender, RoutedEventArgs e) => CopyRegion(cut: false);
        private void OnRegionCut(object sender, RoutedEventArgs e) => CopyRegion(cut: true);
        private void OnHelp(object sender, RoutedEventArgs e) => ShowShortcutHelp();

        // ================================================= 선택 → 리본 역동기 · 최근 색

        /// <summary>
        /// 골라 둔(또는 확정 대기) 주석의 값을 리본에 되비친다. 예전엔 빨간 화살표를 골라도
        /// 색칸은 직전에 쓰던 파랑에 체크돼 있어서, 굵기를 만지면 보이는 값과 다른 값이 먹었다.
        /// </summary>
        private void SyncUiFromEditable()
        {
            Annotation? a = Canvas1.Active ?? (Canvas1.SelectedMany.Count == 1 ? Canvas1.Selected : null);
            if (a == null || a is EraseAnnotation)
            {
                NextNumberBox.SetSilently(_counter);
                return;
            }

            _syncingUi = true;
            try
            {
                _color = a.Color;
                ShowColorInSwatches(_color);
                _thickness = a.Thickness;
                ThicknessBox.SetSilently(_thickness);
                if (a.SupportsOpacity) { _opacity = a.Opacity; OpacityBox.SetSilently(Math.Round(_opacity * 100)); }
                if (a is ShapeAnnotation or PathAnnotation)
                {
                    _dashed = a.Dashed;
                    _dashPattern = a.DashPattern; DashPatternBox.SelectedIndex = _dashed ? (int)_dashPattern + 1 : 0;
                }
                if (a.SupportsShadow) { _shadow = a.Shadow; TbShadow.IsChecked = _shadow; }
                if (a.SupportsBlend) { _blend = a.Blend; BlendBox.SelectedIndex = (int)_blend; }

                switch (a)
                {
                    case ShapeAnnotation s:
                        if (s.CanFill) { _fillColor = s.FillColor ?? s.Color; ShowFillColor(); _filled = s.Filled; TbFill.IsChecked = _filled; _gradient = s.GradientFill; TbGradient.IsChecked = _gradient; }
                        if (s.Kind == ToolKind.Arrow)
                        {
                            _bothArrows = s.BothArrows; TbBothArrows.IsChecked = _bothArrows;
                            _head = s.Head; HeadBox.SelectedIndex = (int)_head;
                        }
                        break;
                    case TextAnnotation t:
                        FontSizeBox.SetSilently(t.FontSize);
                        _bold = t.Bold; TbBold.IsChecked = _bold;
                        _italic = t.Italic; TbItalic.IsChecked = _italic;
                        _textHalo = t.OutlineHalo; TbTextHalo.IsChecked = _textHalo;
                        _textBg = t.Background; TbTextBg.IsChecked = _textBg;
                        _textAlign = t.Align; SetAlignButtons();
                        _fontFamily = t.FontFamilyName;
                        FontCombo.SelectedItem = GetFontList().FirstOrDefault(f =>
                            string.Equals(f.Source, t.FontFamilyName, StringComparison.OrdinalIgnoreCase)) ?? FontCombo.SelectedItem;
                        break;
                    case PixelateAnnotation x:
                        _maskStrength = x.Strength; MaskStrengthBox.SetSilently(_maskStrength);
                        _maskShape = x.Shape; MaskShapeBox.SelectedIndex = Math.Max(0, _maskShapes.IndexOf(_maskShape));
                        break;
                    case SpotlightAnnotation sp:
                        _maskShape = sp.Shape; MaskShapeBox.SelectedIndex = Math.Max(0, _maskShapes.IndexOf(_maskShape));
                        break;
                    case MagnifierAnnotation m:
                        _magnifierZoom = m.Zoom;
                        _magnifierSourceRadius = m.Radius; _magnifierDisplayRadius = m.DisplayRadius;
                        UpdateMagnifierControls();
                        break;
                    case CounterAnnotation c: NextNumberBox.SetSilently(c.Number); break;
                    case NumberArrowAnnotation na: NextNumberBox.SetSilently(na.Number); break;
                    case CropAnnotation crop:
                        CropWidthBox.SetSilently(Math.Round(crop.Bounds.Width));
                        CropHeightBox.SetSilently(Math.Round(crop.Bounds.Height));
                        break;
                }
            }
            finally { _syncingUi = false; }
        }

        /// <summary>최근 색 칸을 다시 그린다(팔레트 밖의 색만 여기 쌓인다).</summary>
        private void RebuildRecentSwatches()
        {
            RecentPanel.Children.Clear();
            foreach (Color c in _recentColors)
            {
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush(c),
                    GroupName = "swatch",
                    Tag = c,
                    ToolTip = "최근 " + c
                };
                rb.Checked += (s, _) =>
                {
                    if (_syncingUi) return;
                    _color = (Color)((RadioButton)s!).Tag;
                    ApplyStyleToEditable(a => a.Color = _color);
                };
                RecentPanel.Children.Add(rb);
            }
        }

        private void PushRecentColor(Color c)
        {
            RecentColors.Push(_recentColors, c);
            RebuildRecentSwatches();
            bool was = _syncingUi;
            _syncingUi = true;
            foreach (object child in RecentPanel.Children)
                if (child is RadioButton rb && rb.Tag is Color rc && rc == c) rb.IsChecked = true;
            _syncingUi = was;
        }

    }
}
