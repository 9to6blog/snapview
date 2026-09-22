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
    /// <summary>확정 대기 주석, 자르기·뒤집기·회전·크기·여백, 픽셀 지우개, 영역 선택.</summary>
    public partial class EditorWindow
    {
        // ================================================= 확정 대기 주석

        /// <summary>지금 만들고 있던 주석을 확정한다. 자르기라면 실제로 자른다.</summary>
        private void ConfirmActive()
        {
            ResetPlacementGestures();
            Annotation? a = Canvas1?.Active;
            if (a == null) return;

            Canvas1!.Active = null;
            Canvas1.ShowActiveHandles = false;

            if (a is CropAnnotation crop) { ApplyCrop(crop); return; }
            if (a is PixelateAnnotation px) px.Fast = false;

            PushUndo();
            Canvas1.Items.Add(a);
            if (a is CounterAnnotation or NumberArrowAnnotation) _counter++;

            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void CancelActive()
        {
            ResetPlacementGestures();
            Stage.ReleaseMouseCapture();
            if (Canvas1.Active == null) return;
            Canvas1.Active = null;
            Canvas1.ShowActiveHandles = false;
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void ApplyCrop(CropAnnotation crop)
        {
            Int32Rect region = crop.ToRegion((int)Canvas1.ImageWidth, (int)Canvas1.ImageHeight);
            if (region.Width < 2 || region.Height < 2) { UpdateStatus(); return; }

            PushUndo();

            var cropped = new CroppedBitmap(_image, region);
            cropped.Freeze();
            _image = cropped;

            // 잘라낸 만큼 원점이 옮겨졌으니 주석도 같이 옮긴다.
            var shift = new Vector(-region.X, -region.Y);
            foreach (Annotation item in Canvas1.Items) item.Move(shift);

            Canvas1.Source = _image;
            _fitToWindow = true;
            Relayout();
            Canvas1.InvalidateVisual();
            StHint.Text = $"{region.Width} × {region.Height} 로 잘랐습니다";
        }

        private void Flip(bool horizontal)
        {
            CommitText();
            ConfirmActive();
            WandClear();
            PushUndo();

            double w = Canvas1.ImageWidth, h = Canvas1.ImageHeight;
            _image = AnnotationRenderer.FlipImage(_image, horizontal);
            foreach (Annotation a in Canvas1.Items)
            {
                a.Flip(horizontal, w, h);
                a.RotationDeg = -a.RotationDeg;   // 거울에 비추면 도는 방향도 뒤집힌다
            }

            Canvas1.Source = _image;
            SetSelection(null);
            Relayout();
            Canvas1.InvalidateVisual();
            StHint.Text = horizontal ? "좌우로 뒤집었습니다" : "상하로 뒤집었습니다";
        }

        private void OnFlipHorizontal(object sender, RoutedEventArgs e) => Flip(true);
        private void OnFlipVertical(object sender, RoutedEventArgs e) => Flip(false);

        // ================================================= 회전 · 크기

        /// <summary>이미지를 90도 돌린다. 주석도 같은 자리에 남도록 함께 돌린다.</summary>
        private void Rotate(bool clockwise)
        {
            CommitText();
            ConfirmActive();
            CloseFilterPopup(restore: true);
            WandClear();
            PushUndo();

            double w = Canvas1.ImageWidth, h = Canvas1.ImageHeight;
            _image = AnnotationRenderer.Rotate90(_image, clockwise);
            foreach (Annotation a in Canvas1.Items) a.Rotate90(clockwise, w, h);

            _region = Rect.Empty;
            Canvas1.Source = _image;
            SetSelection(null);
            Relayout();
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = clockwise ? "오른쪽으로 돌렸습니다" : "왼쪽으로 돌렸습니다";
        }

        private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(true);
        private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(false);

        private void OnExpandCanvas(object sender, RoutedEventArgs e) => ExpandCanvas();

        /// <summary>
        /// 캔버스에 여백을 붙인다 — 그림은 그대로 두고 <b>공간만</b> 늘린다.
        /// 크기 조절은 그림을 늘리지만, 이건 주석 달 자리·여백이 필요할 때 쓴다.
        /// </summary>
        private void ExpandCanvas()
        {
            CommitText();
            ConfirmActive();
            WandClear();

            NumberStepper Box()
            {
                var s = new NumberStepper { Minimum = 0, Maximum = 4000, Step = 10, Width = 86, Height = 26 };
                s.SetSilently(0);
                return s;
            }
            NumberStepper top = Box(), bottom = Box(), left = Box(), right = Box();

            var fill = new ComboBox { Width = 110, Height = 26 };
            foreach (string s in new[] { "투명", "흰색", "검정", "지금 색" }) fill.Items.Add(s);
            fill.SelectedIndex = 0;

            var grid = new Grid { Margin = new Thickness(12) };
            for (int i = 0; i < 2; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition());

            void Put(int row, string label, FrameworkElement control)
            {
                var tb = new TextBlock
                {
                    Text = label, Margin = new Thickness(0, 0, 10, 8),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Fg")
                };
                Grid.SetRow(tb, row); Grid.SetColumn(tb, 0);
                Grid.SetRow(control, row); Grid.SetColumn(control, 1);
                control.Margin = new Thickness(0, 0, 0, 8);
                grid.Children.Add(tb);
                grid.Children.Add(control);
            }
            Put(0, "위(px)", top);
            Put(1, "아래(px)", bottom);
            Put(2, "왼쪽(px)", left);
            Put(3, "오른쪽(px)", right);
            Put(4, "채울 색", fill);

            var ok = new Button { Content = "늘리기", MinWidth = 64, IsDefault = true };
            var cancel = new Button { Content = "취소", MinWidth = 56, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
            ok.Style = (Style)FindResource("ToolButton");
            cancel.Style = (Style)FindResource("ToolButton");
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 5); Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            var dlg = new Window
            {
                Title = "캔버스 여백 늘리기",
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BgChrome"),
                Content = grid
            };
            ok.Click += (_, _) => dlg.DialogResult = true;
            if (dlg.ShowDialog() != true) return;

            int l = (int)left.Value, t = (int)top.Value, r = (int)right.Value, b = (int)bottom.Value;
            if (l + t + r + b == 0) return;

            PushUndo();

            Color? c = fill.SelectedIndex switch
            {
                1 => Colors.White,
                2 => Colors.Black,
                3 => _color,
                _ => (Color?)null
            };
            _image = AnnotationRenderer.Expand(_image, l, t, r, b, c);
            foreach (Annotation a in Canvas1.Items) a.Move(new Vector(l, t));

            _region = Rect.Empty;
            Canvas1.Source = _image;
            SetSelection(null);
            _fitToWindow = true;
            Relayout();
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = $"여백을 붙여 {(int)Canvas1.ImageWidth}×{(int)Canvas1.ImageHeight} 가 됐습니다";
        }

        /// <summary>
        /// 그림 자체를 늘리거나 줄인다(확대 교체). 주석도 같은 비율로 따라간다.
        /// 캔버스 크기... 가 이 일을 한다 — 예전 설명은 "캔버스" 라고만 적혀 있어
        /// 그림이 늘어나는지 공간이 늘어나는지 알 수 없었다.
        /// </summary>
        private void OnResizeCanvas(object sender, RoutedEventArgs e)
        {
            CommitText();
            ConfirmActive();

            WandClear();
            int oldW = (int)Canvas1.ImageWidth, oldH = (int)Canvas1.ImageHeight;
            var dialog = new ResizeDialog(oldW, oldH) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            int newW = dialog.ResultWidth, newH = dialog.ResultHeight;
            if (newW == oldW && newH == oldH) return;

            PushUndo();

            _image = AnnotationRenderer.Resize(_image, newW, newH);
            double fx = (double)newW / oldW, fy = (double)newH / oldH;
            foreach (Annotation a in Canvas1.Items) a.Scale(fx, fy);

            _region = Rect.Empty;
            Canvas1.Source = _image;
            SetSelection(null);
            Relayout();
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = $"크기를 {newW}×{newH} 로 바꿨습니다";
        }

        // ================================================= 픽셀 지우개 · 영역 선택

        /// <summary>지운 자리는 투명해진다. 흰색으로 덮는 것과 달리 뒤가 비치게 저장된다.</summary>
        private void BeginPixelErase(Point p)
        {
            PushUndo();

            BitmapSource src = _image.Format == PixelFormats.Bgra32
                ? _image
                : new FormatConvertedBitmap(_image, PixelFormats.Bgra32, null, 0);

            _eraseBuffer = new WriteableBitmap(src);
            _image = _eraseBuffer;
            Canvas1.Source = _image;
            ErasePixelsAt(p);
        }

        private void ErasePixelsAt(Point p)
        {
            if (_eraseBuffer == null) return;

            int radius = Math.Max(2, (int)Math.Round(_thickness * 2));
            int cx = (int)Math.Round(p.X), cy = (int)Math.Round(p.Y);

            int x0 = Math.Clamp(cx - radius, 0, _eraseBuffer.PixelWidth);
            int y0 = Math.Clamp(cy - radius, 0, _eraseBuffer.PixelHeight);
            int x1 = Math.Clamp(cx + radius + 1, 0, _eraseBuffer.PixelWidth);
            int y1 = Math.Clamp(cy + radius + 1, 0, _eraseBuffer.PixelHeight);
            if (x1 <= x0 || y1 <= y0) return;

            int w = x1 - x0, h = y1 - y0;
            var block = new byte[w * h * 4];
            _eraseBuffer.CopyPixels(new Int32Rect(x0, y0, w, h), block, w * 4, 0);

            int r2 = radius * radius;
            for (int y = 0; y < h; y++)
            {
                int dy = y0 + y - cy;
                for (int x = 0; x < w; x++)
                {
                    int dx = x0 + x - cx;
                    if (dx * dx + dy * dy > r2) continue;

                    int i = (y * w + x) * 4;
                    block[i] = block[i + 1] = block[i + 2] = block[i + 3] = 0;   // 투명
                }
            }

            _eraseBuffer.WritePixels(new Int32Rect(x0, y0, w, h), block, w * 4, 0);
            Canvas1.InvalidateVisual();
        }

        private void EndPixelErase()
        {
            // 획이 끝났으니 얼린다. 다음 획은 새 버퍼를 만들고, 얼려 둬야 임시 저장이 다른 스레드에서 읽을 수 있다.
            if (_eraseBuffer != null && _eraseBuffer.CanFreeze) _eraseBuffer.Freeze();
            _eraseBuffer = null;
            UpdateStatus();
        }

        /// <summary>고른 영역을 잘라 온다. 없으면 null.</summary>
        private BitmapSource? RegionImage()
        {
            if (_region.IsEmpty || _region.Width < 1 || _region.Height < 1) return null;

            BitmapSource flat = Flatten();
            var rect = new Int32Rect(
                (int)Math.Clamp(_region.X, 0, flat.PixelWidth - 1),
                (int)Math.Clamp(_region.Y, 0, flat.PixelHeight - 1),
                (int)Math.Clamp(_region.Width, 1, flat.PixelWidth),
                (int)Math.Clamp(_region.Height, 1, flat.PixelHeight));

            rect.Width = Math.Min(rect.Width, flat.PixelWidth - rect.X);
            rect.Height = Math.Min(rect.Height, flat.PixelHeight - rect.Y);
            if (rect.Width < 1 || rect.Height < 1) return null;

            var crop = new CroppedBitmap(flat, rect);
            crop.Freeze();
            return crop;
        }

        /// <summary>고른 영역을 투명하게 지운다(잘라내기의 뒤처리).</summary>
        private void ClearRegion()
        {
            if (_region.IsEmpty) return;

            BitmapSource src = _image.Format == PixelFormats.Bgra32
                ? _image
                : new FormatConvertedBitmap(_image, PixelFormats.Bgra32, null, 0);

            var buffer = new WriteableBitmap(src);
            int x = (int)Math.Clamp(_region.X, 0, buffer.PixelWidth - 1);
            int y = (int)Math.Clamp(_region.Y, 0, buffer.PixelHeight - 1);
            int w = (int)Math.Min(_region.Width, buffer.PixelWidth - x);
            int h = (int)Math.Min(_region.Height, buffer.PixelHeight - y);
            if (w < 1 || h < 1) return;

            buffer.WritePixels(new Int32Rect(x, y, w, h), new byte[w * h * 4], w * 4, 0);
            buffer.Freeze();

            _image = buffer;
            Canvas1.Source = _image;
            Canvas1.InvalidateVisual();
        }

        /// <summary>고른 영역을 클립보드로. <paramref name="cut"/> 이면 그 자리를 비운다.</summary>
        private void CopyRegion(bool cut)
        {
            BitmapSource? piece = RegionImage();
            if (piece == null) { StHint.Text = "먼저 영역을 고르세요"; return; }

            ImageIO.CopyToClipboard(piece);

            if (cut)
            {
                PushUndo();
                ClearRegion();
            }

            StHint.Text = cut
                ? $"{piece.PixelWidth}×{piece.PixelHeight} 잘라냈습니다"
                : $"{piece.PixelWidth}×{piece.PixelHeight} 복사했습니다";
        }

    }
}
