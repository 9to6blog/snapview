using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapView.Core;
using SnapView.Native;

namespace SnapView.Capture
{
    /// <summary>
    /// 화면의 한 영역을 일정 간격으로 찍어 영상 파일로 흘려 넣는다.
    ///
    /// 기본은 <b>H.264 MP4</b>. 색이 안 뭉개지고 파일도 훨씬 작다.
    /// 인코더를 못 여는 환경에서는 움직이는 GIF 로 물러선다.
    ///
    /// 프레임을 모아 뒀다가 마지막에 저장하지 않는다. 찍는 즉시 파일로 내보낸다 —
    /// 그래야 오래 찍어도 메모리가 안 터진다. 다만 GIF 는 도중에 앱이 죽어도 그때까지가
    /// 남지만, MP4 는 마무리(moov)를 써야 열린다. 그래서 잠금·절전·로그오프·모니터 변경
    /// 때는 컨트롤러가 먼저 저장하고 멈춘다(<see cref="Stop"/> 이 마무리 결과를 돌려준다).
    ///
    /// 소리는 <b>지금 스피커로 나가는 것</b>(시스템 소리)을 같이 담는다.
    /// 소리를 못 잡아도 영상 녹화는 그대로 계속한다.
    ///
    /// 찍는 일은 <b>전용 스레드</b>에서 한다. 예전에는 화면 타이머(DispatcherTimer)로
    /// 돌렸는데, 그 타이머는 창 그리기·입력에 밀리는 낮은 순위라 60 을 골라도 25장쯤에서
    /// 막혔다. 게다가 찍는 동안 화면 전체가 뻣뻣해졌다. 지금은 시계를 직접 보며 도는
    /// 스레드가 찍고, 화면 쪽에는 다 찍고 나서 알려 주기만 한다.
    /// </summary>
    internal sealed class ScreenRecorder : IDisposable
    {
        /// <summary>GIF 로 물러섰을 때만 쓰는 한계. GIF 는 큰 화면에 안 맞는 그릇이다.</summary>
        internal const int GifMaxWidth = 960;

        /// <summary>GIF 간격 단위가 1/100초라 이보다 빠르게는 못 담는다. 설정에서 더 높게 골라도 여기서 붙든다.</summary>
        internal const int GifMaxFps = 50;

        /// <summary>
        /// 고를 수 있는 초당 장수. 위쪽은 인코더가 받아 주는 한계까지 열어 둔다.
        ///
        /// 높게 잡는다고 그만큼 나온다는 보장은 없다 — 넓은 영역을 찍을수록 한 장
        /// 찍는 데 오래 걸린다. 못 따라가면 그냥 덜 찍히고, 대신 <b>영상 길이는
        /// 실제 시간과 맞는다</b>(찍힌 시각을 그대로 적기 때문에).
        /// 실제로 몇 장이 담기고 있는지는 녹화 막대에 나온다.
        /// </summary>
        internal const int MinFps = Mp4Writer.MinFps;
        internal const int MaxFps = Mp4Writer.MaxFps;

        /// <summary>여기를 넘기면 "기계가 못 따라갈 수 있다" 고 미리 일러 준다.</summary>
        internal const int SmoothFpsHint = 30;

        private readonly Stopwatch _clock = new();
        private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
        private readonly Int32Rect _region;
        private int _delayMs;
        private int _fps;

        private readonly bool _preferMp4;
        private readonly bool _wantAudio;
        private readonly bool _includeCursor;

        /// <summary>MP4 로 찍을 때 화면을 떠 넣는 버퍼. 프레임마다 새로 잡지 않고 돌려 쓴다.</summary>
        private byte[]? _pixels;

        private LoopbackCapture? _audio;
        private int _audioChannels;
        private int _audioOutRate;
        private AudioResampler? _resampler;
        private long _lastReattachTick;
        private bool _audioInterrupted;

        private FileStream? _file;
        private GifWriter.Session? _gif;
        private Mp4Writer? _mp4;
        private Thread? _worker;
        private volatile bool _stopped;
        private bool _timerBoosted;
        private TimeSpan _lastFrameAt = TimeSpan.MinValue;

        /// <summary>녹화 시계를 켠 순간의 QPC(100ns). 소리 덩어리의 장치 시각을 녹화 시각으로 옮기는 기준.</summary>
        private long _clockStartQpc;

