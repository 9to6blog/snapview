using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private EditorGuide? _dragGuide;
        private double _guideOriginalPosition;
        private bool _guideIsNew;

        private void InitializeGuides()
        {
            HorizontalRuler.MouseLeftButtonDown += (_, e) => BeginGuide(true, e);
            VerticalRuler.MouseLeftButtonDown += (_, e) => BeginGuide(false, e);
            foreach (EditorRuler ruler in new[] { HorizontalRuler, VerticalRuler })
            {
                var menu = new ContextMenu();
                var clear = new MenuItem { Header = "가이드 모두 지우기" };
                clear.Click += OnClearGuides; menu.Items.Add(clear); ruler.ContextMenu = menu;
            }
            Scroller.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (TbRulers.IsChecked != true || Keyboard.IsKeyDown(Key.Space)) return;
                Point p = Canvas1.ToImage(e.GetPosition(Canvas1));
                var guide = Canvas1.ManualGuides.LastOrDefault(g =>
                    Math.Abs((g.Horizontal ? p.Y : p.X) - g.Position) <= 5 / Canvas1.Scale);
                if (guide == null) return;
                _dragGuide = guide; _guideIsNew = false; _guideOriginalPosition = guide.Position;
                ViewportGrid.CaptureMouse(); e.Handled = true;
            };
            ViewportGrid.PreviewMouseMove += (_, e) =>
            {
                if (_dragGuide == null) return;
                UpdateGuidePosition(e.GetPosition(Canvas1)); e.Handled = true;
            };
            ViewportGrid.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (_dragGuide == null) return;
                FinishGuide(e.GetPosition(Canvas1)); e.Handled = true;
            };
            ViewportGrid.LostMouseCapture += (_, _) => CancelGuideDrag();
            PreviewKeyDown += (_, e) =>
            {
                if (_dragGuide == null || e.Key != Key.Escape) return;
                CancelGuideDrag(); ViewportGrid.ReleaseMouseCapture(); e.Handled = true;
            };
            Canvas1.LayoutUpdated += (_, _) => UpdateRulers();
            Scroller.ScrollChanged += (_, _) => UpdateRulers();
        }

        private void OnRulersToggled(object sender, RoutedEventArgs e)
        {
            bool visible = TbRulers.IsChecked == true;
            HorizontalRuler.Visibility = VerticalRuler.Visibility = RulerCorner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas1.ShowManualGuides = visible; Canvas1.InvalidateVisual();
            Relayout(); UpdateRulers();
            if (visible) StHint.Text = "줄자에서 끌어 가이드 추가 · 가이드를 끌어 이동 · 그림 밖으로 끌면 삭제";
        }

        private void UpdateRulers()
        {
            if (TbRulers.IsChecked != true) return;
            Point h = Canvas1.TranslatePoint(new Point(), HorizontalRuler);
            Point v = Canvas1.TranslatePoint(new Point(), VerticalRuler);
            HorizontalRuler.SetMetrics(h.X, Canvas1.Scale);
            VerticalRuler.SetMetrics(v.Y, Canvas1.Scale);
        }

        private void BeginGuide(bool horizontal, MouseButtonEventArgs e)
        {
            StartGuide(horizontal, e.GetPosition(Canvas1));
            ViewportGrid.CaptureMouse(); e.Handled = true;
        }

        private void StartGuide(bool horizontal, Point canvasPoint)
        {
            _dragGuide = new EditorGuide { Horizontal = horizontal };
            _guideIsNew = true; Canvas1.ManualGuides.Add(_dragGuide);
            UpdateGuidePosition(canvasPoint);
        }

        private void UpdateGuidePosition(Point canvasPoint)
        {
            if (_dragGuide == null) return;
            Point p = Canvas1.ToImage(canvasPoint);
            _dragGuide.Position = Math.Round(_dragGuide.Horizontal ? p.Y : p.X);
            StHint.Text = $"{(_dragGuide.Horizontal ? "가로" : "세로")} 가이드: {_dragGuide.Position:0} px · Esc 취소";
            Canvas1.InvalidateVisual();
        }

        private void FinishGuide(Point canvasPoint)
        {
            if (_dragGuide == null) return;
            UpdateGuidePosition(canvasPoint);
            Point p = Canvas1.ToImage(canvasPoint);
            if (p.X < 0 || p.Y < 0 || p.X > Canvas1.ImageWidth || p.Y > Canvas1.ImageHeight)
                Canvas1.ManualGuides.Remove(_dragGuide);
            _dragGuide = null;
            ViewportGrid.ReleaseMouseCapture(); Canvas1.InvalidateVisual();
            StHint.Text = $"가이드 {Canvas1.ManualGuides.Count}개 · 줄자 옆 지우기 버튼으로 모두 삭제";
        }

        private void CancelGuideDrag()
        {
            if (_dragGuide == null) return;
            if (_guideIsNew) Canvas1.ManualGuides.Remove(_dragGuide);
            else _dragGuide.Position = _guideOriginalPosition;
            _dragGuide = null; Canvas1.InvalidateVisual();
        }

        private void OnClearGuides(object sender, RoutedEventArgs e)
        {
            CancelGuideDrag(); Canvas1.ManualGuides.Clear(); Canvas1.InvalidateVisual();
            StHint.Text = "가이드를 모두 지웠습니다";
        }
    }
}
