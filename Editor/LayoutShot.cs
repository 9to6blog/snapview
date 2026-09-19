using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

namespace SnapView.Editor
{
    /// <summary>
    /// 편집기 창을 실제로 띄워 그린 뒤 PNG 로 떨군다.
    /// 배치는 코드만 봐서는 확인이 안 돼서, 눈으로 볼 그림이 필요할 때 쓴다.
    /// (제품 동작에는 관여하지 않는다 — "--shot 경로" 로만 들어온다)
    /// </summary>
    internal static class LayoutShot
    {
        private static void Call(object target, string method, params object[] args)
            => target.GetType()
                     .GetMethod(method, System.Reflection.BindingFlags.Instance |
                                        System.Reflection.BindingFlags.NonPublic)
                     ?.Invoke(target, args);

        private static void SetField(object target, string field, object value)
            => target.GetType()
                     .GetField(field, System.Reflection.BindingFlags.Instance |
                                      System.Reflection.BindingFlags.NonPublic)
                     ?.SetValue(target, value);

        /// <summary>영역 선택 오버레이의 액션 바를 용도별로 그려 본다.</summary>
        internal static void TakeOverlay(string path, string purpose)
        {
            var px = new byte[1200 * 800 * 4];
            for (int i = 0; i < px.Length; i += 4)
            { px[i] = 60; px[i + 1] = 55; px[i + 2] = 50; px[i + 3] = 255; }
            if (purpose == "magnet")
            {
                // 대충 잡은 회색 영역 안에 실제 콘텐츠 카드가 하나 있는 모양.
                for (int y = 150; y < 610; y++)
                for (int x = 220; x < 980; x++)
                {
                    int i = (y * 1200 + x) * 4;
                    px[i] = (byte)(80 + x % 70);
                    px[i + 1] = (byte)(95 + y % 80);
                    px[i + 2] = 175;
                }
            }
            var frozen = BitmapSource.Create(1200, 800, 96, 96, PixelFormats.Bgra32, null, px, 1200 * 4);
            frozen.Freeze();

            var mode = purpose switch
            {
                "record" => Capture.OverlayPurpose.Record,
                _ => Capture.OverlayPurpose.Capture
            };

            var win = new Capture.OverlayWindow(frozen, new Int32Rect(0, 0, 1200, 800),
                                                true, false, mode,
                                                "파일로 저장 + 클립보드에 복사")
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };

            win.Show();
            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (purpose == "magnet")
            {
                Type phaseType = win.GetType().GetNestedType("Phase",
                    System.Reflection.BindingFlags.NonPublic)!;
                SetField(win, "_selection", new Rect(100, 80, 1000, 640));
                SetField(win, "_phase", Enum.Parse(phaseType, "Adjusting"));
                Call(win, "UpdateVisuals");
                Call(win, "AutoFitSelection");
            }
            else Call(win, "PlaceActionBar", true, new Rect(200, 200, 700, 400));
            win.UpdateLayout();

            Save(win, path);
            win.Close();
        }

        /// <summary>설정 창도 같은 방식으로 그려 본다. 전역 스타일을 건드리면 여기부터 깨진다.</summary>
        internal static void TakeSettings(string path)
        {
            var win = new Prefs.SettingsWindow(Settings.Load())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };

            win.Show();
            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            win.UpdateLayout();

            Save(win, path);
            win.Close();
        }

        private static void Save(Window win, string path)
        {
            var rtb = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight,
                                             96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            rtb.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(path)) encoder.Save(fs);
            Console.WriteLine("saved " + path);
        }

        /// <summary>뷰어에 파일 하나를 열어 놓고 그린다. 재생 막대가 나오는지 볼 때 쓴다.</summary>
        internal static void TakeViewer(string path, string mediaFile)
        {
            var win = new Viewer.ViewerWindow(Settings.Load())
            {
                Width = 1100,
                Height = 720,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };

            if (!File.Exists(mediaFile)) mediaFile = MakeSample(mediaFile);

            win.Show();
            win.LoadFile(mediaFile);

            // 편집 줄(컷·배속)의 배치도 같이 확인한다.
            if (Core.MediaKinds.IsVideo(mediaFile)) win.ShowEditRowForShot();

            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            win.UpdateLayout();

            Save(win, path);
            win.Close();
        }

        /// <summary>확인용 짧은 영상을 하나 만든다. 확장자에 따라 GIF 또는 MP4.</summary>
        private static string MakeSample(string path)
        {
            var frames = new System.Collections.Generic.List<BitmapSource>();
            for (int i = 0; i < 20; i++)
            {
                byte level = (byte)(30 + i * 10);
                frames.Add(Solid(480, 270, System.Windows.Media.Color.FromRgb(level, (byte)(200 - level), 160)));
            }

            if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                using var mp4 = new Capture.Mp4Writer(path, 480, 270, 10);
                foreach (BitmapSource f in frames) mp4.Add(f);
            }
            else
            {
                GifWriter.Save(frames, path, 100);
            }
            return path;
        }

        private static BitmapSource Solid(int w, int h, System.Windows.Media.Color color)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = color.B; px[i + 1] = color.G; px[i + 2] = color.R; px[i + 3] = 255;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        internal static void Take(string path, ToolKind tool = ToolKind.Text)
        {
            var image = new WriteableBitmap(520, 320, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[520 * 320 * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = 90; px[i + 1] = 70; px[i + 2] = 60; px[i + 3] = 255;
            }
            image.WritePixels(new Int32Rect(0, 0, 520, 320), px, 520 * 4, 0);
            image.Freeze();

            var win = new EditorWindow(image, Settings.Load())
            {
                Width = 1180,
                Height = 820,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,          // 화면 밖에서 그린다
                Top = -4000,
                ShowInTaskbar = false
            };

            // 주석이 쌓인 상태와 텍스트 도구를 고른 상태도 같이 보려고 미리 채워 둔다.
            // (제품 코드에 검사용 구멍을 내지 않으려고 리플렉션으로 부른다)
            win.Canvas1.Items.Add(new ShapeAnnotation
            {
                Kind = ToolKind.Rectangle, Start = new Point(40, 40), End = new Point(200, 150),
                Color = Colors.OrangeRed, Thickness = 4, Filled = true, Opacity = 0.5
            });
            win.Canvas1.Items.Add(new ShapeAnnotation
            {
                Kind = ToolKind.Star5, Start = new Point(240, 60), End = new Point(360, 180),
                Color = Colors.Gold, Thickness = 3
            });
            win.Canvas1.Items.Add(new TextAnnotation
            {
                Origin = new Point(60, 220), Text = "여기를 보세요", FontSize = 34, Color = Colors.White
            });
            win.Canvas1.Items.Add(new CounterAnnotation
            {
                Center = new Point(420, 240), Number = 1, Radius = 22, Color = Colors.DeepSkyBlue
            });

            Call(win, "SelectTool", tool);
            if (tool == ToolKind.Crop)
            {
                win.Canvas1.Active = new CropAnnotation
                {
                    Start = new Point(30, 24), End = new Point(490, 296)
                };
                win.Canvas1.ShowActiveHandles = true;
            }
            Call(win, "UpdateStatus");

            win.Show();
            win.UpdateLayout();

            // Loaded 핸들러(배치 계산)가 돌 틈을 준다.
            win.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            win.UpdateLayout();

            Save(win, path);
            win.Close();
        }
    }
}
