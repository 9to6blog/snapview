using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SnapView.Core;

namespace SnapView.Editor
{
    /// <summary>키보드 단축키. 해석은 <see cref="EditorKeyMap"/> 이 하고 여기서는 실행만 한다.</summary>
    public partial class EditorWindow
    {
        // ================================================= 키보드

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            var state = new EditorKeyState
            {
                EditingText = _editingText != null,
                // 굵기 칸·색상판·글꼴 목록에 포커스가 있으면 글자 키가 도구로 새면 안 된다.
                FocusInTextInput = Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox,
                HasWandMask = _wandMask != null,
                HasActive = Canvas1.Active != null,
                SelectionCount = Canvas1.SelectedMany.Count,
                HasRegion = _region.Width >= 1
            };

            KeyAction act = EditorKeyMap.Resolve(e.Key, Keyboard.Modifiers, state);
            if (act.Command == EditorCommand.None) return;

            e.Handled = true;
            Execute(act);
        }

        private void Execute(KeyAction act)
        {
            switch (act.Command)
            {
                case EditorCommand.Tool:
                    if (act.Tool == ToolKind.Picker && _tool != ToolKind.Picker) _toolBeforePicker = _tool;
                    SelectTool(act.Tool);
                    break;
                case EditorCommand.PickColor:
                    if (act.ColorIndex >= 0 && act.ColorIndex < Palette.Length) SetColor(ParseColor(Palette[act.ColorIndex]));
                    break;
                case EditorCommand.ToggleFill:
                    TbFill.IsChecked = !_filled;
                    OnFillToggled(TbFill, new RoutedEventArgs());
                    break;
                case EditorCommand.Nudge: NudgeEditable(new Vector(act.Dx, act.Dy)); break;
                case EditorCommand.Help: ShowShortcutHelp(); break;

                case EditorCommand.Undo: Undo(); break;
                case EditorCommand.Redo: Redo(); break;
                case EditorCommand.SelectAll: SelectAllAnnotations(); break;
                case EditorCommand.Duplicate: DuplicateSelection(new Vector(16, 16)); break;
                case EditorCommand.CopyRegion: CopyRegion(cut: false); break;
                case EditorCommand.CopyAnnotations: CopyAnnotations(); break;
                case EditorCommand.CopyResult: CopyResult(); break;
                case EditorCommand.CutRegion: CopyRegion(cut: true); break;
                case EditorCommand.Paste: PasteSmart(); break;
                case EditorCommand.RotateRight: Rotate(true); break;
                case EditorCommand.RotateLeft: Rotate(false); break;
                case EditorCommand.FlipH: Flip(true); break;
                case EditorCommand.FlipV: Flip(false); break;
                case EditorCommand.Save: SaveResult(); break;
                case EditorCommand.SaveProject: SaveProject(); break;
                case EditorCommand.Done: Done(); break;
                case EditorCommand.LayerUp: MoveLayer(+1, toEnd: false); break;
                case EditorCommand.LayerDown: MoveLayer(-1, toEnd: false); break;
                case EditorCommand.LayerTop: MoveLayer(+1, toEnd: true); break;
                case EditorCommand.LayerBottom: MoveLayer(-1, toEnd: true); break;
                case EditorCommand.Zoom100: SetZoom(1.0, ViewportCenter()); break;
                case EditorCommand.ZoomIn: SetZoom(_zoom * ZoomStep, ViewportCenter()); break;
                case EditorCommand.ZoomOut: SetZoom(_zoom / ZoomStep, ViewportCenter()); break;
                case EditorCommand.ZoomFit: OnZoomFit(this, new RoutedEventArgs()); break;

                case EditorCommand.WandErase: WandErase(); break;
                case EditorCommand.WandFill: WandFill(); break;
                case EditorCommand.WandClear:
                    WandClear();
                    Canvas1.InvalidateVisual();
                    StHint.Text = "선택을 풀었습니다";
                    break;
                case EditorCommand.Confirm: ConfirmActive(); break;
                case EditorCommand.CancelActive: CancelActive(); break;
                case EditorCommand.DeleteSelection: DeleteSelection(); break;
                case EditorCommand.Deselect:
                    SetSelection(null);
                    Canvas1.InvalidateVisual();
                    UpdateStatus();
                    break;
                case EditorCommand.Close: Close(); break;
            }
        }

        /// <summary>화살표 키 이동. 연타는 실행취소 한 칸으로 묶인다.</summary>
        private void NudgeEditable(Vector d)
        {
            if (Canvas1.Active is { } active)
            {
                active.Move(d);
            }
            else if (Canvas1.SelectedMany.Count > 0)
            {
                PushUndo("nudge");
                foreach (Annotation s in Canvas1.SelectedMany)
                    if (!s.Locked) s.Move(d);
            }
            else return;

            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        /// <summary>F1: 단축키 치트시트. 툴팁에 흩어진 것을 한눈에.</summary>
        private void ShowShortcutHelp()
        {
            const string text =
                "도구   V 선택 · A 화살표 · L 선 · R 사각형 · O 타원 · P 펜 · H 형광펜 · T 글자\n" +
                "       N 번호 · Shift+N 번호+화살표 · M 모자이크 · B 흐림 · Shift+L 강조 · Shift+M 돋보기\n" +
                "       I 스포이드 · E 지우개 · Shift+E 픽셀지우개 · W 자동선택 · S 영역 · C 자르기 · F 채우기\n" +
                "색     1~8 팔레트 색\n" +
                "편집   Ctrl+Z 실행취소 · Ctrl+Shift+Z / Ctrl+Y 다시실행 · Ctrl+A 전체 선택 · Ctrl+D 복제\n" +
                "       Ctrl+C 복사(영역 > 주석 > 결과) · Ctrl+X 영역 잘라내기 · Ctrl+V 붙여넣기 · Delete 삭제\n" +
                "       ←↑→↓ 1px 이동(Shift 10px) · Enter 확정 · Esc 취소 → 선택 해제 → 닫기\n" +
                "그리기 Shift 정사각형·45° · Alt 가운데에서 · Ctrl+끌기 복제해서 옮기기 · Alt+끌기 스냅 끄기\n" +
                "이미지 Ctrl+R / Ctrl+Shift+R 90° 회전 · Shift+H / Shift+V 뒤집기\n" +
                "레이어 Ctrl+] / Ctrl+[ 한 칸 · Ctrl+Shift+] / [ 맨 위·아래\n" +
                "보기   Ctrl+휠 확대(커서 기준) · Ctrl+0 100% · Ctrl+Shift+0 창에 맞춤 · 가운데 버튼·Space+끌기 이동\n" +
                "저장   Ctrl+S 그림 저장 · Ctrl+Shift+S 프로젝트 저장 · Ctrl+Enter 완료";

            var box = new TextBox
            {
                Text = text, IsReadOnly = true, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = (Brush)FindResource("Fg"),
                FontFamily = new FontFamily("Consolas, Malgun Gothic"), FontSize = 12.5,
                Margin = new Thickness(16), TextWrapping = TextWrapping.NoWrap
            };
            var dlg = new Window
            {
                Title = "편집기 단축키 (F1)", Owner = this, Content = box,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = (Brush)FindResource("BgChrome")
            };
            dlg.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.F1) dlg.Close(); };
            dlg.ShowDialog();
        }
    }
}
