using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapView.Core;

namespace SnapView
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = @"Local\SnapView.SingleInstance.v1";

        private Mutex? _instanceMutex;
        private AppController? _controller;

        /// <summary>뷰어 전용으로 떴을 때 쓰는 창구. 뒤에 열리는 파일을 여기서 받는다.</summary>
        private Native.MessageWindow? _standaloneIpc;
        private Viewer.ViewerWindow? _standaloneViewer;
        private Editor.EditorWindow? _standaloneEditor;
        private Settings? _standaloneSettings;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 개발용: 편집기 창을 그려서 PNG 로 떨군다. 배치를 눈으로 확인하려고 쓴다.
            if (e.Args.Length >= 2 && e.Args[0] == "--shot")
            {
                base.OnStartup(e);
                if (e.Args.Length >= 4 && e.Args[2] == "overlay")
                    Editor.LayoutShot.TakeOverlay(e.Args[1], e.Args[3]);
                else if (e.Args.Length >= 3 && e.Args[2] == "settings")
                    Editor.LayoutShot.TakeSettings(e.Args[1]);
                else if (e.Args.Length >= 4 && e.Args[2] == "viewer")
                    Editor.LayoutShot.TakeViewer(e.Args[1], e.Args[3]);
                else if (e.Args.Length >= 3 && e.Args[2] == "crop")
                    Editor.LayoutShot.Take(e.Args[1], ToolKind.Crop);
                else if (e.Args.Length >= 3 && e.Args[2] == "region")
                    Editor.LayoutShot.Take(e.Args[1], ToolKind.RegionSelect);
                else
                    Editor.LayoutShot.Take(e.Args[1]);
                Shutdown();
                return;
            }

            // "관리자 권한으로 다시 시작" 에서 넘어온 경우: 이전 인스턴스가 전역 단축키와
            // 단일 인스턴스 뮤텍스를 놓을 때까지 기다린다. 안 기다리면 "이미 떠 있음" 으로
            // 판단하고 조용히 꺼져서, 사용자 눈에는 아무 일도 안 일어난 것이 된다.
            int waitIdx = Array.IndexOf(e.Args, "--wait-pid");
            if (waitIdx >= 0 && waitIdx + 1 < e.Args.Length &&
                int.TryParse(e.Args[waitIdx + 1], out int oldPid))
            {
                try
                {
                    using var old = System.Diagnostics.Process.GetProcessById(oldPid);
                    old.WaitForExit(8000);
                }
                catch { }   // 이미 꺼졌다 — 그게 바라던 상태다.
            }

            LaunchRequest request = LaunchRequest.FromArguments(e.Args);

            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // 이미 떠 있으면 그쪽에 넘기고 조용히 빠진다.
                // (전역 단축키는 한 프로세스만 잡을 수 있으므로 중복 실행은 허용하지 않는다.)
                bool delivered = Native.MessageWindow.SendToExistingInstance(request.ToIpcPayload());
                _instanceMutex.Dispose();
                _instanceMutex = null;

                if (!delivered && request.HasFile)
                {
                    // 저쪽이 대답이 없다. 여기서 그냥 사라지면 사용자 눈에는
                    // "더블클릭했는데 아무 일도 안 일어남" 으로 보인다.
                    // 단축키·트레이는 포기하고 뷰어만이라도 띄운다.
                    Log.Write("기존 인스턴스가 응답하지 않아 뷰어 전용으로 시작: " + request);
                    base.OnStartup(e);
                    ShowStandalone(request);
                    return;
                }

                Shutdown();
                return;
            }

            base.OnStartup(e);

            System.Windows.Forms.Application.EnableVisualStyles();
            DispatcherUnhandledException += OnUnhandledException;

            // 화면 스레드 밖에서 터진 예외는 프로세스를 죽인다. 녹화 중이었으면 파일부터 닫는다 —
            // 마무리를 못 한 MP4 는 어느 재생기로도 못 연다.
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                try { _controller?.EmergencyStop(); } catch { }
            };

            _controller = new AppController();
            _controller.Start(request);
        }

        /// <summary>
        /// 상주 인스턴스에 말을 못 붙였을 때 쓰는 최후의 수단.
        /// 트레이도 단축키도 없이 뷰어 창 하나만 띄우고, 그 창이 닫히면 끝난다.
        ///
        /// <b>창구(IPC 창)는 여기서도 연다.</b> 안 열면 이 뒤에 여는 파일마다 또
        /// "대답이 없다" 며 새 프로세스가 뜬다 — 더블클릭 한 번에 창 하나씩 쌓인다.
        /// 실제로 그렇게 쌓였다. 단축키는 안 건드린다(그건 상주 쪽 몫이다).
        /// </summary>
        private void ShowStandalone(LaunchRequest request)
        {
            _standaloneSettings ??= Settings.Load();
            EnsureStandaloneIpc();

            if (request.OpensEditor) ShowStandaloneEditor(request.FilePath);
            else ShowStandaloneViewer(request.FilePath);
        }

        private Viewer.ViewerWindow EnsureStandaloneViewer()
        {
            if (_standaloneViewer != null) return _standaloneViewer;

            var viewer = new Viewer.ViewerWindow(_standaloneSettings ??= Settings.Load());
            _standaloneViewer = viewer;
            viewer.EditRequested += ShowStandaloneEditor;
            viewer.Closed += (_, _) =>
            {
                if (ReferenceEquals(_standaloneViewer, viewer)) _standaloneViewer = null;
                if (_standaloneEditor == null) Shutdown();
            };
            return viewer;
        }

        private void ShowStandaloneViewer(string path)
        {
            try
            {
                var viewer = EnsureStandaloneViewer();
                viewer.SetRole(MediaKinds.IsVideo(path) ? Viewer.ViewerRole.Video
                                                        : Viewer.ViewerRole.Image);
                viewer.LoadFile(path);
                if (!viewer.IsVisible) viewer.Show();
                viewer.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Write("뷰어 전용 시작 실패: " + ex.Message);
                System.Windows.MessageBox.Show(
                    "이미지를 열지 못했습니다.\n\n" + ex.Message,
                    "SnapView", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
            }
        }

        /// <summary>
        /// 관리자 권한의 상주 인스턴스에는 일반 권한 탐색기가 WM_COPYDATA 를 보낼 수 없다.
        /// 그 경우에도 --edit 의 뜻을 잃지 않고 이 작은 독립 프로세스가 편집기를 직접 연다.
        /// 독립 뷰어의 편집 단추도 같은 경로를 쓴다.
        /// </summary>
        private void ShowStandaloneEditor(string path)
        {
            try
            {
                BitmapSource image = ImageIO.Load(path);
                ShowStandaloneEditor(image, Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                Log.Write("편집기 전용 시작 실패: " + ex.Message);
                System.Windows.MessageBox.Show(
                    "이미지를 편집기로 열지 못했습니다.\n\n" + ex.Message,
                    "SnapView", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (_standaloneViewer == null) Shutdown();
            }
        }

        private void ShowStandaloneEditor(BitmapSource image, string label)
        {
            _standaloneEditor?.Close();

            var editor = new Editor.EditorWindow(image, _standaloneSettings ??= Settings.Load());
            _standaloneEditor = editor;
            editor.Completed += result =>
            {
                Viewer.ViewerWindow viewer = EnsureStandaloneViewer();
                viewer.SetRole(Viewer.ViewerRole.Image);
                viewer.ShowImage(result, null, label);
                if (!viewer.IsVisible) viewer.Show();
                viewer.BringToFront();
            };
            editor.Closed += (_, _) =>
            {
                if (ReferenceEquals(_standaloneEditor, editor)) _standaloneEditor = null;
                if (_standaloneViewer == null) Shutdown();
            };
            editor.Show();
            Native.WindowPlacement.EnsureOnScreen(editor);
            editor.Activate();
        }

        private void EnsureStandaloneIpc()
        {
            if (_standaloneIpc != null) return;
            try
            {
                _standaloneIpc = new Native.MessageWindow();
                _standaloneIpc.RequestReceived += OnStandaloneRequest;
            }
            catch (Exception ex) { Log.Write("독립 실행 창구를 못 열었습니다: " + ex.Message); }
        }

        /// <summary>뷰어 전용으로 떠 있을 때 다른 인스턴스가 넘겨준 파일.</summary>
        private void OnStandaloneRequest(string payload)
        {
            LaunchRequest request = LaunchRequest.FromIpcPayload(payload);
            if (request.HasFile && File.Exists(request.FilePath))
            {
                if (request.OpensEditor) ShowStandaloneEditor(request.FilePath);
                else ShowStandaloneViewer(request.FilePath);
                return;
            }

            if (_standaloneEditor != null) { _standaloneEditor.Activate(); return; }
            _standaloneViewer?.BringToFront();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            System.Windows.MessageBox.Show(
                "예기치 못한 오류가 발생했습니다.\n\n" + e.Exception.Message,
                "SnapView", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;   // 트레이 상주 앱이 오류 하나로 죽으면 안 된다.
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _controller?.Dispose();
            _standaloneIpc?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