        /// <summary>일시정지로 흘려보낸 시간의 합(100ns). 소리 시각에서 이만큼을 뺀다.</summary>
        private long _pausedTotal100ns;

        private volatile bool _paused;
        private bool _pauseHandled;         // 찍는 스레드만 만진다
        private long _pauseStartQpc;

        /// <summary>잠깐 쉬는 중인가.</summary>
        internal bool IsPaused => _paused;

        /// <summary>
        /// 잠깐 쉰다. 쉬는 동안은 화면도 소리도 영상에 안 들어가고 시계도 멈춘다 —
        /// 다시 찍으면 쉰 시간이 없었던 것처럼 이어진다.
        /// </summary>
        internal void Pause()
        {
            if (_paused || _stopped) return;
            _pauseStartQpc = NowQpc100ns();
            _clock.Stop();
            _paused = true;
        }

        /// <summary>다시 찍는다.</summary>
        internal void Resume()
        {
            if (!_paused || _stopped) return;
            _pausedTotal100ns += NowQpc100ns() - _pauseStartQpc;
            _clock.Start();
            _paused = false;
        }

        /// <summary>찍는 스레드가 세는 값. 화면 쪽에서도 읽으므로 따로 둔다.</summary>
        private int _frames;

        /// <summary>실제로 만들어진 파일. MP4 가 안 되면 GIF 로 바뀔 수 있다.</summary>
        internal string Path { get; private set; }

        /// <summary>MP4 로 담고 있는가. 아니면 GIF.</summary>
        internal bool IsMp4 => _mp4 != null;

        internal int FrameCount => _frames;
        internal TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>설정에서 고른 초당 장수.</summary>
        internal int TargetFps => _fps;

        /// <summary>
        /// 지금까지 <b>실제로</b> 담긴 초당 장수. 고른 값과 다를 수 있다 —
        /// 넓은 영역을 높은 값으로 찍으면 기계가 못 따라간다.
        /// 처음 반 초는 아직 셀 게 없어서 0 을 준다.
        /// </summary>
        internal double ActualFps
        {
            get
            {
                double seconds = _clock.Elapsed.TotalSeconds;
                return seconds >= 0.5 ? FrameCount / seconds : 0;
            }
        }

        /// <summary>한 장 찍을 때마다. 화면에 시간·장수를 보여 주는 데 쓴다.</summary>
        internal event Action? Tick;

        /// <summary>더 못 찍고 멈췄을 때(디스크 오류 등). 메시지가 들어온다.</summary>
        internal event Action<string>? Failed;

        /// <summary>소리 트랙이 실제로 붙었는가.</summary>
        internal bool HasAudio => _mp4?.HasAudio == true && _audio?.Started == true;

        /// <summary>녹화 중에 소리 장치가 끊긴 적이 있다(그 구간은 무음이다).</summary>
        internal bool AudioInterrupted => _audioInterrupted;

        internal ScreenRecorder(Int32Rect region, int fps, string path,
                                bool preferMp4 = true, bool withAudio = true, bool includeCursor = true)
        {
            _region = region;
            Path = path;
            _preferMp4 = preferMp4;
            _wantAudio = withAudio;
            _includeCursor = includeCursor;

            fps = Math.Clamp(fps, MinFps, MaxFps);
            _fps = fps;
            _delayMs = Math.Max(1, (int)Math.Round(1000.0 / fps));
        }

        internal void Start()
        {
            if (_preferMp4 && TryStartMp4()) { Begin(); return; }

            StartGif();
            Begin();
        }

