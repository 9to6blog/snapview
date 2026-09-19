using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private CropBoundaryAnalysis? _cropAnalysis;
        private BitmapSource? _cropAnalysisImage;
        private long _cropAnalysisStamp = -1;

        private CropBoundaryAnalysis GetCropAnalysis()
        {
            if (_cropAnalysis == null || !ReferenceEquals(_cropAnalysisImage, _image) ||
                _cropAnalysisStamp != _changeStamp)
            {
                _cropAnalysis = CropBoundaryAnalysis.Analyze(_image);
                _cropAnalysisImage = _image;
                _cropAnalysisStamp = _changeStamp;
            }
            return _cropAnalysis;
        }

        /// <summary>
        /// 흰색·검은색·투명 테두리를 찾아 곧바로 자르지 않고 조절 가능한 자르기 상자로 보여 준다.
        /// </summary>
        private void OnAutoCrop(object sender, RoutedEventArgs e)
        {
            Cursor old = Cursor;
            Cursor = Cursors.Wait;
            try
            {
                AutoCropResult? found = GetCropAnalysis().AutoCrop;
                if (found == null)
                {
                    StHint.Text = "자동으로 제거할 흰색·검은색·투명 여백을 찾지 못했습니다";
                    return;
                }

                Int32Rect r = found.Value.Region;
                _cropAspect = 0;
                CropAspectBox.SelectedIndex = 0;
                Canvas1.Active = new CropAnnotation
                {
                    Start = new Point(r.X, r.Y),
                    End = new Point(r.X + r.Width, r.Y + r.Height)
                };
                Canvas1.ShowActiveHandles = true;
                Canvas1.InvalidateVisual();
                UpdateStatus();
                StHint.Text = $"{found.Value.Description} 감지 — 조절한 뒤 Enter로 자르기 · Esc 취소";
            }
            catch (Exception ex)
            {
                StHint.Text = "자동 여백 감지 실패: " + ex.Message;
            }
            finally { Cursor = old; }
        }

        /// <summary>자르기 끝점을 가까운 긴 콘텐츠 경계에 붙이고 안내선을 표시한다.</summary>
        private Point SnapCropPoint(Point p, bool snapX, bool snapY)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                Canvas1.GuideX = Canvas1.GuideY = null;
                return p;
            }

            CropSnapResult snap = GetCropAnalysis().Snap(p, Canvas1.ToImageLength(10));
            Canvas1.GuideX = snapX ? snap.X : null;
            Canvas1.GuideY = snapY ? snap.Y : null;
            return snap.Apply(p, snapX, snapY);
        }

        private Point SnapCropHandlePoint(Point p, int handle)
        {
            bool snapX = handle is 0 or 2 or 3 or 4 or 6 or 7;
            bool snapY = handle is 0 or 1 or 2 or 4 or 5 or 6;
            return SnapCropPoint(p, snapX, snapY);
        }

    }
}
