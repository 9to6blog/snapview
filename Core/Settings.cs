using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SnapView.Core
{
    /// <summary>뷰어의 기본 배율 정책.</summary>
    internal enum FitMode
    {
        /// <summary>창보다 클 때만 줄인다. 작은 그림은 원본 크기 그대로.</summary>
        ShrinkToFit,
        /// <summary>항상 창에 꽉 맞춘다(작은 그림도 늘림).</summary>
        StretchToFit,
        /// <summary>폭에 맞춘다(세로로 긴 이미지용).</summary>
        FitWidth,
        /// <summary>언제나 100%.</summary>
        Actual
    }

    internal sealed class Settings
    {
        // ---- 캡처 ----
        public bool SaveToDisk { get; set; } = true;
        public bool CopyToClipboard { get; set; } = true;
        public bool OpenViewerAfterCapture { get; set; } = true;
        public bool IncludeCursor { get; set; } = false;
        public bool PlayShutterSound { get; set; } = true;

        /// <summary>알림음 크기(0~100). 조용한 편이 기본이다.</summary>
        public int ShutterVolume { get; set; } = 35;

        /// <summary>영역을 놓은 뒤 바로 확정하지 않고 조절점·액션바를 띄운다.</summary>
        public bool AdjustBeforeCapture { get; set; } = true;

        /// <summary>영역 선택 중 커서를 지나는 십자 안내선을 화면 끝까지 긋는다.</summary>
        public bool ShowCrosshair { get; set; } = true;

        /// <summary>창 캡처에 Windows.Graphics.Capture 를 쓴다(겹친 창이 안 찍힘).</summary>
        public bool UseGraphicsCapture { get; set; } = true;

        /// <summary>캡처하면 뷰어 대신 주석 편집기를 연다.</summary>
        public bool OpenEditorAfterCapture { get; set; } = false;

        public string SaveFolder { get; set; } = DefaultSaveFolder;
        public string FileNamePattern { get; set; } = "SnapView_{0:yyyy-MM-dd_HHmmss}";

        /// <summary>
        /// 영상에서 뜬 장면의 이름 규칙. <c>{0}</c> 은 시각, <c>{1}</c> 은 원본 파일 이름.
        ///
        /// 캡처와 다른 규칙을 쓴다. 장면은 <b>어느 영상에서 나왔는지</b>가 이름에 남아야
        /// 쓸모가 있다 — 날짜만 있으면 나중에 폴더를 열었을 때 무엇을 찍은 것인지 알 수 없다.
        /// </summary>
        public string FrameNamePattern { get; set; } = "스냅뷰_{1}_{0:yyyy-MM-dd_HHmmss}";
        /// <summary>"png" 또는 "jpg".</summary>
        public string ImageFormat { get; set; } = "png";
        public int JpegQuality { get; set; } = 92;

        // ---- 단축키 ----
        // Alt+Shift 로 묶는다. Ctrl+Shift+S/W 는 앱 안에서 "다른 이름으로 저장" ·
        // "탭 닫기" 로 쓰는 데가 많은데, 전역 단축키는 앱 단축키를 이겨 버려서 피했다.
        // 전체 화면에 Alt+Shift+PrtScn 은 못 쓴다 — 윈도우 "고대비 켜기/끄기" 핫키다.
        // (HKCU\Control Panel\Accessibility\HighContrast 의 Flags 0x2 비트)
        public string HotKeyRegion { get; set; } = "Alt+Shift+S";
        public string HotKeyFullScreen { get; set; } = "Alt+Shift+F";
        // Alt+Shift+W 는 쓰지 않는다 — 윈도우가 이미 쓰는 조합과 부딪혀 안 먹는 PC 가 있다.
        public string HotKeyActiveWindow { get; set; } = "Alt+W";

        /// <summary>영역 녹화 시작 / 종료를 같은 키로 토글한다.</summary>
        public string HotKeyRecord { get; set; } = "Alt+Shift+R";

        /// <summary>
        /// 전체 화면 녹화 토글. 영역을 고르는 단계 없이 바로 시작한다.
        /// 이미 녹화 중이면 어느 키를 눌러도 멈춘다 — 켤 때 쓴 키를 기억할 필요는 없어야 한다.
        /// </summary>
        public string HotKeyRecordFullScreen { get; set; } = "Alt+Shift+G";

        /// <summary>활성 창 녹화 토글. 누른 순간의 창 자리를 그대로 찍는다.</summary>
        public string HotKeyRecordWindow { get; set; } = "Alt+Shift+T";

        // ---- 뷰어 ----
        // 캡처 이미지는 화면보다 큰 게 대부분이라 "항상 창에 맞춤" 이 기본이다.
        public FitMode DefaultFitMode { get; set; } = FitMode.StretchToFit;
        public bool ShowCheckerboard { get; set; } = true;
        /// <summary>이 배율을 넘으면 픽셀을 뭉개지 않고 또렷하게(도트 확인용).</summary>
        public double NearestNeighborAbove { get; set; } = 3.0;
        public bool WrapAround { get; set; } = true;

        /// <summary>뷰어 아래에 폴더의 모든 이미지를 썸네일로 늘어놓는다.</summary>
        public bool ShowThumbnailStrip { get; set; } = false;

        // ---- 재생 ----
        // 캡처 도구로 찍은 영상은 대개 짧다. 끝에서 멈춰 서 있는 것보다 계속 도는 편이
        // 어디를 다시 볼지 고르기 좋아서 반복을 기본으로 켠다.
        public bool PlayerLoop { get; set; } = true;

        /// <summary>방향키 한 번에 건너뛸 시간(초).</summary>
        public double PlayerSkipSeconds { get; set; } = 5;

        // 재생 중에 쓰는 키. 전역 단축키와 달리 뷰어 창이 앞에 있을 때만 듣는다.
        // 빈 칸으로 두면 그 기능은 키로는 못 쓴다(단추는 그대로 있다).
        public string PlayerKeyPlayPause { get; set; } = "Space";
        public string PlayerKeyBack { get; set; } = "Left";
        public string PlayerKeyForward { get; set; } = "Right";
        public string PlayerKeyPrevFrame { get; set; } = "Ctrl+Left";
        public string PlayerKeyNextFrame { get; set; } = "Ctrl+Right";
        public string PlayerKeyVolumeUp { get; set; } = "Up";
        public string PlayerKeyVolumeDown { get; set; } = "Down";
        public string PlayerKeyMute { get; set; } = "M";
        public string PlayerKeyLoop { get; set; } = "L";

        /// <summary>지금 보고 있는 한 장면을 그림으로 뽑는다.</summary>
        public string PlayerKeyGrabFrame { get; set; } = "Shift+S";

        /// <summary>
        /// 파일을 열 때 창을 새로 띄운다. 끄면 이미 열려 있는 창에 갈아 끼운다.
        /// 영상 두 개를 나란히 놓고 비교할 때는 켜는 편이 낫다.
        /// </summary>
        public bool OpenInNewWindow { get; set; } = false;

        /// <summary>
        /// 영상이 끝나면 같은 폴더의 다음 영상으로 넘어간다.
        /// 반복이 켜져 있으면 그쪽이 먼저다 — 지금 것을 다시 돌린다.
        /// </summary>
        public bool PlayNextInFolder { get; set; } = true;

        /// <summary>재생 중 마우스 휠로 소리 크기를 조절한다.</summary>
        public bool WheelChangesVolume { get; set; } = true;

        // ---- 주석 편집기 ----
        public string AnnotationColor { get; set; } = "#FFE23B3B";
        public double AnnotationThickness { get; set; } = 3;
        public double AnnotationFontSize { get; set; } = 22;
        public string AnnotationFontFamily { get; set; } = "Malgun Gothic";
        /// <summary>0.1 ~ 1.0. 편집기에서 그리는 것들의 불투명도.</summary>
        public double AnnotationOpacity { get; set; } = 1.0;
        public bool AnnotationFilled { get; set; }
        public string AnnotationFillColor { get; set; } = "#FFFFD166";
        public int MosaicBlockSize { get; set; } = 12;

        /// <summary>마지막에 쓴 편집 도구. 다음에 열 때 그 도구로 시작한다.</summary>
        public string AnnotationTool { get; set; } = "Arrow";

        /// <summary>최근 쓴 색(팔레트 밖). "#AARRGGBB,..." 여섯 칸.</summary>
        public string RecentColors { get; set; } = "";

        /// <summary>편집기 창 자리 "L,T,W,H,최대화". 비면 가운데 기본 크기.</summary>
        public string EditorWindow { get; set; } = "";

        // 꾸며서 내보내기 — 마지막에 쓴 값
        public int ExportPadding { get; set; } = 32;
        public double ExportCornerRadius { get; set; } = 12;
        public bool ExportShadow { get; set; } = true;
        /// <summary>0 없음 · 1 흰색 · 2 검정 · 3 짙은 회색 · 4 지금 색 · 5 지금 색 그라데이션</summary>
        public int ExportBackground { get; set; } = 3;
        public string ExportWatermark { get; set; } = "";

        // ---- 녹화 ----
        /// <summary>
        /// 초당 몇 장. 높일수록 부드럽지만 파일이 커진다.
        /// 화면 녹화는 부드러운 편이 훨씬 보기 좋아서 60 을 기본으로 둔다 —
        /// 못 따라가면 알아서 덜 찍히고, 그래도 영상 길이는 실제 시간과 맞는다.
        /// (GIF 로 물러섰을 때는 10 언저리가 적당하다)
        /// </summary>
        public int RecordingFps { get; set; } = 60;

        /// <summary>"mp4" 또는 "gif". MP4 를 못 열면 자동으로 GIF 로 넘어간다.</summary>
        public string RecordingFormat { get; set; } = "mp4";

        /// <summary>지금 스피커로 나가는 소리를 영상에 같이 담을지. MP4 일 때만 뜻이 있다.</summary>
        public bool RecordSystemAudio { get; set; } = true;

        /// <summary>녹화에 마우스 커서를 그릴지. 캡처의 커서 설정과는 따로 논다 — 설명 영상은 커서가 있어야 한다.</summary>
        public bool RecordCursor { get; set; } = true;

        /// <summary>녹화 시작 전에 세는 초. 0 이면 바로 시작한다.</summary>
        public int RecordCountdownSeconds { get; set; } = 0;

        /// <summary>녹화 시작·종료음을 낼지. 캡처음과 따로 둔다 — 캡처음은 두고 녹화음만 끄고 싶은 사람이 있다.</summary>
        public bool PlayRecordSound { get; set; } = true;

        /// <summary>녹화 시작·종료음 크기(0~100).</summary>
        public int RecordSoundVolume { get; set; } = 35;

        /// <summary>
        /// 녹화 파일 이름 규칙. <c>{0}</c> 은 시각, <c>{1}</c> 은 무엇을 찍었는지(전체 화면·모니터·영역·창 제목).
        /// 캡처와 달리 대상이 이름에 들어간다 — 나중에 폴더를 열었을 때 무엇을 찍은 건지 알 수 있어야 한다.
        /// </summary>
        public string RecordNamePattern { get; set; } = RecordingNames.DefaultPattern;

        /// <summary>
        /// 설정 파일의 판. 항목을 갈라 낼 때 옛 값을 새 항목으로 옮기려면 몇 판에서 왔는지 알아야 한다.
        /// 파일에 없으면(1판) 0 으로 읽힌다. 2: 녹화음 설정이 캡처음에서 갈라졌다.
        /// </summary>
        public int SettingsVersion { get; set; }

        [JsonIgnore]
        internal const int CurrentSettingsVersion = 2;

        /// <summary>옛 판 파일이면 새 항목을 옛 값으로 채운다. 불러온 직후 한 번 부른다.</summary>
        internal void Migrate()
        {
            if (SettingsVersion < 2)
            {
                // 2판에서 녹화음이 캡처음과 갈라졌다. 그 전엔 캡처음 설정이 둘 다를 맡았으니 그대로 잇는다.
                PlayRecordSound = PlayShutterSound;
                RecordSoundVolume = ShutterVolume;
            }
            SettingsVersion = CurrentSettingsVersion;
        }

        // ---- 시작 ----
        public bool RunAtStartup { get; set; } = false;

        // ================= 저장/불러오기 =================

        [JsonIgnore]
        internal static string ConfigDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapView");

        [JsonIgnore]
        internal static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

        [JsonIgnore]
        internal static string DefaultSaveFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapView");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        internal static Settings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), JsonOpts);
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.SaveFolder))
                            loaded.SaveFolder = DefaultSaveFolder;
                        loaded.Migrate();
                        return loaded;
                    }
                }
            }
            catch
            {
                // 설정이 깨졌으면 조용히 기본값으로. 캡처 도구가 이걸로 못 뜨면 안 된다.
            }
            return new Settings { SettingsVersion = CurrentSettingsVersion };
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { }
        }

        /// <summary>시작 프로그램 등록 상태를 설정값에 맞춘다.</summary>
        internal void ApplyStartupRegistration()
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
                if (key == null) return;

                if (RunAtStartup)
                {
                    string? exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                        key.SetValue("SnapView", "\"" + exe + "\"");
                }
                else if (key.GetValue("SnapView") != null)
                {
                    key.DeleteValue("SnapView", throwOnMissingValue: false);
                }
            }
            catch { }
        }
    }
}
