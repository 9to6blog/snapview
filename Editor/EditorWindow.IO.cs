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
    /// <summary>프로젝트(.snapview)·그림 얹기·붙여넣기·필터, 그리고 복사·저장·완료.</summary>
    public partial class EditorWindow
    {
        // ================================================= 프로젝트 (.snapview)

        /// <summary>레이어 살린 채 저장·열기. PNG 로 합치면 못 만지지만 이 파일은 이어서 편집한다.</summary>
        private void OnProjectMenu(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = (UIElement)sender,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };

            var save = new MenuItem { Header = "프로젝트로 저장 (.snapview)..." };
            save.Click += (_, _) => SaveProject();
            menu.Items.Add(save);

            var open = new MenuItem { Header = "프로젝트 열기..." };
            open.Click += (_, _) => OpenProject();
            menu.Items.Add(open);

            menu.IsOpen = true;
        }

        private void SaveProject()
        {
            CommitText();
            ConfirmActive();

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = ProjectFile.Filter,
                DefaultExt = ProjectFile.Extension,
                FileName = "스냅뷰_프로젝트_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss")
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                ProjectFile.Save(dlg.FileName, _image, Canvas1.Items, _counter);
                _projectPath = dlg.FileName;
                _dirty = false;
                UpdateTitle();
                StHint.Text = "프로젝트 저장 — " + System.IO.Path.GetFileName(dlg.FileName) +
                              " (열면 레이어 그대로 이어서 편집)";
            }
            catch (Exception ex)
            {
                StHint.Text = "프로젝트를 저장하지 못했습니다: " + ex.Message;
            }
        }

        private void OpenProject()
        {
            if (_dirty && MessageBox.Show(this, "지금 편집한 내용은 사라집니다. 프로젝트를 열까요?", "SnapView",
                                          MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = ProjectFile.Filter };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                (BitmapSource image, List<Annotation> items, int counter) = ProjectFile.Load(dlg.FileName);

                CommitText();
                CancelActive();
                PushUndo();   // 열기 전 상태로 Ctrl+Z 가능

                _image = image;
                _counter = counter;
                Canvas1.Source = image;
                Canvas1.Items.Clear();
                Canvas1.Items.AddRange(items);
                SetSelection(null);
                _region = Rect.Empty;
                _projectPath = dlg.FileName;
                _dirty = false;
                UpdateTitle();

                _fitToWindow = true;
                _imageSavePath = null;
                Relayout();
                Canvas1.InvalidateVisual();
                UpdateStatus();
                StHint.Text = $"프로젝트를 열었습니다 — 주석 {items.Count(a => a is not EraseAnnotation)}개";
            }
            catch (Exception ex)
            {
                StHint.Text = "프로젝트를 열지 못했습니다: " + ex.Message;
            }
        }

        /// <summary>다른 그림 파일을 불러와 레이어로 얹는다(레이어 패널의 ＋ 그림).</summary>
        private void OnAddImageLayer(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "레이어로 얹을 그림 고르기",
                Filter = "그림 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|모든 파일|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            BitmapSource img;
            try { img = ImageIO.Load(dlg.FileName); }
            catch (Exception ex)
            {
                StHint.Text = "그림을 불러오지 못했습니다: " + ex.Message;
                return;
            }

            CommitText();
            ConfirmActive();
            PushUndo();

            var placed = new ImageAnnotation { Image = img, Shadow = _shadow };
            placed.PlaceAt(new Point(8, 8));

            // 캔버스보다 크면 안에 들어오게 줄여서 얹는다. 원본 파일은 그대로다.
            double fit = Math.Min(1, Math.Min(Canvas1.ImageWidth * 0.9 / Math.Max(1, img.PixelWidth),
                                             Canvas1.ImageHeight * 0.9 / Math.Max(1, img.PixelHeight)));
            if (fit < 1)
                placed.End = new Point(8 + img.PixelWidth * fit, 8 + img.PixelHeight * fit);

            // 도구를 먼저 바꾼다. 뒤에 바꾸면 SelectTool 이 "도구를 바꾸면 만들던 것을
            // 확정한다" 고 놓자마자 확정해 버려서, 끌어 맞추는 단계가 없어진다.
            SelectTool(ToolKind.Select);
            Canvas1.Active = placed;
            Canvas1.ShowActiveHandles = true;
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = "불러왔습니다 — 끌어서 맞춘 뒤 Enter 로 확정";
        }

        /// <summary>클립보드 그림을 옮길 수 있는 주석으로 얹는다.</summary>
        private void PasteImage()
        {
            BitmapSource? clip = ImageIO.ImageFromClipboard();
            if (clip == null) { StHint.Text = "클립보드에 그림이 없습니다"; return; }

            CommitText();
            ConfirmActive();
            PushUndo();

            var placed = new ImageAnnotation { Image = clip };
            placed.PlaceAt(new Point(8, 8));

            // OnAddImageLayer 와 같은 이유로 도구 교환을 먼저 한다.
            SelectTool(ToolKind.Select);
            Canvas1.Active = placed;
            Canvas1.ShowActiveHandles = true;
            Canvas1.InvalidateVisual();
            UpdateStatus();
            StHint.Text = "붙여넣었습니다 — 끌어서 맞춘 뒤 Enter 로 확정";
        }

        // ================================================= 이미지 필터

        /// <summary>필터 목록을 버튼 아래에 펼친다.</summary>
        private void OnFilterMenu(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = BtnFilter,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };

            foreach (ImageEffects.ImageFilter f in Enum.GetValues<ImageEffects.ImageFilter>())
            {
                var item = new MenuItem { Header = ImageEffects.NameOf(f), Tag = f };
                item.Click += (s, _) => BeginFilter((ImageEffects.ImageFilter)((MenuItem)s!).Tag);
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        /// <summary>
        /// 필터를 걸기 시작한다. 곧바로 확정하지 않고 <b>강도를 만지는 동안 화면에
        /// 그대로 보여 준다</b>. 원본은 따로 들고 있다가 취소하면 되돌린다.
        /// </summary>
        private void BeginFilter(ImageEffects.ImageFilter filter)
        {
            CommitText();
            ConfirmActive();
            CloseFilterPopup(restore: true);

            _pendingFilter = filter;
            _filterBase = _image;

            var strength = new NumberStepper
            {
                Minimum = 5, Maximum = 100, Step = 5, Suffix = "%",
                Width = 100, Height = 26
            };
            strength.SetSilently(50);
            strength.ValueChanged += v => PreviewFilter(v / 100.0);

            var apply = new Button { Content = "적용", MinWidth = 54, Margin = new Thickness(6, 0, 0, 0) };
            var cancel = new Button { Content = "취소", MinWidth = 54, Margin = new Thickness(4, 0, 0, 0) };
            apply.Style = (Style)FindResource("ToolButton");
            cancel.Style = (Style)FindResource("ToolButton");
            apply.Click += (_, _) => CloseFilterPopup(restore: false);
            cancel.Click += (_, _) => CloseFilterPopup(restore: true);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "강도",
                Foreground = (Brush)FindResource("FgDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(strength);
            row.Children.Add(apply);
            row.Children.Add(cancel);

            var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
            body.Children.Add(new TextBlock
            {
                Text = ImageEffects.NameOf(filter),
                Foreground = (Brush)FindResource("Fg"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            body.Children.Add(row);

            _filterPopup = new Popup
            {
                PlacementTarget = BtnFilter,
                Placement = PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = (Brush)FindResource("BgChrome"),
                    BorderBrush = (Brush)FindResource("Divider"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = body
                }
            };

            _filterPopup.IsOpen = true;
            PreviewFilter(0.5);
        }

        /// <summary>강도를 바꿀 때마다 원본에서 다시 계산해 화면에만 보여 준다.</summary>
        private void PreviewFilter(double strength)
        {
            if (_filterBase == null) return;

            Cursor = Cursors.Wait;
            try
            {
                BitmapSource? filtered = ImageEffects.Apply(_filterBase, _pendingFilter, strength);
                if (filtered == null)
                {
                    StHint.Text = ImageEffects.NameOf(_pendingFilter) + " 를 걸지 못했습니다";
                    return;
                }

                _image = filtered;
                Canvas1.Source = _image;
                Canvas1.InvalidateVisual();
                StHint.Text = ImageEffects.NameOf(_pendingFilter) + " 미리보기 — 강도를 맞춘 뒤 적용";
            }
            finally { Cursor = Cursors.Arrow; }
        }

        /// <summary>
        /// 필터 팝업을 닫는다. <paramref name="restore"/> 면 원본으로 되돌리고,
        /// 아니면 지금 보이는 상태를 확정한다(실행취소는 필터 걸기 전으로 간다).
        /// </summary>
        private void CloseFilterPopup(bool restore)
        {
            if (_filterPopup != null)
            {
                _filterPopup.IsOpen = false;
                _filterPopup = null;
            }

            if (_filterBase == null) return;

            if (restore)
            {
                _image = _filterBase;
                Canvas1.Source = _image;
                Canvas1.InvalidateVisual();
                StHint.Text = "";
            }
            else
            {
                BitmapSource result = _image;
                _image = _filterBase;      // 실행취소가 필터 걸기 전으로 가도록
                PushUndo();
                _image = result;
                Canvas1.Source = _image;
                Canvas1.InvalidateVisual();
                StHint.Text = ImageEffects.NameOf(_pendingFilter) + " 적용 — Ctrl+Z 로 되돌립니다";
            }

            _filterBase = null;
            UpdateStatus();
        }

        // ================================================= 결과

        private BitmapSource Flatten()
        {
            CommitText();
            ConfirmActive();
            return AnnotationRenderer.Flatten(_image, Canvas1.Items);
        }

        private void OnCopy(object sender, RoutedEventArgs e) => CopyResult();
        private void OnSave(object sender, RoutedEventArgs e) => SaveResult();
        private void OnSaveAs(object sender, RoutedEventArgs e) => SaveResult(saveAs: true);
        private void OnDone(object sender, RoutedEventArgs e) => Done();

        private void OnSaveOptions(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
            var saveAs = new MenuItem { Header = "다른 이름으로 저장..." };
            saveAs.Click += OnSaveAs;
            menu.Items.Add(saveAs);
            var project = new MenuItem { Header = "프로젝트로 저장 (.snapview)...", InputGestureText = "Ctrl+Shift+S" };
            project.Click += (_, _) => SaveProject();
            menu.Items.Add(project);
            menu.IsOpen = true;
        }

        private void CopyResult()
        {
            // 예전엔 실패해도 "복사했습니다" 라고 했다. 다른 프로그램이 클립보드를 잡고 있으면 실패한다.
            StHint.Text = ImageIO.CopyToClipboard(Flatten())
                ? "클립보드에 복사했습니다"
                : "클립보드에 복사하지 못했습니다 — 다른 프로그램이 클립보드를 잡고 있습니다. 잠시 뒤 다시 시도하세요";
        }

        private void SaveResult(bool saveAs = false)
        {
            BitmapSource img = Flatten();
            string? path = _imageSavePath;
            if (saveAs)
            {
                bool jpeg = path != null
                    ? System.IO.Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    : _settings.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "주석 넣은 그림 저장",
                    Filter = "PNG 이미지|*.png|JPEG 이미지|*.jpg",
                    FilterIndex = jpeg ? 2 : 1,
                    InitialDirectory = path != null ? System.IO.Path.GetDirectoryName(path)
                        : string.IsNullOrWhiteSpace(_settings.SaveFolder) ? Settings.DefaultSaveFolder : _settings.SaveFolder,
                    FileName = path != null ? System.IO.Path.GetFileName(path)
                        : ImageIO.BuildName(_settings.FileNamePattern, DateTime.Now, null),
                    DefaultExt = jpeg ? ".jpg" : ".png"
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }

            try
            {
                if (path == null)
                    path = ImageIO.SaveAuto(img, _settings, DateTime.Now);
                else
                    ImageIO.Overwrite(img, path, _settings.JpegQuality);

                // 쓰기에 성공한 뒤에만 저장 경로와 수정 상태를 갱신한다.
                // 다음 Ctrl+S 는 같은 파일에 저장하고, 새 편집 전에는 닫기 확인을 띄우지 않는다.
                _imageSavePath = path;
                _dirty = false;
                UpdateTitle();
                StHint.Text = "저장했습니다 — " + path;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "저장하지 못했습니다.\n\n" + ex.Message, "SnapView",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Done()
        {
            BitmapSource img = Flatten();
            _completed = true;
            PersistPreferences();
            _recovery.Clear();
            Completed?.Invoke(img);
            Close();
        }

        // ================================================= 세션: 자리 기억 · 임시 저장 · 복구 · 닫기 확인

        /// <summary>이번에 쓴 색·굵기·도구·창 자리를 다음 편집에도 이어 쓴다. 닫힐 때 늘 부른다.</summary>
        private void PersistPreferences()
        {
            _settings.AnnotationColor = _color.ToString(CultureInfo.InvariantCulture);
            _settings.AnnotationThickness = _thickness;
            _settings.AnnotationFontSize = FontSizeBox.Value;
            _settings.MosaicBlockSize = _maskStrength;
            _settings.AnnotationOpacity = _opacity;
            _settings.AnnotationFilled = _filled;
            _settings.AnnotationFontFamily = _fontFamily;
            _settings.AnnotationTool = RememberableTool(_tool).ToString();
            _settings.RecentColors = RecentColors.Serialize(_recentColors);

            bool normal = WindowState == WindowState.Normal;
            Rect r = normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (r.Width >= WindowMemory.MinWidth && r.Height >= WindowMemory.MinHeight)
                _settings.EditorWindow = new WindowMemory(r.Left, r.Top, r.Width, r.Height,
                                                          WindowState == WindowState.Maximized).Format();
            _settings.Save();
        }

        /// <summary>다음에 열 때 시작 도구로 삼을 만한 것만. 스포이드·자르기로 시작하면 당황한다.</summary>
        private static ToolKind RememberableTool(ToolKind t)
            => t is ToolKind.Picker or ToolKind.Crop or ToolKind.Wand or ToolKind.RegionSelect
                 or ToolKind.Eraser or ToolKind.PixelEraser ? ToolKind.Arrow : t;

        private ToolKind LastTool()
            => Enum.TryParse(_settings.AnnotationTool, out ToolKind t) && RememberableTool(t) == t ? t : ToolKind.Arrow;

        /// <summary>창을 만든 직후: 자리·최근 색을 되살리고 임시 저장을 돌린다.</summary>
        private void InitializeSession()
        {
            if (WindowMemory.TryParse(_settings.EditorWindow, out WindowMemory? mem) && mem != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = mem.Left;
                Top = mem.Top;
                Width = mem.Width;
                Height = mem.Height;
                if (mem.Maximized) WindowState = WindowState.Maximized;
                SourceInitialized += (_, _) => WindowPlacement.EnsureOnScreen(this);
            }

            foreach (Color c in RecentColors.Parse(_settings.RecentColors)) _recentColors.Add(c);
            RebuildRecentSwatches();

            _autosave.Tick += (_, _) => AutosaveTick();
            _autosave.Start();
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = _projectPath != null ? System.IO.Path.GetFileName(_projectPath) : "주석 편집";
            Title = name + (_dirty ? "*" : "") + " — SnapView";
        }

        /// <summary>바뀐 게 있으면 뒤에서 복구 파일을 쓴다. 그림이 클 때도 화면이 안 굳게 다른 스레드에서.</summary>
        private void AutosaveTick()
        {
            if (_autosaving || !_dirty || _changeStamp == _savedStamp || _editingText != null) return;

            BitmapSource image = _image;
            if (!image.IsFrozen) { if (image.CanFreeze) image.Freeze(); else return; }

            long stamp = _changeStamp;
            List<Annotation> items = ArrangeTools.CloneAll(Canvas1.Items);
            if (Canvas1.Active != null && Canvas1.Active is not CropAnnotation) items.Add(Canvas1.Active.Clone());
            int counter = _counter;

            _autosaving = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _recovery.Save(image, items, counter); }
                catch (Exception ex) { Log.Write("임시 저장 실패: " + ex.Message); }
            }).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                _autosaving = false;
                _savedStamp = stamp;
            })));
        }

        /// <summary>이전에 편집하다 죽은 것이 남아 있으면 이어서 할지 묻는다.</summary>
        private void OfferRecovery()
        {
            if (!_recovery.Exists) return;
            try
            {
                MessageBoxResult r = MessageBox.Show(this,
                    $"저장하지 못한 편집이 남아 있습니다 ({_recovery.SavedAt:M월 d일 HH:mm}).\n이어서 편집할까요?\n\n아니요를 누르면 지웁니다.",
                    "SnapView", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                {
                    (BitmapSource image, List<Annotation> items, int counter) = _recovery.Load();
                    PushUndo();   // 지금 캡처로 되돌릴 수 있게
                    _image = image;
                    _counter = counter;
                    Canvas1.Source = image;
                    Canvas1.Items.Clear();
                    Canvas1.Items.AddRange(items);
                    SetSelection(null);
                    _region = Rect.Empty;
                    _imageSavePath = null;
                    _fitToWindow = true;
                    Relayout();
                    Canvas1.InvalidateVisual();
                    UpdateStatus();
                    StHint.Text = "이전 편집을 이어서 합니다";
                    return;
                }
            }
            catch (Exception ex) { Log.Write("복구 실패: " + ex.Message); }
            _recovery.Clear();
        }

        /// <summary>
        /// 닫기 전 확인. 예전엔 Esc 두 번이면 주석 스무 개가 조용히 사라졌다.
        /// 예 = 완료(설정대로 저장·복사)하고 닫기, 아니요 = 버리고 닫기, 취소 = 계속 편집.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;

            CommitText();
            if (Canvas1.Active != null && Canvas1.Active is not CropAnnotation) ConfirmActive();

            if (!_completed && _dirty)
            {
                MessageBoxResult r = MessageBox.Show(this,
                    "편집한 내용이 있습니다. 완료(설정대로 저장·복사)하고 닫을까요?\n\n" +
                    "예: 완료하고 닫기 · 아니요: 버리고 닫기 · 취소: 계속 편집",
                    "SnapView", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageBoxResult.Yes)
                {
                    e.Cancel = true;   // Done() 이 스스로 닫는다(닫는 중에 또 닫으면 예외)
                    Dispatcher.BeginInvoke(new Action(Done));
                    return;
                }
            }

            _autosave.Stop();
            if (!_completed) PersistPreferences();
            _recovery.Clear();
        }

        // ================================================= 꾸며서 내보내기

        /// <summary>여백 · 배경 · 그림자 · 둥근 모서리 · 워터마크를 입혀 저장하거나 복사한다.</summary>
        private void OnExportDecorated(object sender, RoutedEventArgs e)
        {
            CommitText();
            ConfirmActive();

            NumberStepper Box(double min, double max, double step, double value, string? suffix = null)
            {
                var s = new NumberStepper { Minimum = min, Maximum = max, Step = step, Width = 96, Height = 26, Suffix = suffix ?? "" };
                s.SetSilently(value);
                return s;
            }
            NumberStepper pad = Box(0, 400, 4, _settings.ExportPadding, "px");
            NumberStepper radius = Box(0, 80, 2, _settings.ExportCornerRadius, "px");
            var shadow = new CheckBox { Content = "그림자", IsChecked = _settings.ExportShadow, Foreground = (Brush)FindResource("Fg"), VerticalAlignment = VerticalAlignment.Center };
            var bg = new ComboBox { Width = 130, Height = 26 };
            foreach (string s in new[] { "없음(투명)", "흰색", "검정", "짙은 회색", "지금 색", "지금 색 그라데이션" }) bg.Items.Add(s);
            bg.SelectedIndex = Math.Clamp(_settings.ExportBackground, 0, 5);
            var wm = new TextBox { Text = _settings.ExportWatermark, Width = 200, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
            NumberStepper wmOpacity = Box(10, 100, 10, 50, "%");

            var grid = new Grid { Margin = new Thickness(12) };
            for (int i = 0; i < 2; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 8; i++) grid.RowDefinitions.Add(new RowDefinition());
            void Put(int row, string label, FrameworkElement control)
            {
                var tb = new TextBlock { Text = label, Margin = new Thickness(0, 0, 10, 8), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Fg") };
                Grid.SetRow(tb, row); Grid.SetColumn(tb, 0);
                Grid.SetRow(control, row); Grid.SetColumn(control, 1);
                control.Margin = new Thickness(0, 0, 0, 8);
                grid.Children.Add(tb);
                grid.Children.Add(control);
            }
            Put(0, "여백", pad);
            Put(1, "둥근 모서리", radius);
            Put(2, "배경", bg);
            Put(3, "", shadow);
            Put(4, "워터마크", wm);
            Put(5, "워터마크 진하기", wmOpacity);

            var copy = new Button { Content = "복사", MinWidth = 60, Style = (Style)FindResource("ToolButton") };
            var save = new Button { Content = "저장...", MinWidth = 60, IsDefault = true, Style = (Style)FindResource("ToolButton"), Margin = new Thickness(6, 0, 0, 0) };
            var cancel = new Button { Content = "취소", MinWidth = 56, IsCancel = true, Style = (Style)FindResource("ToolButton"), Margin = new Thickness(6, 0, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(copy); buttons.Children.Add(save); buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 7); Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            var dlg = new Window
            {
                Title = "꾸며서 내보내기", Owner = this, Content = grid,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = (Brush)FindResource("BgChrome")
            };
            bool doCopy = false;
            copy.Click += (_, _) => { doCopy = true; dlg.DialogResult = true; };
            save.Click += (_, _) => dlg.DialogResult = true;
            if (dlg.ShowDialog() != true) return;

            _settings.ExportPadding = (int)pad.Value;
            _settings.ExportCornerRadius = radius.Value;
            _settings.ExportShadow = shadow.IsChecked == true;
            _settings.ExportBackground = bg.SelectedIndex;
            _settings.ExportWatermark = wm.Text.Trim();

            Color? bgColor = bg.SelectedIndex switch
            {
                1 => Colors.White, 2 => Colors.Black, 3 => Color.FromRgb(0x2B, 0x2F, 0x3A),
                4 or 5 => _color, _ => (Color?)null
            };
            var decor = new ExportDecor
            {
                Padding = (int)pad.Value, CornerRadius = radius.Value, Shadow = shadow.IsChecked == true,
                Background = bgColor, GradientBackground = bg.SelectedIndex == 5,
                Watermark = string.IsNullOrWhiteSpace(wm.Text) ? null : wm.Text.Trim(),
                WatermarkOpacity = wmOpacity.Value / 100.0
            };

            BitmapSource img = AnnotationRenderer.Decorate(Flatten(), decor);
            if (doCopy)
            {
                StHint.Text = ImageIO.CopyToClipboard(img) ? "꾸민 그림을 클립보드에 복사했습니다" : "클립보드에 복사하지 못했습니다";
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "꾸민 그림 저장",
                Filter = "PNG 이미지|*.png|JPEG 이미지|*.jpg",
                FileName = "SnapView_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture),
                DefaultExt = ".png"
            };
            if (sfd.ShowDialog() != true) return;
            try { ImageIO.SaveTo(img, sfd.FileName, _settings.JpegQuality); StHint.Text = "저장했습니다"; }
            catch (Exception ex) { MessageBox.Show(this, "저장하지 못했습니다.\n\n" + ex.Message, "SnapView", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
