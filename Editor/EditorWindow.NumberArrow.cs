using System;
using System.Windows;
using System.Windows.Input;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private NumberArrowAnnotation? _editingNumberArrow;
        private NumberArrowAnnotation? _numberArrowBeforeEdit;
        private int _numberArrowEditHandle;
        private Point _numberArrowEditStart;
        private bool _numberArrowEditUndoPending;

        private bool TryBeginNumberArrowEdit(Point p)
        {
            if (Canvas1.Active != null || _tool is not (ToolKind.Select or ToolKind.NumberArrow) ||
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return false;

            if (Canvas1.SelectedMany.Count <= 1 && Canvas1.Selected is NumberArrowAnnotation { Locked: false, Visible: true } selected)
            {
                int handle = HandleAt(selected, p);
                if (handle != NoHandle) { BeginNumberArrowEdit(selected, handle, p); return true; }
            }

            if (_tool != ToolKind.NumberArrow || (Keyboard.Modifiers & ModifierKeys.Control) != 0) return false;
            for (int i = Canvas1.Items.Count - 1; i >= 0; i--)
            {
                Annotation item = Canvas1.Items[i];
                if (item.Locked || !item.Visible || !item.HitTestRotated(p)) continue;
                if (item is not NumberArrowAnnotation arrow) return false;
                int handle = HandleAt(arrow, p);
                // Only visible corner handles resize; an unselected arrow's body moves it.
                if (handle >= 2) handle = NoHandle;
                BeginNumberArrowEdit(arrow, handle, p);
                return true;
            }
            return false;
        }

        private void BeginNumberArrowEdit(NumberArrowAnnotation arrow, int handle, Point p)
        {
            _editingNumberArrow = arrow;
            _numberArrowBeforeEdit = (NumberArrowAnnotation)arrow.Clone();
            _numberArrowEditHandle = handle;
            _numberArrowEditStart = p;
            _numberArrowEditUndoPending = true;
            SetSelection(arrow);
            Stage.CaptureMouse();
            Stage.Cursor = handle == NoHandle ? Cursors.SizeAll : HandleCursor(handle, arrow);
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private void MoveNumberArrowEdit(Point p)
        {
            if (_editingNumberArrow is not { } arrow || _numberArrowBeforeEdit is not { } before) return;
            Vector delta = p - _numberArrowEditStart;
            Point center = before.Center, tip = before.Tip;
            double radius = before.Radius, thickness = before.Thickness;
            switch (_numberArrowEditHandle)
            {
                case 0: tip += delta; break;
                case 1: center += delta; break;
                case >= 2:
                    // Scale from the original geometry, keeping the opposite corner fixed.
                    // Radius, number text, shaft and arrowhead keep the same proportions.
                    Rect b = before.Bounds;
                    Point[] corners = { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft };
                    int corner = _numberArrowEditHandle - 2;
                    Point anchor = corners[(corner + 2) % 4];
                    Vector diagonal = corners[corner] - anchor;
                    double factor = 1 + Vector.Multiply(delta, diagonal) / Math.Max(1, diagonal.LengthSquared);
                    factor = Math.Max(Math.Max(6 / before.Radius, 0.5 / before.Thickness), factor);
                    center = anchor + (before.Center - anchor) * factor;
                    tip = anchor + (before.Tip - anchor) * factor;
                    radius *= factor;
                    thickness *= factor;
                    break;
                default: center += delta; tip += delta; break;
            }

            if ((arrow.Center - center).Length < 0.001 && (arrow.Tip - tip).Length < 0.001 &&
                Math.Abs(arrow.Radius - radius) < 0.001 && Math.Abs(arrow.Thickness - thickness) < 0.001) return;
            if (_numberArrowEditUndoPending) { PushUndo(); _numberArrowEditUndoPending = false; }
            arrow.Center = center;
            arrow.Tip = tip;
            arrow.Radius = radius;
            arrow.Thickness = thickness;
            ShowSize(arrow);
            Canvas1.InvalidateVisual();
        }

        private void EndNumberArrowEdit(Point p)
        {
            MoveNumberArrowEdit(p);
            ResetNumberArrowEdit();
            UpdateStatus();
        }

        private void ResetNumberArrowEdit()
        {
            _editingNumberArrow = _numberArrowBeforeEdit = null;
            _numberArrowEditUndoPending = false;
        }
    }
}
