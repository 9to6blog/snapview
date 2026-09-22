using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private Color _fillColor = Color.FromRgb(255, 209, 102);
        private RadioButton? _customFillSwatch;
        private double _magnifierSourceRadius = 42;
        private double _magnifierDisplayRadius = 84;

        private void BuildFillSwatches()
        {
            foreach (string hex in Palette)
            {
                Color color = ParseColor(hex);
                var swatch = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"), GroupName = "fillSwatch",
                    Background = new SolidColorBrush(color), Tag = color, ToolTip = "채우기 " + hex
                };
                swatch.Checked += (_, _) => { if (!_syncingUi) SetFillColor((Color)swatch.Tag); };
                FillSwatchPanel.Children.Add(swatch);
            }
            _customFillSwatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"), GroupName = "fillSwatch", ToolTip = "직접 고른 채우기 색상"
            };
            _customFillSwatch.Checked += (_, _) =>
            {
                if (!_syncingUi && _customFillSwatch.Tag is Color color) SetFillColor(color);
            };
            FillSwatchPanel.Children.Add(_customFillSwatch);
            var pick = new Button { Style = (Style)FindResource("EditorIconButton"), Content = "채우기 색상 선택", ToolTip = "채우기 색상 직접 고르기" };
            ToolbarIcon.SetData(pick, (Geometry)FindResource("EditPicker"));
            pick.Click += (_, _) => OpenFillColorPicker(pick);
            FillSwatchPanel.Children.Add(pick);
            ShowFillColor();

            foreach (NumberStepper box in new[] { MagnifierSourceBox, MagnifierDisplayBox })
            {
                box.Minimum = 16; box.Maximum = 20000; box.Step = 2; box.Suffix = "px";
            }
            MagnifierSourceBox.ValueChanged += v => ChangeMagnifierSize(v / 2, null);
            MagnifierDisplayBox.ValueChanged += v => ChangeMagnifierSize(null, v / 2);
            UpdateMagnifierControls();
        }

        private void SetFillColor(Color color)
        {
            _fillColor = color;
            _filled = true;
            TbFill.IsChecked = true;
            ApplyStyleToEditable(a => { if (a is ShapeAnnotation s && s.CanFill) { s.FillColor = color; s.Filled = true; } });
            ShowFillColor();
        }

        private void ShowFillColor()
        {
            bool was = _syncingUi; _syncingUi = true;
            try
            {
                bool matched = false;
                foreach (object child in FillSwatchPanel.Children)
                {
                    if (child is not RadioButton button || button == _customFillSwatch) continue;
                    bool match = button.Tag is Color color && color == _fillColor;
                    button.IsChecked = match; matched |= match;
                }
                if (_customFillSwatch == null) return;
                _customFillSwatch.Tag = _fillColor;
                _customFillSwatch.Background = new SolidColorBrush(_fillColor);
                _customFillSwatch.IsChecked = !matched;
            }
            finally { _syncingUi = was; }
        }

        private void OpenFillColorPicker(UIElement target)
        {
            if (_colorPopup != null) _colorPopup.IsOpen = false;
            var picker = new ColorPicker { Color = _fillColor, Margin = new Thickness(10) };
            picker.ColorChanged += SetFillColor;
            _colorPopup = new Popup
            {
                PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                Child = new Border { Background = (Brush)FindResource("BgChrome"), BorderBrush = (Brush)FindResource("Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = picker }
            };
            _colorPopup.Closed += (_, _) => _colorPopup = null;
            _colorPopup.IsOpen = true;
        }

        private void ChangeMagnifierSize(double? sourceRadius, double? displayRadius)
        {
            if (_syncingUi) return;
            if (sourceRadius.HasValue) _magnifierSourceRadius = sourceRadius.Value;
            if (displayRadius.HasValue) _magnifierDisplayRadius = displayRadius.Value;
            ApplyStyleToEditable(a =>
            {
                if (a is not MagnifierAnnotation m) return;
                if (sourceRadius.HasValue) m.Radius = sourceRadius.Value;
                if (displayRadius.HasValue) m.DisplayRadius = displayRadius.Value;
            });
            UpdateMagnifierControls();
        }

        private void UpdateMagnifierControls()
        {
            bool was = _syncingUi; _syncingUi = true;
            try
            {
                MagnifierSourceBox.SetSilently(Math.Round(_magnifierSourceRadius * 2));
                MagnifierDisplayBox.SetSilently(Math.Round(_magnifierDisplayRadius * 2));
                _magnifierZoom = _magnifierDisplayRadius / _magnifierSourceRadius;
                MagnifierZoomBox.SelectedIndex = Array.FindIndex(MagnifierZooms, z => Math.Abs(z - _magnifierZoom) < 0.001);
                MagnifierZoomBox.ToolTip = $"현재 {_magnifierZoom:0.##}× · 배율을 선택하면 확대 창 크기가 바뀝니다";
            }
            finally { _syncingUi = was; }
        }
    }
}
