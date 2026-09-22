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
    /// <summary>레이어(주석 목록) 패널과 선택 묶음 조작(전체 선택·복제·복사·붙여넣기).</summary>
    public partial class EditorWindow
    {
        // ================================================= 텍스트

        // ================================================= 레이어(주석 목록)

        /// <summary>
        /// 주석 목록을 다시 그린다. 위에 그려지는 것이 목록에서도 위로 오도록
        /// <b>거꾸로</b> 늘어놓는다(그림에서 나중에 그린 것이 위에 보이니까).
        /// </summary>
        /// <summary>목록의 한 줄. 통째로 다시 만들지 않고 글자·색만 고칠 수 있게 조각을 들고 있는다.</summary>
        private sealed class LayerRow
        {
            internal Grid Panel = null!;
            internal CheckBox Eye = null!;
            internal Border Chip = null!;
            internal TextBlock Label = null!;
        }

        private readonly Dictionary<Annotation, LayerRow> _layerRows = new();

        /// <summary>
        /// 주석 목록을 맞춘다. 위에 그려지는 것이 목록에서도 위로 오도록 <b>거꾸로</b> 늘어놓는다.
        /// 순서·개수가 그대로면 줄을 다시 만들지 않고 글자만 고친다 — 예전엔 동작마다 컨트롤 수백 개를
        /// 새로 만들어 스크롤과 포커스가 날아갔다.
        /// </summary>
        private void RefreshLayers()
        {
            if (LayerList == null) return;

            _syncingLayers = true;
            try
            {
                var desired = new List<Annotation>();
                for (int i = Canvas1.Items.Count - 1; i >= 0; i--)
                    if (Canvas1.Items[i] is not EraseAnnotation) desired.Add(Canvas1.Items[i]);   // 지운 자리는 레이어가 아니다

                bool same = LayerList.Items.Count == desired.Count;
                for (int i = 0; same && i < desired.Count; i++)
                    same = LayerList.Items[i] is FrameworkElement fe && ReferenceEquals(fe.Tag, desired[i]);

                if (!same)
                {
                    LayerList.Items.Clear();
                    _layerRows.Clear();
                    foreach (Annotation a in desired)
                    {
                        LayerRow row = MakeLayerRow(a);
                        _layerRows[a] = row;
                        LayerList.Items.Add(row.Panel);
                    }
                }

                for (int i = 0; i < desired.Count; i++)
                    if (_layerRows.TryGetValue(desired[i], out LayerRow? row))
                        UpdateLayerRow(row, desired[i], Canvas1.Items.IndexOf(desired[i]));

                LayerList.SelectedItems.Clear();
                foreach (object item in LayerList.Items)
                    if (item is FrameworkElement fe && fe.Tag is Annotation a2 && Canvas1.SelectedMany.Contains(a2))
                        LayerList.SelectedItems.Add(item);
            }
            finally { _syncingLayers = false; }
        }

        private LayerRow MakeLayerRow(Annotation a)
        {
            var eye = new CheckBox
            {
                IsChecked = a.Visible,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "보이기 / 숨기기",
                Margin = new Thickness(0, 0, 6, 0)
            };
            eye.Checked += (_, _) => { if (_syncingLayers) return; PushUndo("visible"); a.Visible = true; Canvas1.InvalidateVisual(); };
            eye.Unchecked += (_, _) => { if (_syncingLayers) return; PushUndo("visible"); a.Visible = false; Canvas1.InvalidateVisual(); };

            var chip = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(a.Color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var panel = new Grid { Tag = a, Margin = new Thickness(0, 3, 0, 3) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(chip, 1);
            Grid.SetColumn(label, 2);
            panel.Children.Add(eye);
            panel.Children.Add(chip);
            panel.Children.Add(label);
            panel.ContextMenu = BuildLayerMenu(a);
            return new LayerRow { Panel = panel, Eye = eye, Chip = chip, Label = label };
        }

        private void UpdateLayerRow(LayerRow row, Annotation a, int index)
        {
            row.Eye.IsChecked = a.Visible;
            row.Chip.Background = new SolidColorBrush(a.Color);
            row.Label.Text = $"{index + 1}. " + (a.Locked ? "(잠김) " : "") +
                             (string.IsNullOrWhiteSpace(a.Name) ? DescribeLayer(a) : a.Name);
            row.Label.ToolTip = row.Label.Text;
            row.Label.Opacity = a.Locked ? 0.6 : 1.0;
        }

        /// <summary>레이어 행 우클릭: 이름 바꾸기 · 잠금 · 복제 · 삭제.</summary>
        private ContextMenu BuildLayerMenu(Annotation a)
        {
            var menu = new ContextMenu();

            var rename = new MenuItem { Header = "이름 바꾸기..." };
            rename.Click += (_, _) => RenameLayer(a);
            menu.Items.Add(rename);

            var lockItem = new MenuItem
            {
                Header = a.Locked ? "잠금 풀기" : "잠금",
                ToolTip = "잠근 주석은 캔버스에서 안 집히고 옮기거나 지울 수 없다"
            };
            lockItem.Click += (_, _) =>
            {
                PushUndo();
                a.Locked = !a.Locked;
                if (a.Locked && Canvas1.SelectedMany.Remove(a) &&
                    ReferenceEquals(Canvas1.Selected, a))
                    Canvas1.Selected = Canvas1.SelectedMany.Count > 0 ? Canvas1.SelectedMany[^1] : null;
                Canvas1.InvalidateVisual();
                UpdateStatus();
            };
            menu.Items.Add(lockItem);

            var dup = new MenuItem { Header = "복제 (Ctrl+D)" };
            dup.Click += (_, _) => { SetSelection(a); DuplicateSelection(new Vector(16, 16)); };
            menu.Items.Add(dup);

            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "삭제", Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x73, 0x6B)) };
            del.Click += (_, _) =>
            {
                if (a.Locked) { StHint.Text = "잠긴 주석입니다 — 먼저 잠금을 푸세요"; return; }
                SetSelection(a);
                DeleteSelection();
            };
            menu.Items.Add(del);

            return menu;
        }

        /// <summary>레이어 이름을 바꾼다. 비우면 다시 종류로 부른다.</summary>
        private void RenameLayer(Annotation a)
        {
            var box = new TextBox
            {
                Text = a.Name ?? "", MinWidth = 200, Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var ok = new Button { Content = "확인", MinWidth = 56, IsDefault = true, Margin = new Thickness(0, 10, 0, 0) };
            ok.Style = (Style)FindResource("ToolButton");
            ok.HorizontalAlignment = HorizontalAlignment.Right;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(box);
            panel.Children.Add(ok);

            var dlg = new Window
            {
                Title = "레이어 이름",
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BgChrome"),
                Content = panel
            };
            ok.Click += (_, _) => dlg.DialogResult = true;
            box.Focus();
            box.SelectAll();
            if (dlg.ShowDialog() != true) return;

            PushUndo();
            a.Name = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            UpdateStatus();
        }

        private static string DescribeLayer(Annotation a) => a switch
        {
            TextAnnotation t => "글자 " + (t.Text.Length > 10 ? t.Text[..10] + "…" : t.Text),
            CounterAnnotation c => "번호 " + c.Number,
            NumberArrowAnnotation na => "번호+화살표 " + na.Number,
            PixelateAnnotation p => p.UseBlur ? "흐림" : "모자이크",
            PathAnnotation p => p.Highlighter ? "형광펜" : "펜",
            EraseAnnotation => "지운 자리",
            ShapeAnnotation sh => ShapeGeometry.NameOf(sh.Kind),
            CropAnnotation => "자르기",
            SpotlightAnnotation => "강조",
            MagnifierAnnotation => "돋보기",
            ImageAnnotation => "붙여넣은 그림",
            _ => a.GetType().Name
        };

        /// <summary>레이어 목록에서도 여럿을 고를 수 있다(Ctrl/Shift+클릭). 캔버스와 동기.</summary>
        private void OnLayerSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLayers) return;
            if (LayerList.SelectedItems.Count == 0) return;

            ConfirmActive();

            Canvas1.SelectedMany.Clear();
            foreach (object item in LayerList.SelectedItems)
                if (item is FrameworkElement fe && fe.Tag is Annotation a)
                    Canvas1.SelectedMany.Add(a);
            Canvas1.Selected = Canvas1.SelectedMany.Count > 0 ? Canvas1.SelectedMany[^1] : null;

            SelectTool(ToolKind.Select);
            Canvas1.InvalidateVisual();
        }

        /// <summary>고른 주석을 목록에서 옮긴다. delta 가 +면 위(나중에 그림)로.</summary>
        private void MoveLayer(int delta, bool toEnd)
        {
            Annotation? target = Canvas1.Selected;
            if (target == null) { StHint.Text = "먼저 주석을 고르세요"; return; }

            int from = Canvas1.Items.IndexOf(target);
            if (from < 0) return;

            int to = toEnd
                ? (delta > 0 ? Canvas1.Items.Count - 1 : 0)
                : Math.Clamp(from + delta, 0, Canvas1.Items.Count - 1);

            if (to == from) return;

            PushUndo();
            Canvas1.Items.RemoveAt(from);
            Canvas1.Items.Insert(to, target);
            SetSelection(target);
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void OnLayerToFront(object sender, RoutedEventArgs e) => MoveLayer(+1, toEnd: true);
        private void OnLayerForward(object sender, RoutedEventArgs e) => MoveLayer(+1, toEnd: false);
        private void OnLayerBackward(object sender, RoutedEventArgs e) => MoveLayer(-1, toEnd: false);
        private void OnLayerToBack(object sender, RoutedEventArgs e) => MoveLayer(-1, toEnd: true);

        private void OnLayerDelete(object sender, RoutedEventArgs e)
        {
            DeleteSelection();
        }

        /// <summary>골라 둔 주석 전부 삭제. 잠긴 것은 남긴다.</summary>
        private void DeleteSelection()
        {
            if (Canvas1.SelectedMany.Count == 0) { StHint.Text = "먼저 주석을 고르세요"; return; }

            List<Annotation> doomed = Canvas1.SelectedMany.Where(s => !s.Locked).ToList();
            if (doomed.Count == 0) { StHint.Text = "잠긴 주석입니다 — 레이어 우클릭으로 잠금을 푸세요"; return; }

            PushUndo();
            foreach (Annotation s in doomed) Canvas1.Items.Remove(s);
            SetSelection(null);
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        /// <summary>전체 선택(Ctrl+A). 지운 자리는 대상이 아니다.</summary>
        private void SelectAllAnnotations()
        {
            ConfirmActive();
            SelectTool(ToolKind.Select);
            Canvas1.SelectedMany.Clear();
            foreach (Annotation a in Canvas1.Items)
                if (a is not EraseAnnotation && !a.Locked) Canvas1.SelectedMany.Add(a);
            Canvas1.Selected = Canvas1.SelectedMany.Count > 0 ? Canvas1.SelectedMany[^1] : null;
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        /// <summary>골라 둔 주석을 복제해(Ctrl+D) 살짝 어긋난 자리에 놓고, 복제본을 고른다.</summary>
        private void DuplicateSelection(Vector offset)
        {
            if (Canvas1.SelectedMany.Count == 0) { StHint.Text = "먼저 주석을 고르세요"; return; }

            PushUndo();
            var clones = new List<Annotation>();
            foreach (Annotation s in Canvas1.SelectedMany)
            {
                Annotation c = s.Clone();
                c.Move(offset);
                Canvas1.Items.Add(c);
                clones.Add(c);
                if (c is CounterAnnotation n) n.Number = _counter++;
                else if (c is NumberArrowAnnotation an) an.Number = _counter++;
            }

            Canvas1.SelectedMany.Clear();
            Canvas1.SelectedMany.AddRange(clones);
            Canvas1.Selected = clones[^1];
            SelectTool(ToolKind.Select);
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        /// <summary>골라 둔 주석을 편집기 안 클립보드에 담는다. 시스템 클립보드에는 표식만.</summary>
        private void CopyAnnotations()
        {
            if (Canvas1.SelectedMany.Count == 0) return;

            _annClipboard = Canvas1.SelectedMany.Select(a => a.Clone()).ToList();
            try { Clipboard.SetText(AnnClipboardMarker); } catch { }
            StHint.Text = $"주석 {_annClipboard.Count}개 복사 — Ctrl+V 로 붙여넣기";
        }

        /// <summary>
        /// Ctrl+V. 마지막 복사가 주석이면 주석을, 아니면 클립보드의 그림을 붙여넣는다.
        /// 판별은 시스템 클립보드의 표식으로 한다 — 밖에서 그림을 복사해 오면
        /// 표식이 지워져 있으므로 자연히 그림 붙여넣기가 된다.
        /// </summary>
        private void PasteSmart()
        {
            bool wantAnnotations = false;
            try
            {
                wantAnnotations = _annClipboard is { Count: > 0 } &&
                                  Clipboard.ContainsText() && Clipboard.GetText() == AnnClipboardMarker;
            }
            catch { }

            if (!wantAnnotations) { PasteImage(); return; }

            CommitText();
            ConfirmActive();
            PushUndo();

            var clones = new List<Annotation>();
            foreach (Annotation s in _annClipboard!)
            {
                Annotation c = s.Clone();
                c.Move(new Vector(20, 20));
                Canvas1.Items.Add(c);
                clones.Add(c);
                if (c is CounterAnnotation n) n.Number = _counter++;
                else if (c is NumberArrowAnnotation an) an.Number = _counter++;
            }

            Canvas1.SelectedMany.Clear();
            Canvas1.SelectedMany.AddRange(clones);
            Canvas1.Selected = clones[^1];
            SelectTool(ToolKind.Select);
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = $"주석 {clones.Count}개 붙여넣었습니다";
        }

    }
}
