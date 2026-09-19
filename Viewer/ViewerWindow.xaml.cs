using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapView.Core;
using SnapView.Native;

namespace SnapView.Viewer
{
    /// <summary>이 창이 무엇을 보는 창인가.</summary>
    internal enum ViewerRole
    {
        /// <summary>그림 뷰어.</summary>
        Image,
        /// <summary>동영상 재생기.</summary>
        Video
    }

    /// <summary>
    /// 이미지 뷰어.
    ///
    /// 배율(_zoom)은 <b>이미지 픽셀 : 화면 물리 픽셀</b> 비율이다. 즉 100% 는 고DPI
    /// 모니터에서도 진짜 1:1 이다. WPF 는 DIP 로 배치하므로 실제 변환에 쓰는 값은
    /// Eff = _zoom / DPI배율 이다.
    ///
    /// 마우스 휠은 <b>확대·축소 전용</b>이다(스크롤로 쓰지 않는다).
    /// </summary>
    public partial class ViewerWindow : Window
    {
        private const double MinZoom = 0.02;
        private const double MaxZoom = 64.0;
        private const double ZoomStep = 1.2;

        private Settings _settings;

        private BitmapSource? _image;
        private string? _path;
        private List<string> _files = new();
        private int _index = -1;

        private double _zoom = 1.0;
        // ---- 재생 ----
        private AnimatedImage? _animation;      // 움직이는 GIF
        private int _animIndex;
        private readonly DispatcherTimer _animTimer = new();
        private readonly DispatcherTimer _videoTimer = new();
        private bool _playing;
        private bool _seeking;
        private bool _isVideo;
        private double _volumeBeforeMute = 0.7;

        /// <summary>
        /// 동영상 재생기. MediaElement 를 쓰지 않는 이유는 하나다 —
        /// MediaElement 의 그림은 <see cref="RenderTargetBitmap"/> 에 안 잡혀서
        /// "이 장면 저장" 을 만들 수가 없다. MediaPlayer 는 DrawingContext.DrawVideo 로
        /// 직접 그릴 수 있어서 화면에 칠하는 것과 같은 그림을 그대로 떠낼 수 있다.
        /// </summary>
        private readonly MediaPlayer _player = new();

        // 방금 캡처해서 저장한 파일이면 디스크에서 되읽지 않도록 넘겨받은 이미지 (일회용).
        private BitmapSource? _preloaded;
        private string? _preloadedPath;

        /// <summary>지금 영상의 장 수·초당 장수. 파일에서 직접 읽는다(재생기는 안 알려 준다).</summary>
        private VideoInfo? _videoInfo;

        /// <summary>막대 값을 코드가 고쳐 넣는 중. 그동안의 값 변화는 사용자가 민 게 아니다.</summary>
        private bool _syncingSlider;

        /// <summary>
        /// 아직 재생기에 안 넘긴 목적지. 방향키를 꾹 누르거나 막대를 끌면 위치 요청이
        /// 초당 수십 번 들어오는데, 그걸 하나하나 다 넘기면 디코더가 그 요청을 차례로
        /// 전부 처리하느라 손을 뗀 뒤에도 한참 멈춰 있는다. 마지막 목적지 하나만 넘긴다.
        /// </summary>
        private TimeSpan? _pendingSeek;
        private readonly DispatcherTimer _seekTimer = new();

        /// <summary>알림이 머무는 시간. 지나면 스르르 사라진다.</summary>
        private readonly DispatcherTimer _toastTimer = new();
        private readonly System.Diagnostics.Stopwatch _sinceSeek = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>이 시간 안에 다시 들어온 위치 요청은 묶어서 한 번만 옮긴다.</summary>
        private const int SeekCoalesceMs = 90;

        private static readonly double[] PlaybackSpeeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 };

        private int _rotation;              // 0 / 90 / 180 / 270
        private bool _flipH, _flipV;
        private double _originX, _originY;  // Stage 안에서 표시 내용의 좌상단(DIP)
        private FitMode? _activeFit;        // null 이면 사용자가 직접 맞춘 상태

        private bool _panning;
        private Point _panLast;

        /// <summary>영상 위에서 왼쪽 버튼을 눌렀다. 실제로 움직이면 창 끌기로 넘어간다.</summary>
        private bool _videoDragArmed;
        private Point _videoDragFrom;

        private bool _fullScreen;
        private WindowState _prevState;
        private WindowStyle _prevStyle;
        private ResizeMode _prevResize;

        private readonly ObservableCollection<ThumbItem> _thumbs = new();
        private bool _syncingStrip;

        /// <summary>"편집" 을 눌렀을 때. 컨트롤러가 편집기를 띄운다.</summary>
        internal event Action<BitmapSource, string>? EditRequested;

        /// <summary>"글자" 를 눌렀을 때. 컨트롤러가 읽어서 창을 띄운다.</summary>

        /// <summary>
        /// 이 창이 무엇을 보는 창인가. <b>창을 만들 때 정해지고 바뀌지 않는다.</b>
        ///
        /// 그림 창과 재생 창을 갈라 놓는 이유는, 하나로 쓰면 영상을 열 때 보고 있던 그림이
        /// 사라지고 그림을 열 때 보던 영상이 멎기 때문이다. 둘은 따로 살아 있어야 한다.
        /// </summary>
        internal ViewerRole Role { get; private set; } = ViewerRole.Image;

        /// <summary>
        /// 이 창이 맡을 종류가 아닌 파일이 들어왔다. 컨트롤러가 맞는 창으로 보낸다.
        /// 받아 줄 데가 없으면(뷰어 전용으로 떴을 때) 그냥 여기서 연다.
        /// </summary>
        internal event Action<string>? OpenElsewhere;

        internal ViewerWindow(Settings settings)
        {
            _settings = settings;
            InitializeComponent();

            Strip.ItemsSource = _thumbs;

            // 움직이는 GIF 는 프레임마다 간격이 달라서, 한 장 그릴 때마다 다음 간격을 다시 잡는다.
            _animTimer.Tick += (_, _) => AdvanceAnimation();

            // 동영상은 위치를 물어봐야 알 수 있어서 짧은 간격으로 훑는다.
            // 장 번호까지 보여 주므로 예전(200ms)보다 촘촘히 본다.
            _videoTimer.Interval = TimeSpan.FromMilliseconds(60);
            _videoTimer.Tick += (_, _) => SyncVideoPosition();

            foreach (double rate in PlaybackSpeeds)
                SpeedBox.Items.Add(rate.ToString("0.##", CultureInfo.InvariantCulture) + "×");
            SpeedBox.SelectedIndex = Array.IndexOf(PlaybackSpeeds, 1.0);

            foreach (double rate in ExportSpeeds)
                EditSpeedBox.Items.Add(rate.ToString("0.##", CultureInfo.InvariantCulture) + "×");
            EditSpeedBox.SelectedIndex = Array.IndexOf(ExportSpeeds, 1.0);

            _seekTimer.Interval = TimeSpan.FromMilliseconds(SeekCoalesceMs);
            _seekTimer.Tick += (_, _) => ApplyPendingSeek();

            _toastTimer.Interval = TimeSpan.FromMilliseconds(1800);
            _toastTimer.Tick += (_, _) => FadeToast();

            // 멈춰 세운 자리의 그림이 그대로 보여야 한다(장 단위로 옮길 때 필수).
            _player.ScrubbingEnabled = true;
            _player.MediaOpened += OnVideoOpened;
            _player.MediaEnded += OnVideoEnded;
            _player.MediaFailed += OnVideoFailed;
            _player.Volume = VolumeSlider.Value;

            Stage.MouseLeftButtonDown += OnStageMouseDown;
            Stage.MouseLeftButtonUp += OnStageMouseUp;
            Stage.MouseMove += OnStageMouseMove;
            Stage.MouseWheel += OnStageWheel;
            Stage.MouseDown += OnStageMouseDownAny;

            SizeChanged += (_, _) => Relayout();
            Drop += OnDrop;
            DragOver += OnDragOver;

            ApplySettings(settings);
            UpdateChrome();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        /// <summary>
        /// MediaPlayer 와 DispatcherTimer 는 창이 아니라 스레드에 매여 있어서,
        /// 창을 닫아도 스스로 멈추지 않는다 — 재생 중에 닫으면 소리만 계속 났다.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            StopPlayback();
            _player.Close();
            _toastTimer.Stop();
            base.OnClosed(e);
        }

        internal void ApplySettings(Settings settings)
        {
            _settings = settings;
            Stage.Background = _settings.ShowCheckerboard
                ? (Brush)FindResource("Checkerboard")
                : (Brush)FindResource("Bg");

            SetStripVisible(_settings.ShowThumbnailStrip, save: false);
            ApplyPlayerSettings();
            ApplyTransform();
        }

