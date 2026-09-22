using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private readonly HashSet<string> _collapsedGroups = new();
        private readonly Dictionary<string, LayerRow> _groupRows = new();
        private static readonly object BackgroundLayerTag = new();
        private bool _alignToBackground;
        private bool _alignKeepGroups = true;
        private bool _backgroundSelected;

        private void SetSingleSelection(Annotation a)
        {
            _backgroundSelected = false;
            Canvas1.SelectedMany.Clear(); Canvas1.SelectedMany.Add(a); Canvas1.Selected = a;
        }

        private void OnGroupLayers(object sender, RoutedEventArgs e) => GroupSelection();
        private void OnUngroupLayers(object sender, RoutedEventArgs e) => UngroupSelection();

        private void GroupSelection()
        {
            ConfirmActive();
            var chosen = Canvas1.SelectedMany.SelectMany(a => LayerGroups.Members(Canvas1.Items, a)).Distinct().ToList();
            if (chosen.Count < 2) { StHint.Text = "그룹으로 묶을 레이어를 둘 이상 고르세요"; return; }
            if (chosen.Any(a => a.Locked)) { StHint.Text = "잠긴 레이어의 잠금을 먼저 풀어 주세요"; return; }
            PushUndo();
            string id = Guid.NewGuid().ToString("N");
            string name = "그룹 " + (Canvas1.Items.Select(a => a.GroupId).Where(g => g != null).Distinct().Count() + 1);
            // Keep render order unchanged; grouping is selection metadata, never image flattening.
            foreach (Annotation a in chosen) { a.GroupId = id; a.GroupName = name; }
            SetSelection(chosen[0]); SelectTool(ToolKind.Select);
            Canvas1.InvalidateVisual(); UpdateStatus();
        }

        private void UngroupSelection()
        {
            var ids = Canvas1.SelectedMany.Where(a => a.GroupId != null).Select(a => a.GroupId!).ToHashSet();
            if (ids.Count == 0) { StHint.Text = "해제할 그룹을 고르세요"; return; }
            PushUndo();
            foreach (Annotation a in Canvas1.Items.Where(a => a.GroupId != null && ids.Contains(a.GroupId)))
            { a.GroupId = null; a.GroupName = null; }
            Canvas1.InvalidateVisual(); UpdateStatus();
        }

        private LayerRow MakeGroupRow(string id)
        {
            var members = Canvas1.Items.Where(a => a.GroupId == id).ToList();
            var row = MakeLayerRow(members[0]);
            row.Panel.Tag = id;
            row.Panel.Margin = new Thickness(0, 5, 0, 3);
            row.Panel.Children.Remove(row.Chip);
            var fold = new Button { Style = (Style)FindResource("EditorIconButton"), Width = 20, Height = 20,
                Padding = new Thickness(3), ToolTip = "그룹 접기 / 펼치기" };
            ToolbarIcon.SetData(fold, Geometry.Parse(_collapsedGroups.Contains(id) ? "M 7,3 L 15,11 L 7,19" : "M 3,7 L 11,15 L 19,7"));
            Grid.SetColumn(fold, 1); row.Panel.Children.Add(fold);
            fold.Click += (_, _) => { if (!_collapsedGroups.Add(id)) _collapsedGroups.Remove(id); RefreshLayers(); };
            // Replace the individual eye so a group visibility change is one undo operation.
            row.Panel.Children.Remove(row.Eye);
            row.Eye = new CheckBox { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "그룹 전체 보이기 / 숨기기" };
            row.Eye.Click += (_, _) =>
            {
                PushUndo(); bool visible = row.Eye.IsChecked != false;
                foreach (Annotation a in Canvas1.Items.Where(a => a.GroupId == id)) a.Visible = visible;
                Canvas1.InvalidateVisual(); UpdateStatus();
            };
            row.Panel.Children.Add(row.Eye);
            row.Label.FontWeight = FontWeights.SemiBold;
            row.Panel.ContextMenu = new ContextMenu();
            var rename = new MenuItem { Header = "그룹 이름 바꾸기..." };
            rename.Click += (_, _) => RenameLayer(members[0], group: true);
            var ungroup = new MenuItem { Header = "그룹 해제", InputGestureText = "Ctrl+Shift+G" };
            ungroup.Click += (_, _) => { SetSelection(members[0]); UngroupSelection(); };
            row.Panel.ContextMenu.Items.Add(rename); row.Panel.ContextMenu.Items.Add(ungroup);
            return row;
        }

        private FrameworkElement MakeBackgroundRow()
        {
            var panel = new Grid { Tag = BackgroundLayerTag, Margin = new Thickness(0, 8, 0, 4), ToolTip = "캡처·불러온 바탕 이미지 · 고정 레이어 0 · 배경 기준 정렬" };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.Children.Add(new Image { Source = _image, Width = 30, Height = 24, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) });
            var label = new StackPanel(); Grid.SetColumn(label, 1);
            label.Children.Add(new TextBlock { Text = "0. 배경 이미지", FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = $"{_image.PixelWidth} × {_image.PixelHeight} px · 고정", Foreground = (Brush)FindResource("FgDim"), FontSize = 10 });
            panel.Children.Add(label); return panel;
        }

        private void AlignSelected(AlignMode mode)
        {
            var units = LayerGroups.Units(Canvas1.Items, Canvas1.SelectedMany, _alignKeepGroups);
            if (units.Count == 0 || (!_alignToBackground && units.Count < 2)) return;
            PushUndo();
            LayerGroups.Align(units, mode, _alignToBackground ? new Rect(0, 0, Canvas1.ImageWidth, Canvas1.ImageHeight) : null);
            Canvas1.InvalidateVisual(); UpdateStatus();
        }
    }
}
