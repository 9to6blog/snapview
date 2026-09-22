using System;
using System.Windows;
using System.Windows.Media.Imaging;
using SnapView.Core;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private void OnAiImage(object sender, RoutedEventArgs e)
        {
            CommitText(); ConfirmActive();
            BitmapSource input = Flatten();
            Int32Rect? region = null;
            if (!_region.IsEmpty)
            {
                Rect clipped = Rect.Intersect(_region, new Rect(0, 0, input.PixelWidth, input.PixelHeight));
                if (!clipped.IsEmpty && clipped.Width >= 2 && clipped.Height >= 2)
                {
                    int x = (int)Math.Floor(clipped.X), y = (int)Math.Floor(clipped.Y);
                    region = new Int32Rect(x, y, Math.Min(input.PixelWidth, (int)Math.Ceiling(clipped.Right)) - x,
                        Math.Min(input.PixelHeight, (int)Math.Ceiling(clipped.Bottom)) - y);
                }
            }
            var dialog = new AiImageDialog(input, region) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result != null)
                AddAiResult(dialog.Result, dialog.ResultBounds, dialog.ResultProvider);
        }
        private void AddAiResult(BitmapSource image, Rect bounds, string provider)
        {
            ConfirmActive(); SelectTool(ToolKind.Select); PushUndo();
            // Fit the source area without stretching a provider's different aspect ratio.
            double fit = Math.Min(bounds.Width / image.PixelWidth, bounds.Height / image.PixelHeight);
            double w = image.PixelWidth * fit, h = image.PixelHeight * fit;
            var start = new Point(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2);
            var placed = new ImageAnnotation { Image = image, Start = start, End = new Point(start.X + w, start.Y + h), Name = "AI · " + provider };
            Canvas1.Items.Add(placed); SetSelection(placed);
            Canvas1.InvalidateVisual(); UpdateStatus();
            StHint.Text = "AI 결과를 새 레이어로 추가했습니다 · 이동·크기 조절 가능 · Ctrl+Z로 되돌리기";
        }
    }
}