        private void Begin()
        {
            // 윈도우의 기본 타이머 눈금은 약 15.6ms 다. 그대로 두면 아무리 높게 잡아도
            // 초당 64장 근처에서 막히고, 30장조차 들쭉날쭉해진다. 녹화하는 동안만
            // 눈금을 1ms 로 당겨 둔다(끝나면 반드시 되돌린다 — 안 되돌리면 시스템
            // 전체가 계속 잘게 깨어 있어 배터리를 먹는다).
            if (NativeMethods.timeBeginPeriod(1) == 0) _timerBoosted = true;

            _clockStartQpc = NowQpc100ns();
            _clock.Start();

            _worker = new Thread(Loop)
            {
                IsBackground = true,        // 이것 때문에 앱이 안 닫히면 안 된다
                Name = "SnapView 녹화",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.Start();
        }

        /// <summary>
        /// 찍는 스레드. 시계를 보고 다음 차례까지 기다렸다가 한 장 찍는다.
        ///
        /// 밀렸을 때 밀린 만큼 몰아 찍지 않는다. 따라잡으려 들면 점점 더 밀리기만 하고,
        /// 어차피 장마다 찍힌 시각을 적으므로 덜 찍혀도 영상 길이는 안 틀어진다.
        /// </summary>
        private void Loop()
        {
            TimeSpan step = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _fps);
            TimeSpan next = TimeSpan.Zero;

            while (!_stopped)
            {
                if (_paused)
                {
                    if (!_pauseHandled) { _pauseHandled = true; EnterPause(); }
                    _audio?.DrainBlocks();          // 쉬는 동안 난 소리는 버린다
                    Thread.Sleep(20);
                    next = _clock.Elapsed;          // 다시 찍을 때 밀린 만큼 몰아 찍지 않는다
                    continue;
                }
                _pauseHandled = false;

                if (!CaptureOne()) break;

                next += step;
                TimeSpan wait = next - _clock.Elapsed;

                // 이미 지났으면 따라잡기를 포기하고 지금을 기준으로 다시 센다.
                if (wait <= TimeSpan.Zero) { next = _clock.Elapsed; continue; }

                // 1ms 눈금을 켜 뒀으므로 Sleep 이 이 정도 정확도로 깬다.
                Thread.Sleep(wait);
            }

            // 정지가 우리를 기다리다 포기했으면, 파일은 우리가 닫는다. 그래야 그나마 열린다.
            if (_finishOnExit)
            {
                try { CloseFiles(); }
                catch (Exception ex) { Log.Write("뒤늦은 마무리 실패: " + ex.Message); }
            }
        }

        private bool TryStartMp4()
        {
            (int Channels, int SampleRate, int Bits)? audioFormat = _wantAudio ? StartAudio() : null;

            try
            {
                _mp4 = new Mp4Writer(Path, _region.Width, _region.Height, _fps, 0, audioFormat);
                if (!_mp4.HasAudio) DropAudio();
                return true;
            }
            catch (Exception ex)
            {
                // 인코더가 받아 주는 초당 장수는 기계마다 다르다. 높게 잡았다가 거절당했다고
                // GIF 로 물러설 일은 아니다 — 흔한 값으로 한 번 더 열어 본다.
                // (찍는 속도는 그대로고, 장마다 찍힌 시각을 적으므로 길이도 그대로다)
                if (_fps > SmoothFpsHint && TryOpenMp4At(SmoothFpsHint, audioFormat))
                {
                    Log.Write($"인코더가 {_fps}fps 를 거절해 머리말만 {SmoothFpsHint}fps 로 담습니다: "
                              + ex.Message);
                    if (_mp4 != null && !_mp4.HasAudio) DropAudio();
                    return true;
                }

                // 인코더가 없거나 못 열리는 환경. 녹화 자체를 포기하지는 않는다.
                // GIF 에는 소리가 못 들어가니 장치를 놓는다 — 안 놓으면 큐만 계속 쌓인다.
                Log.Write("MP4 인코더를 못 열어 GIF 로 넘어갑니다: " + ex.Message);
                _mp4 = null;
                DropAudio();
                return false;
            }
        }

        /// <summary>소리를 쓸 수 없게 됐다. 장치를 놓는다.</summary>
        private void DropAudio()
        {
            _audio?.Dispose();
            _audio = null;
            _resampler = null;
        }

        private bool TryOpenMp4At(int fps, (int Channels, int SampleRate, int Bits)? audioFormat)
        {
            try
            {
                _mp4 = new Mp4Writer(Path, _region.Width, _region.Height, fps, 0, audioFormat);
                return true;
            }
            catch { _mp4 = null; return false; }
        }

        /// <summary>
        /// 시스템 소리 받기를 시작한다. 형식이 AAC 로 못 넘어가는 것이면 소리를 포기한다.
        /// 소리 하나 때문에 화면 녹화를 접지는 않는다.
        /// </summary>
        private (int Channels, int SampleRate, int Bits)? StartAudio()
        {
            LoopbackCapture? capture = null;
            try
            {
                capture = new LoopbackCapture();
                capture.Start();

                _audio = capture;
                _audioChannels = Math.Clamp(capture.Channels, 1, 2);
                _audioOutRate = AudioConvert.IsSupportedRate(capture.SampleRate)
                    ? capture.SampleRate : AudioConvert.FallbackRate;
                _resampler = MakeResampler(capture);
                return (_audioChannels, _audioOutRate, 16);
            }
            catch (Exception ex)
            {
                Log.Write("소리를 못 받아 영상만 담습니다: " + ex.Message);
                capture?.Dispose();
                _audio = null;
                return null;
            }
        }

