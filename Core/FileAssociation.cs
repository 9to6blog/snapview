using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SnapView.Core
{
    /// <summary>
    /// SnapView 를 윈도우의 이미지 뷰어로 등록한다.
    ///
    /// 윈도우 8 이후로는 <b>기본 앱을 프로그램이 직접 바꿀 수 없다</b>(UserChoice 가 해시로
    /// 보호된다). 그래서 여기서는 "연결 프로그램 목록과 기본 앱 설정에 SnapView 가 뜨게"
    /// 까지만 하고, 마지막 선택은 윈도우 설정 화면에서 사용자가 하도록 안내한다.
    /// 전부 HKCU 라 관리자 권한이 필요 없다.
    /// </summary>
    internal static class FileAssociation
    {
        internal const string ProgId = "SnapView.Image";

        /// <summary>
        /// 동영상은 그림과 <b>다른 ProgId</b> 로 등록한다.
        /// 하나로 묶으면 윈도우 기본 앱 화면에서 mp4 항목에 "이미지 파일" 로 뜨거나
        /// 아예 후보에 안 나온다. 종류마다 제 이름을 달아 줘야 목록에 제대로 오른다.
        /// </summary>
        internal const string VideoProgId = "SnapView.Video";

        /// <summary>
        /// 윈도우에 "이걸로 열 수 있다" 고 알리는 확장자.
        /// 그림뿐 아니라 동영상도 넣는다 — 뷰어가 재생까지 하기 때문이다.
        /// </summary>
        internal static string[] AssociatedExtensions => MediaKinds.All;
        private const string AppName = "SnapView";
        private const string CapabilitiesPath = @"Software\SnapView\Capabilities";
        internal const string EditorVerbPath =
            @"Software\Classes\SystemFileAssociations\image\shell\SnapView.Edit";

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>
        /// 지금 도는 실행 파일. 설치기는 자기 자신이 아니라 <b>설치한 SnapView.exe</b> 를
        /// 등록해야 하므로, 등록·해제는 경로를 받는 쪽을 쓴다.
        /// </summary>
        private static string? ExePath => Environment.ProcessPath;

        /// <summary>지금 등록되어 있는 실행 명령. 등록 안 됐으면 null.</summary>
        internal static string? RegisteredCommand() => CommandOf(ProgId);

        private static string? CommandOf(string progId)
        {
            try
            {
                using RegistryKey? cmd = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{progId}\shell\open\command");
                return cmd?.GetValue(null) as string;
            }
            catch { return null; }
        }

        private static bool PointsToUs(string progId)
        {
            string? exe = ExePath;
            if (string.IsNullOrEmpty(exe)) return false;

            string? value = CommandOf(progId);
            return value != null && value.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }

        internal static string? RegisteredEditorCommand()
        {
            try
            {
                using RegistryKey? cmd = Registry.CurrentUser.OpenSubKey(EditorVerbPath + @"\command");
                return cmd?.GetValue(null) as string;
            }
            catch { return null; }
        }

        private static bool EditorMenuPointsToUs()
        {
            string? exe = ExePath;
            string? value = RegisteredEditorCommand();
            return !string.IsNullOrEmpty(exe) && value != null &&
                   value.Contains(exe, StringComparison.OrdinalIgnoreCase) &&
                   value.Contains("--edit", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>한 번이라도 등록한 적이 있는가(낡았을 수는 있다).</summary>
        internal static bool WasEverRegistered() => RegisteredCommand() != null;

        /// <summary>
        /// <b>지금 판</b>의 등록이 빠짐없이 들어가 있는가.
        ///
        /// 그림 ProgId 하나만 보고 판단하면 안 된다. 앱이 자라면서 다룰 수 있는
        /// 확장자가 늘어나는데(동영상이 그랬다), 예전에 등록해 둔 것만 보고
        /// "이미 등록됨" 이라고 하면 새 확장자는 영영 목록에 안 오른다 —
        /// mp4 를 SnapView 로 열 수가 없게 된다. 그래서 전부 확인한다.
        /// </summary>
        internal static bool IsRegistered()
            => PointsToUs(ProgId) && PointsToUs(VideoProgId) && EditorMenuPointsToUs() &&
               MissingAssociations().Length == 0;

        /// <summary>
        /// 등록해 둔 것이 낡았으면 조용히 다시 쓴다. 시작할 때 한 번 부른다.
        ///
        /// 등록한 적이 <b>없으면 아무것도 하지 않는다</b> — 묻지도 않고 남의 목록에
        /// 끼어들지는 않는다. 이미 등록한 사람에게만, 새 확장자와 새 exe 경로를
        /// 반영해 준다(HKCU 안에서 우리 것만 다시 쓰는 것이라 기본 앱은 안 바뀐다).
        /// </summary>
        internal static bool RefreshIfStale()
        {
            if (!WasEverRegistered() || IsRegistered()) return false;

            bool ok = Register(out string error);
            Log.Write(ok
                ? "파일 연결 등록이 낡아 새로 고쳤습니다(동영상 포함)."
                : "파일 연결 새로 고침 실패: " + error);
            return ok;
        }

        internal static bool Register(out string error) => Register(ExePath, out error);

        /// <summary>
        /// 이 exe 를 그림·동영상 연결 프로그램 후보로 등록한다.
        ///
        /// 설치기도 이 함수를 부른다. 예전에는 설치기가 같은 일을 따로 구현하고 있었는데,
        /// 그쪽이 그림 확장자만 등록하는 바람에 설치한 사람은 mp4 를 SnapView 로 열 수가
        /// 없었다(본체는 "이미 등록됨" 으로 보고 손대지 않았다). 한 군데에서만 쓴다.
        /// </summary>
        internal static bool Register(string? exe, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(exe))
            {
                error = "실행 파일 경로를 알 수 없습니다.";
                return false;
            }

            try
            {
                string open = "\"" + exe + "\" \"%1\"";

                // 1) 우리 파일 형식 정의 — 그림과 동영상을 따로
                WriteProgId(ProgId, "이미지 파일", exe, open);
                WriteProgId(VideoProgId, "동영상 파일", exe, open);

                // 이미지의 현재 기본 앱이 무엇이든 탐색기 우클릭 메뉴에서 바로 편집할 수 있다.
                // SystemFileAssociations\image 는 윈도우가 그림으로 분류한 파일에만 붙는다.
                string edit = "\"" + exe + "\" --edit \"%1\"";
                using (RegistryKey verb = Registry.CurrentUser.CreateSubKey(EditorVerbPath)!)
                {
                    verb.SetValue(null, "SnapView 편집기로 열기");
                    verb.SetValue("MUIVerb", "SnapView 편집기로 열기");
                    verb.SetValue("Icon", exe + ",0");
                    verb.SetValue("MultiSelectModel", "Single");
                    using RegistryKey command = verb.CreateSubKey("command")!;
                    command.SetValue(null, edit);
                }

                // 2) "연결 프로그램" 목록에 뜨도록
                string appKey = $@"Software\Classes\Applications\{System.IO.Path.GetFileName(exe)}";
                using (RegistryKey app = Registry.CurrentUser.CreateSubKey(appKey)!)
                {
                    app.SetValue("FriendlyAppName", AppName);
                    using (RegistryKey command = app.CreateSubKey(@"shell\open\command")!)
                        command.SetValue(null, open);
                    using (RegistryKey types = app.CreateSubKey("SupportedTypes")!)
                    {
                        foreach (string ext in AssociatedExtensions) types.SetValue(ext, "");
                    }
                }

                // 3) 확장자마다 후보로 등록 + 기본 앱 설정 화면에 노출
                using (RegistryKey caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath)!)
                {
                    caps.SetValue("ApplicationName", AppName);
                    caps.SetValue("ApplicationDescription", "화면 캡처 · 이미지 뷰어 · 영상 재생");
                    caps.SetValue("ApplicationIcon", exe + ",0");

                    using RegistryKey assoc = caps.CreateSubKey("FileAssociations")!;
                    foreach (string ext in AssociatedExtensions)
                    {
                        string id = MediaKinds.IsVideo(ext) ? VideoProgId : ProgId;
                        assoc.SetValue(ext, id);

                        using RegistryKey withProgIds = Registry.CurrentUser.CreateSubKey(
                            $@"Software\Classes\{ext}\OpenWithProgids")!;
                        withProgIds.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);
                    }
                }

                using (RegistryKey reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")!)
                    reg.SetValue(AppName, CapabilitiesPath);

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool Unregister(out string error) => Unregister(ExePath, out error);

        internal static bool Unregister(string? exe, out string error)
        {
            error = "";
            try
            {
                foreach (string ext in AssociatedExtensions)
                {
                    using RegistryKey? withProgIds = Registry.CurrentUser.OpenSubKey(
                        $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                    withProgIds?.DeleteValue(ProgId, throwOnMissingValue: false);
                    withProgIds?.DeleteValue(VideoProgId, throwOnMissingValue: false);
                }

                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{VideoProgId}", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(EditorVerbPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\SnapView", throwOnMissingSubKey: false);

                if (!string.IsNullOrEmpty(exe))
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\Applications\{System.IO.Path.GetFileName(exe)}",
                        throwOnMissingSubKey: false);
                }

                using (RegistryKey? reg = Registry.CurrentUser.OpenSubKey(
                           @"Software\RegisteredApplications", writable: true))
                    reg?.DeleteValue(AppName, throwOnMissingValue: false);

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>윈도우 "기본 앱" 설정 화면을 연다. 마지막 선택은 사용자만 할 수 있다.</summary>
        /// <summary>
        /// 기본 앱 설정을 연다. 윈도우 10/11 은 <c>registeredAppUser</c> 를 붙이면
        /// 그 앱 화면으로 바로 간다. 안 먹는 버전에서는 그냥 기본 앱 목록이 열린다.
        /// </summary>
        internal static void OpenDefaultAppsSettings()
        {
            if (Open("ms-settings:defaultapps?registeredAppUser=" + AppName)) return;
            Open("ms-settings:defaultapps");
        }

        private static bool Open(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        /// <summary>파일 형식 하나를 등록한다. 그림·동영상이 각자 제 이름을 갖게 한다.</summary>
        private static void WriteProgId(string id, string friendlyType, string exe, string open)
        {
            using RegistryKey progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{id}")!;

            progId.SetValue(null, friendlyType);
            progId.SetValue("FriendlyTypeName", friendlyType);

            using (RegistryKey icon = progId.CreateSubKey("DefaultIcon")!)
                icon.SetValue(null, exe + ",0");

            using RegistryKey shell = progId.CreateSubKey(@"shell\open")!;
            shell.SetValue("FriendlyAppName", AppName);

            using RegistryKey command = shell.CreateSubKey("command")!;
            command.SetValue(null, open);
        }

        /// <summary>
        /// 등록이 실제로 먹었는지 확장자별로 확인한다.
        /// "등록했다" 는 말만 하고 목록에 안 뜨면 사용자는 어디가 잘못됐는지 알 길이 없다.
        /// </summary>
        internal static string[] MissingAssociations()
        {
            var missing = new System.Collections.Generic.List<string>();

            foreach (string ext in AssociatedExtensions)
            {
                string id = MediaKinds.IsVideo(ext) ? VideoProgId : ProgId;

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{ext}\OpenWithProgids");

                if (key?.GetValue(id) == null) missing.Add(ext);
            }

            return missing.ToArray();
        }
    }
}
