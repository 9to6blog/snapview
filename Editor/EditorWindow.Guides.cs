using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private EditorGuide? _dragGuide;
        private double _guideOriginalPosition;
        private bool _guideIsNew;
        private EditorGuide? _guideOriginal;
        private EditorSnapshot? _guidesBeforeEdit;
        private Point _guideDragStart;
        private int _guidePart = -1;

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
                if (TbDiagonalGuide.IsChecked == true)
                {
                    StartDiagonalGuide(p); ViewportGrid.CaptureMouse(); e.Handled = true; return;
                }
                var guide = Canvas1.ManualGuides.LastOrDefault(g => g.DistanceTo(p) <= 5 / Canvas1.Scale);
                if (guide == null) return;
                _guidesBeforeEdit = Current(); _guideOriginal = guide.Clone(); _guideDragStart = p;
                _guidePart = guide.Diagonal && (p - guide.Start).Length <= 9 / Canvas1.Scale ? 0
                    : guide.Diagonal && (p - guide.End).Length <= 9 / Canvas1.Scale ? 1 : -1;
                _dragGuide = guide; _guideIsNew = false; _guideOriginalPosition = guide.Position;
                ViewportGrid.CaptureMouse(); e.Handled = true;
            };
            ViewportGrid.PreviewMouseMove += (_, e) =>
            {
                if (_dragGuide == null)
                {
                    if (TbRulers.IsChecked == true && Canvas1.ShowGuideMeasurements && Canvas1.ManualGuides.Count > 0)
                    {
                        Point p = Canvas1.ToImage(e.GetPosition(Canvas1));
                        foreach (Rect cell in GuideMeasurements.Cells(Canvas1.ManualGuides, Canvas1.ImageWidth, Canvas1.ImageHeight))
                            if (cell.Contains(p)) { StHint.Text = $"구간 {cell.Width:0.#} × {cell.Height:0.#} px · X {cell.Left:0.#}–{cell.Right:0.#} · Y {cell.Top:0.#}–{cell.Bottom:0.#}"; break; }
                    }
                    return;
                }
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
            if (!visible) { TbDiagonalGuide.IsChecked = false; CancelGuideDrag(); }
            Relayout(); UpdateRulers();
            if (visible) StHint.Text = "줄자에서 끌어 가이드 추가 · 치수는 원본 px 기준 · 대각 버튼을 누른 뒤 드래그";
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
            _guidesBeforeEdit = Current(); _guideOriginal = null;
            _dragGuide = new EditorGuide { Horizontal = horizontal };
            _guideIsNew = true; Canvas1.ManualGuides.Add(_dragGuide);
            UpdateGuidePosition(canvasPoint);
        }

        private void UpdateGuidePosition(Point canvasPoint)
        {
            if (_dragGuide == null) return;
            Point p = Canvas1.ToImage(canvasPoint);
            if (_dragGuide.Diagonal)
            {
                if (_guideIsNew || _guidePart == 1) _dragGuide.End = p;
                else if (_guidePart == 0) _dragGuide.Start = p;
                else if (_guideOriginal != null)
                {
                    Vector delta = p - _guideDragStart;
                    _dragGuide.Start = _guideOriginal.Start + delta; _dragGuide.End = _guideOriginal.End + delta;
                }
                StHint.Text = $"대각 가이드: {_dragGuide.Length:0.#} px · {_dragGuide.Angle:0.#}° · 끝점은 각도·길이, 선은 이동";
                Canvas1.InvalidateVisual(); return;
            }
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
            if (_dragGuide.Diagonal && _dragGuide.Length < 2) Canvas1.ManualGuides.Remove(_dragGuide);
            bool changed = _guideIsNew ? Canvas1.ManualGuides.Contains(_dragGuide)
                : !Canvas1.ManualGuides.Contains(_dragGuide) || _guideOriginal == null || _guideOriginal.Position != _dragGuide.Position ||
                  _guideOriginal.Start != _dragGuide.Start || _guideOriginal.End != _dragGuide.End;
            if (changed && _guidesBeforeEdit != null) { _undoStack.Push(_guidesBeforeEdit); MarkDirty(); }
            _dragGuide = null;
            _guidesBeforeEdit = null; TbDiagonalGuide.IsChecked = false;
            ViewportGrid.ReleaseMouseCapture(); Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = $"가이드 {Canvas1.ManualGuides.Count}개 · 줄자 옆 지우기 버튼으로 모두 삭제";
        }

        private void CancelGuideDrag()
        {
            if (_dragGuide == null) return;
            if (_guideIsNew) Canvas1.ManualGuides.Remove(_dragGuide);
            else
            {
                _dragGuide.Position = _guideOriginalPosition;
                if (_guideOriginal != null) { _dragGuide.Start = _guideOriginal.Start; _dragGuide.End = _guideOriginal.End; }
            }
            _dragGuide = null; Canvas1.InvalidateVisual();
            _guidesBeforeEdit = null;
        }

        private void OnClearGuides(object sender, RoutedEventArgs e)
        {
            CancelGuideDrag();
            if (Canvas1.ManualGuides.Count == 0) return;
            PushUndo(); Canvas1.ManualGuides.Clear(); Canvas1.InvalidateVisual(); UpdateStatus();
            StHint.Text = "가이드를 모두 지웠습니다";
        }

        private void OnDiagonalGuide(object sender, RoutedEventArgs e)
        {
            if (TbDiagonalGuide.IsChecked != true) return;
            TbRulers.IsChecked = true; OnRulersToggled(sender, e);
            StHint.Text = "그림 위에서 드래그해 대각 가이드의 두 기준점을 정하세요";
        }

        private void StartDiagonalGuide(Point imagePoint)
        {
            _guidesBeforeEdit = Current(); _guideOriginal = null; _guideIsNew = true; _guidePart = 1;
            _dragGuide = new EditorGuide { Diagonal = true, Start = imagePoint, End = imagePoint };
            Canvas1.ManualGuides.Add(_dragGuide);
        }

        private void OnGuideMeasurements(object sender, RoutedEventArgs e)
        {
            Canvas1.ShowGuideMeasurements = TbGuideMeasurements.IsChecked == true;
            Canvas1.InvalidateVisual();
        }

        private void RestoreGuides(System.Collections.Generic.IEnumerable<EditorGuide>? guides)
        {
            CancelGuideDrag(); Canvas1.ManualGuides.Clear();
            if (guides != null) Canvas1.ManualGuides.AddRange(guides.Select(g => g.Clone()));
            if (Canvas1.ManualGuides.Count > 0) { TbRulers.IsChecked = true; OnRulersToggled(this, new RoutedEventArgs()); }
        }
    }
}