        /// <summary>장치 속도가 AAC 로 못 가는 값이면 바꿔 주는 변환기. 같으면 null.</summary>
        private AudioResampler? MakeResampler(LoopbackCapture capture)
        {
            if (capture.SampleRate == _audioOutRate) return null;
            Log.Write($"소리 표본 속도 {capture.SampleRate}Hz 는 AAC 로 못 담아 {_audioOutRate}Hz 로 바꿔 담습니다.");
            return new AudioResampler(_audioChannels, capture.SampleRate, _audioOutRate);
        }

        /// <summary>
        /// 재생 장치가 바뀌어(헤드폰을 뽑는 등) 받기가 끊겼다. 새 기본 장치에 다시 붙는다.
        /// 트랙 형식은 그대로 두고, 장치 형식이 다르면 변환기만 다시 맞춘다.
        /// 끊긴 동안은 무음으로 채워지고 새 소리는 제 시각에 놓이므로 싱크는 유지된다.
        /// </summary>
        private void TryReattachAudio()
        {
            long now = Environment.TickCount64;
            if (now - _lastReattachTick < 1000) return;
            _lastReattachTick = now;
            _audioInterrupted = true;

            LoopbackCapture? fresh = null;
            try
            {
                fresh = new LoopbackCapture();
                fresh.Start();
                _audio?.Dispose();
                _audio = fresh;
                _resampler = MakeResampler(fresh);
                Log.Write("재생 장치가 바뀌어 새 장치로 소리 받기를 이어 갑니다.");
            }
            catch (Exception ex)
            {
                fresh?.Dispose();
                Log.Write("소리 장치에 다시 붙지 못했습니다(1초 뒤 다시 시도): " + ex.Message);
            }
        }

        /// <summary>
        /// 모인 소리를 파일로 넘긴다. 프레임 찍을 때마다 같이 부른다.
        ///
        /// 아무 소리도 안 나는 동안에는 장치가 <b>아무것도 주지 않는다</b>. 그대로 두면
        /// 소리 트랙만 안 자라서, 조용한 구간이 끝난 뒤의 소리가 그만큼 앞당겨져 붙는다.
        /// 그래서 지금 시각까지 무음으로 메워 둔다.
        /// </summary>
        /// <param name="at">영상이 여기까지 왔다. 소리도 (여유를 두고) 여기까지 채운다.</param>
        /// <param name="limit">이 시각 뒤의 소리는 버린다. 쉬기 시작할 때·끝낼 때 쓴다.</param>
        private void PumpAudio(TimeSpan at, TimeSpan? limit = null)
        {
            if (_audio == null || _mp4 == null || !_mp4.HasAudio) return;
            if (_audio.Failed) TryReattachAudio();

            int bytesPerSecond = _audioOutRate * _audioChannels * 2;
            int blockAlign = _audioChannels * 2;

            foreach (LoopbackCapture.Block block in _audio.DrainBlocks())
            {
                byte[] pcm = AudioConvert.ToPcm16(block.Data, block.Data.Length, _audio.Channels,
                                                  _audio.IsFloat, _audio.BitsPerSample, _audioChannels);
                if (_resampler != null) pcm = _resampler.Process(pcm, pcm.Length);
                if (pcm.Length == 0) continue;

                // 장치가 시각을 줬으면 그 자리에 놓는다. 못 줬으면 예전처럼 뒤에 붙인다.
                if (!block.HasTime) { if (limit == null) _mp4.AddAudio(pcm, pcm.Length); continue; }

                TimeSpan time = RecordingTimeOf(block.Qpc100ns);
                int length = pcm.Length;
                if (limit.HasValue)
                {
                    if (time >= limit.Value) continue;
                    long keep = (long)((limit.Value - time).TotalSeconds * bytesPerSecond);
                    keep -= keep % blockAlign;
                    if (keep < length) length = (int)Math.Max(0, keep);
                    if (length <= 0) continue;
                }
                _mp4.AddAudioAt(pcm, 0, length, time);
            }

            _mp4.PadAudioTo(at, limit.HasValue ? TimeSpan.Zero : AudioLatencyAllowance);
        }

