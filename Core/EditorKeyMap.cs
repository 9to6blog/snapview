using System.Windows.Input;

namespace SnapView.Core
{
    /// <summary>편집기가 키 하나로 할 수 있는 일. 창은 이 값만 보고 움직인다.</summary>
    internal enum EditorCommand
    {
        None, Tool, PickColor, ToggleFill, Nudge, Help,
        Undo, Redo, SelectAll, Duplicate, CopyRegion, CopyAnnotations, CopyResult, CutRegion, Paste,
        RotateRight, RotateLeft, FlipH, FlipV, Save, SaveProject, Done,
        LayerUp, LayerDown, LayerTop, LayerBottom, Zoom100, ZoomIn, ZoomOut, ZoomFit,
        WandErase, WandFill, WandClear, Confirm, CancelActive, DeleteSelection, Deselect, Close
    }

    /// <summary>키를 해석한 결과. 도구·색 번호·이동량은 명령에 따라 채워진다.</summary>
    internal readonly record struct KeyAction(EditorCommand Command, ToolKind Tool = ToolKind.Select,
                                              int ColorIndex = -1, double Dx = 0, double Dy = 0)
    {
        internal static readonly KeyAction None = new(EditorCommand.None);
        internal static KeyAction Of(EditorCommand c) => new(c);
        internal static KeyAction ToolOf(ToolKind t) => new(EditorCommand.Tool, t);
    }

    /// <summary>키를 해석할 때 필요한 편집기 상태. 창이 채워서 넘긴다.</summary>
    internal sealed class EditorKeyState
    {
        /// <summary>글자 입력칸이 떠 있다. 모든 키는 입력칸 몫.</summary>
        internal bool EditingText { get; set; }

        /// <summary>굵기 칸·색상판 같은 입력칸에 포커스가 있다. 글자 키가 도구로 새면 안 된다.</summary>
        internal bool FocusInTextInput { get; set; }

        internal bool HasWandMask { get; set; }
        internal bool HasActive { get; set; }
        internal int SelectionCount { get; set; }
        internal bool HasRegion { get; set; }
    }

