using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SnapView.Capture;
using SnapView.Core;
using SnapView.Native;

namespace SnapView.Prefs
{
    /// <summary>
    /// 설정 창. 여기서 고친 값은 <see cref="Result"/> 에 담아 돌려주고,
    /// 실제 적용(단축키 재등록 등)은 컨트롤러가 한다.
    ///
    /// 이 창은 모달이 아니다. 상주 앱의 설정 창이 화면을 붙들면 그동안 캡처를
    /// 못 하는데, 정작 설정 창 자체를 찍고 싶을 때가 있다.
    ///
    /// 전역 단축키는 <b>키를 눌러 넣는 동안만</b> 풀어 준다. 창이 떠 있는 내내
    /// 풀어 버리면 설정을 열어 둔 채로는 캡처가 안 된다. 반대로 안 풀면
    /// 사용자가 Alt+Shift+S 를 눌러 지정하려 할 때 캡처가 튀어나온다.
    /// 그 신호가 <see cref="RecordingChanged"/> 다.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private void OnAiSettings(object sender, RoutedEventArgs e)
        {
            try { new AiSettingsWindow { Owner = this }.ShowDialog(); }
            catch { MessageBox.Show(this, "AI 설정을 읽지 못했습니다. 설정 파일과 접근 권한을 확인해 주세요.", "AI 설정", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private const int ProbeIdBase = 41000;

        private readonly Settings _working;

        /// <summary>지금 고른 초당 장수. 막대가 못 닿는 큰 값도 그대로 담아 둔다.</summary>
        private int _fps = 10;
        private bool _syncingFps;

        /// <summary>이 창을 열 때 실제로 걸려 있던 단축키. 내 것을 남의 것으로 오해하지 않으려고 둔다.</summary>
        private readonly string[] _applied;

        /// <summary>저장을 눌렀을 때의 결과. 취소하면 null.</summary>
        internal Settings? Result { get; private set; }

        /// <summary>단축키 지정 칸이 키를 받는 중인지. true 동안 컨트롤러가 전역 단축키를 풀어 준다.</summary>
        internal event Action<bool>? RecordingChanged;

        internal SettingsWindow(Settings current)
        {
            _working = Clone(current);
            _applied = new[]
            {
                current.HotKeyRegion, current.HotKeyFullScreen,
                current.HotKeyActiveWindow, current.HotKeyRecord,
                current.HotKeyRecordFullScreen, current.HotKeyRecordWindow
            };
            InitializeComponent();

            // 초당 장수는 막대와 숫자칸 둘 다에서 고칠 수 있다. 막대는 어림잡아 끌기 좋고,
            // 숫자칸은 막대가 안 닿는 값까지 직접 쳐 넣을 수 있다.
            // (값을 읽어 넣기 전에 한계부터 잡아 둬야 큰 값이 깎이지 않는다)
            FpsBox.Minimum = ScreenRecorder.MinFps;
            FpsBox.Maximum = ScreenRecorder.MaxFps;
            FpsBox.Step = 1;
            FpsBox.Suffix = "fps";

            SlFps.ValueChanged += (_, e) => SetFps((int)Math.Round(e.NewValue));
            FpsBox.ValueChanged += v => SetFps((int)Math.Round(v));

            Load(_working);

            TbPattern.TextChanged += (_, _) => UpdatePreview();
            TbRecPattern.TextChanged += (_, _) => UpdateRecPreview();
            RbGif.Checked += (_, _) => { SetFps(_fps); UpdateRecPreview(); };
            RbMp4.Checked += (_, _) => { SetFps(_fps); UpdateRecPreview(); };
            RbPng.Checked += (_, _) => UpdatePreview();
            RbJpg.Checked += (_, _) => UpdatePreview();
            SlQuality.ValueChanged += (_, e) =>
                LbQuality.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
            SlNearest.ValueChanged += (_, e) =>
                LbNearest.Text = ((int)(e.NewValue * 100)).ToString(CultureInfo.InvariantCulture) + "%";

            foreach (HotKeyBox box in GlobalHotKeyBoxes()) box.HotKeyChanged += CheckHotKeys;
            foreach (HotKeyBox box in PlayerKeyBoxes()) box.HotKeyChanged += CheckPlayerKeys;

            SkipBox.Minimum = 0.1;
            SkipBox.Maximum = 600;
            SkipBox.Step = 1;
            SkipBox.Suffix = "초";
            CountdownBox.Minimum = 0;
            CountdownBox.Maximum = 10;
            CountdownBox.Step = 1;
            CountdownBox.Suffix = "초";

            // 이 칸에 포커스가 있는 동안만 전역 단축키를 풀어 준다.
            // 재생 키 칸도 마찬가지다 — 여기에 Alt+Shift+S 를 넣으려는데 캡처가 튀어나오면 안 된다.
            foreach (HotKeyBox box in GlobalHotKeyBoxes().Concat(PlayerKeyBoxes()))
            {
                box.GotKeyboardFocus += (_, _) => RecordingChanged?.Invoke(true);
                box.LostKeyboardFocus += (_, _) => RecordingChanged?.Invoke(false);
            }

            Loaded += (_, _) => { CheckHotKeys(); CheckPlayerKeys(); UpdateAssocNote(); UpdateStoredNote(); UpdateAssocFormats(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private static Settings Clone(Settings s) => new()
        {
            SaveToDisk = s.SaveToDisk,
            CopyToClipboard = s.CopyToClipboard,
            OpenViewerAfterCapture = s.OpenViewerAfterCapture,
            OpenEditorAfterCapture = s.OpenEditorAfterCapture,
            AdjustBeforeCapture = s.AdjustBeforeCapture,
            ShowCrosshair = s.ShowCrosshair,
            UseGraphicsCapture = s.UseGraphicsCapture,
            IncludeCursor = s.IncludeCursor,
            PlayShutterSound = s.PlayShutterSound,
            ShutterVolume = s.ShutterVolume,
            SaveFolder = s.SaveFolder,
            FileNamePattern = s.FileNamePattern,
            FrameNamePattern = s.FrameNamePattern,
            ImageFormat = s.ImageFormat,
            JpegQuality = s.JpegQuality,
            HotKeyRegion = s.HotKeyRegion,
            HotKeyFullScreen = s.HotKeyFullScreen,
            HotKeyActiveWindow = s.HotKeyActiveWindow,
            HotKeyRecord = s.HotKeyRecord,
            DefaultFitMode = s.DefaultFitMode,
            ShowCheckerboard = s.ShowCheckerboard,
            ShowThumbnailStrip = s.ShowThumbnailStrip,
            NearestNeighborAbove = s.NearestNeighborAbove,
            WrapAround = s.WrapAround,
            RunAtStartup = s.RunAtStartup,
            AnnotationColor = s.AnnotationColor,
            AnnotationThickness = s.AnnotationThickness,
            AnnotationFontSize = s.AnnotationFontSize,
            AnnotationFontFamily = s.AnnotationFontFamily,
            AnnotationOpacity = s.AnnotationOpacity,
            AnnotationFilled = s.AnnotationFilled,
            MosaicBlockSize = s.MosaicBlockSize,
            RecordingFps = s.RecordingFps,
            RecordingFormat = s.RecordingFormat,
            RecordSystemAudio = s.RecordSystemAudio,
            AnnotationTool = s.AnnotationTool,
            RecentColors = s.RecentColors,
            EditorWindow = s.EditorWindow,
            ExportPadding = s.ExportPadding,
            ExportCornerRadius = s.ExportCornerRadius,
            ExportShadow = s.ExportShadow,
            ExportBackground = s.ExportBackground,
            ExportWatermark = s.ExportWatermark,
            RecordCursor = s.RecordCursor,
            RecordCountdownSeconds = s.RecordCountdownSeconds,
            PlayRecordSound = s.PlayRecordSound,
            RecordSoundVolume = s.RecordSoundVolume,
            RecordNamePattern = s.RecordNamePattern,
            SettingsVersion = s.SettingsVersion,
            HotKeyRecordFullScreen = s.HotKeyRecordFullScreen,
            HotKeyRecordWindow = s.HotKeyRecordWindow,
            PlayerLoop = s.PlayerLoop,
            PlayNextInFolder = s.PlayNextInFolder,
            WheelChangesVolume = s.WheelChangesVolume,
            OpenInNewWindow = s.OpenInNewWindow,
            PlayerSkipSeconds = s.PlayerSkipSeconds,
            PlayerKeyPlayPause = s.PlayerKeyPlayPause,
            PlayerKeyBack = s.PlayerKeyBack,
            PlayerKeyForward = s.PlayerKeyForward,
            PlayerKeyPrevFrame = s.PlayerKeyPrevFrame,
            PlayerKeyNextFrame = s.PlayerKeyNextFrame,
            PlayerKeyVolumeUp = s.PlayerKeyVolumeUp,
            PlayerKeyVolumeDown = s.PlayerKeyVolumeDown,
            PlayerKeyMute = s.PlayerKeyMute,
            PlayerKeyLoop = s.PlayerKeyLoop,
            PlayerKeyGrabFrame = s.PlayerKeyGrabFrame
        };

        private void Load(Settings s)
        {
            CbSave.IsChecked = s.SaveToDisk;
            CbClipboard.IsChecked = s.CopyToClipboard;
            CbViewer.IsChecked = s.OpenViewerAfterCapture;
            CbEditor.IsChecked = s.OpenEditorAfterCapture;
            CbAdjust.IsChecked = s.AdjustBeforeCapture;
            CbCross.IsChecked = s.ShowCrosshair;
            CbWgc.IsChecked = s.UseGraphicsCapture;
            CbCursor.IsChecked = s.IncludeCursor;
            CbSound.IsChecked = s.PlayShutterSound;
            SlVolume.Value = Math.Clamp(s.ShutterVolume, 0, 100);

            TbFolder.Text = s.SaveFolder;
            TbPattern.Text = s.FileNamePattern;
            RbPng.IsChecked = !s.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
            RbJpg.IsChecked = !RbPng.IsChecked!.Value;
            SlQuality.Value = Math.Clamp(s.JpegQuality, 40, 100);
            LbQuality.Text = ((int)SlQuality.Value).ToString(CultureInfo.InvariantCulture);

            HkRegion.HotKeyText = s.HotKeyRegion;
            HkFull.HotKeyText = s.HotKeyFullScreen;
            HkWindow.HotKeyText = s.HotKeyActiveWindow;
            HkRecord.HotKeyText = s.HotKeyRecord;
            HkRecordFull.HotKeyText = s.HotKeyRecordFullScreen;
            HkRecordWindow.HotKeyText = s.HotKeyRecordWindow;

            CbLoop.IsChecked = s.PlayerLoop;
            CbNextInFolder.IsChecked = s.PlayNextInFolder;
            CbWheelVolume.IsChecked = s.WheelChangesVolume;
            CbNewWindow.IsChecked = s.OpenInNewWindow;
            SkipBox.Value = Math.Clamp(s.PlayerSkipSeconds, 0.1, 600);
            PkPlayPause.HotKeyText = s.PlayerKeyPlayPause;
            PkBack.HotKeyText = s.PlayerKeyBack;
            PkForward.HotKeyText = s.PlayerKeyForward;
            PkPrevFrame.HotKeyText = s.PlayerKeyPrevFrame;
            PkNextFrame.HotKeyText = s.PlayerKeyNextFrame;
            PkVolumeUp.HotKeyText = s.PlayerKeyVolumeUp;
            PkVolumeDown.HotKeyText = s.PlayerKeyVolumeDown;
            PkMute.HotKeyText = s.PlayerKeyMute;
            PkLoop.HotKeyText = s.PlayerKeyLoop;
            PkGrabFrame.HotKeyText = s.PlayerKeyGrabFrame;

            RbMp4.IsChecked = !string.Equals(s.RecordingFormat, "gif", StringComparison.OrdinalIgnoreCase);
            RbGif.IsChecked = !RbMp4.IsChecked!.Value;
            CbRecAudio.IsChecked = s.RecordSystemAudio;
            CbRecCursor.IsChecked = s.RecordCursor;
            CountdownBox.Value = Math.Clamp(s.RecordCountdownSeconds, 0, 10);
            CbRecSound.IsChecked = s.PlayRecordSound;
            SlRecVolume.Value = Math.Clamp(s.RecordSoundVolume, 0, 100);
            TbRecPattern.Text = s.RecordNamePattern;
            UpdateRecPreview();
            SetFps(s.RecordingFps);

            RbShrink.IsChecked = s.DefaultFitMode == FitMode.ShrinkToFit;
            RbStretch.IsChecked = s.DefaultFitMode == FitMode.StretchToFit;
            RbFitWidth.IsChecked = s.DefaultFitMode == FitMode.FitWidth;
            RbActual.IsChecked = s.DefaultFitMode == FitMode.Actual;

            CbChecker.IsChecked = s.ShowCheckerboard;
            CbStrip.IsChecked = s.ShowThumbnailStrip;
            CbWrap.IsChecked = s.WrapAround;
            SlNearest.Value = Math.Clamp(s.NearestNeighborAbove, 1, 10);
            LbNearest.Text = ((int)(SlNearest.Value * 100)).ToString(CultureInfo.InvariantCulture) + "%";

            CbStartup.IsChecked = s.RunAtStartup;

            UpdatePreview();
        }

        /// <summary>
        /// 초당 장수 하나를 막대·숫자칸·설명에 한꺼번에 적는다.
        /// 둘 중 어디를 고쳐도 여기로 모이고, 여기서 되돌려 쓰는 동안은
        /// 서로의 신호를 무시해 되받아치기를 끊는다.
        /// </summary>
        private void SetFps(int fps)
        {
            if (_syncingFps) return;
            _syncingFps = true;
            try
            {
                fps = Math.Clamp(fps, ScreenRecorder.MinFps, ScreenRecorder.MaxFps);

                // GIF 는 간격 단위가 1/100초라 50 이 한계다. 더 높게 골라도 그렇게 안 담긴다.
                bool gif = RbGif?.IsChecked == true;
                if (gif) fps = Math.Min(fps, ScreenRecorder.GifMaxFps);

                // 막대는 여기까지밖에 안 닿는다. 그보다 큰 값은 숫자칸에만 정확히 남는다.
                SlFps.Value = Math.Clamp(fps, SlFps.Minimum, SlFps.Maximum);
                FpsBox.Value = fps;
                _fps = fps;

                LbFpsNote.Text = FpsAdvice(fps, gif);
            }
            finally { _syncingFps = false; }
        }

        /// <summary>
        /// 고른 값이 실제로 어떤 뜻인지 한 줄로 알려 준다.
        /// 높게 잡는다고 그만큼 담긴다는 보장이 없다는 걸 녹화 뒤가 아니라 여기서 말해 준다.
        /// </summary>
        private static string FpsAdvice(int fps, bool gif)
        {
            if (gif)
            {
                return fps + " fps — GIF 는 초당 " + ScreenRecorder.GifMaxFps + "장까지만 담을 수 있고, 폭이 " +
                       ScreenRecorder.GifMaxWidth + " 을 넘으면 줄어들며, 장마다 색표를 갖고 있어 파일이 매우 커집니다. " +
                       "10~20 을 권합니다. 소리는 안 담깁니다.";
            }

            string common = fps <= 8
                ? "가볍고 파일이 작습니다. 문서·설명용."
                : fps <= 20 ? "보통 화면 녹화에 넉넉합니다."
                : fps <= 35 ? "움직임이 매끄럽습니다. 파일이 커집니다."
                : "아주 매끄럽지만 기계가 못 따라갈 수 있습니다.";

            string caution = fps > ScreenRecorder.SmoothFpsHint
                ? " 넓은 영역을 이 값으로 찍으면 실제로는 덜 담깁니다 — 녹화 막대에 실제 장수가 나옵니다."
                : "";

            return fps + " fps — " + common + caution;
        }

        /// <summary>새로 설치했을 때의 값으로. 예전엔 GIF 시절 값(10)으로 되돌려서 영상이 뚝뚝 끊겼다.</summary>
        private void OnFpsDefault(object sender, RoutedEventArgs e) => SetFps(new Settings().RecordingFps);

        private Settings Collect()
        {
            _working.SaveToDisk = CbSave.IsChecked == true;
            _working.CopyToClipboard = CbClipboard.IsChecked == true;
            _working.OpenViewerAfterCapture = CbViewer.IsChecked == true;
            _working.OpenEditorAfterCapture = CbEditor.IsChecked == true;
            _working.AdjustBeforeCapture = CbAdjust.IsChecked == true;
            _working.ShowCrosshair = CbCross.IsChecked == true;
            _working.UseGraphicsCapture = CbWgc.IsChecked == true;
            _working.IncludeCursor = CbCursor.IsChecked == true;
            _working.PlayShutterSound = CbSound.IsChecked == true;
            _working.ShutterVolume = (int)SlVolume.Value;

            _working.SaveFolder = string.IsNullOrWhiteSpace(TbFolder.Text)
                ? Settings.DefaultSaveFolder : TbFolder.Text.Trim();
            _working.FileNamePattern = string.IsNullOrWhiteSpace(TbPattern.Text)
                ? "SnapView_{0:yyyy-MM-dd_HHmmss}" : TbPattern.Text.Trim();
            _working.ImageFormat = RbJpg.IsChecked == true ? "jpg" : "png";
            _working.JpegQuality = (int)SlQuality.Value;

            _working.HotKeyRegion = HkRegion.HotKeyText;
            _working.HotKeyFullScreen = HkFull.HotKeyText;
            _working.HotKeyActiveWindow = HkWindow.HotKeyText;
            _working.HotKeyRecord = HkRecord.HotKeyText;
            _working.HotKeyRecordFullScreen = HkRecordFull.HotKeyText;
            _working.HotKeyRecordWindow = HkRecordWindow.HotKeyText;

            _working.PlayerLoop = CbLoop.IsChecked == true;
            _working.PlayNextInFolder = CbNextInFolder.IsChecked == true;
            _working.WheelChangesVolume = CbWheelVolume.IsChecked == true;
            _working.OpenInNewWindow = CbNewWindow.IsChecked == true;
            _working.PlayerSkipSeconds = Math.Round(SkipBox.Value, 2);
            _working.PlayerKeyPlayPause = PkPlayPause.HotKeyText;
            _working.PlayerKeyBack = PkBack.HotKeyText;
            _working.PlayerKeyForward = PkForward.HotKeyText;
            _working.PlayerKeyPrevFrame = PkPrevFrame.HotKeyText;
            _working.PlayerKeyNextFrame = PkNextFrame.HotKeyText;
            _working.PlayerKeyVolumeUp = PkVolumeUp.HotKeyText;
            _working.PlayerKeyVolumeDown = PkVolumeDown.HotKeyText;
            _working.PlayerKeyMute = PkMute.HotKeyText;
            _working.PlayerKeyLoop = PkLoop.HotKeyText;
            _working.PlayerKeyGrabFrame = PkGrabFrame.HotKeyText;

            _working.RecordingFormat = RbGif.IsChecked == true ? "gif" : "mp4";
            _working.RecordingFps = _fps;
            _working.RecordSystemAudio = CbRecAudio.IsChecked == true;
            _working.RecordCursor = CbRecCursor.IsChecked == true;
            _working.RecordCountdownSeconds = (int)Math.Round(CountdownBox.Value);
            _working.PlayRecordSound = CbRecSound.IsChecked == true;
            _working.RecordSoundVolume = (int)SlRecVolume.Value;
            _working.RecordNamePattern = string.IsNullOrWhiteSpace(TbRecPattern.Text)
                ? RecordingNames.DefaultPattern : TbRecPattern.Text.Trim();

            _working.DefaultFitMode =
                RbStretch.IsChecked == true ? FitMode.StretchToFit :
                RbFitWidth.IsChecked == true ? FitMode.FitWidth :
                RbActual.IsChecked == true ? FitMode.Actual : FitMode.ShrinkToFit;

            _working.ShowCheckerboard = CbChecker.IsChecked == true;
            _working.ShowThumbnailStrip = CbStrip.IsChecked == true;
            _working.WrapAround = CbWrap.IsChecked == true;
            _working.NearestNeighborAbove = SlNearest.Value;
            _working.RunAtStartup = CbStartup.IsChecked == true;

            return _working;
        }

        private void UpdatePreview()
        {
            if (TbPreview == null) return;

            string ext = RbJpg.IsChecked == true ? ".jpg" : ".png";
            string name;
            try
            {
                name = string.Format(CultureInfo.InvariantCulture, TbPattern.Text, DateTime.Now);
            }
            catch
            {
                TbPreview.Text = "이름 규칙이 올바르지 않습니다. 예: SnapView_{0:yyyy-MM-dd_HHmmss}";
                return;
            }
            TbPreview.Text = "예시:  " + name + ext + "      ({0} 자리에 캡처한 시각이 들어갑니다)";
        }

        /// <summary>전역 단축키 칸들. 순서는 아래 안내 칸과 짝이 맞아야 한다.</summary>
        private HotKeyBox[] GlobalHotKeyBoxes()
            => new[] { HkRegion, HkFull, HkWindow, HkRecord, HkRecordFull, HkRecordWindow };

        /// <summary>재생 중에만 듣는 키 칸들.</summary>
        private HotKeyBox[] PlayerKeyBoxes()
            => new[] { PkPlayPause, PkBack, PkForward, PkPrevFrame, PkNextFrame,
                       PkVolumeUp, PkVolumeDown, PkMute, PkLoop, PkGrabFrame };

        /// <summary>
        /// 재생 키끼리 겹치는지만 본다.
        ///
        /// 전역 단축키와 달리 다른 프로그램에 물어볼 필요가 없다 — 뷰어 창이 앞에 있을 때만
        /// 듣기 때문이다. 대신 <b>같은 키를 두 기능에 넣으면</b> 하나는 영영 안 먹으므로 그건 짚어 준다.
        /// </summary>
        private void CheckPlayerKeys()
        {
            HotKeyBox[] boxes = PlayerKeyBoxes();
            var seen = new System.Collections.Generic.Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var clashes = new System.Collections.Generic.List<string>();

            foreach (HotKeyBox box in boxes)
            {
                string text = box.HotKeyText;
                if (string.IsNullOrEmpty(text)) continue;

                if (seen.TryGetValue(text, out int _)) clashes.Add(text);
                else seen[text] = 1;
            }

            PlayerKeyNote.Text = clashes.Count == 0
                ? ""
                : "같은 키가 두 곳에 들어갔습니다: " + string.Join(", ", clashes.Distinct()) +
                  " — 위쪽 기능만 듣습니다.";
        }

        /// <summary>단축키끼리 겹치지 않는지, 다른 프로그램이 쓰고 있지 않은지 확인.</summary>
        private void CheckHotKeys()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            HotKeyBox[] boxes = GlobalHotKeyBoxes();
            var notes = new[] { HkRegionNote, HkFullNote, HkWindowNote, HkRecordNote,
                                HkRecordFullNote, HkRecordWindowNote };

            for (int i = 0; i < boxes.Length; i++)
            {
                string text = boxes[i].HotKeyText;
                if (string.IsNullOrEmpty(text))
                {
                    notes[i].Text = "지정 안 함";
                    continue;
                }

                bool duplicate = false;
                for (int j = 0; j < boxes.Length; j++)
                {
                    if (i != j && string.Equals(boxes[j].HotKeyText, text, StringComparison.OrdinalIgnoreCase))
                        duplicate = true;
                }

                if (duplicate)
                    notes[i].Text = "다른 항목과 겹칩니다";
                else if (SystemHotKeys.UsesPrintScreen(text))
                    notes[i].Text = SystemHotKeys.PrintScreenTakenByWindows()
                        ? "사용 가능 — 윈도우 캡처 도구보다 먼저 가로챕니다"
                        : "사용 가능";
                else if (!IsMine(text) && !HotKeyBox.IsAvailable(text, hwnd, ProbeIdBase + i))
                    notes[i].Text = "다른 프로그램이 쓰고 있습니다";
                else notes[i].Text = "사용 가능";
            }
        }

        /// <summary>
        /// 지금 SnapView 자신이 잡고 있는 단축키인가.
        /// 이 창은 모달이 아니라서 단축키가 살아 있는 채로 떠 있다. 그대로 등록을
        /// 시험해 보면 <b>내가 쥔 키를 남이 쥔 것으로</b> 보고한다.
        /// (키 순서는 달라도 같은 조합이면 같은 것으로 친다: "Shift+Alt+S" == "Alt+Shift+S")
        /// </summary>
        private bool IsMine(string text)
        {
            HotKeySpec? want = HotKeySpec.Parse(text);
            if (want == null) return false;

            foreach (string applied in _applied)
            {
                HotKeySpec? have = HotKeySpec.Parse(applied);
                if (have != null && have.Modifiers == want.Modifiers && have.VirtualKey == want.VirtualKey)
                    return true;
            }
            return false;
        }

        // ================= 버튼 =================

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "캡처 이미지를 저장할 폴더",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(TbFolder.Text) ? TbFolder.Text : Settings.DefaultSaveFolder
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TbFolder.Text = dlg.SelectedPath;
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LbVolume != null)
                LbVolume.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void OnPreviewSound(object sender, RoutedEventArgs e)
            => CaptureSound.Play((int)SlVolume.Value);

        private void OnRecVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LbRecVolume != null)
                LbRecVolume.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>시작음을 내고 이어서 종료음. 둘이 다르게 들리는지 여기서 확인한다.</summary>
        private void OnPreviewRecordSound(object sender, RoutedEventArgs e)
        {
            int volume = (int)SlRecVolume.Value;
            RecordSound.PlayStart(volume);      // 다 날 때까지 기다린다
            RecordSound.PlayStop(volume);
        }

        private void UpdateRecPreview()
        {
            if (TbRecPreview == null || TbRecPattern == null) return;
            string ext = RbGif?.IsChecked == true ? ".gif" : ".mp4";
            TbRecPreview.Text = "예시:  " + RecordingNames.Build(TbRecPattern.Text, DateTime.Now, "전체 화면") + ext +
                                "      ({0} 자리에 시각, {1} 자리에 무엇을 찍었는지가 들어갑니다)";
        }

        private void UpdateAssocNote()
        {
            AssocNote.Text =
                FileAssociation.IsRegistered() ? "등록됨 — 윈도우 설정에서 고르면 됩니다" :
                FileAssociation.WasEverRegistered() ? "등록이 낡았습니다 — 눌러서 새로 고치세요 (동영상 포함)" :
                "아직 등록 안 됨";
        }

        private void OnAssociate(object sender, RoutedEventArgs e)
        {
            if (FileAssociation.IsRegistered())
            {
                MessageBoxResult keep = MessageBox.Show(this,
                    "이미 등록되어 있습니다.\n\n" +
                    "[확인] 윈도우 기본 앱 설정 열기\n" +
                    "[취소] 등록 해제",
                    "SnapView", MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (keep == MessageBoxResult.OK) FileAssociation.OpenDefaultAppsSettings();
                else if (!FileAssociation.Unregister(out string removeError))
                    MessageBox.Show(this, "해제하지 못했습니다.\n\n" + removeError, "SnapView",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);

                UpdateAssocNote();
                return;
            }

            if (!FileAssociation.Register(out string error))
            {
                MessageBox.Show(this, "등록하지 못했습니다.\n\n" + error, "SnapView",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateAssocNote();

            // 등록했다고만 말하고 목록에 안 뜨면 사용자는 어디가 잘못됐는지 알 길이 없다.
            string[] missing = FileAssociation.MissingAssociations();
            string report = missing.Length == 0
                ? "이미지와 동영상 확장자를 모두 등록했습니다."
                : "일부 확장자를 등록하지 못했습니다: " + string.Join(" ", missing);

            if (missing.Length > 0) Log.Write("파일 연결 일부 실패: " + string.Join(" ", missing));

            MessageBoxResult open = MessageBox.Show(this,
                report + "\n\n" +
                "윈도우 8 이후로는 기본 앱을 프로그램이 마음대로 바꿀 수 없습니다.\n" +
                "설정 화면의 SnapView 항목에서 이미지·동영상 확장자를 직접 골라 주세요.\n\n지금 열까요?",
                "SnapView", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (open == MessageBoxResult.OK) FileAssociation.OpenDefaultAppsSettings();
        }

        /// <summary>
        /// 연결 등록이 무엇을 다루는지 적는다. 목록을 XAML 에 손으로 적어 두면
        /// 확장자가 늘어날 때마다 또 어긋난다 — 실제 목록에서 만든다.
        /// </summary>
        private void UpdateAssocFormats()
        {
            AssocFormats.Text =
                $"그림 {MediaKinds.ImageExtensions.Length}가지 · 동영상 {MediaKinds.VideoExtensions.Length}가지를 " +
                "SnapView 로 열 수 있게 등록합니다 (" +
                string.Join(" ", MediaKinds.ImageExtensions).Replace(".", "") + " / " +
                string.Join(" ", MediaKinds.VideoExtensions).Replace(".", "") + "). " +
                "윈도우 8 이후로는 마지막 선택을 윈도우 설정에서 해야 합니다.";
        }

        /// <summary>이 PC 에 남아 있는 것을 한 줄로 적는다.</summary>
        private void UpdateStoredNote()
        {
            int lines = AppData.HistoryLines();

            var parts = new System.Collections.Generic.List<string>();
            foreach (AppData.Item i in AppData.Stored())
            {
                parts.Add(i.Name + " " + AppData.SizeText(i.Bytes) +
                          (i.Clearable && lines > 0 ? $" ({lines}줄)" : ""));
            }
            LbStored.Text = parts.Count == 0 ? "쌓인 것이 없습니다." : string.Join("   ·   ", parts);

            BtnClearData.IsEnabled = AppData.ClearableBytes() > 0;
        }

        private void OnClearData(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this,
                    "열어 본 파일 경로가 담긴 기록을 지웁니다.\n설정은 그대로 남습니다.\n\n지울까요?",
                    "SnapView", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            LbClearNote.Text = AppData.Clear(out string error) ? "지웠습니다." : "지우지 못했습니다: " + error;
            UpdateStoredNote();
        }

        private void OnBrandLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            AppController.OpenBrandSite();
            e.Handled = true;
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.ConfigDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Settings.ConfigDirectory + "\"")
                { UseShellExecute = true });
            }
            catch { }
        }

        private void OnResetDefaults(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "모든 설정을 처음 상태로 되돌릴까요?", "SnapView",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            Load(new Settings());
            CheckHotKeys();
            CheckPlayerKeys();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Result = null;   // 모달이 아니므로 DialogResult 는 못 쓴다
            Close();
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            Result = Collect();
            Close();
        }

    }
}
