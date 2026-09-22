using System;
using System.Windows;
using System.Windows.Input;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private enum NumberArrowPhase { None, Pressed, AwaitingTip }
        private NumberArrowPhase _numberArrowPhase;
        private bool _numberArrowDragged;
        private MagnifierAnnotation? _dragMagnifier;
        private MagnifierPart _dragMagnifierPart;
        private Vector _magnifierDragOffset;
        private bool _placingMagnifier;
        private bool _magnifierPlacementDragged;
        private bool _magnifierUndoPending;
        private Point _placementStart;

        private bool PastDragThreshold(Point p)
        {
            Vector delta = (p - _placementStart) * Canvas1.Scale;
            return Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                   Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
        }

        private void BeginNumberArrow(Point p)
        {
            SetSelection(null);
            Canvas1.Active = CreateNumberArrow(p);
            Canvas1.ShowActiveHandles = false;
            _placementStart = p;
            _numberArrowPhase = NumberArrowPhase.Pressed;
            _numberArrowDragged = false;
            Stage.CaptureMouse();
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = "번호 위치에서 화살촉까지 끌기 · 또는 클릭 후 화살촉 위치를 다시 클릭";
        }

        private void UpdateNumberArrowTip(Point p)
        {
            if (Canvas1.Active is not NumberArrowAnnotation arrow) return;
            Vector direction = arrow.Tip - arrow.Center;
            arrow.Tip = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                ? Constrain(arrow.Center, p, ToolKind.Arrow) : p;
            arrow.EnsureVisibleArrow(direction);
            Canvas1.InvalidateVisual();
            ShowSize(arrow);
        }

        private void CompleteNumberArrow(Point p)
        {
            if (Canvas1.Active is not NumberArrowAnnotation arrow) return;
            UpdateNumberArrowTip(p);
            ConfirmActive();
            SetSelection(arrow);
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void BeginMagnifierPlacement(Point p)
        {
            var mag = (MagnifierAnnotation)Init(new MagnifierAnnotation
            {
                SourceCenter = p, Radius = _magnifierSourceRadius, DisplayRadius = _magnifierDisplayRadius
            });
            double r = mag.DisplayRadius;
            mag.Center = new Point(
                Math.Clamp(p.X + r + 46, r, Math.Max(r, Canvas1.ImageWidth - r)),
                Math.Clamp(p.Y - r - 26, r, Math.Max(r, Canvas1.ImageHeight - r)));
            SetSelection(null);
            Canvas1.Active = mag;
            Canvas1.ShowActiveHandles = true;
            _dragMagnifier = mag;
            _dragMagnifierPart = MagnifierPart.Display;
            _magnifierDragOffset = new Vector();
            _placingMagnifier = true;
            _magnifierPlacementDragged = false;
            _placementStart = p;
            Stage.CaptureMouse();
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = "누른 곳이 확대 대상 · 그대로 끌어 확대 창 배치 · 각 원 안을 끌어 따로 이동";
        }

        private bool BeginMagnifierMove(MagnifierAnnotation mag, Point p, MagnifierPart? targetPart = null)
        {
            MagnifierPart part = targetPart ?? mag.PartAt(p);
            if (part == MagnifierPart.None) return false;
            _dragMagnifier = mag;
            _dragMagnifierPart = part;
            _magnifierDragOffset = (part == MagnifierPart.Source ? mag.SourceCenter : mag.Center) - p;
            _placingMagnifier = false;
            _magnifierUndoPending = !ReferenceEquals(Canvas1.Active, mag);
            if (_magnifierUndoPending) SetSelection(mag);
            Stage.CaptureMouse();
            Stage.Cursor = Cursors.SizeAll;
            Canvas1.InvalidateVisual();
            UpdateStatus();
            return true;
        }

        private void MoveMagnifier(Point p)
        {
            if (_dragMagnifier is not { } mag) return;
            if (_placingMagnifier && !_magnifierPlacementDragged)
            {
                if (!PastDragThreshold(p)) return;
                _magnifierPlacementDragged = true;
            }
            Point target = p + _magnifierDragOffset;
            Point current = _dragMagnifierPart == MagnifierPart.Source ? mag.SourceCenter : mag.Center;
            if ((target - current).Length < 0.001) return;
            if (_magnifierUndoPending) { PushUndo(); _magnifierUndoPending = false; }
            if (_dragMagnifierPart == MagnifierPart.Source) mag.SourceCenter = target;
            else mag.Center = target;
            Canvas1.InvalidateVisual();
        }

        private void ResetPlacementGestures()
        {
            _numberArrowPhase = NumberArrowPhase.None;
            _numberArrowDragged = false;
            _dragMagnifier = null;
            _placingMagnifier = false;
            _magnifierUndoPending = false;
        }
    }
}