        internal void BringToFront()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            // 이 창은 한 번 만들면 계속 재사용한다. 그사이 모니터가 꺼졌다 켜지면
            // 사라진 화면 좌표에 남아 버려서, Activate() 를 불러도 아무것도 안 보인다.
            // 앞으로 가져오기 전에 화면 안으로 끌어다 놓는다.
            if (Native.WindowPlacement.EnsureOnScreen(this))
                Core.Log.Write("뷰어가 화면 밖에 있어 되돌림");

            Activate();
            Focus();
        }

        // ===================================================== 불러오기

        /// <summary>파일을 열고 같은 폴더의 이미지들로 앞뒤 탐색 목록을 만든다.</summary>
        /// <summary>재생 중인 것을 모두 멈추고 원래(그림) 상태로 되돌린다.</summary>
        private void StopPlayback()
        {
            _animTimer.Stop();
            _videoTimer.Stop();
            _seekTimer.Stop();
            _pendingSeek = null;
            _animation = null;
            _animIndex = 0;
            _playing = false;

            if (_isVideo)
            {
                _player.Stop();
                _player.Close();
                Video.Fill = null;
                Video.Visibility = Visibility.Collapsed;
                Surface.Visibility = Visibility.Visible;
                _isVideo = false;
            }

            _videoInfo = null;
            StPlayFrame.Text = "";
            PlayBar.Visibility = Visibility.Collapsed;

            // 편집 구간은 파일에 딸린 것이다. 다른 파일로 넘어가면 같이 버린다.
            _editIn = _editOut = null;
            EditStatus.Text = "";
            TbEdit.IsChecked = false;
            EditRow.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 움직이는 GIF 인지 보고, 맞으면 프레임을 돌린다.
        /// 한 장짜리 GIF 는 그냥 그림이므로 손대지 않는다.
        /// </summary>
        private bool TryStartAnimation(string path)
        {
            AnimatedImage? clip = AnimatedImage.TryLoad(path);
            if (clip == null) return false;

            _animation = clip;
            _animIndex = 0;

            PlaySlider.Minimum = 0;
            PlaySlider.Maximum = clip.Count - 1;
            PlaySlider.Value = 0;
            PlayBar.Visibility = Visibility.Visible;
            StPlayNote.Text = $"{clip.Count}장 · {clip.Duration.TotalSeconds:0.0}초";

            ShowAnimationFrame(0);
            SetPlaying(true);
            return true;
        }

        private void ShowAnimationFrame(int index)
        {
            if (_animation == null || _animation.Count == 0) return;

            _animIndex = ((index % _animation.Count) + _animation.Count) % _animation.Count;

            BitmapSource frame = _animation.Frames[_animIndex];
            _image = frame;
            Canvas1.Source = frame;

            if (!_seeking) PlaySlider.Value = _animIndex;
            StPlayTime.Text = $"{_animIndex + 1} / {_animation.Count}";

            _animTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(10, _animation.DelaysMs[_animIndex] / CurrentSpeed));
        }

        private void AdvanceAnimation()
        {
            if (_animation == null) return;

            bool wrapping = _animIndex + 1 >= _animation.Count;
            if (wrapping && TbLoop.IsChecked != true)
            {
                // 반복이 꺼져 있으면 마지막 장에서 멈춘다.
                SetPlaying(false);
                return;
            }

            ShowAnimationFrame(_animIndex + 1);
        }

        /// <summary>동영상 파일이면 재생 층으로 넘긴다.</summary>
        private bool TryStartVideo(string path, bool force = false)
        {
            if (!force && !ImageIO.IsVideo(path)) return false;

            _isVideo = true;
            Surface.Visibility = Visibility.Collapsed;
            Video.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;

            PlayBar.Visibility = Visibility.Visible;
            StPlayNote.Text = "여는 중...";
            StPlayTime.Text = "0:00";
            StPlayFrame.Text = "";

            // 장 수·초당 장수는 재생기가 안 알려 준다. 파일을 직접 읽어 둔다.
            // 못 읽으면(형식이 다르면) 시간만 보여 주고 장 단위 이동은 잠근다.
            _videoInfo = VideoInfo.TryRead(path);

            _player.Open(new Uri(path));
            SetPlaying(true);
            return true;
        }

        private void SetPlaying(bool playing)
        {
            _playing = playing;
            PlayIcon.Data = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
            BtnPlayPause.ToolTip = (playing ? "멈춤" : "재생") + KeyHint(_keys.PlayPause);

            if (_isVideo)
            {
                if (playing) { _player.Play(); _videoTimer.Start(); }
                else { _player.Pause(); _videoTimer.Stop(); }
                return;
            }

            if (_animation == null) return;
            if (playing) _animTimer.Start(); else _animTimer.Stop();
        }

        private void OnPlayPause(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

        /// <summary>재생 위치를 막대에 옮겨 적는다. 사용자가 끌고 있는 동안은 건드리지 않는다.</summary>
        private void SyncVideoPosition()
        {
            if (!_isVideo) return;

            // 사용자가 짚는 중이면 그쪽이 이긴다. 재생기가 아직 옛 자리에 있는데
            // 그 값을 막대에 적으면, 끌어 놓은 손잡이가 뒤로 튕겼다가 돌아온다.
            if (_seeking || _pendingSeek != null) return;

            TimeSpan pos = _player.Position;
            ShowPosition(pos);
            SetSliderValue(pos.TotalSeconds);
        }

        /// <summary>시간과 장 번호를 적는다. 막대는 건드리지 않는다.</summary>
        private void ShowPosition(TimeSpan pos)
        {
            TimeSpan total = _player.NaturalDuration.HasTimeSpan
                ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;

            StPlayTime.Text = total > TimeSpan.Zero
                ? Clock(pos) + " / " + Clock(total)
                : Clock(pos);

            StPlayFrame.Text = _videoInfo != null
                ? (_videoInfo.FrameAt(pos) + 1).ToString(CultureInfo.InvariantCulture) + " / " +
                  _videoInfo.FrameCount.ToString(CultureInfo.InvariantCulture) + "장"
                : "";
        }

        /// <summary>
        /// 여기로 옮겨 달라고 <b>요청</b>한다. 곧바로 옮기지 않을 수 있다.
        ///
        /// 방향키를 꾹 누르면 초당 수십 번 들어온다. 그때마다 재생기에 넘기면 디코더가
        /// 그 요청을 하나도 안 버리고 차례로 다 처리하느라, 손을 뗀 뒤에도 한참 멈춰 있는다.
        /// 그래서 <b>마지막 목적지 하나만</b> 실제로 넘긴다.
        ///
        /// 대신 눈금·시간·장 번호는 누르는 즉시 고친다 — 기다리는 느낌이 나면 안 된다.
        /// 첫 요청은 미루지 않는다(한 번만 눌렀는데 굳이 늦출 이유가 없다).
        /// </summary>
        private void RequestSeek(TimeSpan position)
        {
            if (!_isVideo) return;

            double seconds = Math.Clamp(position.TotalSeconds, PlaySlider.Minimum, PlaySlider.Maximum);
            _pendingSeek = TimeSpan.FromSeconds(seconds);

            SetSliderValue(seconds);
            ShowPosition(_pendingSeek.Value);

            if (_sinceSeek.ElapsedMilliseconds >= SeekCoalesceMs) { ApplyPendingSeek(); return; }

            _seekTimer.Stop();
            _seekTimer.Start();
        }

        private void ApplyPendingSeek()
        {
            _seekTimer.Stop();
            if (_pendingSeek == null) return;

            _player.Position = _pendingSeek.Value;
            _pendingSeek = null;
            _sinceSeek.Restart();
        }

        /// <summary>지금 짚고 있는 자리. 아직 안 넘긴 요청이 있으면 그쪽이 진짜 자리다.</summary>
        private TimeSpan CurrentPosition => _pendingSeek ?? _player.Position;

        /// <summary>
        /// 막대 값을 코드가 고쳐 넣는다. 사용자가 민 것과 구분해야 한다 —
        /// 안 그러면 재생 중에 스스로 옮긴 값을 "사용자가 옮겼다" 고 보고 되감기를 반복한다.
        /// </summary>
        private void SetSliderValue(double value)
        {
            _syncingSlider = true;
            try { PlaySlider.Value = Math.Clamp(value, PlaySlider.Minimum, PlaySlider.Maximum); }
            finally { _syncingSlider = false; }
        }

        // ---- 조작 ----

        /// <summary>영상은 시간으로, 움직이는 그림은 장 수로 건너뛴다.</summary>
        private void Skip(double seconds)
        {
            if (_isVideo)
            {
                // 아직 안 넘긴 요청이 있으면 그 자리에서 이어서 센다.
                // 재생기의 위치를 보면 연타할 때마다 같은 자리에서 다시 시작한다.
                RequestSeek(CurrentPosition + TimeSpan.FromSeconds(seconds));
                return;
            }

            if (_animation == null) return;
            ShowAnimationFrame(_animIndex + (seconds > 0 ? 1 : -1));
        }

        /// <summary>
        /// 한 장씩 옮긴다. 장 수를 아는 영상에서만 뜻이 있다.
        /// 옮기고 나면 멈춘다 — 한 장을 짚어 보려는 것이지 계속 보려는 게 아니다.
        /// </summary>
        private void StepFrame(int delta)
        {
            // 옮기고 나면 멈춘다 — 한 장을 짚어 보려는 것이지 계속 보려는 게 아니다.
            if (_animation != null) { SetPlaying(false); ShowAnimationFrame(_animIndex + delta); return; }
            if (!_isVideo || _videoInfo == null) return;

            SetPlaying(false);
            RequestSeek(_videoInfo.TimeOfFrame(_videoInfo.FrameAt(CurrentPosition) + delta));
        }

        private double SkipSeconds => Math.Clamp(_settings.PlayerSkipSeconds, 0.1, 600);

        private void OnBack(object sender, RoutedEventArgs e) => Skip(-SkipSeconds);
        private void OnForward(object sender, RoutedEventArgs e) => Skip(SkipSeconds);
        private void OnPrevFrame(object sender, RoutedEventArgs e) => StepFrame(-1);
        private void OnNextFrame(object sender, RoutedEventArgs e) => StepFrame(+1);
        private void OnToggleLoop(object sender, RoutedEventArgs e)
        {
            _settings.PlayerLoop = TbLoop.IsChecked == true;
            _settings.Save();
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML 을 읽는 도중에도 값이 설정되면서 이 핸들러가 불린다.
            // 그때는 아직 다른 요소들이 안 만들어져 있다 — 여기서 안 막으면 창이 통째로 못 뜬다.
            if (BtnMute == null) return;

            _player.Volume = e.NewValue;
            if (e.NewValue > 0) _volumeBeforeMute = e.NewValue;
            UpdateMuteButton();
        }

        private void OnToggleMute(object sender, RoutedEventArgs e) => ToggleMute();

        private void ToggleMute()
        {
            if (VolumeSlider.Value > 0)
            {
                _volumeBeforeMute = VolumeSlider.Value;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.7;
            }
        }

        private void UpdateMuteButton()
        {
            if (BtnMute == null || MuteIcon == null) return;

            bool silent = VolumeSlider.Value <= 0;
            MuteIcon.Data = (Geometry)FindResource(silent ? "IconMute" : "IconVolume");
            BtnMute.ToolTip = (silent ? "소리 켜기" : "소리 끄기") + KeyHint(_keys.Mute);
        }

        private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedBox == null) return;

            int i = SpeedBox.SelectedIndex;
            if (i < 0 || i >= PlaybackSpeeds.Length) return;

            double rate = PlaybackSpeeds[i];
            _player.SpeedRatio = rate;

            // 움직이는 그림은 재생기가 없으니 타이머 간격을 직접 줄이고 늘린다.
            if (_animation != null && _animIndex < _animation.Count)
            {
                _animTimer.Interval = TimeSpan.FromMilliseconds(
                    Math.Max(10, _animation.DelaysMs[_animIndex] / rate));
            }
        }

        /// <summary>그림에만 뜻이 있는 단추들. 동영상일 때는 흐려진다.</summary>
        private IEnumerable<FrameworkElement> ImageOnlyButtons() => new FrameworkElement[]
        {
            BtnRotateLeft, BtnRotateRight, BtnFlipH, BtnFlipV,
            BtnEdit, BtnCopy, BtnSave, BtnSaveAs
        };

        /// <summary>지금 재생할 수 있는 것을 보고 있는가(영상 또는 움직이는 그림).</summary>
        private bool IsPlayable => _isVideo || _animation != null;

        private void NudgeVolume(double delta)
            => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 1);

        private double CurrentSpeed
        {
            get
            {
                int i = SpeedBox?.SelectedIndex ?? -1;
                return i >= 0 && i < PlaybackSpeeds.Length ? PlaybackSpeeds[i] : 1.0;
            }
        }

        private void OnVideoOpened(object? sender, EventArgs e)
        {
            double seconds = _player.NaturalDuration.HasTimeSpan
                ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0;

            // 범위를 줄이면 WPF 가 Value 를 새 Maximum 으로 눌러 내리면서 ValueChanged 를 낸다.
            // 이전 영상의 위치가 막대에 남아 있으면(긴 영상을 보다가 짧은 걸 열면) 그 눌린 값이
            // "사용자가 민" 이동으로 처리돼 새 영상이 곧장 끝으로 되감긴다. 코드가 넣는 값이니 막아 둔다.
            _syncingSlider = true;
            try
            {
                PlaySlider.Minimum = 0;
                PlaySlider.Maximum = Math.Max(0.1, seconds);
            }
            finally { _syncingSlider = false; }

            int w = _player.NaturalVideoWidth, h = _player.NaturalVideoHeight;

            // 이제야 크기를 안다. 이 크기로 칠해야 원본 비율이 안 뭉개진다.
            var drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, w, h) };
            var brush = new DrawingBrush(drawing) { Stretch = Stretch.Uniform };
            Video.Fill = brush;

            StPlayNote.Text = _videoInfo != null && _videoInfo.Fps > 0
                ? $"{w}×{h} · {_videoInfo.Fps:0.#}fps"
                : $"{w}×{h}";

            // 장 수를 못 읽었으면 장 단위 이동은 잠근다. 눌러도 아무 일 없는 단추는 없느니만 못하다.
            bool byFrame = _videoInfo != null;
            BtnPrevFrame.IsEnabled = BtnNextFrame.IsEnabled = byFrame;

            SyncVideoPosition();
            UpdateChrome();
        }

        private void OnVideoEnded(object? sender, EventArgs e)
        {
            // 반복이 켜져 있으면 그대로 다시 돌린다. 반복이 먼저다 —
            // "이것만 계속" 을 골라 둔 사람을 다음 파일로 끌고 가면 안 된다.
            if (TbLoop.IsChecked == true)
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
                return;
            }

            // 폴더를 이어서 보는 중이면 다음 영상으로 넘어간다.
            if (_settings.PlayNextInFolder && PlayNextVideo()) return;

            // 아니면 처음으로 되감아 두고 멈춘다. 다시 누르면 바로 재생된다.
            _player.Position = TimeSpan.Zero;
            SetPlaying(false);
        }

        /// <summary>
        /// 같은 폴더의 <b>다음 영상</b>으로 넘어간다. 넘어갔으면 true.
        ///
        /// 그림은 건너뛴다 — 영상을 이어 보는 중에 사진 한 장이 끼어들어 재생이 멎으면
        /// 그건 이어 보기가 아니다. 마지막 영상이면 "돌아가기" 설정을 따른다.
        /// </summary>
        private bool PlayNextVideo()
        {
            if (_files.Count == 0 || _index < 0) return false;

            for (int step = 1; step <= _files.Count; step++)
            {
                int next = _index + step;

                if (next >= _files.Count)
                {
                    if (!_settings.WrapAround) return false;
                    next %= _files.Count;
                }

                if (next == _index) return false;
                if (!ImageIO.IsVideo(_files[next])) continue;

                LoadIndex(next);
                return true;
            }
            return false;
        }

        private void OnVideoFailed(object? sender, ExceptionEventArgs e)
        {
            StPlayNote.Text = "재생하지 못했습니다";
            Core.Log.Write("동영상 재생 실패: " + _path + " — " + e.ErrorException?.Message);

            MessageBox.Show(this,
                "이 동영상을 재생하지 못했습니다.\n\n" +
                "윈도우에 그 형식의 코덱이 없을 수 있습니다.\n" +
                "(윈도우 N 버전은 미디어 기능 팩이 따로 필요합니다)",
                "SnapView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSeekStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
            => _seeking = true;

        private void OnSeekEnd(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _seeking = false;
            ApplySeek();

            // 손을 뗐으면 더 기다릴 이유가 없다. 미뤄 둔 것을 지금 옮긴다.
            ApplyPendingSeek();
        }

        /// <summary>
        /// 막대 값이 바뀌었다. 코드가 넣은 값이 아니면 <b>전부 사용자가 민 것</b>으로 본다.
        ///
        /// 예전에는 손잡이를 잡고 끄는 동안(DragStarted~DragCompleted)만 반응했다.
        /// 그래서 막대 아무 데나 눌러도 잠깐 움직였다가 곧 제자리로 돌아왔다 —
        /// 손잡이를 정확히 집어야만 옮길 수 있는 것처럼 보인 이유다.
        /// </summary>
        private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingSlider) return;
            ApplySeek();
        }

        private void ApplySeek()
        {
            if (_isVideo) RequestSeek(TimeSpan.FromSeconds(PlaySlider.Value));
            else if (_animation != null) ShowAnimationFrame((int)Math.Round(PlaySlider.Value));
        }

        // ===================================================== 재생 키 · 장면 저장

        /// <summary>
        /// 설정에서 고른 재생 키를 미리 풀어 둔 것.
        /// 키를 누를 때마다 문자열을 다시 해석하면 낭비이기도 하고,
        /// 잘못 쓴 값이 있어도 그때그때 조용히 무시되어 눈치채기 어렵다.
        /// </summary>
        private sealed class PlayerKeys
        {
            internal HotKeySpec? PlayPause, Back, Forward, PrevFrame, NextFrame;
            internal HotKeySpec? VolumeUp, VolumeDown, Mute, Loop, GrabFrame;

            internal static PlayerKeys From(Settings s) => new()
            {
                PlayPause = HotKeySpec.Parse(s.PlayerKeyPlayPause),
                Back = HotKeySpec.Parse(s.PlayerKeyBack),
                Forward = HotKeySpec.Parse(s.PlayerKeyForward),
                PrevFrame = HotKeySpec.Parse(s.PlayerKeyPrevFrame),
                NextFrame = HotKeySpec.Parse(s.PlayerKeyNextFrame),
                VolumeUp = HotKeySpec.Parse(s.PlayerKeyVolumeUp),
                VolumeDown = HotKeySpec.Parse(s.PlayerKeyVolumeDown),
                Mute = HotKeySpec.Parse(s.PlayerKeyMute),
                Loop = HotKeySpec.Parse(s.PlayerKeyLoop),
                GrabFrame = HotKeySpec.Parse(s.PlayerKeyGrabFrame)
            };
        }

        private PlayerKeys _keys = PlayerKeys.From(new Settings());

        private static string KeyHint(HotKeySpec? spec)
            => spec == null ? "" : " (" + spec.Display + ")";

        /// <summary>설정에서 온 재생 관련 값들을 화면에 반영한다.</summary>
        private void ApplyPlayerSettings()
        {
            _keys = PlayerKeys.From(_settings);

            TbLoop.IsChecked = _settings.PlayerLoop;
            TbLoop.ToolTip = "끝나면 처음부터 다시" + KeyHint(_keys.Loop);

            string skip = SkipSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "초";
            BtnBack.ToolTip = skip + " 뒤로" + KeyHint(_keys.Back);
            BtnForward.ToolTip = skip + " 앞으로" + KeyHint(_keys.Forward);
            BtnPrevFrame.ToolTip = "한 장 뒤로" + KeyHint(_keys.PrevFrame);
            BtnNextFrame.ToolTip = "한 장 앞으로" + KeyHint(_keys.NextFrame);
            BtnGrabFrame.ToolTip = "지금 보이는 한 장면을 그림으로 저장" + KeyHint(_keys.GrabFrame);
            BtnPlayPause.ToolTip = (_playing ? "멈춤" : "재생") + KeyHint(_keys.PlayPause);
            UpdateMuteButton();
        }

        /// <summary>
        /// 재생 중에만 듣는 키. 처리했으면 true.
        ///
        /// 설정에서 바꿀 수 있게 문자열로 받아 두고 여기서 맞춰 본다.
        /// 순서가 중요하다 — 한 장 이동(Ctrl+←)이 건너뛰기(←)보다 먼저 걸려야 한다.
        /// </summary>
        private bool HandlePlayerKey(Key key, ModifierKeys mods)
        {
            if (!IsPlayable) return false;

            if (_keys.PrevFrame?.Matches(key, mods) == true) { StepFrame(-1); return true; }
            if (_keys.NextFrame?.Matches(key, mods) == true) { StepFrame(+1); return true; }
            if (_keys.PlayPause?.Matches(key, mods) == true) { SetPlaying(!_playing); return true; }
            if (_keys.Back?.Matches(key, mods) == true) { Skip(-SkipSeconds); return true; }
            if (_keys.Forward?.Matches(key, mods) == true) { Skip(SkipSeconds); return true; }
            if (_keys.VolumeUp?.Matches(key, mods) == true) { NudgeVolume(+0.05); return true; }
            if (_keys.VolumeDown?.Matches(key, mods) == true) { NudgeVolume(-0.05); return true; }
            if (_keys.Mute?.Matches(key, mods) == true) { ToggleMute(); return true; }

            if (_keys.Loop?.Matches(key, mods) == true)
            {
                TbLoop.IsChecked = TbLoop.IsChecked != true;
                OnToggleLoop(this, new RoutedEventArgs());
                return true;
            }

            if (_keys.GrabFrame?.Matches(key, mods) == true) { GrabFrame(); return true; }

            return false;
        }

        private void OnGrabFrame(object sender, RoutedEventArgs e) => GrabFrame();

        // ===================================================== 컷 · 배속 편집

        private static readonly double[] ExportSpeeds = { 0.25, 0.5, 1, 2, 4 };

        private TimeSpan? _editIn, _editOut;
        private System.Threading.CancellationTokenSource? _exportCts;

        private TimeSpan VideoDuration =>
            _videoInfo != null && _videoInfo.Seconds > 0
                ? TimeSpan.FromSeconds(_videoInfo.Seconds)
                : _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan
                                                      : TimeSpan.Zero;

        /// <summary>개발용(--shot): 편집 줄을 펼친 상태로 그려 배치를 확인한다.</summary>
        internal void ShowEditRowForShot()
        {
            TbEdit.IsChecked = true;
            EditRow.Visibility = Visibility.Visible;
            _editIn = TimeSpan.FromSeconds(0.4);
            _editOut = TimeSpan.FromSeconds(1.6);
            UpdateEditRange();
        }

        private void OnToggleEdit(object sender, RoutedEventArgs e)
        {
            if (!_isVideo)
            {
                TbEdit.IsChecked = false;
                ShowToast("동영상을 열었을 때 쓸 수 있습니다");
                return;
            }
            EditRow.Visibility = TbEdit.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateEditRange();
        }

        private void OnMarkIn(object sender, RoutedEventArgs e)
        {
            _editIn = CurrentPosition;
            if (_editOut.HasValue && _editOut <= _editIn) _editOut = null;
            UpdateEditRange();
        }

        private void OnMarkOut(object sender, RoutedEventArgs e)
        {
            _editOut = CurrentPosition;
            if (_editIn.HasValue && _editOut <= _editIn) _editIn = null;
            UpdateEditRange();
        }

        private void UpdateEditRange()
        {
            static string F(TimeSpan t) =>
                t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);

            if (_editIn == null && _editOut == null) { EditRangeText.Text = "구간: 전체"; return; }

            TimeSpan a = _editIn ?? TimeSpan.Zero;
            TimeSpan b = _editOut ?? VideoDuration;
            EditRangeText.Text = $"구간: {F(a)} ~ {F(b)}  ({(b - a).TotalSeconds:0.0}초)";
        }

        /// <summary>
        /// 정한 구간·배속으로 <b>새 MP4</b> 를 저장 폴더에 만든다. 원본은 건드리지 않는다.
        /// 내보내는 중에 다시 누르면 취소.
        /// </summary>
        private void OnExportClip(object sender, RoutedEventArgs e)
        {
            if (_exportCts != null) { _exportCts.Cancel(); return; }
            if (!_isVideo || _path == null)
            {
                ShowToast("동영상을 열었을 때만 내보낼 수 있습니다");
                return;
            }

            TimeSpan dur = VideoDuration;
            if (dur <= TimeSpan.Zero) { ShowToast("영상 길이를 알 수 없습니다"); return; }

            TimeSpan from = _editIn ?? TimeSpan.Zero;
            TimeSpan to = _editOut ?? dur;
            if (to <= from) { ShowToast("구간 끝이 시작보다 앞입니다"); return; }

            double speed = ExportSpeeds[Math.Clamp(EditSpeedBox.SelectedIndex, 0, ExportSpeeds.Length - 1)];

            string folder = string.IsNullOrWhiteSpace(_settings.SaveFolder)
                ? Settings.DefaultSaveFolder : _settings.SaveFolder;
            string name = ImageIO.BuildName("스냅뷰_{1}_컷_{0:yyyy-MM-dd_HHmmss}", DateTime.Now,
                                            Path.GetFileNameWithoutExtension(_path));
            Directory.CreateDirectory(folder);
            string dst = Path.Combine(folder, name + ".mp4");
            for (int i = 2; File.Exists(dst); i++)
                dst = Path.Combine(folder, name + " (" + i + ").mp4");

            string src = _path;
            _exportCts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = _exportCts.Token;
            ExportLabel.Text = "취소";
            EditStatus.Text = "내보내는 중...";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    int lastPct = -1;
                    Capture.VideoEditor.Export(src, dst, from, to, speed, p =>
                    {
                        int pct = (int)(p * 100);
                        if (pct == lastPct) return;   // 매 장마다 UI 로 넘어가지 않게
                        lastPct = pct;
                        Dispatcher.BeginInvoke(new Action(() => EditStatus.Text = $"내보내는 중 {pct}%"));
                    }, token);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        EditStatus.Text = "";
                        ShowToast("내보냄  ·  " + Path.GetFileName(dst));
                    }));
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                    Dispatcher.BeginInvoke(new Action(() =>
                    { EditStatus.Text = ""; ShowToast("내보내기를 취소했습니다"); }));
                }
                catch (Exception ex)
                {
                    Log.Write("영상 내보내기 실패: " + ex.Message);
                    Dispatcher.BeginInvoke(new Action(() =>
                    { EditStatus.Text = ""; ShowToast("내보내기 실패: " + ex.Message); }));
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _exportCts?.Dispose();
                        _exportCts = null;
                        ExportLabel.Text = "MP4로 내보내기";
                    }));
                }
            });
        }

        /// <summary>
        /// 무대 위에 잠깐 띄웠다 지우는 알림.
        ///
        /// 재생 막대에 글자를 밀어 넣으면 안 된다 — 이름이 길면 가운데 위치 막대가
        /// 밀려나 사라진다(실제로 그렇게 사라졌다). 배치에 끼지 않는 층에 띄운다.
        /// </summary>
        private void ShowToast(string message)
        {
            ToastText.Text = message;
            Toast.Visibility = Visibility.Visible;

            _toastTimer.Stop();
            Toast.BeginAnimation(OpacityProperty, null);
            Toast.Opacity = 1;
            _toastTimer.Start();
        }

        private void FadeToast()
        {
            _toastTimer.Stop();

            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                0, TimeSpan.FromMilliseconds(400));
            fade.Completed += (_, _) =>
            {
                // 사라지는 사이에 새 알림이 뜰 수 있다. 그때는 그대로 둔다.
                if (Toast.Opacity <= 0.01) Toast.Visibility = Visibility.Collapsed;
            };
            Toast.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>
        /// 지금 보이는 한 장면을 그림으로 떠서 <b>바로 저장</b>한다.
        ///
        /// 화면에 칠하는 것과 같은 방법으로 한 번 더 그린다 — DrawVideo 로 그린 것은
        /// RenderTargetBitmap 에 잡힌다(MediaElement 는 안 잡혀서 이 기능을 못 만들었다).
        /// 움직이는 GIF 는 이미 그림이므로 지금 장을 그대로 쓴다.
        ///
        /// 재생을 멈추지 않는다. 누른 <b>그 순간</b>의 장면을 뜨는 것이지,
        /// 세워 놓고 고르라는 게 아니다. 세워서 고르고 싶으면 한 장씩 옮기는 키가 있다.
        ///
        /// 저장 위치·형식·이름 규칙은 캡처와 똑같이 설정을 따른다. 캡처는 저장되는데
        /// 장면은 어디로 갔는지 모르겠다면 그게 더 이상하다.
        /// </summary>
        private void GrabFrame()
        {
            // 어느 장면인지 먼저 붙잡는다. 저장하는 사이에도 영상은 계속 흐른다.
            TimeSpan at = _player.Position;

            BitmapSource? shot = _isVideo ? CurrentVideoFrame() : _animation?.Frames[_animIndex];
            if (shot == null)
            {
                StPlayNote.Text = "이 장면을 뜨지 못했습니다";
                return;
            }

            string where;
            try
            {
                // 이름에 원본 영상 이름이 들어간다 — 어느 영상의 장면인지 알아야 쓸모가 있다.
                string? source = _path != null ? Path.GetFileNameWithoutExtension(_path) : null;
                where = ImageIO.SaveFrame(shot, _settings, DateTime.Now, source);
            }
            catch (Exception ex)
            {
                Core.Log.Write("장면 저장 실패: " + ex.Message);
                MessageBox.Show(this, "장면을 저장하지 못했습니다.\n\n" + ex.Message,
                                "SnapView", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_settings.PlayShutterSound) CaptureSound.Play(_settings.ShutterVolume);

            // 잠깐 떴다 사라지는 알림으로 알린다. 재생 막대에 글자를 밀어 넣으면
            // 이름이 길 때 가운데 위치 막대가 밀려나 사라진다 — 실제로 그렇게 사라졌다.
            string frame = _videoInfo != null
                ? (_videoInfo.FrameAt(at) + 1).ToString(CultureInfo.InvariantCulture) + "장"
                : Clock(at);
            ShowToast(frame + " 저장  ·  " + Path.GetFileName(where));
            _lastGrab = where;

            // 편집기를 켜 두는 설정이면 그쪽으로도 보낸다.
            if (_settings.OpenEditorAfterCapture)
                EditRequested?.Invoke(shot, Path.GetFileNameWithoutExtension(where));
        }

        /// <summary>방금 저장한 장면. 재생 막대의 알림을 눌러 찾아갈 때 쓴다.</summary>
        private string? _lastGrab;

        private BitmapSource? CurrentVideoFrame()
        {
            int w = _player.NaturalVideoWidth, h = _player.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return null;

            try
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                    dc.DrawVideo(_player, new Rect(0, 0, w, h));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                Core.Log.Write("장면 뜨기 실패: " + ex.Message);
                return null;
            }
        }

        private static string Clock(TimeSpan t)
            => ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" +
               t.Seconds.ToString("00", CultureInfo.InvariantCulture);

        internal void LoadFile(string path) => LoadFile(path, null);

        /// <param name="preloaded">
        /// 이미 메모리에 있는 그 파일의 이미지(방금 캡처해서 저장한 것). 넘겨 주면 같은
        /// 파일을 디스크에서 되읽어 다시 디코딩하는 일을 건너뛴다 — 저사양에서 캡처 직후
        /// 뷰어가 뜨는 시간이 그만큼 준다. PNG 처럼 무손실로 저장된 것만 넘길 것.
        /// </param>
        internal void LoadFile(string path, BitmapSource? preloaded)
        {
            // 종류가 다르면 내 일이 아니다. 맞는 창으로 넘긴다.
            if (WantsOtherWindow(path)) return;

            _preloaded = preloaded;
            _preloadedPath = preloaded != null ? path : null;

            _files = ImageIO.Siblings(path, Role == ViewerRole.Video);
            _index = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            if (_index < 0)
            {
                _files.Insert(0, path);
                _index = 0;
            }
            RebuildStrip();
            LoadIndex(_index);
        }

        /// <summary>파일 없이 메모리에 있는 이미지(방금 캡처한 것)를 띄운다.</summary>
        internal void ShowImage(BitmapSource image, string? path, string title)
        {
            StopPlayback();
            Role = ViewerRole.Image;

            _image = image;
            _path = path;
            _files = new List<string>();
            _index = -1;
            ResetOrientation();

            Canvas1.Source = image;
            Canvas1.Width = image.PixelWidth;
            Canvas1.Height = image.PixelHeight;
            EmptyHint.Visibility = Visibility.Collapsed;

            RebuildStrip();
            Title = title + " — SnapView";
            ResetView();
            UpdateChrome();
        }

        private void ResetOrientation()
        {
            _rotation = 0;
            _flipH = _flipV = false;
        }

        /// <summary>한 번만 쓰고 버린다 — 폴더를 넘긴 뒤 돌아왔을 때는 파일에서 읽는다.</summary>
        private BitmapSource? TakePreloaded(string path)
        {
            BitmapSource? img = _preloaded;
            if (img == null ||
                !string.Equals(path, _preloadedPath, StringComparison.OrdinalIgnoreCase))
                return null;

            _preloaded = null;
            _preloadedPath = null;
            return img;
        }

        private void LoadIndex(int i)
        {
            if (_files.Count == 0) return;
            i = Math.Clamp(i, 0, _files.Count - 1);
            string path = _files[i];

            StopPlayback();

            // 동영상은 그림으로 못 읽는다. 재생 층으로 넘긴다.
            if (ImageIO.IsVideo(path)) { OpenAsVideo(path, i); return; }

            try
            {
                BitmapSource img = TakePreloaded(path) ?? ImageIO.Load(path);
                _index = i;
                _path = path;
                _image = img;
                ResetOrientation();

                Canvas1.Source = img;
                Canvas1.Width = img.PixelWidth;
                Canvas1.Height = img.PixelHeight;
                EmptyHint.Visibility = Visibility.Collapsed;

                Title = Path.GetFileName(path) + " — SnapView";
                ResetView();
                SyncStripSelection();

                // 움직이는 GIF 면 첫 장을 보여 준 뒤 돌리기 시작한다.
                TryStartAnimation(path);
            }
            catch (Exception ex)
            {
                // 그림으로 안 읽혔다. 확장자가 우리 목록에 없는 것이면 영상일 수도 있다 —
                // 세상의 모든 확장자를 적어 둘 수는 없으니 한 번 재생해 본다.
                if (!ImageIO.IsSupported(path) && TryOpenAsVideo(path, i)) return;

                StFile.Text = Path.GetFileName(path) + " — 열지 못했습니다: " + ex.Message;
                StPlayNote.Text = "";
            }
            UpdateChrome();
        }

        /// <summary>이 파일을 재생 층에서 연다.</summary>
        private void OpenAsVideo(string path, int i)
        {
            _index = i;
            _path = path;
            _image = null;
            ResetOrientation();
            Title = Path.GetFileName(path) + " — SnapView";
            TryStartVideo(path, force: true);
            SyncStripSelection();
            UpdateChrome();
        }

        /// <summary>그림으로 못 읽은 파일을 영상으로 열어 본다.</summary>
        private bool TryOpenAsVideo(string path, int i)
        {
            try { OpenAsVideo(path, i); return true; }
            catch { return false; }
        }

        /// <summary>
        /// 이 파일이 이 창의 종류와 맞는가. 안 맞으면 맞는 창으로 넘기고 true 를 돌려준다.
        ///
        /// 넘길 데가 없으면(뷰어 전용으로 떴을 때) 이 창의 종류를 바꿔서라도 연다 —
        /// 사용자가 연 파일이 아무 데서도 안 열리는 것보다는 낫다.
        /// </summary>
        private bool WantsOtherWindow(string path)
        {
            ViewerRole want = ImageIO.IsVideo(path) ? ViewerRole.Video : ViewerRole.Image;
            if (want == Role) return false;

            if (OpenElsewhere != null) { OpenElsewhere(path); return true; }

            Role = want;
            Title = System.IO.Path.GetFileName(path) + " — SnapView";
            return false;
        }

        /// <summary>이 창이 처음 맡을 종류를 정한다. 창을 만든 직후에만 부른다.</summary>
        internal void SetRole(ViewerRole role) => Role = role;

        private void Navigate(int delta)
        {
            if (_files.Count <= 1) return;
            int next = _index + delta;

            if (next < 0) next = _settings.WrapAround ? _files.Count - 1 : 0;
            else if (next >= _files.Count) next = _settings.WrapAround ? 0 : _files.Count - 1;

            if (next != _index) LoadIndex(next);
        }

        // ===================================================== 썸네일 줄

        private void RebuildStrip()
        {
            _syncingStrip = true;
            try
            {
                _thumbs.Clear();
                foreach (string f in _files) _thumbs.Add(new ThumbItem(f));
            }
            finally { _syncingStrip = false; }

            SyncStripSelection();
        }

        private void SyncStripSelection()
        {
            if (_index < 0 || _index >= _thumbs.Count) return;

            _syncingStrip = true;
            try
            {
                Strip.SelectedIndex = _index;
                Strip.ScrollIntoView(_thumbs[_index]);
            }
            finally { _syncingStrip = false; }
        }

        private void OnStripSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingStrip) return;
            if (Strip.SelectedIndex >= 0 && Strip.SelectedIndex != _index)
                LoadIndex(Strip.SelectedIndex);
        }

        /// <summary>썸네일 칸이 화면에 실제로 나타났을 때 비로소 그림을 읽는다.</summary>
        private void OnThumbRealized(object sender, RoutedEventArgs e) => Realize(sender);

        /// <summary>가상화가 칸을 재활용하면 Loaded 는 다시 안 오고 DataContext 만 바뀐다.</summary>
        private void OnThumbContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Realize(sender);

        private static void Realize(object sender)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThumbItem item)
                item.BeginLoad();
        }

        private void SetStripVisible(bool show, bool save)
        {
            StripPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TbStrip.IsChecked = show;

            if (show) SyncStripSelection();
            if (save)
            {
                _settings.ShowThumbnailStrip = show;
                _settings.Save();
            }
            Relayout();
        }

        private void OnToggleStrip(object sender, RoutedEventArgs e)
            => SetStripVisible(StripPanel.Visibility != Visibility.Visible, save: true);

        // ===================================================== 배율 / 배치

        private double DpiScale
        {
            get
            {
                try { return VisualTreeHelper.GetDpi(this).DpiScaleX; }
                catch { return 1.0; }
            }
        }

        private double ImgW => _image?.PixelWidth ?? 0;
        private double ImgH => _image?.PixelHeight ?? 0;
        private double RotW => _rotation % 180 == 0 ? ImgW : ImgH;
        private double RotH => _rotation % 180 == 0 ? ImgH : ImgW;

        /// <summary>이미지 픽셀 하나가 차지하는 DIP.</summary>
        private double Eff => _zoom / DpiScale;
        private double DispW => RotW * Eff;
        private double DispH => RotH * Eff;

        private void ResetView()
        {
            _activeFit = _settings.DefaultFitMode;
            ApplyFit(_settings.DefaultFitMode);
        }

        private void ApplyFit(FitMode mode)
        {
            if (_image == null || RotW <= 0 || RotH <= 0) return;

            _zoom = ViewMath.FitZoom(mode,
                Math.Max(1, Stage.ActualWidth), Math.Max(1, Stage.ActualHeight),
                RotW, RotH, DpiScale, MinZoom, MaxZoom);
            _activeFit = mode;
            CenterAndApply();
        }

        private void CenterAndApply()
        {
            _originX = (Stage.ActualWidth - DispW) / 2;
            _originY = (Stage.ActualHeight - DispH) / 2;
            // 폭 맞춤에서 세로로 넘치면 위쪽부터 보여 주는 게 자연스럽다.
            if (DispH > Stage.ActualHeight) _originY = 0;
            ClampOrigin();
            ApplyTransform();
        }

        private void Relayout()
        {
            if (_image == null) return;
            if (_activeFit.HasValue) ApplyFit(_activeFit.Value);
            else { ClampOrigin(); ApplyTransform(); }
        }

        private void ClampOrigin()
        {
            (_originX, _originY) = ViewMath.ClampOrigin(
                _originX, _originY, DispW, DispH, Stage.ActualWidth, Stage.ActualHeight);
        }

        private void ApplyTransform()
        {
            if (_image == null) return;

            double eff = Eff;
            (double tx, double ty) = ViewMath.TranslationFor(_originX, _originY, ImgW, ImgH, RotW, RotH, eff);

            var tg = new TransformGroup();
            // 뒤집기는 이미지 한가운데를 기준으로 하므로 크기가 바뀌지 않는다.
            if (_flipH || _flipV)
                tg.Children.Add(new ScaleTransform(_flipH ? -1 : 1, _flipV ? -1 : 1, ImgW / 2, ImgH / 2));
            tg.Children.Add(new RotateTransform(_rotation, ImgW / 2, ImgH / 2));
            tg.Children.Add(new ScaleTransform(eff, eff));
            tg.Children.Add(new TranslateTransform(tx, ty));
            Canvas1.RenderTransform = tg;

            // 많이 확대했을 때는 뭉개지 말고 픽셀을 또렷하게(도트 확인용).
            RenderOptions.SetBitmapScalingMode(Canvas1,
                _zoom >= _settings.NearestNeighborAbove
                    ? BitmapScalingMode.NearestNeighbor
                    : BitmapScalingMode.HighQuality);

            UpdateChrome();
        }

        private void SetZoom(double zoom, Point? anchor = null)
        {
            if (_image == null) return;

            zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            if (Math.Abs(zoom - _zoom) < 1e-9) return;

            Point a = anchor ?? new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2);
            double oldEff = Eff;

            _zoom = zoom;
            _activeFit = null;

            // 커서 밑에 있던 내용 지점이 그대로 커서 밑에 남도록 원점을 다시 잡는다.
            (_originX, _originY) = ViewMath.ZoomAnchor(a.X, a.Y, _originX, _originY, oldEff, Eff);

            ClampOrigin();
            ApplyTransform();
        }

        // ===================================================== 상태 표시

        private void UpdateChrome()
        {
            bool has = _image != null;
            BtnPrev.IsEnabled = BtnNext.IsEnabled = _files.Count > 1;
            BtnFolder.IsEnabled = !string.IsNullOrEmpty(_path) && File.Exists(_path);

            // 동영상은 그림이 아니라 재생 층에 나온다. 그림이 없다고 안내 문구를 띄우면 안 된다.
            EmptyHint.Visibility = has || _isVideo ? Visibility.Collapsed : Visibility.Visible;

            // 동영상은 픽셀을 만질 수 없다. 그림 전용 단추는 흐리게 둔다.
            foreach (FrameworkElement el in ImageOnlyButtons())
                el.IsEnabled = has;

            if (_isVideo)
            {
                StFile.Text = _path != null ? Path.GetFileName(_path) : "";
                StIndex.Text = _files.Count > 1
                    ? (_index + 1).ToString(CultureInfo.InvariantCulture) + " / " +
                      _files.Count.ToString(CultureInfo.InvariantCulture)
                    : "";
                StSize.Text = _player.NaturalVideoWidth > 0
                    ? _player.NaturalVideoWidth + " × " + _player.NaturalVideoHeight
                    : "";
                StBytes.Text = FileSizeText(_path);
                StZoom.Text = "동영상";
                BtnZoomLabel.Content = "—";
                return;
            }

            if (!has)
            {
                StFile.Text = StIndex.Text = StSize.Text = StBytes.Text = StZoom.Text = "";
                BtnZoomLabel.Content = "—";
                return;
            }

            StFile.Text = _path != null ? Path.GetFileName(_path) : "(저장하지 않은 캡처)";
            StIndex.Text = _files.Count > 1
                ? (_index + 1).ToString(CultureInfo.InvariantCulture) + " / " +
                  _files.Count.ToString(CultureInfo.InvariantCulture)
                : "";
            StSize.Text = ImgW.ToString("0", CultureInfo.InvariantCulture) + " × " +
                          ImgH.ToString("0", CultureInfo.InvariantCulture);
            StBytes.Text = FileSizeText(_path);

            string pct = (_zoom * 100).ToString(_zoom < 1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";
            var extra = new List<string>();
            if (_rotation != 0) extra.Add(_rotation.ToString(CultureInfo.InvariantCulture) + "° 회전");
            if (_flipH) extra.Add("좌우 뒤집음");
            if (_flipV) extra.Add("상하 뒤집음");

            StZoom.Text = extra.Count > 0 ? pct + "   ·   " + string.Join(" · ", extra) : pct;
            BtnZoomLabel.Content = pct;
        }

        private static string FileSizeText(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                long n = new FileInfo(path).Length;
                if (n < 1024) return n + " B";
                if (n < 1024 * 1024) return (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
                return (n / (1024.0 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
            }
            catch { return ""; }
        }

        // ===================================================== 마우스

        private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Grid(Panel) 에는 MouseDoubleClick 이 없으므로 ClickCount 로 직접 판별한다.
            //
            // 영상 위에서는 그림과 다르게 굴러야 한다. 영상에는 확대가 없으니 끌어 봐야
            // 밀 것이 없고(그림처럼 밀면 아무 일도 안 일어난다), 재생기에서 화면을 끌면
            // 보통 <b>창이 따라온다</b>. 두 번 누르는 것도 전체 화면보다 재생/멈춤이 먼저다.
            if (_isVideo)
            {
                if (e.ClickCount == 2) { _videoDragArmed = false; SetPlaying(!_playing); return; }

                // 여기서 바로 DragMove 를 부르면 안 된다. DragMove 는 윈도우의 창 이동
                // 루프로 들어가 버튼을 놓을 때까지 안 돌아오는데, 그러면 <b>두 번 누르기</b>가
                // 그 루프에 먹혀서 재생/멈춤이 안 된다. 실제로 움직이기 시작할 때 부른다.
                _videoDragArmed = true;
                _videoDragFrom = e.GetPosition(this);
                return;
            }

            if (_image == null) return;
            if (e.ClickCount == 2) { ToggleFullScreen(); return; }

            _panning = true;
            _panLast = e.GetPosition(Stage);
            Stage.CaptureMouse();
            Stage.Cursor = Cursors.SizeAll;
        }

        private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
        {
            _videoDragArmed = false;
            _panning = false;
            Stage.ReleaseMouseCapture();
            Stage.Cursor = Cursors.Arrow;
        }

        private void OnStageMouseMove(object sender, MouseEventArgs e)
        {
            // 영상 위에서 끌면 창이 따라온다. 재생기에서 흔히 그렇게 한다 —
            // 영상에는 확대가 없으니 그림처럼 밀어 봐야 밀 것이 없다.
            if (_videoDragArmed)
            {
                if (e.LeftButton != MouseButtonState.Pressed) { _videoDragArmed = false; return; }

                Point now = e.GetPosition(this);
                if (Math.Abs(now.X - _videoDragFrom.X) < 4 && Math.Abs(now.Y - _videoDragFrom.Y) < 4)
                    return;   // 아직 "누른 것" 이다. 두 번 누르기일 수도 있으니 기다린다.

                _videoDragArmed = false;

                // 최대화·전체 화면에서는 창을 끌 자리가 없다(DragMove 가 예외를 던진다).
                if (WindowState != WindowState.Normal || _fullScreen) return;

                try { DragMove(); } catch { /* 그사이 버튼을 놓았을 수 있다 */ }
                return;
            }

            if (!_panning || _image == null) return;

            Point p = e.GetPosition(Stage);
            _originX += p.X - _panLast.X;
            _originY += p.Y - _panLast.Y;
            _panLast = p;
            _activeFit = null;

            ClampOrigin();
            ApplyTransform();
        }

        private void OnStageMouseDownAny(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            // 가운데 버튼: 100% ↔ 맞춤 토글
            if (Math.Abs(_zoom - 1.0) < 0.01) ApplyFit(FitMode.ShrinkToFit);
            else SetZoom(1.0, e.GetPosition(Stage));
        }

        /// <summary>휠은 확대·축소만 한다. 스크롤로는 쓰지 않는다.</summary>
        private void OnStageWheel(object sender, MouseWheelEventArgs e)
        {
            // 영상에는 확대·축소가 없다. 그 자리에서 제일 자주 쓰는 건 소리 크기다.
            if (_isVideo && _settings.WheelChangesVolume)
            {
                NudgeVolume(e.Delta > 0 ? +0.05 : -0.05);
                e.Handled = true;
                return;
            }

            if (_image == null) return;
            e.Handled = true;
            SetZoom(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), e.GetPosition(Stage));
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            foreach (string f in files)
            {
                // 확장자를 가리지 않는다. 못 열면 그때 열지 못했다고 말해 준다.
                if (File.Exists(f)) { LoadFile(f); return; }
            }
        }

        // ===================================================== 키보드

        /// <summary>
        /// 재생 키는 <b>누구에게 포커스가 있든</b> 먼저 듣는다.
        ///
        /// 단추를 한 번 누르면 그 단추가 키보드 포커스를 쥔다. 그러면 그다음 Space 가
        /// 재생/멈춤이 아니라 <b>그 단추를 또 누른다</b> — 폴더나 목록 단추를 눌렀다가
        /// 스페이스가 안 먹던 게 이것 때문이다. 여기(터널링 단계)서 먼저 가로챈다.
        ///
        /// 글자를 넣는 칸과 목록 상자는 건드리지 않는다. 거기서는 Space 가 제 일을 해야 한다.
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Handled) return;

            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                return;

            // 콤보 상자는 <b>목록이 펼쳐져 있는 동안만</b> 키를 가져간다(방향키·Enter 로 골라야
            // 하니까). 닫힌 콤보가 포커스만 쥔 채 키를 다 가져가면, 속도를 고른 직후의
            // Space 가 재생/멈춤이 아니라 콤보를 다시 펼치거나 — 콤보가 안 받으면 창까지
            // 흘러내려 "다음 파일" 이 됐다. 실제로 그랬다.
            if (Keyboard.FocusedElement is System.Windows.Controls.ComboBox { IsDropDownOpen: true } or
                                            System.Windows.Controls.ComboBoxItem)
                return;

            if (HandlePlayerKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // 재생 키는 위(OnPreviewKeyDown)에서 이미 걸러졌다. 여기 오는 것은
            // 그림을 볼 때 쓰는 키다 — 영상에서 →가 다음 파일로 넘어가면 곤란하므로
            // 파일 이동은 재생 중에도 PageUp/PageDown 으로 남긴다.

            // 안전망: 포커스 예외(콤보 등)로 터널링을 건너뛴 재생 키가 여기까지 내려와도
            // 그림 키로 오해하지 않는다 — Space 가 "다음 파일" 이 되는 사고 방지.
            // 재생할 게 없으면 HandlePlayerKey 가 스스로 false 를 돌려준다.
            if (HandlePlayerKey(e.Key, Keyboard.Modifiers)) { e.Handled = true; return; }

            switch (e.Key)
            {
                case Key.Left or Key.PageUp: Navigate(-1); break;
                case Key.Right or Key.PageDown or Key.Space: Navigate(1); break;
                case Key.Home: if (_files.Count > 0) LoadIndex(0); break;
                case Key.End: if (_files.Count > 0) LoadIndex(_files.Count - 1); break;

                case Key.Up: Nudge(0, 60); break;
                case Key.Down: Nudge(0, -60); break;

                case Key.OemPlus or Key.Add: SetZoom(_zoom * ZoomStep); break;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom / ZoomStep); break;
                case Key.D0 or Key.NumPad0: ApplyFit(FitMode.Actual); break;
                case Key.F: ApplyFit(FitMode.ShrinkToFit); break;
                case Key.W when !ctrl: ApplyFit(FitMode.FitWidth); break;

                case Key.R: Rotate(shift ? -90 : 90); break;
                case Key.H: Flip(horizontal: true); break;
                case Key.V when !ctrl: Flip(horizontal: false); break;

                case Key.G: SetStripVisible(StripPanel.Visibility != Visibility.Visible, save: true); break;
                case Key.E when !ctrl: OpenInEditor(); break;

                case Key.C when ctrl: CopyCurrent(); break;
                case Key.S when ctrl && shift: SaveAs(); break;
                case Key.S when ctrl: SaveInPlace(); break;
                case Key.O when ctrl: OpenDialog(); break;
                case Key.E when ctrl: RevealInExplorer(); break;
                case Key.Delete: DeleteCurrent(); break;

                case Key.F11: ToggleFullScreen(); break;
                case Key.Escape:
                    if (_fullScreen) ToggleFullScreen();
                    else Close();
                    break;

                default: return;
            }
            e.Handled = true;
        }

        private void Nudge(double dx, double dy)
        {
            if (_image == null) return;
            _originX += dx;
            _originY += dy;
            _activeFit = null;
            ClampOrigin();
            ApplyTransform();
        }

        // ===================================================== 동작

        private void Rotate(int degrees)
        {
            if (_image == null) return;
            _rotation = ((_rotation + degrees) % 360 + 360) % 360;
            if (_activeFit.HasValue) ApplyFit(_activeFit.Value);
            else CenterAndApply();
        }

        private void Flip(bool horizontal)
        {
            if (_image == null) return;
            if (horizontal) _flipH = !_flipH; else _flipV = !_flipV;
            ApplyTransform();
        }

        /// <summary>화면에 보이는 그대로(회전·뒤집기 반영) 비트맵을 만든다.</summary>
        private BitmapSource? CurrentAsShown()
        {
            if (_image == null) return null;
            if (_rotation == 0 && !_flipH && !_flipV) return _image;

            var tg = new TransformGroup();
            if (_flipH || _flipV)
                tg.Children.Add(new ScaleTransform(_flipH ? -1 : 1, _flipV ? -1 : 1));
            if (_rotation != 0)
                tg.Children.Add(new RotateTransform(_rotation));

            var t = new TransformedBitmap(_image, tg);
            t.Freeze();
            return t;
        }

        private void OpenInEditor()
        {
            BitmapSource? img = CurrentAsShown();
            if (img == null) return;

            string label = _path != null ? Path.GetFileNameWithoutExtension(_path) : "이미지";
            EditRequested?.Invoke(img, label);
        }

        private void CopyCurrent()
        {
            BitmapSource? img = CurrentAsShown();
            if (img != null) ImageIO.CopyToClipboard(img);
        }

        /// <summary>돌리거나 뒤집은 상태가 아직 파일에 안 들어갔는가.</summary>
        private bool HasUnsavedTransform => _rotation != 0 || _flipH || _flipV;

        /// <summary>
        /// 지금 보이는 그대로 <b>원래 파일에</b> 덮어쓴다.
        /// 돌려 놓고 저장할 데가 없으면 회전 기능이 반쪽짜리라 넣는다.
        /// </summary>
        private void SaveInPlace()
        {
            if (_path == null) { SaveAs(); return; }        // 파일에서 온 게 아니면 새로 저장
            if (!HasUnsavedTransform) { StBytes.Text = "바뀐 것이 없습니다"; return; }

            BitmapSource? img = CurrentAsShown();
            if (img == null) return;

            try
            {
                ImageIO.Overwrite(img, _path, _settings.JpegQuality);

                // 파일이 바뀌었으니 그 파일을 다시 읽는다. 회전 값도 0 으로 돌아간다.
                string saved = _path;
                LoadIndex(_index);
                StBytes.Text = Path.GetFileName(saved) + " 에 저장했습니다";
            }
            catch (Exception ex)
            {
                Core.Log.Write("덮어쓰기 실패: " + _path + " — " + ex.Message);
                MessageBox.Show(this, "저장하지 못했습니다.\n\n" + ex.Message, "SnapView",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveAs()
        {
            BitmapSource? img = CurrentAsShown();
            if (img == null) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "다른 이름으로 저장",
                Filter = "PNG 이미지|*.png|JPEG 이미지|*.jpg|BMP 이미지|*.bmp",
                FileName = _path != null
                    ? Path.GetFileNameWithoutExtension(_path)
                    : "SnapView_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture),
                DefaultExt = ".png"
            };
            if (dlg.ShowDialog() != true) return;

            try { ImageIO.SaveTo(img, dlg.FileName, _settings.JpegQuality); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "저장하지 못했습니다.\n\n" + ex.Message, "SnapView",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "이미지 열기",
                Filter = "이미지 · 동영상|*" + string.Join(";*", ImageIO.SupportedExtensions) +
                         ";*" + string.Join(";*", ImageIO.VideoExtensions) +
                         "|이미지|*" + string.Join(";*", ImageIO.SupportedExtensions) +
                         "|동영상|*" + string.Join(";*", ImageIO.VideoExtensions) +
                         "|모든 파일|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true) LoadFile(dlg.FileName);
        }

        private void RevealInExplorer()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _path + "\"")
                { UseShellExecute = true });
            }
            catch { }
        }

        private void DeleteCurrent()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;

            var answer = MessageBox.Show(this,
                Path.GetFileName(_path) + "\n\n이 파일을 휴지통으로 보낼까요?",
                "SnapView", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            string gone = _path;
            if (!ImageIO.RecycleFile(gone))
            {
                MessageBox.Show(this, "삭제하지 못했습니다.", "SnapView",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int removed = _files.FindIndex(f => string.Equals(f, gone, StringComparison.OrdinalIgnoreCase));
            if (removed >= 0)
            {
                _files.RemoveAt(removed);
                if (removed < _thumbs.Count) _thumbs.RemoveAt(removed);
            }

            if (_files.Count == 0)
            {
                // 마지막 파일을 지웠어도 재생은 알아서 멈춰야 한다. 다른 파일로
                // 넘어가는 길은 전부 LoadIndex → StopPlayback 을 지나는데, 여기는 없어서
                // 지운 영상이 소리를 내며 계속 돌거나(영상 창) 지운 그림이 다시 그려졌다(GIF).
                StopPlayback();
                _image = null;
                _path = null;
                _index = -1;
                Canvas1.Source = null;
                Title = "SnapView";
                UpdateChrome();
                return;
            }
            LoadIndex(Math.Min(removed < 0 ? 0 : removed, _files.Count - 1));
        }

        private void ToggleFullScreen()
        {
            if (!_fullScreen)
            {
                _prevState = WindowState;
                _prevStyle = WindowStyle;
                _prevResize = ResizeMode;

                Toolbar.Visibility = Visibility.Collapsed;
                StatusBar.Visibility = Visibility.Collapsed;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;   // Maximized 에서 바로 바꾸면 작업 표시줄이 남는다
                WindowState = WindowState.Maximized;
                _fullScreen = true;
            }
            else
            {
                Toolbar.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Visible;
                WindowStyle = _prevStyle;
                ResizeMode = _prevResize;
                WindowState = _prevState;
                _fullScreen = false;
            }
            Dispatcher.BeginInvoke(new Action(Relayout),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ===================================================== 툴바 핸들러

        private void OnPrev(object sender, RoutedEventArgs e) => Navigate(-1);
        private void OnNext(object sender, RoutedEventArgs e) => Navigate(1);
        private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_zoom * ZoomStep);
        private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_zoom / ZoomStep);
        private void OnActualSize(object sender, RoutedEventArgs e) => ApplyFit(FitMode.Actual);
        private void OnFit(object sender, RoutedEventArgs e) => ApplyFit(FitMode.ShrinkToFit);
        private void OnFitWidth(object sender, RoutedEventArgs e) => ApplyFit(FitMode.FitWidth);
        private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-90);
        private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(90);
        private void OnFlipHorizontal(object sender, RoutedEventArgs e) => Flip(true);
        private void OnFlipVertical(object sender, RoutedEventArgs e) => Flip(false);
        private void OnEdit(object sender, RoutedEventArgs e) => OpenInEditor();
        private void OnCopy(object sender, RoutedEventArgs e) => CopyCurrent();
        private void OnSaveAs(object sender, RoutedEventArgs e) => SaveAs();
        private void OnSave(object sender, RoutedEventArgs e) => SaveInPlace();
        private void OnOpen(object sender, RoutedEventArgs e) => OpenDialog();
        private void OnRevealInExplorer(object sender, RoutedEventArgs e) => RevealInExplorer();
        private void OnToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();
    }
}