        /// <summary>쉬기 시작했다. 소리를 딱 여기까지만 채우고, 이후 들어오는 건 버린다.</summary>
        private void EnterPause()
        {
            TimeSpan point = _clock.Elapsed;
            try { PumpAudio(point, point); }
            catch (Exception ex) { Log.Write("쉬기 전 소리 마무리 실패: " + ex.Message); }
        }

        /// <summary>
        /// 소리 트랙이 영상보다 이만큼까지는 뒤처져도 둔다.
        ///
        /// 덩어리는 제 시각에 놓이므로 이 값이 싱크를 흔들지는 않는다. 다만 무음을 너무 일찍
        /// 채우면 늦게 도착한 덩어리의 앞부분이 잘리므로, 장치 지연보다 넉넉히 기다린다.
        /// 너무 크면 SinkWriter 가 영상을 그만큼 쥐고 있어야 하니 무한정 키우지도 않는다.
        /// </summary>
        private static readonly TimeSpan AudioLatencyAllowance = TimeSpan.FromMilliseconds(500);

        /// <summary>지금 QPC 를 100ns 단위로. WASAPI 가 주는 패킷 시각과 같은 기준이다.</summary>
        private static long NowQpc100ns()
        {
            long ts = Stopwatch.GetTimestamp();
            long f = Stopwatch.Frequency;
            return ts / f * 10_000_000 + (ts % f) * 10_000_000 / f;
        }

        /// <summary>장치가 말한 시각(QPC 100ns)을 녹화 시작 기준 시각으로 옮긴다. 일시정지한 만큼은 뺀다.</summary>
        private TimeSpan RecordingTimeOf(long qpc100ns)
            => TimeSpan.FromTicks(qpc100ns - _clockStartQpc - _pausedTotal100ns);

        private void StartGif()
        {
            Path = System.IO.Path.ChangeExtension(Path, ".gif");

            if (_fps > GifMaxFps)
            {
                Log.Write($"GIF 는 초당 {GifMaxFps}장까지라 {_fps} 대신 {GifMaxFps}fps 로 찍습니다.");
                _fps = GifMaxFps;
                _delayMs = Math.Max(1, (int)Math.Round(1000.0 / _fps));
            }

            (int w, int h) = GifSize();
            _file = File.Create(Path);
            _gif = new GifWriter.Session(_file, w, h);
        }

        private bool _stopResult;
        private volatile bool _finishOnExit;

        /// <summary>찍는 스레드가 멈추길 이만큼 기다린다. 화면 스레드 밖에서 부르므로 넉넉히 둔다.</summary>
        private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(20);

        /// <summary>정지가 실패했을 때의 이유(파일 마무리 실패, 스레드가 안 멈춤). 성공이면 null.</summary>
        internal string? LastError { get; private set; }

        /// <summary>
        /// 멈추고 파일을 닫는다. <b>쓸 수 있는 파일이 남았으면 true.</b>
        /// 한 장도 못 찍었거나(빈 파일은 지운다) 마무리에 실패하면 false 고, 이유는 <see cref="LastError"/> 에 있다.
        ///
        /// 찍는 스레드가 파일에 한 장을 더 넣는 중에 파일을 닫으면 깨진 영상이 된다. 그래서
        /// 스레드가 멈출 때까지 기다린다 — 느린 드라이브에서는 몇 초가 걸릴 수 있어 화면 스레드가
        /// 아니라 뒤에서 부르는 게 맞다. 그래도 안 멈추면 파일은 손대지 않고(스레드가 나올 때
        /// 스스로 마무리한다) 실패로 돌려준다. 예전엔 3초 뒤 그냥 닫아서 깨진 파일을 만들었다.
        /// </summary>
        internal bool Stop()
        {
            if (_stopped) return _stopResult;
            _stopped = true;

            if (_worker != null && _worker.IsAlive && _worker != Thread.CurrentThread)
            {
                if (!_worker.Join(WorkerStopTimeout))
                {
                    Log.Write("녹화 스레드가 제때 안 멈췄습니다. 파일은 스레드가 나올 때 마무리합니다.");
                    LastError = "녹화 스레드가 멈추지 않아 파일을 마무리하지 못했습니다.";
                    _finishOnExit = true;
                    _worker = null;
                    _clock.Stop();
                    if (_timerBoosted) { NativeMethods.timeEndPeriod(1); _timerBoosted = false; }
                    _stopResult = false;
                    return false;
                }
            }
            _worker = null;

            _clock.Stop();
            if (_timerBoosted) { NativeMethods.timeEndPeriod(1); _timerBoosted = false; }

            _stopResult = CloseFiles();
            return _stopResult;
        }

