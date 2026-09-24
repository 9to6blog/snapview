using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using SnapView.Capture;
using SnapView.Editor;
using SnapView.Native;
using SnapView.Prefs;
using SnapView.Viewer;
using Forms = System.Windows.Forms;
using static SnapView.Native.NativeMethods;

namespace SnapView.Core
{
    /// <summary>트레이 아이콘 · 전역 단축키 · 캡처 파이프라인을 묶는 상주 컨트롤러.</summary>
    internal sealed class AppController : IDisposable
    {
        private const int HK_REGION = 9001;
        private const int HK_FULLSCREEN = 9002;
        private const int HK_WINDOW = 9003;
        private const int HK_RECORD = 9004;
        private const int HK_RECORD_FULL = 9005;
        private const int HK_RECORD_WINDOW = 9006;

        private Settings _settings = new();
        private MessageWindow? _msgWindow;
        private KeyboardHook? _hook;
        private ForegroundWatcher? _foreground;
        private ScreenRecorder? _recorder;
        private RecorderBar? _recorderBar;
        private RecordingFrame? _recorderFrame;
        private Forms.NotifyIcon? _tray;
        /// <summary>
        /// 열려 있는 뷰어 창들. 하나만 두지 않는 이유는 설정에서 "새 창으로 열기" 를
        /// 고를 수 있기 때문이다 — 영상 둘을 나란히 놓고 비교할 때 필요하다.
        /// 맨 뒤가 가장 최근에 쓴 창이다.
        /// </summary>
        private readonly List<ViewerWindow> _viewers = new();

        /// <summary>가장 최근에 쓴 창. 종류를 가리지 않는다.</summary>
        private ViewerWindow? _viewer => _viewers.Count > 0 ? _viewers[^1] : null;

        /// <summary>이 종류로 가장 최근에 쓴 창. 없으면 null.</summary>
        private ViewerWindow? LastOf(ViewerRole role)
        {
            for (int i = _viewers.Count - 1; i >= 0; i--)
                if (_viewers[i].Role == role) return _viewers[i];
            return null;
        }
        private EditorWindow? _editor;
        private SettingsWindow? _prefs;
        private OverlayWindow? _overlay;
        private string? _lastSavedPath;

        // ===================================================== 시작

        internal void Start(LaunchRequest initialRequest)
        {
            _settings = Settings.Load();
            _settings.ApplyStartupRegistration();

            _msgWindow = new MessageWindow();
            _msgWindow.HotKeyPressed += OnHotKey;
            _msgWindow.RequestReceived += OnRequestFromOtherInstance;

            _hook = new KeyboardHook();
            _hook.HotKeyPressed += OnHotKey;

            // 단축키를 누르는 순간에는 이미 우리 창이 앞일 수 있다. 그 전에 뭐가 있었는지 봐 둔다.
            _foreground = new ForegroundWatcher();
            _foreground.ForeignChanged += OnForeignForeground;

            // 모니터가 붙었다 떨어지면 열려 있던 창이 사라진 좌표에 남는다.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // 잠금·절전·로그오프 때는 녹화를 살려 둘 수 없다(잠금 화면은 검게 찍히고, 절전은
            // 파일을 닫을 기회 자체를 안 준다). 그 전에 저장하고 멈춘다.
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

            BuildTray();
            RegisterHotKeys(announceFailures: true);

            // 예전 판으로 등록해 둔 사람은 동영상 확장자가 빠져 있다. 조용히 채워 준다.
            FileAssociation.RefreshIfStale();

            // 시스템 글꼴 열거는 첫 회가 느리다(수백 ms 도 나온다). 편집기를 처음 열 때
            // 그 값을 치르지 않도록 한가할 때 미리 데워 둔다. FontFamily 는 불변이라
            // 백그라운드에서 만들어도 안전하다.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { EditorWindow.GetFontList(); } catch { }
            });