    /// <summary>
    /// 편집기 단축키 표. 창의 OnKeyDown 이 여기로 묻고 답대로만 움직인다 —
    /// "이 상황에서 이 키가 무엇을 하는가"를 창 없이 검사할 수 있게 하기 위한 것.
    /// </summary>
    internal static class EditorKeyMap
    {
        internal static KeyAction Resolve(Key key, ModifierKeys mods, EditorKeyState s)
        {
            // 입력칸이 살아 있으면 편집기는 아무 키도 안 가로챈다.
            if (s.EditingText || s.FocusInTextInput) return KeyAction.None;

            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;

            if (ctrl) return ResolveCtrl(key, shift, s);

            if (shift)
            {
                switch (key)
                {
                    case Key.H: return KeyAction.Of(EditorCommand.FlipH);
                    case Key.V: return KeyAction.Of(EditorCommand.FlipV);
                    case Key.L: return KeyAction.ToolOf(ToolKind.Spotlight);
                    case Key.M: return KeyAction.ToolOf(ToolKind.Magnifier);
                    case Key.N: return KeyAction.ToolOf(ToolKind.NumberArrow);
                    case Key.E: return KeyAction.ToolOf(ToolKind.PixelEraser);
                }
            }

            switch (key)
            {
                case Key.Delete or Key.Back:
                    if (s.HasWandMask) return KeyAction.Of(EditorCommand.WandErase);
                    if (s.HasActive) return KeyAction.Of(EditorCommand.CancelActive);
                    if (s.SelectionCount > 0) return KeyAction.Of(EditorCommand.DeleteSelection);
                    return KeyAction.None;

                case Key.Enter:
                    return KeyAction.Of(s.HasWandMask ? EditorCommand.WandFill : EditorCommand.Confirm);

                case Key.Escape:
                    if (s.HasWandMask) return KeyAction.Of(EditorCommand.WandClear);
                    if (s.HasActive) return KeyAction.Of(EditorCommand.CancelActive);
                    if (s.SelectionCount > 0) return KeyAction.Of(EditorCommand.Deselect);
                    return KeyAction.Of(EditorCommand.Close);

                case Key.V: return KeyAction.ToolOf(ToolKind.Select);
                case Key.A: return KeyAction.ToolOf(ToolKind.Arrow);
                case Key.L: return KeyAction.ToolOf(ToolKind.Line);
                case Key.R: return KeyAction.ToolOf(ToolKind.Rectangle);
                case Key.O: return KeyAction.ToolOf(ToolKind.Ellipse);
                case Key.P: return KeyAction.ToolOf(ToolKind.Pen);
                case Key.H: return KeyAction.ToolOf(ToolKind.Highlighter);
                case Key.T: return KeyAction.ToolOf(ToolKind.Text);
                case Key.N: return KeyAction.ToolOf(ToolKind.Counter);
                case Key.M: return KeyAction.ToolOf(ToolKind.Mosaic);
                case Key.B: return KeyAction.ToolOf(ToolKind.Blur);
                case Key.C: return KeyAction.ToolOf(ToolKind.Crop);
                case Key.I: return KeyAction.ToolOf(ToolKind.Picker);
                case Key.E: return KeyAction.ToolOf(ToolKind.Eraser);
                case Key.W: return KeyAction.ToolOf(ToolKind.Wand);
                case Key.S: return KeyAction.ToolOf(ToolKind.RegionSelect);
                case Key.F: return KeyAction.Of(EditorCommand.ToggleFill);
                case Key.F1: return KeyAction.Of(EditorCommand.Help);

                case Key.Left: return new KeyAction(EditorCommand.Nudge, Dx: shift ? -10 : -1);
                case Key.Right: return new KeyAction(EditorCommand.Nudge, Dx: shift ? 10 : 1);
                case Key.Up: return new KeyAction(EditorCommand.Nudge, Dy: shift ? -10 : -1);
                case Key.Down: return new KeyAction(EditorCommand.Nudge, Dy: shift ? 10 : 1);
            }

            int digit = key switch
            {
                >= Key.D1 and <= Key.D8 => key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad8 => key - Key.NumPad1,
                _ => -1
            };
            if (digit >= 0) return new KeyAction(EditorCommand.PickColor, ColorIndex: digit);

            return KeyAction.None;
        }

        private static KeyAction ResolveCtrl(Key key, bool shift, EditorKeyState s) => key switch
        {
            Key.Z => KeyAction.Of(shift ? EditorCommand.Redo : EditorCommand.Undo),
            Key.Y => KeyAction.Of(EditorCommand.Redo),
            Key.A => KeyAction.Of(EditorCommand.SelectAll),
            Key.D => KeyAction.Of(EditorCommand.Duplicate),
            Key.C => KeyAction.Of(s.HasRegion ? EditorCommand.CopyRegion
                                : s.SelectionCount > 0 ? EditorCommand.CopyAnnotations
                                : EditorCommand.CopyResult),
            Key.X => KeyAction.Of(s.HasRegion ? EditorCommand.CutRegion : EditorCommand.None),
            Key.V => KeyAction.Of(EditorCommand.Paste),
            Key.R => KeyAction.Of(shift ? EditorCommand.RotateLeft : EditorCommand.RotateRight),
            Key.S => KeyAction.Of(shift ? EditorCommand.SaveProject : EditorCommand.Save),
            Key.Enter => KeyAction.Of(EditorCommand.Done),
            Key.OemCloseBrackets => KeyAction.Of(shift ? EditorCommand.LayerTop : EditorCommand.LayerUp),
            Key.OemOpenBrackets => KeyAction.Of(shift ? EditorCommand.LayerBottom : EditorCommand.LayerDown),
            Key.D0 or Key.NumPad0 => KeyAction.Of(shift ? EditorCommand.ZoomFit : EditorCommand.Zoom100),
            Key.OemPlus or Key.Add => KeyAction.Of(EditorCommand.ZoomIn),
            Key.OemMinus or Key.Subtract => KeyAction.Of(EditorCommand.ZoomOut),
            _ => KeyAction.None
        };
    }
}
