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
    /// <summary>스포이드·지우개·글자 입력.</summary>
    public partial class EditorWindow
    {
        // ================================================= 스포이드 · 지우개

        /// <summary>그림의 그 자리 색을 집어 지금 색으로 삼는다.</summary>
        private void PickColorAt(Point imagePoint)
        {
            int x = (int)Math.Clamp(Math.Floor(imagePoint.X), 0, Canvas1.ImageWidth - 1);
            int y = (int)Math.Clamp(Math.Floor(imagePoint.Y), 0, Canvas1.ImageHeight - 1);

            try
            {
                BitmapSource src = _image.Format == PixelFormats.Bgra32
                    ? _image
                    : new FormatConvertedBitmap(_image, PixelFormats.Bgra32, null, 0);

                var one = new byte[4];
                src.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);

                SetColor(Color.FromRgb(one[2], one[1], one[0]));
                StHint.Text = $"색을 집었습니다 — {_color}";
            }
            catch (Exception ex)
            {
                StHint.Text = "색을 집지 못했습니다: " + ex.Message;
            }

            SelectTool(_toolBeforePicker);   // 한 번 집으면 원래 도구로
        }

        /// <summary>
        /// 지우개 획을 시작한다. 주석을 지우는 게 아니라 <b>지운 자리를 하나 얹는다</b>.
        /// 그 자리는 아래에 있던 주석들에서 구멍으로 뚫린다 — 선 하나를 지우려다
        /// 선 전체가 사라지면 그건 지우개가 아니라 삭제다.
        /// </summary>
        private void BeginErase(Point p)
        {
            // 주석 하나를 골라 두고 지우개를 쓰면 <b>그 주석만</b> 판다(마스크처럼).
            // 아무것도 안 골랐으면 예전처럼 아래 전부를 판다.
            Annotation? target = Canvas1.SelectedMany.Count == 1 &&
                                 Canvas1.Selected is not (null or EraseAnnotation)
                ? Canvas1.Selected : null;

            _eraseStroke = new EraseAnnotation
            {
                Radius = Math.Max(3, _thickness * 2),
                Target = target
            };
            _eraseStroke.Add(p);

            Canvas1.Items.Add(_eraseStroke);
            if (target == null) SetSelection(null);
            else StHint.Text = "골라 둔 주석만 지웁니다 (다른 것은 안 다침)";
            Canvas1.InvalidateVisual();
        }

        private void EraseAt(Point p)
        {
            if (_eraseStroke == null) { BeginErase(p); return; }

            _eraseStroke.Add(p);
            Canvas1.InvalidateVisual();
        }

        private void EndErase()
        {
            // 아무것도 안 스친 획은 버린다. 빈 데를 문질렀는데 실행취소 기록과
            // 지운 자리만 쌓이면 "지우개가 뭔가를 만들어 낸다"는 이상한 그림이 된다.
            if (_eraseStroke != null)
            {
                int idx = Canvas1.Items.IndexOf(_eraseStroke);
                bool touched = false;
                if (_eraseStroke.Target != null)
                {
                    touched = _eraseStroke.Touches(_eraseStroke.Target);
                }
                else
                {
                    for (int i = 0; i < idx && !touched; i++)
                        touched = _eraseStroke.Touches(Canvas1.Items[i]);
                }

                if (!touched)
                {
                    Canvas1.Items.Remove(_eraseStroke);
                    _undoStack.DropLast();   // 지우개 시작 때 쌓은 것
                    Canvas1.InvalidateVisual();
                    StHint.Text = "지울 주석이 없는 자리입니다 (그림 자체를 지우려면 픽셀지우개)";
                }
            }

            _eraseStroke = null;
            UpdateStatus();
        }

        /// <summary>이미지 밖으로 나간 부분을 잘라 낸다.</summary>
        private Rect ClipToImage(Rect r)
        {
            double x = Math.Max(0, Math.Min(r.X, Canvas1.ImageWidth));
            double y = Math.Max(0, Math.Min(r.Y, Canvas1.ImageHeight));
            double right = Math.Min(Canvas1.ImageWidth, Math.Max(0, r.Right));
            double bottom = Math.Min(Canvas1.ImageHeight, Math.Max(0, r.Bottom));
            return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }

        private void BeginText(Point imagePoint)
        {
            _editingText = new TextAnnotation
            {
                Origin = imagePoint,
                Color = _color,
                Opacity = _opacity,
                FontSize = FontSizeBox.Value,
                FontFamilyName = _fontFamily,
                Bold = _bold,
                Italic = _italic,
                OutlineHalo = _textHalo,
                Background = _textBg,
                Shadow = _shadow,
                Align = _textAlign
            };

            TextEntry.Text = "";
            StyleTextEntry(_editingText);
            TextEntry.Visibility = Visibility.Visible;

            // 클릭이 다 흘러간 뒤에 포커스를 준다. 지금 주면 뒤이어 올라오는
            // 마우스 이벤트가 포커스를 다른 데로 옮겨 버릴 수 있다.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_editingText == null) return;
                TextEntry.Focus();
                Keyboard.Focus(TextEntry);
                TextEntry.CaretIndex = TextEntry.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);

            StHint.Text = "Enter 줄바꿈 · Ctrl+Enter 로 입력 마침 · ESC 취소";
        }

        private void OnTextEntryKey(object sender, KeyEventArgs e)
        {
            // Enter 는 줄바꿈. 입력을 마치는 건 Ctrl+Enter.
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CommitText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelText();
                e.Handled = true;
            }
        }

        private void CommitText()
        {
            if (_editingText == null) return;

            string text = TextEntry.Text;
            TextAnnotation pending = _editingText;
            bool existing = _editingExisting;
            _editingText = null;
            _editingExisting = false;
            TextEntry.Visibility = Visibility.Collapsed;

            if (existing)
            {
                // 있던 글자를 고친 것. 비우면 지운다.
                pending.Visible = true;
                if (string.IsNullOrWhiteSpace(text)) Canvas1.Items.Remove(pending);
                else pending.Text = text.TrimEnd('\r', '\n');
                MarkDirty();
                Canvas1.InvalidateVisual();
                UpdateStatus();
                return;
            }

            if (string.IsNullOrWhiteSpace(text)) { UpdateStatus(); return; }

            pending.Text = text.TrimEnd('\r', '\n');

            // 글자도 다른 도형처럼 위치를 맞춘 뒤 Enter 로 확정한다.
            Canvas1.Active = pending;
            Canvas1.ShowActiveHandles = true;
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void CancelText()
        {
            if (_editingExisting && _editingText != null) _editingText.Visible = true;
            _editingExisting = false;
            _editingText = null;
            TextEntry.Visibility = Visibility.Collapsed;
            Keyboard.ClearFocus();
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private bool _editingExisting;

        /// <summary>더블클릭한 글자를 다시 편집한다. 예전엔 오타 하나 때문에 지우고 다시 써야 했다.</summary>
        private void EditExistingText(TextAnnotation t)
        {
            CommitText();
            ConfirmActive();
            PushUndo();
            SetSelection(null);

            _editingText = t;
            _editingExisting = true;
            t.Visible = false;   // 입력칸이 그 자리에 뜨므로 원본은 잠깐 숨긴다
            TextEntry.Text = t.Text;
            StyleTextEntry(t);
            TextEntry.Visibility = Visibility.Visible;
            Canvas1.InvalidateVisual();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_editingText == null) return;
                TextEntry.Focus();
                Keyboard.Focus(TextEntry);
                TextEntry.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);

            StHint.Text = "글자를 고친 뒤 Ctrl+Enter · ESC 취소";
        }

        /// <summary>입력칸을 실제 글자처럼 보이게: 글꼴·색·바탕·폭·정렬을 그대로 입힌다.</summary>
        private void StyleTextEntry(TextAnnotation t)
        {
            double scale = Canvas1.Scale;
            TextEntry.FontSize = Math.Max(8, t.FontSize * scale);
            TextEntry.FontFamily = new FontFamily(t.FontFamilyName + ", Malgun Gothic, Segoe UI");
            TextEntry.FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal;
            TextEntry.FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal;
            TextEntry.Foreground = new SolidColorBrush(t.Color);

            double luma = (0.299 * t.Color.R + 0.587 * t.Color.G + 0.114 * t.Color.B) / 255.0;
            TextEntry.Background = new SolidColorBrush(t.Background
                ? (luma > 0.6 ? Color.FromArgb(232, 18, 18, 22) : Color.FromArgb(238, 255, 255, 255))
                : (luma > 0.6 ? Color.FromArgb(0xB0, 0x10, 0x13, 0x1A) : Color.FromArgb(0xB0, 0xF4, 0xF4, 0xF6)));
            TextEntry.TextAlignment = t.Align switch
            {
                TextAlign.Center => TextAlignment.Center,
                TextAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            };
            if (t.MaxWidth > 0)
            {
                TextEntry.TextWrapping = TextWrapping.Wrap;
                TextEntry.Width = Math.Max(40, t.MaxWidth * scale);
            }
            else
            {
                TextEntry.TextWrapping = TextWrapping.NoWrap;
                TextEntry.Width = double.NaN;
            }
            Canvas.SetLeft(TextEntry, t.Origin.X * scale);
            Canvas.SetTop(TextEntry, t.Origin.Y * scale);
        }

    }
}
