using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using SnapView.Core;

namespace SnapView.Editor
{
    /// <summary>실행취소·다시실행. 스냅샷 관리는 <see cref="UndoStack"/> 이 한다.</summary>
    public partial class EditorWindow
    {
        // ================================================= 실행취소

        /// <summary>
        /// 지금 상태를 한 칸 쌓는다. <paramref name="coalesceKey"/> 를 주면 같은 종류의 연속 변경
        /// (굵기 연타, 화살표 키 이동)은 한 칸으로 묶인다.
        /// </summary>
        private void PushUndo(string? coalesceKey = null)
        {
            _undoStack.Push(Current(), coalesceKey);
            MarkDirty();
        }

        private EditorSnapshot Current()
            => new(_image, ArrangeTools.CloneAll(Canvas1.Items), _counter);

        private void Restore(EditorSnapshot snap)
        {
            ResetPlacementGestures();
            WandClear();   // 마스크는 지금 그림에 맞춰진 것이다
            _image = snap.Image;
            _counter = snap.Counter;

            Canvas1.Source = _image;
            Canvas1.Items.Clear();
            Canvas1.Items.AddRange(snap.Items);
            Canvas1.Active = null;
            SetSelection(null);

            Relayout();
            Canvas1.InvalidateVisual();
            MarkDirty();
            UpdateStatus();
        }

        private void OnUndo(object sender, RoutedEventArgs e) => Undo();
        private void OnRedo(object sender, RoutedEventArgs e) => Redo();

        private void Undo()
        {
            CommitText();
            if (_numberArrowPhase != NumberArrowPhase.None) { CancelActive(); return; }
            EditorSnapshot? snap = _undoStack.Undo(Current());
            if (snap != null) Restore(snap);
        }

        private void Redo()
        {
            CommitText();
            EditorSnapshot? snap = _undoStack.Redo(Current());
            if (snap != null) Restore(snap);
        }

        private void OnClearAll(object sender, RoutedEventArgs e)
        {
            if (Canvas1.Items.Count == 0 && Canvas1.Active == null) return;
            PushUndo();
            ResetPlacementGestures();
            Canvas1.Items.Clear();
            Canvas1.Active = null;
            SetSelection(null);
            _counter = 1;
            Canvas1.InvalidateVisual();
            UpdateStatus();
        }

        /// <summary>바뀐 게 있다고 표시한다. 제목의 * 와 임시 저장·닫기 확인이 이걸 본다.</summary>
        private void MarkDirty()
        {
            _dirty = true;
            _changeStamp++;
            UpdateTitle();
        }
    }
}
