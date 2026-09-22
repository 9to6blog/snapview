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
    /// <summary>마우스: 조절점·이동·스냅·그리기.</summary>
    public partial class EditorWindow
    {
        // ================================================= 마우스

        private Point ImagePoint(MouseEventArgs e)
        {
            Point img = Canvas1.ToImage(e.GetPosition(Canvas1));
            return new Point(Math.Clamp(img.X, 0, Canvas1.ImageWidth),
                             Math.Clamp(img.Y, 0, Canvas1.ImageHeight));
        }

        private const int NoHandle = -1;
        private const int RotateHandle = -2;   // 위쪽 회전 손잡이

        private int HandleAt(Annotation a, Point p)
        {
            p = a.ToLocal(p);   // 회전해 둔 주석의 조절점은 눕기 전 좌표에 있다
            IReadOnlyList<Point> handles = Canvas1.EditHandles(a);
            double tol = Canvas1.ToImageLength(9);
            for (int i = 0; i < handles.Count; i++)
            {
                if (Math.Abs(p.X - handles[i].X) <= tol && Math.Abs(p.Y - handles[i].Y) <= tol)
                    return i;
            }
            return NoHandle;
        }

        /// <summary>조절점 + 회전 손잡이까지 판정. 회전 손잡이면 <see cref="RotateHandle"/>.</summary>
        private int HandleAtOrRotate(Annotation a, Point p)
        {
            if (a.CanRotate && !a.Bounds.IsEmpty &&
                (p - Canvas1.RotateKnobPoint(a)).Length <= Canvas1.ToImageLength(10))
                return RotateHandle;
            return HandleAt(a, p);
        }

        /// <summary>
        /// 끌던 무리를 커서를 따라 옮기되, 다른 주석·캔버스의 기준선(가장자리·가운데)에
        /// 가까워지면 착 붙인다(스마트 가이드). Alt 를 누르면 스냅 없이 그대로 끈다.
        /// </summary>
        private void MoveWithSnap(IReadOnlyList<Annotation> group, Point cursor)
        {
            group = group.Where(a => !a.Locked).ToList();
            if (group.Count == 0) return;

            Rect current = ArrangeTools.Union(group);
            if (current.IsEmpty || _dragUnionStart.IsEmpty)
            {
                // 테두리가 없는 특수한 경우: 그냥 증분으로 끈다.
                Vector d0 = cursor - _dragLast;
                foreach (Annotation a in group) a.Move(d0);
                _dragLast = cursor;
                Canvas1.InvalidateVisual();
                return;
            }

            var target = new Rect(_dragUnionStart.TopLeft + (cursor - _dragAnchor),
                                  _dragUnionStart.Size);

            double? gx = null, gy = null;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
            {
                IEnumerable<Rect> others = Canvas1.Items
                    .Where(a => a is not EraseAnnotation && !group.Contains(a))
                    .Select(a => a.Bounds);

                SnapGuide.Result snap = SnapGuide.Solve(
                    target, others, Canvas1.ImageWidth, Canvas1.ImageHeight,
                    Canvas1.ToImageLength(6));
                target.Offset(snap.Adjust.X, snap.Adjust.Y);
                gx = snap.GuideX;
                gy = snap.GuideY;
            }

            Vector moveBy = target.TopLeft - current.TopLeft;
            if (Math.Abs(moveBy.X) > 0.0001 || Math.Abs(moveBy.Y) > 0.0001)
                foreach (Annotation a in group) a.Move(moveBy);

            _dragLast = cursor;
            Canvas1.GuideX = gx;
            Canvas1.GuideY = gy;
            Canvas1.InvalidateVisual();
        }

        /// <summary>회전 손잡이를 끄는 중: 축에서 손끝까지의 방향이 곧 각도. Shift 는 15° 단위.</summary>
        private static void RotateTo(Annotation a, Point p)
        {
            Vector v = p - a.RotationCenter;
            if (v.Length < 2) return;

            double ang = Math.Atan2(v.Y, v.X) * 180 / Math.PI + 90;   // 손잡이가 위쪽(-90°)이라
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                ang = Math.Round(ang / 15) * 15;

            ang %= 360;
            if (ang > 180) ang -= 360;
            if (ang < -180) ang += 360;
            a.RotationDeg = ang;
        }

        private void OnStageDown(object sender, MouseButtonEventArgs e)
        {
            if (_editingText != null) { CommitText(); return; }

            // Space+끌기: 화면 이동. 도구와 무관하다.
            if (Keyboard.IsKeyDown(Key.Space)) { BeginPan(e); return; }

            HandlePointerDown(Canvas1.ToImage(e.GetPosition(Canvas1)), IsInsideImage(e), e.ClickCount);
        }

        private void HandlePointerDown(Point p, bool insideImage, int clickCount)
        {
            if (_numberArrowPhase == NumberArrowPhase.AwaitingTip)
            {
                if (insideImage) CompleteNumberArrow(p);
                return;
            }

            // Selected handles can extend into the margin beside the image.
            if (TryBeginNumberArrowEdit(p)) return;

            // 그림 바깥 여백을 눌렀다: 그리기가 아니라 "확정하고 선택 풀기" 다.
            // 예전엔 좌표가 가장자리로 잘려서 여백을 눌러도 0픽셀 도형이 생겼다.
            if (!insideImage)
            {
                ConfirmActive();
                if (Canvas1.SelectedMany.Count > 0) { SetSelection(null); Canvas1.InvalidateVisual(); UpdateStatus(); }
                return;
            }

            if (clickCount == 2)
            {
                // 글자 더블클릭: 다시 편집. 자르기 영역 안 더블클릭: 확정.
                if (Canvas1.Active is CropAnnotation crop && crop.Bounds.Contains(p)) { ConfirmActive(); return; }
                if (_tool == ToolKind.Select && Canvas1.Active == null)
                {
                    for (int i = Canvas1.Items.Count - 1; i >= 0; i--)
                        if (Canvas1.Items[i] is TextAnnotation t && !t.Locked && t.HitTestRotated(p))
                        {
                            EditExistingText(t);
                            return;
                        }
                }
            }

            // 1) 확정 대기 중인 주석을 먼저 만져 본다
            if (Canvas1.Active is { } active)
            {
                int h = HandleAtOrRotate(active, p);
                if (h == 1 && active is MagnifierAnnotation sourceMag &&
                    BeginMagnifierMove(sourceMag, p, MagnifierPart.Source)) return;
                if (h != NoHandle)
                {
                    _handleIndex = h;
                    Stage.CaptureMouse();
                    return;
                }
                if (active is MagnifierAnnotation activeMagnifier && BeginMagnifierMove(activeMagnifier, p)) return;
                if (active.HitTestRotated(p) || active.Bounds.Contains(active.ToLocal(p)))
                {
                    _movingActive = true;
                    _dragLast = p;
                    _dragAnchor = p;
                    _dragUnionStart = active.Bounds;
                    Stage.CaptureMouse();
                    return;
                }
                ConfirmActive();      // 바깥을 눌렀으면 확정하고 새로 시작
            }

            // 2) 스포이드 — 그림에서 색을 집고 쓰던 도구로 돌아간다
            if (_tool == ToolKind.Picker)
            {
                PickColorAt(p);
                return;
            }

            // 3) 픽셀 지우개 — 그림 자체를 투명하게 판다
            if (_tool == ToolKind.PixelEraser)
            {
                _erasing = true;
                Stage.CaptureMouse();
                BeginPixelErase(p);
                return;
            }

            // 4) 영역 선택 — 끌어서 네모를 잡는다
            if (_tool == ToolKind.RegionSelect)
            {
                _draggingRegion = true;
                _regionStart = p;
                _region = Rect.Empty;
                Canvas1.Region = Rect.Empty;
                Stage.CaptureMouse();
                return;
            }

            // 5) 지우개 — 지나간 자리를 파낸다(주석을 통째로 지우지 않는다)
            if (_tool == ToolKind.Eraser)
            {
                PushUndo();
                _erasing = true;
                Stage.CaptureMouse();
                BeginErase(p);
                return;
            }

            // 자동 선택 — 클릭한 자리와 비슷한 색 영역을 고른다
            if (_tool == ToolKind.Wand)
            {
                WandClick(p, additive: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                return;
            }

            // 4) 선택 도구
            if (_tool == ToolKind.Select)
            {
                bool shiftKey = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                bool ctrlKey = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

                // 여럿을 골랐으면 무리 전체 테두리의 조절점으로 함께 키우고 줄인다.
                if (!shiftKey && Canvas1.SelectedMany.Count > 1)
                {
                    int gh = EditorMath.BoxHandleAt(ArrangeTools.Union(Canvas1.SelectedMany), p, Canvas1.ToImageLength(9));
                    if (gh >= 0)
                    {
                        PushUndo();
                        _groupResizing = true;
                        _groupHandle = gh;
                        Stage.CaptureMouse();
                        return;
                    }
                }

                // 이미 골라 둔 주석의 조절점을 먼저 만져 본다 —
                // 확정한 주석도 크기를 다시 바꿀 수 있다.
                if (!shiftKey && Canvas1.SelectedMany.Count <= 1 &&
                    Canvas1.Selected is { Locked: false, Visible: true } sel)
                {
                    int hs = HandleAtOrRotate(sel, p);
                    if (hs == 1 && sel is MagnifierAnnotation selectedSourceMag &&
                        BeginMagnifierMove(selectedSourceMag, p, MagnifierPart.Source)) return;
                    if (hs != NoHandle)
                    {
                        PushUndo();
                        _resizingSelected = sel;
                        _handleIndex = hs;
                        Stage.CaptureMouse();
                        return;
                    }
                }

                Annotation? hit = null;
                for (int i = Canvas1.Items.Count - 1; i >= 0; i--)   // 위에 있는 것부터
                {
                    if (Canvas1.Items[i].Locked || !Canvas1.Items[i].Visible) continue;
                    if (Canvas1.Items[i].HitTestRotated(p)) { hit = Canvas1.Items[i]; break; }
                }

                if (hit != null)
                {
                    if (shiftKey)
                    {
                        // Shift+클릭: 넣었다 뺐다. 끌기는 시작하지 않는다 —
                        // 빼려고 클릭한 손이 밀려서 주석이 따라오면 곤란하다.
                        ToggleSelection(hit);
                        Canvas1.InvalidateVisual();
                        UpdateStatus();
                        return;
                    }

                    if (!ctrlKey && hit is MagnifierAnnotation { GroupId: null } pickedMagnifier &&
                        (Canvas1.SelectedMany.Count <= 1 || !Canvas1.SelectedMany.Contains(hit)) &&
                        BeginMagnifierMove(pickedMagnifier, p)) return;

                    // 이미 골라 둔 무리 안을 눌렀으면 무리를 유지한 채 함께 끈다.
                    if (!Canvas1.SelectedMany.Contains(hit)) SetSelection(hit);
                    else Canvas1.Selected = hit;

                    if (ctrlKey)
                    {
                        // Ctrl+끌기: 복제본을 만들어 그것을 끈다. 원본은 제자리에.
                        DuplicateSelection(new Vector(0, 0));
                        hit = Canvas1.Selected ?? hit;
                    }
                    else PushUndo();
                    _draggingSelected = hit;
                    _dragLast = p;
                    _dragAnchor = p;
                    _dragUnionStart = ArrangeTools.Union(Canvas1.SelectedMany);
                    Stage.CaptureMouse();
                    Canvas1.InvalidateVisual();
                    UpdateStatus();
                    return;
                }

                // 빈 곳: 올가미 시작. Shift 면 기존 선택에 더한다.
                if (!shiftKey) SetSelection(null);
                _banding = true;
                _bandAdditive = shiftKey;
                _bandStart = p;
                Stage.CaptureMouse();
                Canvas1.InvalidateVisual();
                UpdateStatus();
                return;
            }

            // 3) 글자는 입력칸부터
            if (_tool == ToolKind.Text) { BeginText(p); return; }

            // 첫 누름이 확대 대상, 드래그 끝이 확대 창. 등록한 원도 같은 도구로 바로 움직인다.
            if (_tool == ToolKind.Magnifier)
            {
                for (int i = Canvas1.Items.Count - 1; i >= 0; i--)
                {
                    Annotation item = Canvas1.Items[i];
                    if (!item.Visible || item.Locked || !item.HitTestRotated(p)) continue;
                    if (item is MagnifierAnnotation existing)
                    {
                        int handle = ReferenceEquals(Canvas1.Selected, existing) ? HandleAt(existing, p) : NoHandle;
                        if (handle == 1 && BeginMagnifierMove(existing, p, MagnifierPart.Source)) return;
                        if (handle != NoHandle)
                        {
                            PushUndo(); _resizingSelected = existing; _handleIndex = handle;
                            Stage.CaptureMouse(); return;
                        }
                        if (BeginMagnifierMove(existing, p)) return;
                    }
                    break;
                }
                BeginMagnifierPlacement(p);
                return;
            }

            // 4) 번호는 클릭 한 번으로 놓고 조절 상태로
            if (_tool == ToolKind.Counter)
            {
                Canvas1.Active = Init(new CounterAnnotation
                {
                    Center = p,
                    Number = _counter,
                    Radius = Math.Max(12, _thickness * 5)
                });
                Canvas1.ShowActiveHandles = true;
                Canvas1.InvalidateVisual();
                UpdateStatus();
                return;
            }

            if (_tool == ToolKind.NumberArrow) { BeginNumberArrow(p); return; }

            // 5) 나머지는 끌어서 그린다
            _drawing = true;
            _startImage = p;
            Canvas1.Active = CreateAnnotation(p);
            Canvas1.ShowActiveHandles = false;
            Stage.CaptureMouse();
        }

        private void OnStageMove(object sender, MouseEventArgs e)
        {
            if (_panning) { PanTo(e); return; }
            bool pressed = e.LeftButton == MouseButtonState.Pressed;
            if (!pressed && _numberArrowPhase != NumberArrowPhase.AwaitingTip) { UpdateHover(e); return; }
            HandlePointerMove(_editingNumberArrow != null ? Canvas1.ToImage(e.GetPosition(Canvas1)) : ImagePoint(e), pressed);
        }

        private void HandlePointerMove(Point p, bool pressed)
        {
            if (_numberArrowPhase == NumberArrowPhase.AwaitingTip)
            {
                UpdateNumberArrowTip(p); return;
            }
            if (!pressed) return;
            if (_editingNumberArrow != null) { MoveNumberArrowEdit(p); return; }
            if (_numberArrowPhase == NumberArrowPhase.Pressed)
            {
                _numberArrowDragged |= PastDragThreshold(p);
                if (_numberArrowDragged) UpdateNumberArrowTip(p);
                return;
            }
            if (_dragMagnifier != null) { MoveMagnifier(p); return; }
            StPos.Text = $"{(int)p.X}, {(int)p.Y}";

            if (_groupResizing)
            {
                Rect union = ArrangeTools.Union(Canvas1.SelectedMany);
                Rect target = EditorMath.ResizeBox(union, _groupHandle, p);
                if (target.Width >= 4 && target.Height >= 4)
                {
                    ArrangeTools.ScaleGroup(Canvas1.SelectedMany, union, target);
                    foreach (Annotation g in Canvas1.SelectedMany) if (g is PixelateAnnotation gp) gp.Fast = true;
                    StSize.Text = $"{(int)target.Width} × {(int)target.Height}";
                }
                Canvas1.InvalidateVisual();
                return;
            }

            if (_draggingRegion)
            {
                _region = ClipToImage(new Rect(_regionStart, p));
                Canvas1.Region = _region;
                Canvas1.InvalidateVisual();
                return;
            }

            if (_erasing)
            {
                if (_tool == ToolKind.PixelEraser) ErasePixelsAt(p);
                else EraseAt(p);
                return;
            }

            if (_banding)
            {
                Canvas1.RubberBand = new Rect(_bandStart, p);
                Canvas1.InvalidateVisual();
                return;
            }

            if (_resizingSelected != null && _handleIndex == RotateHandle)
            {
                RotateTo(_resizingSelected, p);
                Canvas1.InvalidateVisual();
                return;
            }

            if (_resizingSelected != null && _handleIndex >= 0)
            {
                if (_resizingSelected is PixelateAnnotation rp) rp.Fast = true;
                _resizingSelected.DragHandle(_handleIndex, _resizingSelected.ToLocal(p));
                ShowSize(_resizingSelected);
                Canvas1.InvalidateVisual();
                return;
            }

            if (_draggingSelected != null)
            {
                // 골라 둔 것 전부가 함께, 기준선에 스냅하며 움직인다.
                IReadOnlyList<Annotation> group = Canvas1.SelectedMany.Count > 0
                    ? Canvas1.SelectedMany
                    : new[] { _draggingSelected };
                foreach (Annotation g in group) if (g is PixelateAnnotation gp) gp.Fast = true;
                MoveWithSnap(group, p);
                return;
            }

            if (Canvas1.Active is { } active)
            {
                if (_handleIndex == RotateHandle)
                {
                    RotateTo(active, p);
                    Canvas1.InvalidateVisual();
                    return;
                }
                if (_handleIndex >= 0)
                {
                    if (active is PixelateAnnotation ap) ap.Fast = true;
                    if (active is CropAnnotation) p = SnapCropHandlePoint(p, _handleIndex);
                    active.DragHandle(_handleIndex, active.ToLocal(p));
                    ShowSize(active);
                    Canvas1.InvalidateVisual();
                    return;
                }
                if (_movingActive)
                {
                    MoveWithSnap(new[] { active }, p);
                    return;
                }
            }

            if (!_drawing || Canvas1.Active == null) return;

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            switch (Canvas1.Active)
            {
                case PathAnnotation path:
                    // Shift: 획의 시작점 기준으로 가로/세로 일자로만 나간다.
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && path.Points.Count > 0)
                    {
                        Point p0 = path.Points[0];
                        p = Math.Abs(p.X - p0.X) >= Math.Abs(p.Y - p0.Y)
                            ? new Point(p.X, p0.Y)
                            : new Point(p0.X, p.Y);
                    }
                    // 너무 촘촘한 점은 버려서 렌더링을 가볍게 유지한다.
                    if (path.Points.Count == 0 || (p - path.Points[^1]).Length > 1.5)
                        path.Points.Add(p);
                    break;

                case ShapeAnnotation shape:
                {
                    // Shift 는 정사각형·45°, Alt 는 누른 자리를 가운데로.
                    Point end = shift ? Constrain(_startImage, p, shape.Kind) : p;
                    (shape.Start, shape.End) = alt ? EditorMath.FromCenter(_startImage, end) : (_startImage, end);
                    break;
                }

                case PixelateAnnotation pix:
                    pix.Fast = true;   // 끄는 동안은 1/4 로 줄여 계산. 놓으면 정밀하게.
                    (pix.Start, pix.End) = alt ? EditorMath.FromCenter(_startImage, p) : (_startImage, p);
                    break;

                case SpotlightAnnotation spot:
                    (spot.Start, spot.End) = alt ? EditorMath.FromCenter(_startImage, p) : (_startImage, p);
                    break;

                case CropAnnotation crop:
                {
                    if (_cropAspect <= 0) p = SnapCropPoint(p, snapX: true, snapY: true);
                    Point end = _cropAspect > 0 ? EditorMath.FitAspect(_startImage, p, _cropAspect) : p;
                    (crop.Start, crop.End) = alt ? EditorMath.FromCenter(_startImage, end) : (_startImage, end);
                    break;
                }
            }
            ShowSize(Canvas1.Active);
            Canvas1.InvalidateVisual();
        }

        private void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            Stage.ReleaseMouseCapture();
            if (_panning) { EndPan(); return; }
            HandlePointerUp(_editingNumberArrow != null ? Canvas1.ToImage(e.GetPosition(Canvas1)) : ImagePoint(e));
        }

        private void HandlePointerUp(Point p)
        {
            if (_editingNumberArrow != null) { EndNumberArrowEdit(p); return; }
            if (_numberArrowPhase == NumberArrowPhase.Pressed)
            {
                if (_numberArrowDragged || PastDragThreshold(p)) CompleteNumberArrow(p);
                else
                {
                    _numberArrowPhase = NumberArrowPhase.AwaitingTip;
                    StHint.Text = "화살촉 위치를 클릭하면 등록됩니다 · Shift 45° · Esc 취소";
                }
                return;
            }
            if (_dragMagnifier is { } mag)
            {
                MoveMagnifier(p);
                bool placing = _placingMagnifier;
                ResetPlacementGestures();
                if (placing) { ConfirmActive(); SetSelection(mag); }
                Canvas1.InvalidateVisual(); UpdateStatus(); return;
            }
            ClearFast();   // 끌기가 끝났으니 가리개를 정밀하게 다시 계산한다

            if (_groupResizing)
            {
                _groupResizing = false;
                _groupHandle = -1;
                UpdateStatus();
                return;
            }

            // 스마트 가이드는 끌기가 끝나면 치운다.
            if (Canvas1.GuideX.HasValue || Canvas1.GuideY.HasValue)
            {
                Canvas1.GuideX = Canvas1.GuideY = null;
                Canvas1.InvalidateVisual();
            }

            if (_banding)
            {
                _banding = false;
                Rect band = Canvas1.RubberBand;
                Canvas1.RubberBand = Rect.Empty;

                if (band.Width >= 3 || band.Height >= 3)
                {
                    if (!_bandAdditive) Canvas1.SelectedMany.Clear();
                    foreach (Annotation a in Canvas1.Items)
                    {
                        if (a is EraseAnnotation || a.Locked) continue;
                        if (band.IntersectsWith(a.Bounds) && !Canvas1.SelectedMany.Contains(a))
                            foreach (Annotation member in LayerGroups.Members(Canvas1.Items, a))
                                if (!Canvas1.SelectedMany.Contains(member)) Canvas1.SelectedMany.Add(member);
                    }
                    Canvas1.Selected = Canvas1.SelectedMany.Count > 0 ? Canvas1.SelectedMany[^1] : null;
                }

                Canvas1.InvalidateVisual();
                UpdateStatus();
                return;
            }

            if (_draggingRegion)
            {
                _draggingRegion = false;
                StHint.Text = _region.Width < 2 || _region.Height < 2
                    ? "영역이 너무 작습니다"
                    : $"영역 {(int)_region.Width}×{(int)_region.Height} — Ctrl+C 복사 · Ctrl+X 잘라내기";
                RegionInfo.Text = _region.Width < 2 || _region.Height < 2
                    ? "끌어서 네모 영역을 잡으세요"
                    : $"영역 {(int)_region.Width}×{(int)_region.Height}";
                UpdateStatus();
                return;
            }

            if (_erasing)
            {
                _erasing = false;
                if (_tool == ToolKind.PixelEraser) EndPixelErase();
                else EndErase();
                UpdateStatus();
                return;
            }

            if (_draggingSelected != null)
            {
                _draggingSelected = null;
                UpdateStatus();
                return;
            }

            if (_handleIndex != NoHandle || _movingActive)
            {
                _handleIndex = NoHandle;
                _movingActive = false;
                _resizingSelected = null;
                UpdateStatus();
                return;
            }

            if (!_drawing || Canvas1.Active == null) { _drawing = false; return; }
            _drawing = false;

            if (IsTooSmall(Canvas1.Active))
            {
                Canvas1.Active = null;
                Canvas1.InvalidateVisual();
                return;
            }

            // 여기서 바로 확정하지 않는다. 조절점을 붙여 두고 Enter 를 기다린다.
            Canvas1.ShowActiveHandles = true;
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        private static bool IsTooSmall(Annotation a) => a switch
        {
            PathAnnotation p => p.Points.Count < 2,
            ShapeAnnotation s => (s.End - s.Start).Length < 3,
            PixelateAnnotation x => Math.Abs(x.End.X - x.Start.X) < 4 || Math.Abs(x.End.Y - x.Start.Y) < 4,
            SpotlightAnnotation l => Math.Abs(l.End.X - l.Start.X) < 6 || Math.Abs(l.End.Y - l.Start.Y) < 6,
            CropAnnotation c => Math.Abs(c.End.X - c.Start.X) < 8 || Math.Abs(c.End.Y - c.Start.Y) < 8,
            _ => false
        };

        /// <summary>Shift 를 누르면 45도 단위 / 정사각형으로 맞춘다.</summary>
        private static Point Constrain(Point start, Point p, ToolKind kind)
        {
            double dx = p.X - start.X, dy = p.Y - start.Y;

            if (ShapeGeometry.IsBoxShape(kind))
            {
                double s = Math.Max(Math.Abs(dx), Math.Abs(dy));
                return new Point(start.X + Math.Sign(dx) * s, start.Y + Math.Sign(dy) * s);
            }

            double angle = Math.Atan2(dy, dx);
            double step = Math.PI / 4;
            double snapped = Math.Round(angle / step) * step;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return new Point(start.X + Math.Cos(snapped) * len, start.Y + Math.Sin(snapped) * len);
        }

        private Annotation CreateAnnotation(Point p) => _tool switch
        {
            ToolKind.Pen => Init(new PathAnnotation { Points = { p } }),
            ToolKind.Highlighter => Init(new PathAnnotation { Highlighter = true, Points = { p } }),
            ToolKind.Mosaic => Init(new PixelateAnnotation
            {
                Start = p, End = p, UseBlur = false, Strength = _maskStrength, Shape = _maskShape
            }),
            ToolKind.Blur => Init(new PixelateAnnotation
            {
                Start = p, End = p, UseBlur = true, Strength = _maskStrength, Shape = _maskShape
            }),
            ToolKind.Crop => new CropAnnotation { Start = p, End = p },
            ToolKind.Spotlight => Init(new SpotlightAnnotation { Start = p, End = p, Shape = _maskShape }),
            // 시작점에 번호를 놓고 끝점을 화살촉으로 쓴다.
            ToolKind.NumberArrow => CreateNumberArrow(p),
            _ => Init(new ShapeAnnotation { Kind = _tool, Start = p, End = p })
        };

        private Annotation CreateNumberArrow(Point p)
        {
            var arrow = (NumberArrowAnnotation)Init(new NumberArrowAnnotation
            {
                Tip = p, Center = p, Number = _counter, Radius = Math.Max(12, _thickness * 5)
            });
            arrow.EnsureVisibleArrow(new Vector(p.X < Canvas1.ImageWidth / 2 ? 1 : -1,
                                                p.Y < Canvas1.ImageHeight / 2 ? 1 : -1));
            return arrow;
        }

        private Annotation Init(Annotation a)
        {
            a.Color = _color;
            a.Thickness = _thickness;
            if (a.SupportsOpacity) a.Opacity = _opacity;
            if (a.SupportsShadow) a.Shadow = _shadow;
            if (a is PathAnnotation { Highlighter: true })
                a.Blend = _blend == BlendMode.Normal ? BlendMode.Multiply : _blend;   // 형광펜은 곱하기가 기본
            else if (a.SupportsBlend) a.Blend = _blend;
            if (a is ShapeAnnotation or PathAnnotation) { a.Dashed = _dashed; a.DashPattern = _dashPattern; }
            if (a is ShapeAnnotation s)
            {
                if (CanFill(s.Kind)) { s.Filled = _filled; s.GradientFill = _gradient; s.FillColor = _fillColor; }
                if (s.Kind == ToolKind.Arrow) { s.BothArrows = _bothArrows; s.Head = _head; }
            }
            return a;
        }

        // ================================================= 화면 이동 · 호버 · 보조

        private void BeginPan(MouseButtonEventArgs e)
        {
            _panning = true;
            _panStart = e.GetPosition(Scroller);
            _panOffset = new Vector(Scroller.HorizontalOffset, Scroller.VerticalOffset);
            Stage.CaptureMouse();
            Stage.Cursor = Cursors.ScrollAll;
        }

        private void PanTo(MouseEventArgs e)
        {
            Point now = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(_panOffset.X - (now.X - _panStart.X));
            Scroller.ScrollToVerticalOffset(_panOffset.Y - (now.Y - _panStart.Y));
        }

        private void EndPan()
        {
            _panning = false;
            Stage.ReleaseMouseCapture();
            Stage.Cursor = ToolCursor();
        }

        /// <summary>마우스가 그림(캔버스) 위에 있는가. 여백을 눌렀는지 가리는 데 쓴다.</summary>
        private bool IsInsideImage(MouseEventArgs e)
        {
            Point raw = e.GetPosition(Canvas1);
            return raw.X >= 0 && raw.Y >= 0 && raw.X <= Canvas1.ActualWidth && raw.Y <= Canvas1.ActualHeight;
        }

        /// <summary>
        /// 버튼을 안 누른 채 움직일 때: 커서 모양으로 "여기서 끌면 무슨 일이 생기는지" 를 미리 알려 주고,
        /// 집을 수 있는 주석은 살짝 테두리를 띄운다. 예전엔 조절점이 어디인지 눈으로만 찾아야 했다.
        /// </summary>
        private void UpdateHover(MouseEventArgs e)
        {
            if (!IsInsideImage(e))
            {
                if (Canvas1.Hover != null) { Canvas1.Hover = null; Canvas1.InvalidateVisual(); }
                if (_tool is ToolKind.Select or ToolKind.NumberArrow && Canvas1.SelectedMany.Count == 1 &&
                    Canvas1.Selected is NumberArrowAnnotation { Locked: false, Visible: true } arrow)
                {
                    int handle = HandleAt(arrow, Canvas1.ToImage(e.GetPosition(Canvas1)));
                    if (handle != NoHandle) { Stage.Cursor = HandleCursor(handle, arrow); return; }
                }
                Stage.Cursor = ToolCursor();
                return;
            }

            Point p = ImagePoint(e);
            StPos.Text = $"{(int)p.X}, {(int)p.Y}";

            Cursor cursor = ToolCursor();
            Annotation? hover = null;

            if (Canvas1.Active is { } active)
            {
                int h = HandleAtOrRotate(active, p);
                if (h != NoHandle) cursor = HandleCursor(h, active);
                else if (active.HitTestRotated(p) || active.Bounds.Contains(active.ToLocal(p))) cursor = Cursors.SizeAll;
            }
            else if (_tool is ToolKind.Select or ToolKind.Magnifier or ToolKind.NumberArrow)
            {
                if (_tool == ToolKind.Select && Canvas1.SelectedMany.Count > 1)
                {
                    int gh = EditorMath.BoxHandleAt(ArrangeTools.Union(Canvas1.SelectedMany), p, Canvas1.ToImageLength(9));
                    if (gh >= 0) cursor = HandleCursor(gh, null);
                }
                else if (Canvas1.Selected is { Locked: false, Visible: true } sel)
                {
                    int h = HandleAtOrRotate(sel, p);
                    if (h != NoHandle) cursor = HandleCursor(h, sel);
                }

                if (ReferenceEquals(cursor, ToolCursor()))
                {
                    for (int i = Canvas1.Items.Count - 1; i >= 0; i--)
                    {
                        Annotation a = Canvas1.Items[i];
                        if (a is EraseAnnotation || a.Locked || !a.Visible) continue;
                        if (a.HitTestRotated(p)) { hover = a; cursor = Cursors.SizeAll; break; }
                    }
                }
            }

            if (!ReferenceEquals(Canvas1.Hover, hover))
            {
                Canvas1.Hover = hover;
                Canvas1.InvalidateVisual();
            }
            Stage.Cursor = cursor;
        }

        private static Cursor HandleCursor(int handle, Annotation? a)
        {
            if (handle == RotateHandle) return Cursors.Hand;
            if (a is TextAnnotation) return Cursors.SizeWE;
            if (a is MagnifierAnnotation) return handle == 1 ? Cursors.SizeAll : Cursors.SizeWE;
            if (a is NumberArrowAnnotation) return handle switch
            {
                0 or 1 => Cursors.SizeAll,
                2 or 4 => Cursors.SizeNWSE,
                _ => Cursors.SizeNESW
            };
            if (a != null && a.Handles().Count != 8) return Cursors.Cross;   // 선 끝점 · 돋보기 · 번호화살표
            return handle switch
            {
                0 or 4 => Cursors.SizeNWSE,
                2 or 6 => Cursors.SizeNESW,
                1 or 5 => Cursors.SizeNS,
                3 or 7 => Cursors.SizeWE,
                _ => Cursors.Cross
            };
        }

        private void ShowSize(Annotation? a)
        {
            StSize.Text = a != null && !a.Bounds.IsEmpty
                ? $"{(int)Math.Round(a.Bounds.Width)} × {(int)Math.Round(a.Bounds.Height)}" : "";
        }

        /// <summary>끌기가 끝났다. 빠른 미리보기로 그리던 가리개를 정밀 계산으로 되돌린다.</summary>
        private void ClearFast()
        {
            bool any = false;
            if (Canvas1.Active is PixelateAnnotation ap && ap.Fast) { ap.Fast = false; any = true; }
            foreach (Annotation a in Canvas1.Items)
                if (a is PixelateAnnotation x && x.Fast) { x.Fast = false; any = true; }
            if (any) Canvas1.InvalidateVisual();
        }

    }
}