            if (initialRequest.HasFile)
                Open(initialRequest);
        }

        /// <summary>
        /// 화면 구성이 바뀌었다. 열려 있는 창이 화면 밖으로 밀려났으면 되돌린다.
        /// (SystemEvents 는 별도 스레드에서 부르므로 UI 스레드로 넘긴다)
        /// </summary>
        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            // 찍던 모니터가 사라지면 그 자리는 검게 찍힌다. 알아채기 전에 저장하고 멈춘다.
            SaveRecordingBecause("모니터 구성이 바뀌어");

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                int moved = 0;
                foreach (ViewerWindow v in _viewers.ToArray())
                    if (Native.WindowPlacement.EnsureOnScreen(v)) moved++;
                if (_editor != null && Native.WindowPlacement.EnsureOnScreen(_editor)) moved++;
                if (_prefs != null && Native.WindowPlacement.EnsureOnScreen(_prefs)) moved++;
                if (moved > 0) Log.Write($"화면 구성이 바뀌어 창 {moved}개를 되돌림");
            }));
        }

        private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case Microsoft.Win32.SessionSwitchReason.SessionLock:
                case Microsoft.Win32.SessionSwitchReason.SessionLogoff:
                case Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect:
                case Microsoft.Win32.SessionSwitchReason.RemoteDisconnect:
                    SaveRecordingBecause("화면이 잠겨서");
                    break;
            }
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) SaveRecordingBecause("절전에 들어가서", now: true);
        }

        private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
            => SaveRecordingBecause("윈도우가 끝나서", now: true);

        /// <summary>
        /// 녹화 중이면 저장하고 멈춘다. SystemEvents 는 다른 스레드에서 오므로 UI 스레드로 넘긴다.
        /// <paramref name="now"/> 면 그 자리에서 끝낸다 — 절전·종료는 우리를 기다려 주지 않는다.
        /// </summary>
        private void SaveRecordingBecause(string why, bool now = false)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            void Work()
            {
                if (_countdown != null) { CancelCountdown(); return; }
                if (_recorder == null || _stopping) return;

                Log.Write(why + " 녹화를 저장하고 멈춥니다.");
                Notify("녹화를 저장합니다", why + " 녹화를 멈추고 저장합니다.", Forms.ToolTipIcon.Info);
                if (now) StopRecordingNow(save: true);
                else StopRecording(save: true);
            }

            if (dispatcher.CheckAccess()) { Work(); return; }
            if (now) { try { dispatcher.Invoke(Work, TimeSpan.FromSeconds(30)); } catch { } }
            else dispatcher.BeginInvoke(new Action(Work));
        }

        /// <summary>앱이 죽기 직전. 녹화 파일만이라도 닫아 둔다 — 마무리 안 된 MP4 는 못 연다.</summary>
        internal void EmergencyStop()
        {
            try { SaveRecordingBecause("오류가 나서", now: true); } catch { }
        }

        private bool _warnedElevated;

        /// <summary>
        /// 관리자 권한 창이 앞에 나왔다. 그 창이 활성인 동안에는 우리(일반 권한)가 등록한
        /// 전역 단축키가 눌리지 않는다 — 게임·런처가 관리자로 돌면 "게임 안에서만 단축키가
        /// 안 먹는" 증상이 된다. 등록은 성공하므로 다른 방법으로는 알 수 없고, 여기서
        /// 한 번만 알려 준다.
        /// </summary>
        private void OnForeignForeground(IntPtr hwnd)
        {
            if (_warnedElevated || Elevation.IsSelfElevated) return;
            if (Elevation.IsWindowElevated(hwnd) != true) return;

            _warnedElevated = true;
            Log.Write("관리자 권한 창이 활성: " + ForegroundWatcher.Describe(hwnd) +
                      " — 이 창이 앞에 있는 동안 전역 단축키가 전달되지 않음");
            Notify("이 프로그램 안에서는 단축키가 안 눌립니다",
                   "앞의 프로그램이 관리자 권한으로 실행 중이라, 그 창이 활성인 동안에는\n" +
                   "캡처 단축키가 SnapView 까지 오지 않습니다.\n" +
                   "트레이 메뉴 → \"관리자 권한으로 다시 시작\" 을 누르면 해결됩니다.",
                   Forms.ToolTipIcon.Warning);
        }

        /// <summary>
        /// 관리자 권한으로 다시 시작한다. 새 인스턴스는 <c>--wait-pid</c> 로 우리가
        /// 단축키·단일 인스턴스 뮤텍스를 놓을 때까지 기다렸다가 정상 시작한다.
        /// </summary>
        private void RestartElevated()
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;

            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--wait-pid " + Environment.ProcessId
                });
            }
            catch { return; }   // UAC 를 취소했다 — 그대로 계속 산다.

            _settings.Save();
            System.Windows.Application.Current.Shutdown();
        }

        private void OnRequestFromOtherInstance(string payload)
        {
            LaunchRequest request = LaunchRequest.FromIpcPayload(payload);
            if (request.HasFile && File.Exists(request.FilePath)) Open(request);
            else _viewer?.BringToFront();
        }

        private void Open(LaunchRequest request)
        {
            if (request.OpensEditor) OpenFileInEditor(request.FilePath);
            else OpenInViewer(request.FilePath);
        }

        // ===================================================== 트레이

        private void BuildTray()
        {
            _tray = new Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "SnapView — 캡처 & 이미지 뷰어",
                Visible = true
            };
            _tray.DoubleClick += (_, _) => CaptureRegion();
            _tray.BalloonTipClicked += (_, _) =>
            {
                if (!string.IsNullOrEmpty(_lastSavedPath) && File.Exists(_lastSavedPath))
                    OpenInViewer(_lastSavedPath);
            };
            RebuildTrayMenu();
        }

        private void RebuildTrayMenu()
        {
            if (_tray == null) return;

            var menu = new Forms.ContextMenuStrip { Font = new System.Drawing.Font("Segoe UI", 9f) };

            menu.Items.Add(Item("영역 캡처\t" + Show(_settings.HotKeyRegion), (_, _) => CaptureRegion()));
            menu.Items.Add(Item("전체 화면\t" + Show(_settings.HotKeyFullScreen), (_, _) => CaptureFullScreen()));
            menu.Items.Add(Item("현재 모니터", (_, _) => CaptureCurrentMonitor()));
            menu.Items.Add(Item("활성 창\t" + Show(_settings.HotKeyActiveWindow), (_, _) => CaptureActiveWindow()));
            menu.Items.Add(new Forms.ToolStripSeparator());

            // 녹화는 무엇을 찍을지가 여러 가지라 한 칸에 다 못 넣는다. 하위 메뉴로 묶는다.
            // 찍는 중일 때는 하위 메뉴를 걷어내고 멈추기·버리기만 남긴다 — 그때 필요한 건 그것뿐이다.
            if (_recorder == null)
            {
                var record = new Forms.ToolStripMenuItem("녹화");
                record.DropDownItems.Add(Item("영역 고르기...\t" + Show(_settings.HotKeyRecord),
                                              (_, _) => ToggleRecording(RecordTarget.Region)));
                record.DropDownItems.Add(Item("전체 화면\t" + Show(_settings.HotKeyRecordFullScreen),
                                              (_, _) => ToggleRecording(RecordTarget.FullScreen)));
                record.DropDownItems.Add(Item("현재 모니터",
                                              (_, _) => ToggleRecording(RecordTarget.Monitor)));
                record.DropDownItems.Add(Item("활성 창\t" + Show(_settings.HotKeyRecordWindow),
                                              (_, _) => ToggleRecording(RecordTarget.ActiveWindow)));
                menu.Items.Add(record);
            }
            else
            {
                menu.Items.Add(Item("녹화 멈추기\t" + Show(_settings.HotKeyRecord),
                                    (_, _) => StopRecording(save: true)));
                menu.Items.Add(Item(_recorder.IsPaused ? "다시 찍기" : "잠깐 쉬기", (_, _) => TogglePause()));
                menu.Items.Add(Item("녹화 버리기", (_, _) => StopRecording(save: false)));
            }

            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(Item("이미지 열기...", (_, _) => BrowseAndOpen()));
            menu.Items.Add(Item("클립보드 이미지 편집", (_, _) => EditClipboardImage()));
            menu.Items.Add(Item("저장 폴더 열기", (_, _) => OpenSaveFolder()));
            menu.Items.Add(new Forms.ToolStripSeparator());

            // 자주 바꾸는 것만 트레이에 남기고 나머지는 설정 창으로
            menu.Items.Add(Check("파일로 저장", _settings.SaveToDisk, v => _settings.SaveToDisk = v));
            menu.Items.Add(Check("클립보드에 복사", _settings.CopyToClipboard, v => _settings.CopyToClipboard = v));
            menu.Items.Add(Check("캡처 후 편집기 열기", _settings.OpenEditorAfterCapture,
                                 v => _settings.OpenEditorAfterCapture = v));
            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(Item("설정...", (_, _) => OpenSettings()));
            menu.Items.Add(Item("윈도우 이미지 뷰어로 연결...", (_, _) => AssociateImages()));

            // 관리자 권한 게임·프로그램 안에서도 단축키가 먹게 하는 길.
            // 이미 관리자면 필요 없는 항목이라 아예 안 보여 준다.
            if (!Elevation.IsSelfElevated)
                menu.Items.Add(Item("관리자 권한으로 다시 시작", (_, _) => RestartElevated()));
            menu.Items.Add(new Forms.ToolStripSeparator());

            var brand = new Forms.ToolStripMenuItem("9to6blog  9to6blog.com")
            {
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold)
            };
            brand.Click += (_, _) => OpenBrandSite();
            menu.Items.Add(brand);

            menu.Items.Add(Item("종료", (_, _) => ExitApp()));

            Forms.ContextMenuStrip? old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = menu;
            old?.Dispose();
        }

        private static string Show(string hotkey) => string.IsNullOrEmpty(hotkey) ? "" : hotkey;

        private static Forms.ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            var mi = new Forms.ToolStripMenuItem(text);
            mi.Click += onClick;
            return mi;
        }

        private Forms.ToolStripMenuItem Check(string text, bool state, Action<bool> apply)
        {
            var mi = new Forms.ToolStripMenuItem(text) { Checked = state, CheckOnClick = true };
            mi.CheckedChanged += (s, _) =>
            {
                apply(((Forms.ToolStripMenuItem)s!).Checked);
                _settings.Save();
            };
            return mi;
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/assets/app.ico"));
                if (info != null)
                {
                    using Stream s = info.Stream;
                    return new System.Drawing.Icon(s, 32, 32);
                }
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        // ===================================================== 단축키

        /// <summary>
        /// 단축키를 건다. 두 가지 방식을 섞어 쓴다.
        ///
        ///   · 보통 키    — RegisterHotKey. 가볍고 얌전하다.
        ///   · PrintScreen — 저수준 키보드 훅. RegisterHotKey 로는 등록이 "성공" 해도
        ///                   윈도우 11 의 캡처 도구가 입력 단계에서 먼저 먹어 버려
        ///                   WM_HOTKEY 가 아예 오지 않는다. 훅은 그보다 앞단이라 확실하다.
        ///   · 등록 실패   — 다른 프로그램이 쥐고 있어 RegisterHotKey 가 거절한 키도
        ///                   훅으로 넘겨 살린다. "지정했는데 아무 일도 안 남" 은 없다.
        /// </summary>
        private void RegisterHotKeys(bool announceFailures)
        {
            if (_msgWindow == null) return;
            _msgWindow.UnregisterAll();

            var hooked = new List<KeyboardHook.Binding>();
            var stolen = new List<string>();
            var bad = new List<string>();

            Bind(HK_REGION, _settings.HotKeyRegion, "영역 캡처", hooked, stolen, bad);
            Bind(HK_FULLSCREEN, _settings.HotKeyFullScreen, "전체 화면", hooked, stolen, bad);
            Bind(HK_WINDOW, _settings.HotKeyActiveWindow, "활성 창", hooked, stolen, bad);
            Bind(HK_RECORD, _settings.HotKeyRecord, "영역 녹화", hooked, stolen, bad);
            Bind(HK_RECORD_FULL, _settings.HotKeyRecordFullScreen, "전체 화면 녹화", hooked, stolen, bad);
            Bind(HK_RECORD_WINDOW, _settings.HotKeyRecordWindow, "활성 창 녹화", hooked, stolen, bad);

            _hook?.SetBindings(hooked);

            if (!announceFailures) return;

            // 훅을 걸어야 하는데 못 걸었다면 그 키들은 정말로 안 먹는다.
            if (hooked.Count > 0 && _hook?.IsInstalled != true)
            {
                Notify("단축키를 걸지 못했습니다",
                       "키보드 훅을 설치하지 못했습니다.\n" +
                       "보안 프로그램이 막고 있을 수 있습니다. 다른 키로 바꿔 보세요.",
                       Forms.ToolTipIcon.Warning);
            }

            if (stolen.Count > 0)
            {
                Notify("단축키를 넘겨받았습니다",
                       "다른 프로그램이 쓰고 있던 키라 가로채서 씁니다:\n" +
                       string.Join(", ", stolen) +
                       "\n그 프로그램에서 필요하면 설정에서 다른 키로 바꿔 주세요.",
                       Forms.ToolTipIcon.Info);
            }

            if (bad.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("쓸 수 없는 키라 건너뛰었습니다:\n");
                sb.Append(string.Join(", ", bad));
                sb.Append("\n설정에서 다른 키로 바꿀 수 있습니다.");
                Notify("단축키 지정 실패", sb.ToString(), Forms.ToolTipIcon.Warning);
            }
        }

        private void Bind(int id, string text, string label,
                          List<KeyboardHook.Binding> hooked,
                          List<string> stolen, List<string> bad)
        {
            if (string.IsNullOrWhiteSpace(text)) return;   // 일부러 비워 둔 것은 실패가 아니다

            HotKeySpec? spec = HotKeySpec.Parse(text);
            if (spec == null) { bad.Add($"{label}({text})"); return; }

            // PrintScreen 은 처음부터 훅으로 간다. RegisterHotKey 로 같이 잡아 둬 봐야
            // 훅이 키를 삼켜서 WM_HOTKEY 는 어차피 안 온다.
            if (spec.VirtualKey == VK_SNAPSHOT)
            {
                hooked.Add(new KeyboardHook.Binding(id, spec.Modifiers, spec.VirtualKey));
                return;
            }

            if (_msgWindow!.TryRegisterHotKey(id, spec.Modifiers, spec.VirtualKey)) return;

            // 남이 쥐고 있다 — 훅으로 넘겨서라도 살린다.
            hooked.Add(new KeyboardHook.Binding(id, spec.Modifiers, spec.VirtualKey));
            stolen.Add($"{label}({text})");
        }

        private void OnHotKey(int id)
        {
            switch (id)
            {
                case HK_REGION: CaptureRegion(); break;
                case HK_FULLSCREEN: CaptureFullScreen(); break;
                case HK_WINDOW: CaptureActiveWindow(); break;
                case HK_RECORD: ToggleRecording(); break;
                case HK_RECORD_FULL: ToggleRecording(RecordTarget.FullScreen); break;
                case HK_RECORD_WINDOW: ToggleRecording(RecordTarget.ActiveWindow); break;
            }
        }

        // ===================================================== 녹화

        /// <summary>무엇을 찍을지.</summary>
        private enum RecordTarget
        {
            /// <summary>오버레이로 직접 고른다.</summary>
            Region,
            /// <summary>가상 화면 전체(모니터 여러 대면 다 합쳐서).</summary>
            FullScreen,
            /// <summary>커서가 올라가 있는 모니터 하나.</summary>
            Monitor,
            /// <summary>지금 앞에 있는 창.</summary>
            ActiveWindow
        }

        /// <summary>
        /// 녹화를 켜고 끈다.
        ///
        /// 이미 찍고 있으면 <b>어느 키를 눌러도 멈춘다</b>. 무엇으로 시작했는지 기억해 뒀다가
        /// 같은 키를 찾아 눌러야 한다면 그게 더 이상하다.
        /// </summary>
        private void ToggleRecording(RecordTarget target = RecordTarget.Region)
        {
            if (_countdown != null) { CancelCountdown(); return; }
            if (_recorder != null) { StopRecording(save: true); return; }

            switch (target)
            {
                case RecordTarget.Region: StartRecording(); break;
                case RecordTarget.FullScreen: StartRecordingAt(VirtualScreenRegion(), "전체 화면"); break;
                case RecordTarget.Monitor: StartRecordingAt(MonitorRegion(), "모니터"); break;
                case RecordTarget.ActiveWindow: StartRecordingActiveWindow(); break;
            }
        }

        private static Int32Rect VirtualScreenRegion()
        {
            Int32Rect v = ScreenCapture.VirtualScreen();
            return new Int32Rect(v.X, v.Y, v.Width, v.Height);
        }

        private static Int32Rect MonitorRegion()
        {
            System.Drawing.Rectangle b = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
            return new Int32Rect(b.X, b.Y, b.Width, b.Height);
        }

        /// <summary>
        /// 지금 앞에 있는 창의 자리를 찍는다.
        ///
        /// <b>자리를 잡아 두고 그 자리를 찍는다</b> — 창을 따라다니지 않는다. 녹화 중에 창을
        /// 옮기면 옮긴 만큼 화면 밖이 찍힌다. 따라다니게 만들면 창을 옮길 때마다 영상이
        /// 덜컥거려서 오히려 못 쓴다. 창을 옮길 생각이면 영역 녹화를 쓰는 편이 낫다.
        /// </summary>
        private void StartRecordingActiveWindow()
        {
            IntPtr hwnd = _foreground?.Target() ?? NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Notify("녹화 실패", "찍을 창을 찾지 못했습니다.\n" +
                                 "다른 창을 한 번 누른 뒤 다시 시도해 보세요.",
                       Forms.ToolTipIcon.Warning);
                return;
            }

            Int32Rect? rect = ScreenCapture.WindowRect(hwnd);
            if (rect == null)
            {
                Notify("녹화 실패", "창 영역을 구하지 못했습니다.", Forms.ToolTipIcon.Warning);
                return;
            }

            StartRecordingAt(rect.Value, ForegroundWatcher.Describe(hwnd));
        }

        /// <summary>
        /// 찍을 영역을 고르고 녹화를 시작한다.
        /// 영역 고르기는 캡처와 똑같은 오버레이를 쓴다 — 새로 배울 게 없어야 한다.
        /// </summary>
        private void StartRecording()
        {
            if (_overlay != null) return;

            Int32Rect region;
            try
            {
                BitmapSource frozen = ScreenCapture.CaptureVirtualScreen(includeCursor: false);
                Int32Rect virt = ScreenCapture.VirtualScreen();

                _overlay = new OverlayWindow(frozen, virt, adjustBeforeCapture: true,
                                             _settings.ShowCrosshair, OverlayPurpose.Record,
                                             boundarySnap: _settings.CaptureBoundarySnap, boundarySnapChanged: SaveCaptureBoundarySnap);
                _overlay.ShowDialog();
                OverlayResult? picked = _overlay.Result;
                _overlay = null;

                if (picked == null) return;

                // 오버레이는 얼린 그림 기준 좌표를 준다. 실제 화면 좌표로 옮긴다.
                region = new Int32Rect(virt.X + picked.Region.X, virt.Y + picked.Region.Y,
                                       picked.Region.Width, picked.Region.Height);
            }
            catch (Exception ex)
            {
                _overlay = null;
                Notify("녹화 실패", ex.Message, Forms.ToolTipIcon.Error);
                return;
            }

            StartRecordingAt(region, "영역");
        }

        /// <summary>
        /// 영역이 정해진 뒤부터는 어떻게 골랐든 똑같다.
        /// 영역 녹화·전체 화면·모니터·활성 창이 모두 여기로 모인다.
        /// </summary>
        private void StartRecordingAt(Int32Rect region, string what)
        {
            if (_recorder != null || _stopping || _countdown != null) return;

            // 영역을 고르는 중에 다른 녹화 키가 눌리면, 화면을 덮은 정지 화면(오버레이)을
            // 찍기 시작한다. 그 뒤 고른 영역은 조용히 무시된다. 고르는 중에는 받지 않는다.
            if (_overlay != null) return;

            if (_settings.RecordCountdownSeconds <= 0) { BeginRecordingAt(region, what); return; }

            _countdown = new CountdownWindow(region, _settings.RecordCountdownSeconds);
            _countdown.Finished += () => { _countdown = null; BeginRecordingAt(region, what); };
            _countdown.Show();
        }

        private CountdownWindow? _countdown;

        /// <summary>세는 중에 녹화 키를 또 누르면 그만둔다.</summary>
        private void CancelCountdown()
        {
            CountdownWindow? c = _countdown;
            _countdown = null;
            c?.Cancel();
            Notify("녹화 취소", "시작하기 전에 그만뒀습니다.", Forms.ToolTipIcon.Info);
        }

        /// <summary>잠깐 쉬기 / 다시 찍기.</summary>
        private void TogglePause()
        {
            if (_recorder == null || _stopping) return;

            if (_recorder.IsPaused) _recorder.Resume();
            else _recorder.Pause();

            _recorderBar?.SetPaused(_recorder.IsPaused);
            RebuildTrayMenu();
        }

        /// <summary>카운트다운이 끝났거나 없을 때. 실제로 찍기 시작한다.</summary>
        private void BeginRecordingAt(Int32Rect region, string what)
        {
            if (_recorder != null || _stopping) return;

            if (region.Width < 16 || region.Height < 16)
            {
                Notify("녹화 실패", "영역이 너무 작습니다.", Forms.ToolTipIcon.Warning);
                return;
            }

            try
            {
                string folder = string.IsNullOrWhiteSpace(_settings.SaveFolder)
                    ? Settings.DefaultSaveFolder : _settings.SaveFolder;
                Directory.CreateDirectory(folder);

                bool wantMp4 = !string.Equals(_settings.RecordingFormat, "gif",
                                              StringComparison.OrdinalIgnoreCase);

                string path = Path.Combine(folder,
                    RecordingNames.Build(_settings.RecordNamePattern, DateTime.Now, what) +
                    (wantMp4 ? ".mp4" : ".gif"));

                _recorder = new ScreenRecorder(region, _settings.RecordingFps, path, wantMp4,
                                               _settings.RecordSystemAudio, _settings.RecordCursor);
                _recorder.Failed += reason =>
                {
                    Notify("녹화가 멈췄습니다", reason, Forms.ToolTipIcon.Error);
                    StopRecording(save: true);
                };

                // 어디를 찍고 있는지 화면에 표시한다. 단축키로 시작하면 이게 없으면
                // 엉뚱한 영역을 찍고 있어도 알 수가 없다.
                _recorderFrame = new RecordingFrame(region);
                _recorderFrame.Show();

                _recorderBar = new RecorderBar();
                _recorderBar.Stopped += () => StopRecording(save: true);
                _recorderBar.Cancelled += () => StopRecording(save: false);
                _recorderBar.PauseToggled += TogglePause;
                _recorderBar.Show();
                _recorderBar.PlaceBottomRight(region);

                _recorder.Tick += () => _recorderBar?.Update(_recorder!.Elapsed, _recorder.FrameCount,
                                                             _recorder.ActualFps);

                // 알림과 시작음은 녹화기를 켜기 전에 낸다. 녹화기는 스피커로 나가는 소리를
                // 통째로 담으므로, 켠 뒤에 내면 시작음(과 알림음)이 영상 맨 앞에 그대로 들어간다.
                // 시작음은 다 날 때까지 기다린다(약 0.2초). 그 뒤에야 소리 받기가 시작된다.
                // 단축키로 바로 시작하면 무엇이 찍히는지 알 길이 없어서 알림도 한 번 띄운다.
                Notify("녹화 시작",
                       $"{what} · {region.Width}×{region.Height} · {_settings.RecordingFps}fps\n" +
                       "멈추려면 " + Show(_settings.HotKeyRecord) + " 또는 정지 단추",
                       Forms.ToolTipIcon.Info);
                if (_settings.PlayRecordSound) RecordSound.PlayStart(_settings.RecordSoundVolume);

                _recorder.Start();
                RebuildTrayMenu();
            }
            catch (Exception ex)
            {
                Log.Write("녹화 시작 실패: " + ex.Message);
                Notify("녹화 실패", ex.Message, Forms.ToolTipIcon.Error);
                StopRecording(save: false);
            }
        }

        private bool _stopping;
        private System.Threading.Tasks.Task? _stopTask;

        /// <summary>
        /// 녹화를 멈추고 저장(또는 버리기)한다. 파일 마무리는 <b>뒤에서</b> 한다 —
        /// 찍는 스레드가 멈추길 기다리고 moov 를 쓰는 데 느린 드라이브에서는 몇 초가 걸리는데,
        /// 그동안 화면 전체가 굳어 있으면 "정지를 눌렀는데 반응이 없다" 가 된다.
        /// 막대에는 저장 중이라고 표시하고, 끝나면 알림으로 결과를 알린다.
        /// </summary>
        private void StopRecording(bool save)
        {
            if (_recorder == null || _stopping) return;
            _stopping = true;

            ScreenRecorder recorder = _recorder;
            bool hadAudio = recorder.HasAudio;          // Stop() 이 장치를 놓고 나면 알 수 없다
            bool audioCut = recorder.AudioInterrupted;

            _recorderFrame?.Finish();
            _recorderFrame = null;
            _recorderBar?.ShowSaving();

            if (_settings.PlayRecordSound) RecordSound.PlayStop(_settings.RecordSoundVolume);

            _stopTask = System.Threading.Tasks.Task.Run(() =>
            {
                bool wrote = false;
                string? error = null;
                try { wrote = recorder.Stop(); }
                catch (Exception ex) { error = ex.Message; }

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    FinishStop(recorder, save, wrote, error, hadAudio, audioCut)));
            });
        }

        /// <summary>
        /// 앱이 끝나거나 세션이 닫힐 때. 뒤로 미룰 수 없으니 그 자리에서 끝낸다.
        /// 이미 뒤에서 마무리 중이면 그게 끝나길 기다린다 — 프로세스가 먼저 죽으면 파일이 깨진다.
        /// </summary>
        private void StopRecordingNow(bool save)
        {
            if (_stopping)
            {
                try { _stopTask?.Wait(TimeSpan.FromSeconds(30)); } catch { }
                return;
            }
            if (_recorder == null) return;
            _stopping = true;

            ScreenRecorder recorder = _recorder;
            bool hadAudio = recorder.HasAudio;
            bool audioCut = recorder.AudioInterrupted;
            _recorderFrame?.Finish();
            _recorderFrame = null;

            bool wrote = false;
            string? error = null;
            try { wrote = recorder.Stop(); }
            catch (Exception ex) { error = ex.Message; }
            FinishStop(recorder, save, wrote, error, hadAudio, audioCut);
        }

        private void FinishStop(ScreenRecorder recorder, bool save, bool wrote, string? error,
                                bool hadAudio, bool audioCut)
        {
            string path = recorder.Path;
            int frames = recorder.FrameCount;
            TimeSpan length = recorder.Elapsed;
            string? reason = recorder.LastError ?? error;
            try { recorder.Dispose(); } catch { }

            if (ReferenceEquals(_recorder, recorder)) _recorder = null;
            _stopping = false;
            _stopTask = null;

            _recorderBar?.Close();
            _recorderBar = null;
            RebuildTrayMenu();

            if (!save)
            {
                bool recycled = Discard(path);
                Notify("녹화 취소", recycled ? "저장하지 않았습니다. 파일은 휴지통에 있습니다."
                                          : "저장하지 않았습니다.", Forms.ToolTipIcon.Info);
                return;
            }

            if (!wrote)
            {
                Notify("녹화 실패", reason ?? "한 장도 담기지 않았습니다.", Forms.ToolTipIcon.Warning);
                return;
            }

            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception ex)
            {
                // 녹화 중에 저장 폴더가 사라졌다(USB 를 뽑았다든지). 예전엔 여기서 앱이 죽었다.
                Log.Write("녹화 파일을 못 찾습니다: " + ex.Message);
                Notify("저장 실패", "녹화 파일이 사라졌습니다.\n" + path, Forms.ToolTipIcon.Error);
                return;
            }

            _lastSavedPath = path;

            // 고른 값이 아니라 실제로 담긴 초당 장수를 적는다. 60 으로 찍었는데 22 밖에
            // 안 담겼으면 그 사실을 알아야 다음에 영역을 줄이든 값을 낮추든 할 수 있다.
            double avgFps = length.TotalSeconds > 0.2 ? frames / length.TotalSeconds : 0;

            string audioNote = !hadAudio ? "" : audioCut ? " · 소리 포함(중간에 끊김)" : " · 소리 포함";
            Notify("녹화 완료",
                   $"{(int)length.TotalSeconds}초 · {frames}장" +
                   (avgFps > 0 ? $" ({avgFps:0.#}fps)" : "") +
                   $" · {size / 1024 / 1024.0:0.0}MB" + audioNote + "\n" +
                   Path.GetFileName(path) + " (눌러서 열기)",
                   Forms.ToolTipIcon.Info);
        }

        /// <summary>버리는 녹화물은 휴지통으로. 긴 녹화를 실수로 버렸을 때 되찾을 길은 남긴다. 휴지통에 갔으면 true.</summary>
        private static bool Discard(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                if (ImageIO.RecycleFile(path)) return true;
                File.Delete(path);
            }
            catch { }
            return false;
        }

        // ===================================================== 캡처

        private void SaveCaptureBoundarySnap(bool enabled)
        {
            _settings.CaptureBoundarySnap = enabled;
            _settings.Save();
        }

        private void CaptureRegion()
        {
            if (_overlay != null) return;   // 이미 선택 중

            try
            {
                // 먼저 화면을 얼려 두고 그 위에서 고른다. 이래야 선택하는 동안
                // 화면이 바뀌지 않고, 하드웨어 가속 창도 그대로 남는다.
                BitmapSource frozen = ScreenCapture.CaptureVirtualScreen(includeCursor: false);
                Int32Rect virt = ScreenCapture.VirtualScreen();

                _overlay = new OverlayWindow(frozen, virt, _settings.AdjustBeforeCapture,
                                             _settings.ShowCrosshair,
                                             OverlayPurpose.Capture, ConfirmHint(),
                                             boundarySnap: _settings.CaptureBoundarySnap, boundarySnapChanged: SaveCaptureBoundarySnap);
                _overlay.ShowDialog();
                OverlayResult? result = _overlay.Result;
                _overlay = null;

                if (result == null) return;

                BitmapSource? image = ResolveSelection(frozen, result);
                if (image == null) return;

                switch (result.Action)
                {
                    case OverlayAction.Edit: OpenEditor(image, "영역"); break;
                    case OverlayAction.CopyOnly: CopyOnly(image); break;
                    case OverlayAction.SaveOnly: SaveOnly(image); break;
                    default: Deliver(image, "영역", viaEditor: false); break;
                }
            }
            catch (Exception ex)
            {
                _overlay = null;
                Notify("캡처 실패", ex.Message, Forms.ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// "확인" 을 누르면 실제로 무슨 일이 일어나는지 한 줄로.
        ///
        /// "설정대로 처리" 라고만 하면 옆에 있는 "저장" 과 무엇이 다른지 알 수가 없다.
        /// 지금 설정으로 무엇을 하는지 그대로 적어 준다.
        /// </summary>
        private string ConfirmHint()
        {
            var does = new List<string>();
            if (_settings.SaveToDisk) does.Add("파일로 저장");
            if (_settings.CopyToClipboard) does.Add("클립보드에 복사");
            if (_settings.OpenEditorAfterCapture) does.Add("편집기 열기");
            else if (_settings.OpenViewerAfterCapture) does.Add("뷰어 열기");

            return does.Count == 0
                ? "설정에 아무것도 안 켜져 있습니다 — 아무 일도 일어나지 않습니다"
                : string.Join(" + ", does);
        }

        /// <summary>
        /// 영역 캡처는 항상 사용자가 본 얼린 화면의 선택 영역을 그대로 잘라낸다.
        /// 별도의 활성 창 캡처만 CaptureWindowSmart를 사용한다.
        /// </summary>
        private BitmapSource? ResolveSelection(BitmapSource frozen, OverlayResult result)
        {
            var crop = new CroppedBitmap(frozen, result.Region);
            crop.Freeze();
            return crop;
        }

        private void CaptureFullScreen()
        {
            if (_overlay != null) return;
            try
            {
                // 조절 단계를 쓰는 설정이면 전체 화면도 곧바로 확정하지 않고 전체 영역이
                // 선택된 오버레이를 보여 준다. 이때 툴바의 자석으로 콘텐츠 경계까지 줄일 수 있다.
                if (!_settings.AdjustBeforeCapture)
                {
                    Deliver(ScreenCapture.CaptureVirtualScreen(_settings.IncludeCursor), "전체 화면", false);
                    return;
                }

                BitmapSource frozen = ScreenCapture.CaptureVirtualScreen(_settings.IncludeCursor);
                Int32Rect virt = ScreenCapture.VirtualScreen();
                _overlay = new OverlayWindow(frozen, virt, adjustBeforeCapture: true,
                                             _settings.ShowCrosshair, OverlayPurpose.Capture,
                                             ConfirmHint(), startWithFullSelection: true,
                                             boundarySnap: _settings.CaptureBoundarySnap, boundarySnapChanged: SaveCaptureBoundarySnap);
                _overlay.ShowDialog();
                OverlayResult? result = _overlay.Result;
                _overlay = null;
                if (result == null) return;

                BitmapSource? image = ResolveSelection(frozen, result);
                if (image == null) return;

                switch (result.Action)
                {
                    case OverlayAction.Edit: OpenEditor(image, "전체 화면"); break;
                    case OverlayAction.CopyOnly: CopyOnly(image); break;
                    case OverlayAction.SaveOnly: SaveOnly(image); break;
                    default: Deliver(image, "전체 화면", viaEditor: false); break;
                }
            }
            catch (Exception ex)
            {
                _overlay = null;
                Notify("캡처 실패", ex.Message, Forms.ToolTipIcon.Error);
            }
        }

        private void CaptureCurrentMonitor()
        {
            try
            {
                System.Drawing.Rectangle b = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
                var rect = new Int32Rect(b.X, b.Y, b.Width, b.Height);
                Deliver(ScreenCapture.CaptureRect(rect, _settings.IncludeCursor), "모니터", false);
            }
            catch (Exception ex) { Notify("캡처 실패", ex.Message, Forms.ToolTipIcon.Error); }
        }

        private void CaptureActiveWindow()
        {
            try
            {
                // 앞에 있는 게 우리 창(트레이 메뉴·뷰어·편집기)이면 그 직전 창을 찍는다.
                IntPtr hwnd = _foreground?.Target() ?? NativeMethods.GetForegroundWindow();

                if (hwnd == IntPtr.Zero)
                {
                    Log.Write("활성 창 캡처: 찍을 창을 못 찾음");
                    Notify("캡처 실패", "찍을 창을 찾지 못했습니다.\n" +
                                    "다른 창을 한 번 누른 뒤 다시 시도해 보세요.",
                           Forms.ToolTipIcon.Warning);
                    return;
                }

                BitmapSource? image = ScreenCapture.CaptureWindowSmart(
                    hwnd, _settings.IncludeCursor, _settings.UseGraphicsCapture,
                    out ScreenCapture.WindowCaptureMethod method, out string note);

                if (image == null)
                {
                    Log.Write($"활성 창 캡처 실패: {ForegroundWatcher.Describe(hwnd)} — {note}");
                    Notify("캡처 실패", string.IsNullOrEmpty(note) ? "활성 창을 찍지 못했습니다." : note,
                           Forms.ToolTipIcon.Warning);
                    return;
                }

                string label = method == ScreenCapture.WindowCaptureMethod.ScreenCrop
                    ? "활성 창(화면에서 잘라냄)"
                    : "활성 창";
                Deliver(image, label, viaEditor: false);
            }
            catch (Exception ex) { Notify("캡처 실패", ex.Message, Forms.ToolTipIcon.Error); }
        }

        // ===================================================== 결과 처리

        /// <summary>캡처 결과를 설정대로 처리한다: 편집기 / 클립보드 / 저장 / 뷰어.</summary>
        private void Deliver(BitmapSource image, string kindLabel, bool viaEditor)
        {
            if (_settings.OpenEditorAfterCapture && !viaEditor)
            {
                Shutter();
                OpenEditor(image, kindLabel);
                return;
            }

            string? savedPath = null;
            bool savesPng = _settings.SaveToDisk &&
                            !_settings.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);

            // 클립보드와 저장이 둘 다 켜져 있으면 같은 이미지를 각자 PNG 로 인코딩했다.
            // 한 번만 인코딩해서 나눠 쓴다 — 저사양에서 확정 후 멈춤이 절반으로 준다.
            byte[]? png = null;
            if (_settings.CopyToClipboard && savesPng)
            {
                try { png = ImageIO.EncodePng(image); } catch { }
            }

            if (_settings.CopyToClipboard) ImageIO.CopyToClipboard(image, png);

            if (_settings.SaveToDisk)
            {
                try
                {
                    savedPath = ImageIO.SaveAuto(image, _settings, DateTime.Now, png);
                    _lastSavedPath = savedPath;
                }
                catch (Exception ex) { Notify("저장 실패", ex.Message, Forms.ToolTipIcon.Error); }
            }

            if (!viaEditor) Shutter();

            if (_settings.OpenViewerAfterCapture)
            {
                // PNG 는 무손실이라 방금 그 이미지가 곧 파일 내용이다 — 되읽어 디코딩하지
                // 않고 그대로 넘긴다. JPG 는 파일과 픽셀이 다르므로 파일에서 읽게 둔다.
                if (savedPath != null) OpenInViewer(savedPath, savesPng ? image : null);
                else ShowInViewer(image, null, $"{kindLabel} 캡처");
            }
            else
            {
                // 실제로 일어난 일만 적는다. 복사가 꺼져 있는데 "복사됨" 이라고 하면
                // 클립보드를 열어 본 사람이 헷갈린다.
                string body = savedPath != null
                    ? Path.GetFileName(savedPath) + "\n클릭하면 엽니다."
                    : _settings.CopyToClipboard
                        ? $"{image.PixelWidth}×{image.PixelHeight} 클립보드에 복사됨"
                        : "저장·복사가 모두 꺼져 있어 처리할 일이 없었습니다";
                Notify($"{kindLabel} 캡처 완료", body, Forms.ToolTipIcon.Info);
            }
        }

        private void CopyOnly(BitmapSource image)
        {
            ImageIO.CopyToClipboard(image);
            Shutter();
            Notify("복사 완료", $"{image.PixelWidth}×{image.PixelHeight} 클립보드에 복사됨",
                   Forms.ToolTipIcon.Info);
        }

        private void SaveOnly(BitmapSource image)
        {
            try
            {
                string path = ImageIO.SaveAuto(image, _settings, DateTime.Now);
                _lastSavedPath = path;
                Shutter();
                Notify("저장 완료", Path.GetFileName(path) + "\n클릭하면 엽니다.", Forms.ToolTipIcon.Info);
            }
            catch (Exception ex) { Notify("저장 실패", ex.Message, Forms.ToolTipIcon.Error); }
        }

        private void Shutter()
        {
            if (!_settings.PlayShutterSound) return;
            CaptureSound.Play(_settings.ShutterVolume);
        }

        // ===================================================== 편집기

        private void OpenEditor(BitmapSource image, string kindLabel)
        {
            if (_editor != null)
            {
                _editor.Close();
                _editor = null;
            }

            var ed = new EditorWindow(image, _settings);
            _editor = ed;
            ed.Closed += (_, _) => { if (ReferenceEquals(_editor, ed)) _editor = null; };
            ed.Completed += result => Deliver(result, kindLabel, viaEditor: true);
            ed.Show();
            Native.WindowPlacement.EnsureOnScreen(ed);
            ed.Activate();
        }

        /// <summary>탐색기의 "SnapView 편집기로 열기" 요청을 뷰어를 거치지 않고 연다.</summary>
        private void OpenFileInEditor(string path)
        {
            try
            {
                BitmapSource image = ImageIO.Load(path);
                OpenEditor(image, Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                Notify("편집기로 열지 못했습니다", ex.Message, Forms.ToolTipIcon.Error);
            }
        }

        private void EditClipboardImage()
        {
            try
            {
                BitmapSource? img = System.Windows.Clipboard.ContainsImage()
                    ? System.Windows.Clipboard.GetImage() : null;
                if (img == null)
                {
                    Notify("편집할 그림이 없습니다", "클립보드에 이미지가 없습니다.", Forms.ToolTipIcon.Info);
                    return;
                }
                if (!img.IsFrozen) img.Freeze();
                OpenEditor(img, "클립보드");
            }
            catch (Exception ex) { Notify("클립보드 읽기 실패", ex.Message, Forms.ToolTipIcon.Error); }
        }

        // ===================================================== 뷰어

        private ViewerWindow EnsureViewer(ViewerRole role) => LastOf(role) ?? NewViewer(role);

        /// <summary>
        /// 창을 하나 만든다. 창이 닫히면 목록에서 빠지고, 앞으로 나오면 목록 맨 뒤로
        /// 간다 — "가장 최근에 쓴 창" 이 곧 갈아 끼울 대상이다.
        /// </summary>
        private ViewerWindow NewViewer(ViewerRole role)
        {
            var v = new ViewerWindow(_settings);
            v.SetRole(role);
            v.Closed += (_, _) => _viewers.Remove(v);
            v.Activated += (_, _) =>
            {
                if (_viewers.Remove(v)) _viewers.Add(v);
            };
            v.EditRequested += (img, label) => OpenEditor(img, label);

            // 그림 창에 영상을 떨구거나 그 반대일 때. 창을 갈아엎지 않고 맞는 창으로 보낸다.
            v.OpenElsewhere += OpenInViewer;

            _viewers.Add(v);
            return v;
        }

        /// <summary>
        /// 파일을 연다. <b>그림은 그림 창, 영상은 재생 창</b>으로 간다.
        ///
        /// 둘을 한 창에서 돌려쓰면 영상을 열 때 보던 그림이 사라지고 그림을 열 때 보던
        /// 영상이 멎는다. 종류마다 제 창을 갖는다.
        ///
        /// "새 창으로 열기" 설정은 <b>영상에만</b> 적용한다 — 폴더의 사진을 훑을 때마다
        /// 창이 하나씩 쌓이면 못 쓴다. 사진은 언제나 보던 창에 갈아 끼운다.
        /// </summary>
        internal void OpenInViewer(string path) => OpenInViewer(path, null);

        /// <param name="preloaded">방금 저장한 그 파일의 이미지(무손실일 때만). 되읽기를 건너뛴다.</param>
        private void OpenInViewer(string path, BitmapSource? preloaded)
        {
            ViewerRole role = ImageIO.IsVideo(path) ? ViewerRole.Video : ViewerRole.Image;

            ViewerWindow v = role == ViewerRole.Video && _settings.OpenInNewWindow
                ? NewViewer(role)
                : EnsureViewer(role);

            v.LoadFile(path, preloaded);
            v.BringToFront();
        }

        private void ShowInViewer(BitmapSource image, string? path, string title)
        {
            ViewerWindow v = EnsureViewer(ViewerRole.Image);
            v.ShowImage(image, path, title);
            v.BringToFront();
        }

        private void BrowseAndOpen()
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
            if (dlg.ShowDialog() == true) OpenInViewer(dlg.FileName);
        }

        // ===================================================== 설정

        /// <summary>
        /// 설정 창을 연다. 모달이 아니다 — 설정을 열어 둔 채로도 캡처가 돼야 한다.
        /// (설정 창 자체를 찍고 싶을 때가 있다)
        ///
        /// 단축키는 창이 떠 있는 내내가 아니라 <b>지정 칸에 키를 눌러 넣는 동안만</b>
        /// 풀어 준다.
        /// </summary>
        private void OpenSettings()
        {
            if (_prefs != null) { _prefs.Activate(); return; }

            var win = new SettingsWindow(_settings);
            _prefs = win;

            win.RecordingChanged += recording =>
            {
                if (recording) SuspendHotKeys();
                else RegisterHotKeys(announceFailures: false);
            };

            win.Closed += (_, _) =>
            {
                _prefs = null;

                if (win.Result != null)
                {
                    // Capture pin can change while this modeless settings window is open.
                    // This dialog has no pin control, so preserve the most recent capture choice.
                    win.Result.CaptureBoundarySnap = _settings.CaptureBoundarySnap;
                    _settings = win.Result;
                    _settings.Save();
                    _settings.ApplyStartupRegistration();
                    foreach (ViewerWindow v in _viewers.ToArray()) v.ApplySettings(_settings);
                    RebuildTrayMenu();
                }

                RegisterHotKeys(announceFailures: true);
            };

            win.Show();
            win.Activate();
        }

        /// <summary>전역 단축키를 잠시 놓는다. RegisterHotKeys 로 되돌린다.</summary>
        private void SuspendHotKeys()
        {
            _msgWindow?.UnregisterAll();
            _hook?.SetBindings(Array.Empty<KeyboardHook.Binding>());
        }

        // ===================================================== 잡다

        private void OpenSaveFolder()
        {
            string folder = string.IsNullOrWhiteSpace(_settings.SaveFolder)
                ? Settings.DefaultSaveFolder : _settings.SaveFolder;
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Notify("폴더 열기 실패", ex.Message, Forms.ToolTipIcon.Error); }
        }

        /// <summary>
        /// 윈도우의 이미지 뷰어 후보로 등록한다.
        /// 윈도우 8 이후로는 기본 앱을 프로그램이 직접 바꿀 수 없어서,
        /// 등록만 하고 마지막 선택은 설정 화면에서 사용자가 하도록 안내한다.
        /// </summary>
        internal void AssociateImages()
        {
            if (FileAssociation.IsRegistered())
            {
                MessageBoxResult keep = System.Windows.MessageBox.Show(
                    "이미 등록되어 있습니다.\n\n" +
                    "[확인] 윈도우 기본 앱 설정 열기\n" +
                    "[취소] 등록 해제",
                    "SnapView", MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (keep == MessageBoxResult.OK) { FileAssociation.OpenDefaultAppsSettings(); return; }

                if (FileAssociation.Unregister(out string removeError))
                    Notify("연결 해제", "이미지 연결 등록을 지웠습니다.", Forms.ToolTipIcon.Info);
                else
                    Notify("연결 해제 실패", removeError, Forms.ToolTipIcon.Error);
                return;
            }

            if (!FileAssociation.Register(out string error))
            {
                System.Windows.MessageBox.Show("등록하지 못했습니다.\n\n" + error, "SnapView",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string[] missing = FileAssociation.MissingAssociations();
            if (missing.Length > 0) Log.Write("파일 연결 일부 실패: " + string.Join(" ", missing));

            MessageBoxResult open = System.Windows.MessageBox.Show(
                (missing.Length == 0
                    ? "SnapView 를 연결 프로그램 목록에 등록했습니다.\n"
                    : "일부 확장자를 등록하지 못했습니다: " + string.Join(" ", missing) + "\n") +
                "이미지: " + string.Join(" ", ImageIO.SupportedExtensions) + "\n" +
                "동영상: " + string.Join(" ", ImageIO.VideoExtensions) + "\n\n" +
                "윈도우 8 이후로는 기본 앱을 프로그램이 마음대로 바꿀 수 없습니다.\n" +
                "설정 화면에서 SnapView 를 직접 골라 주세요.\n\n" +
                "지금 열까요?",
                "SnapView", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (open == MessageBoxResult.OK) FileAssociation.OpenDefaultAppsSettings();
        }

        internal static void OpenBrandSite()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://9to6blog.com") { UseShellExecute = true });
            }
            catch { }
        }

        private void Notify(string title, string text, Forms.ToolTipIcon icon)
        {
            try { _tray?.ShowBalloonTip(3500, title, text, icon); } catch { }
        }

        private void ExitApp()
        {
            _settings.Save();
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.ContextMenuStrip?.Dispose();
                _tray.Dispose();
                _tray = null;
            }
            StopRecordingNow(save: true);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
            _foreground?.Dispose();
            _foreground = null;
            _msgWindow?.Dispose();
            _msgWindow = null;
            _hook?.Dispose();
            _hook = null;
        }
    }
}
