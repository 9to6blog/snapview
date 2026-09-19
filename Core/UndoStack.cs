using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>실행취소 한 칸. 자르기·뒤집기가 이미지 자체를 바꾸므로 그림도 같이 담는다.</summary>
    internal sealed record EditorSnapshot(BitmapSource Image, List<Annotation> Items, int Counter);

    /// <summary>
    /// 편집기 실행취소 스택.
    ///
    /// 두 가지를 챙긴다. 하나는 <b>묶음</b>: 굵기 칸을 연타하거나 화살표 키로 자리를 맞출 때
    /// 눌림마다 한 칸씩 쌓이면 Ctrl+Z 를 스무 번 눌러야 한다. 같은 종류의 변경이 짧은 시간 안에
    /// 이어지면 한 칸으로 친다. 다른 하나는 <b>메모리 예산</b>: 픽셀 지우개·자르기·필터는 그림을
    /// 통째로 새로 만드는데 8K 한 장이 132MB 라, 예산을 넘으면 오래된 칸부터 버린다.
    /// </summary>
    internal sealed class UndoStack
    {
        internal const long CoalesceWindowMs = 400;

        private readonly List<EditorSnapshot> _undo = new();
        private readonly List<EditorSnapshot> _redo = new();
        private readonly int _limit;
        private readonly long _budget;
        private readonly Func<long> _clock;
        private string? _lastKey;
        private long _lastAt = long.MinValue;

        internal UndoStack(int limit = 60, long budgetBytes = 512L * 1024 * 1024, Func<long>? clock = null)
        {
            _limit = Math.Max(1, limit);
            _budget = Math.Max(1, budgetBytes);
            _clock = clock ?? (() => Environment.TickCount64);
        }

        internal int UndoCount => _undo.Count;
        internal int RedoCount => _redo.Count;
        internal bool CanUndo => _undo.Count > 0;
        internal bool CanRedo => _redo.Count > 0;

        /// <summary>
        /// 한 칸 쌓는다. <paramref name="coalesceKey"/> 가 직전과 같고 짧은 시간 안이면
        /// 안 쌓고 false 를 돌려준다(그 변경은 직전 칸에 묶인다).
        /// </summary>
        internal bool Push(EditorSnapshot snapshot, string? coalesceKey = null)
        {
            long now = _clock();
            if (coalesceKey != null && coalesceKey == _lastKey && now - _lastAt <= CoalesceWindowMs)
            {
                _lastAt = now;
                return false;
            }

            _lastKey = coalesceKey;
            _lastAt = now;
            _undo.Add(snapshot);
            _redo.Clear();
            while (_undo.Count > _limit) _undo.RemoveAt(0);
            TrimToBudget();
            return true;
        }

        /// <summary>마지막 칸을 꺼낸다. <paramref name="current"/> 는 다시실행에 들어간다.</summary>
        internal EditorSnapshot? Undo(EditorSnapshot current)
        {
            if (_undo.Count == 0) return null;
            _redo.Add(current);
            EditorSnapshot s = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _lastKey = null;
            return s;
        }

        internal EditorSnapshot? Redo(EditorSnapshot current)
        {
            if (_redo.Count == 0) return null;
            _undo.Add(current);
            EditorSnapshot s = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _lastKey = null;
            return s;
        }

        /// <summary>방금 쌓은 칸을 없던 일로(빈 데를 문지른 지우개처럼).</summary>
        internal void DropLast()
        {
            if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            _lastKey = null;
        }

        internal void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _lastKey = null;
        }

        /// <summary>스택이 붙들고 있는 그림 바이트. 같은 그림을 여러 칸이 공유하면 한 번만 센다.</summary>
        internal long ImageBytes
        {
            get
            {
                var seen = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
                long total = 0;
                foreach (EditorSnapshot s in _undo) if (seen.Add(s.Image)) total += Bytes(s.Image);
                foreach (EditorSnapshot s in _redo) if (seen.Add(s.Image)) total += Bytes(s.Image);
                return total;
            }
        }

        private static long Bytes(BitmapSource b) => (long)b.PixelWidth * b.PixelHeight * 4;

        private void TrimToBudget()
        {
            while (_undo.Count > 1 && ImageBytes > _budget) _undo.RemoveAt(0);
        }
    }
}
