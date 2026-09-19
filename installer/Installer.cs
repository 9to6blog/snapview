using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace SnapViewSetup
{
    internal sealed class InstallOptions
    {
        internal string TargetDirectory { get; set; } = Installer.DefaultDirectory;
        internal bool DesktopShortcut { get; set; } = true;
        internal bool StartMenuShortcut { get; set; } = true;
        internal bool RunAtStartup { get; set; }
        internal bool AssociateImages { get; set; } = true;
        internal bool LaunchAfterInstall { get; set; } = true;
    }

    /// <summary>
    /// 실제 설치·제거 작업.
    ///
    /// 두 가지 원칙이 있다(WinPurge 만들 때 겪은 사고에서 나온 것):
    ///  1) 제거는 <b>레지스트리 InstallLocation</b> 을 기준으로 한다. uninstall.exe 가
    ///     있다는 이유로 폴더를 통째로 지우면 남의 폴더를 날릴 수 있다.
    ///  2) 설치 폴더는 제거할 때 통째로 지우므로, 바탕화면·문서·Windows 같은
    ///     폴더에는 설치를 <b>거부</b>한다. 판정은 "그 폴더 자체인지"와
    ///     "경로 끝 이름이 위험한 이름인지" 두 가지를 모두 본다.
    /// </summary>
    internal static class Installer
    {
        internal const string AppName = "SnapView";
        internal const string Publisher = "9to6blog";
        internal const string HomePage = "https://9to6blog.com";
        internal const string ExeName = "SnapView.exe";
        internal const string UninstallerName = "uninstall.exe";
        internal const string PayloadResource = "SnapView.exe";

        private const string UninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SnapView";
        private const string RunKey =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        /// <summary>
        /// 기본 설치 위치. <c>C:\Program Files\SnapView</c> 다.
        ///
        /// 여기에 쓰려면 <b>관리자 권한이 있어야 한다</b> — 그래서 설치기 매니페스트가
        /// 권한 상승을 요구한다(installer/app.manifest). 모델까지 370MB 라 사용자 폴더에
        /// 두기에는 덩치가 크고, 프로그램은 프로그램 자리에 있는 편이 찾기도 쉽다.
        /// </summary>
        internal static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        // ================================================= 안전 검사

        /// <summary>제거할 때 통째로 지워도 되는 폴더인지. 아니면 이유를 돌려준다.</summary>
        internal static bool IsUnsafeTarget(string path, out string reason)
        {
            reason = "";

            if (string.IsNullOrWhiteSpace(path)) { reason = "경로가 비어 있습니다."; return true; }

            // 상대 경로 판정은 GetFullPath 앞에서 해야 한다. 뒤에서 하면 현재 폴더 기준으로
            // 절대 경로가 되어 버려서 검사가 항상 통과한다.
            string raw = path.Trim();
            if (!Path.IsPathFullyQualified(raw))
            {
                reason = "드라이브 문자로 시작하는 전체 경로를 적어 주세요.\n예: " + DefaultDirectory;
                return true;
            }

            string full;
            try { full = Path.GetFullPath(raw); }
            catch { reason = "경로 형식이 올바르지 않습니다."; return true; }

            // 드라이브 바로 밑(C:\) 은 안 된다 — 제거할 때 드라이브를 통째로 지우게 된다.
            string? parent = Path.GetDirectoryName(full);
            if (parent == null)
            {
                reason = "드라이브 최상위에는 설치할 수 없습니다.";
                return true;
            }

            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
            string leaf = Path.GetFileName(trimmed);

            // 1) 그 폴더 자체가 특별 폴더인가
            foreach (Environment.SpecialFolder sf in ProtectedFolders)
            {
                string special;
                try { special = Environment.GetFolderPath(sf); }
                catch { continue; }

                if (special.Length == 0) continue;
                if (string.Equals(trimmed, special.TrimEnd(Path.DirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"'{leaf}' 폴더 자체에는 설치할 수 없습니다.\n제거할 때 폴더를 통째로 지우기 때문입니다.";
                    return true;
                }
            }

            // 2) 경로 끝 이름이 위험한 이름인가 (예전에 이 검사를 빠뜨려 바탕화면 설치가 통과했었다)
            if (ProtectedLeafNames.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            {
                reason = $"'{leaf}' 는 설치 폴더로 쓸 수 없는 이름입니다.\n" +
                         $"예: {Path.Combine(trimmed, AppName)} 처럼 하위 폴더를 지정하세요.";
                return true;
            }

            // 3) 이미 다른 프로그램이 쓰고 있는 폴더인가
            try
            {
                if (Directory.Exists(full) && !IsOurInstallFolder(full))
                {
                    bool empty = !Directory.EnumerateFileSystemEntries(full).Any();
                    if (!empty)
                    {
                        reason = "이미 다른 파일이 들어 있는 폴더입니다.\n" +
                                 "제거할 때 폴더를 통째로 지우므로 빈 폴더나 새 폴더를 골라 주세요.";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "폴더를 확인하지 못했습니다: " + ex.Message;
                return true;
            }

            return false;
        }

        private static readonly Environment.SpecialFolder[] ProtectedFolders =
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.Startup
        };

        private static readonly string[] ProtectedLeafNames =
        {
            "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)",
            "ProgramData", "Users", "AppData", "Local", "LocalLow", "Roaming",
            "Desktop", "바탕 화면", "Documents", "문서", "Downloads", "다운로드",
            "Pictures", "사진", "Music", "음악", "Videos", "비디오",
            "Start Menu", "시작 메뉴", "Programs", "Startup", "Temp", "Public"
        };

        private static bool IsOurInstallFolder(string path)
        {
            string? current = ReadInstallLocation();
            return current != null && SamePath(current, path);
        }

        internal static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ================================================= 설치

        internal static string? ReadInstallLocation()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") as string;
            }
            catch { return null; }
        }

        internal static bool IsInstalled => !string.IsNullOrEmpty(ReadInstallLocation());

        /// <summary>설치 폴더 안에서 돌고 있는 SnapView 를 모두 멈춘다.</summary>
        internal static int StopRunning(string installDirectory)
        {
            int stopped = 0;
            foreach (Process p in Process.GetProcessesByName("SnapView"))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (path != null && !path.StartsWith(installDirectory, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!p.CloseMainWindow() || !p.WaitForExit(1500))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    stopped++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (stopped > 0) Thread.Sleep(250);   // 파일 잠금이 풀릴 틈
            return stopped;
        }

        internal static void Install(InstallOptions options, Action<string> report)
        {
            string dir = Path.GetFullPath(options.TargetDirectory.Trim());

            report("설치 폴더 준비 중...");
            Directory.CreateDirectory(dir);

            report("실행 중인 SnapView 확인 중...");
            StopRunning(dir);

            string exePath = Path.Combine(dir, ExeName);
            report("프로그램 파일 복사 중...");
            WritePayload(exePath);

            report("제거 프로그램 준비 중...");
            string uninstaller = Path.Combine(dir, UninstallerName);
            CopySelf(uninstaller);

            report("바로가기 만드는 중...");
            string desktopLink = DesktopLinkPath();
            string startMenuLink = StartMenuLinkPath();

            SafeDelete(desktopLink);
            SafeDelete(startMenuLink);

            if (options.DesktopShortcut)
                Shortcut.Create(desktopLink, exePath, description: "화면 캡처 + 주석 편집 + 이미지 뷰어");

            if (options.StartMenuShortcut)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(startMenuLink)!);
                Shortcut.Create(startMenuLink, exePath, description: "화면 캡처 + 주석 편집 + 이미지 뷰어");
            }

            report("등록 정보 쓰는 중...");
            WriteUninstallEntry(dir, exePath, uninstaller);

            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                if (run != null)
                {
                    if (options.RunAtStartup) run.SetValue(AppName, "\"" + exePath + "\"");
                    else run.DeleteValue(AppName, throwOnMissingValue: false);
                }
            }

            if (options.AssociateImages)
            {
                report("이미지 파일 연결 등록 중...");
                RegisterFileAssociations(exePath);
            }

            report("설치가 끝났습니다.");
        }

        private static void WritePayload(string exePath)
        {
            using Stream? src = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(PayloadResource)
                ?? throw new InvalidOperationException(
                    "설치 파일 안에 SnapView 본체가 들어 있지 않습니다. build.bat installer 로 다시 만들어 주세요.");

            // 돌고 있어서 못 지우면 이름만 바꿔 두고 새 파일을 제자리에 넣는다.
            if (File.Exists(exePath))
            {
                try { File.Delete(exePath); }
                catch (IOException)
                {
                    string old = exePath + ".old";
                    SafeDelete(old);
                    File.Move(exePath, old);
                }
            }

            using var dst = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }

        private static void CopySelf(string destination)
        {
            string? self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return;
            if (SamePath(self, destination)) return;

            try
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Copy(self, destination);
            }
            catch (IOException)
            {
                string old = destination + ".old";
                SafeDelete(old);
                try { File.Move(destination, old); File.Copy(self, destination); } catch { }
            }
        }

        private static void WriteUninstallEntry(string dir, string exePath, string uninstaller)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey)!;

            long size = 0;
            try
            {
                size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                                             .Sum(f => f.Length) / 1024;
            }
            catch { }

            key.SetValue("DisplayName", AppName + " — 화면 캡처 & 이미지 뷰어");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("DisplayIcon", exePath + ",0");
            key.SetValue("Publisher", Publisher);
            key.SetValue("URLInfoAbout", HomePage);
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
            key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /S");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Max(1, size), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }

        // ================================================= 제거

        internal static void Uninstall(Action<string> report)
        {
            string? dir = ReadInstallLocation();
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException("설치 정보를 찾지 못했습니다.");

            // 레지스트리에 적힌 경로라도 위험한 곳이면 지우지 않는다.
            if (IsUnsafeTarget(dir, out string reason) && !SamePath(dir, DefaultDirectory))
                throw new InvalidOperationException("설치 폴더가 이상합니다.\n" + reason);

            report("실행 중인 SnapView 를 멈추는 중...");
            StopRunning(dir);

            report("바로가기 지우는 중...");
            SafeDelete(DesktopLinkPath());
            SafeDelete(StartMenuLinkPath());

            report("등록 정보 지우는 중...");
            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                run?.DeleteValue(AppName, throwOnMissingValue: false);

            UnregisterFileAssociations();

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            }
            catch { }

            report("파일 지우는 중...");
            DeleteInstallFolder(dir);

            report("제거가 끝났습니다.");
        }

        /// <summary>
        /// 자기 자신(uninstall.exe)이 그 폴더 안에서 돌고 있으므로 폴더를 통째로 못 지운다.
        /// 지울 수 있는 것을 먼저 지우고, 남은 것은 재부팅 뒤 지우도록 예약한다.
        /// </summary>
        private static void DeleteInstallFolder(string dir)
        {
            string? self = Environment.ProcessPath;

            foreach (string file in SafeEnumerate(dir))
            {
                if (self != null && SamePath(file, self)) continue;
                SafeDelete(file);
            }

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                    Directory.Delete(sub, recursive: true);
            }
            catch { }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    return;
                }
            }
            catch { }

            // 아직 남은 것(대개 실행 중인 uninstall.exe)은 다음 부팅 때 지운다.
            if (self != null && SamePath(Path.GetDirectoryName(self) ?? "", dir))
            {
                NativeHelpers.DeleteOnReboot(self);
                NativeHelpers.DeleteOnReboot(dir);
            }
        }

        private static IEnumerable<string> SafeEnumerate(string dir)
        {
            try { return Directory.EnumerateFiles(dir).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        internal static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ================================================= 바로가기 경로

        internal static string DesktopLinkPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        internal static string StartMenuLinkPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        // ================================================= 이미지 연결
        // (본체의 Core/FileAssociation.cs 와 같은 내용을, 설치기에서 쓸 수 있게 옮겨 둔 것)

        /// <summary>
        /// 설치한 exe 를 그림·동영상 연결 프로그램 후보로 등록한다.
        ///
        /// 예전에는 설치기가 이 일을 <b>따로 구현</b>하고 있었다. 그쪽은 그림 확장자만
        /// 등록했는데, 본체는 "이미 등록되어 있다" 고만 보고 손대지 않아서 설치한 사람은
        /// mp4 를 SnapView 로 열 수가 없었다. 이제 본체와 같은 코드를 부른다.
        /// </summary>
        private static void RegisterFileAssociations(string exePath)
        {
            SnapView.Core.FileAssociation.Register(exePath, out _);
        }

        private static void UnregisterFileAssociations()
        {
            SnapView.Core.FileAssociation.Unregister(InstalledExePath(), out _);
        }

        /// <summary>설치해 둔 exe 의 경로. 없으면 기본 위치로 짐작한다.</summary>
        private static string InstalledExePath()
        {
            string? dir = ReadInstallLocation();
            return Path.Combine(string.IsNullOrEmpty(dir) ? DefaultDirectory : dir, ExeName);
        }

    }
}