        /// <summary>남은 소리를 넣고 파일을 마무리한다. 쓸 수 있는 파일이 남았으면 true.</summary>
        private bool CloseFiles()
        {
            bool any = FrameCount > 0;

            // 마지막까지 남은 소리를 마저 넣고 장치를 놓는다.
            // 끝은 봐 주는 시간 없이 실제 길이까지 채운다 — 소리 트랙이 짧게 끝나면 안 된다.
            TimeSpan end = _clock.Elapsed;
            try { PumpAudio(end, end); }
            catch (Exception ex) { Log.Write("마지막 소리를 못 넣었습니다: " + ex.Message); }
            DropAudio();

            bool finished = true;
            if (_mp4 != null)
            {
                if (any)
                {
                    finished = _mp4.Finish();
                    if (!finished) LastError = _mp4.Error ?? "파일을 마무리하지 못했습니다.";
                }
                _mp4.Dispose();
                _mp4 = null;
            }

            _gif?.Dispose();     // 끝 표시를 쓴다
            _gif = null;
            _file = null;        // Session 이 스트림도 닫는다

            if (!any)
            {
                try { if (File.Exists(Path)) File.Delete(Path); } catch { }
                return false;
            }
            return finished;
        }

        /// <summary>
        /// GIF 한 장에 적을 간격. 정해 둔 간격이 아니라 <b>실제로 걸린 시간</b>을 적는다.
        /// 못 따라가서 덜 찍혔을 때 GIF 가 빨리 감기지 않게 하려는 것이다.
        /// (한 장 늦게 반영되지만 전체 길이는 실제와 맞는다)
        /// </summary>
        private int GifDelayFor(TimeSpan at)
        {
            if (_lastFrameAt == TimeSpan.MinValue) return _delayMs;

            double ms = (at - _lastFrameAt).TotalMilliseconds;
            return (int)Math.Clamp(ms, 10, 10_000);
        }

        private (int Width, int Height) GifSize()
        {
            int w = Math.Max(1, _region.Width);
            int h = Math.Max(1, _region.Height);

            if (w <= GifMaxWidth) return (w, h);

            double scale = (double)GifMaxWidth / w;
            return (GifMaxWidth, Math.Max(1, (int)Math.Round(h * scale)));
        }

        /// <summary>한 장 찍어 파일로 흘려 넣는다. 계속 찍어도 되면 true.</summary>
        private bool CaptureOne()
        {
            if (_stopped || (_gif == null && _mp4 == null)) return false;

            try
            {
                TimeSpan at = _clock.Elapsed;

                if (_mp4 != null)
                {
                    // BitmapSource 를 안 거친다. 1080p 한 장이 8MB 라 프레임마다 새로 만들면
                    // 그 할당·복사만으로 초당 장수가 깎였다.
                    _pixels ??= new byte[checked(_region.Width * _region.Height * 4)];
                    if (!ScreenCapture.CaptureRectInto(_region, _includeCursor, _pixels))
                        throw new InvalidOperationException("화면 복사에 실패했습니다.");
                    _mp4.AddBgra(_pixels, _region.Width, _region.Height, at);
                    PumpAudio(at);
                }
                else
                {
                    BitmapSource shot = ScreenCapture.CaptureRect(_region, _includeCursor);
                    _gif!.Add(shot, GifDelayFor(at));
                }

                _lastFrameAt = at;
                _frames++;

                // 화면에 보여 주는 일은 화면 스레드 몫이다. 여기서 직접 만지면 안 된다.
                // 밀렸으면 그냥 거른다 — 시간 표시 한 번 건너뛰는 것보다 찍는 게 우선이다.
                Post(() => Tick?.Invoke());
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("녹화 중단: " + ex.Message);
                Post(() => Failed?.Invoke(ex.Message));
                return false;
            }
        }

        private void Post(Action action)
        {
            try { _ui.BeginInvoke(action, DispatcherPriority.Background); }
            catch { /* 창이 이미 닫히는 중일 수 있다 */ }
        }

        public void Dispose() => Stop();
    }
}
