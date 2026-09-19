using System;
using System.IO;
using System.Reflection;
using SnapViewSetup;

// 설치기 검사. 실제로 설치하지는 않고, 폴더 안전 판정과 포장 상태만 본다.
// (제거할 때 설치 폴더를 통째로 지우므로 이 판정이 틀리면 남의 폴더가 날아간다)

internal static partial class SelfTest
{
    private static void TestInstallerSafety()
    {
        Section("설치 폴더 안전 판정");

        // --- 막아야 하는 것들 ---
        string[] mustReject =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"C:\",
            @"C:\Windows",
            @"C:\Windows\System32",
            @"C:\Program Files",
            @"C:\Users",
            "",
            "   ",
            "SnapView"                     // 상대 경로
        };

        int leaked = 0;
        foreach (string path in mustReject)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!Installer.IsUnsafeTarget(path, out _))
            {
                leaked++;
                Console.WriteLine($"         (통과되면 안 되는데 통과함: {path})");
            }
        }
        Check("특별 폴더·드라이브 최상위를 모두 막는다", leaked == 0, leaked + "개 새어 나감");

        Check("빈 경로를 막는다", Installer.IsUnsafeTarget("", out _));
        Check("공백만 있는 경로를 막는다", Installer.IsUnsafeTarget("   ", out _));
        Check("상대 경로를 막는다", Installer.IsUnsafeTarget("SnapView", out _));

        // 경로 끝 이름 검사 — 예전에 이걸 빠뜨려 바탕화면 설치가 통과했었다
        Check("끝 이름이 Desktop 이면 막는다",
              Installer.IsUnsafeTarget(@"D:\어딘가\Desktop", out _));
        Check("끝 이름이 '바탕 화면' 이어도 막는다",
              Installer.IsUnsafeTarget(@"D:\어딘가\바탕 화면", out _));
        Check("끝 이름이 Program Files 면 막는다",
              Installer.IsUnsafeTarget(@"D:\Program Files", out _));

        // --- 허용해야 하는 것들 ---
        Check("기본 설치 위치는 허용",
              !Installer.IsUnsafeTarget(Installer.DefaultDirectory, out string why1),
              why1);
        Check("Program Files 아래 하위 폴더는 허용",
              !Installer.IsUnsafeTarget(@"C:\Program Files\SnapView", out string why2), why2);
        Check("바탕화면 아래 하위 폴더는 허용",
              !Installer.IsUnsafeTarget(
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                               "SnapView"), out string why3), why3);

        string fresh = Path.Combine(Path.GetTempPath(), "SnapViewInstallProbe_" + Guid.NewGuid().ToString("N")[..8]);
        Check("없는 폴더는 허용", !Installer.IsUnsafeTarget(fresh, out string why4), why4);

        // 이미 다른 파일이 든 폴더는 막아야 한다 (제거할 때 통째로 지우므로)
        string busy = Path.Combine(Path.GetTempPath(), "SnapViewBusy_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "남의파일.txt"), "hi");
        try
        {
            Check("남의 파일이 든 폴더는 막는다", Installer.IsUnsafeTarget(busy, out _));

            string empty = Path.Combine(Path.GetTempPath(), "SnapViewEmpty_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(empty);
            try { Check("빈 폴더는 허용", !Installer.IsUnsafeTarget(empty, out string why5), why5); }
            finally { try { Directory.Delete(empty); } catch { } }
        }
        finally { try { Directory.Delete(busy, true); } catch { } }

        // --- 경로 비교 ---
        Check("경로 비교는 대소문자·끝 역슬래시를 무시",
              Installer.SamePath(@"C:\Temp\SnapView", @"c:\temp\snapview\"));
        Check("다른 경로는 다르다고 본다",
              !Installer.SamePath(@"C:\Temp\SnapView", @"C:\Temp\SnapView2"));
    }

    private static void TestInstallerPackaging()
    {
        Section("설치기 포장 상태");

        string root = FindRepoRoot();
        string setupDll = Path.Combine(root, "installer", "bin", "Release",
                                       "net8.0-windows", "win-x64", "SnapView-Setup.dll");
        string appExe = Path.Combine(root, "dist", "SnapView.exe");

        if (!File.Exists(setupDll))
        {
            Console.WriteLine("         (아직 build.bat installer 를 안 돌려 건너뜁니다)");
            return;
        }

        long payloadSize = -1;
        try
        {
            Assembly asm = Assembly.LoadFrom(setupDll);
            using (Stream? payload = asm.GetManifestResourceStream(Installer.PayloadResource))
                payloadSize = payload?.Length ?? -1;
        }
        catch (Exception ex)
        {
            Check("설치기 어셈블리를 읽을 수 있음", false, ex.Message);
            return;
        }

        Check("설치기 안에 SnapView 본체가 들어 있음", payloadSize > 0, "크기 " + payloadSize);

        if (File.Exists(appExe))
        {
            long actual = new FileInfo(appExe).Length;
            Check("품고 있는 본체가 방금 만든 exe 와 같은 크기", payloadSize == actual,
                  $"{payloadSize} vs {actual}");
        }

        string setupExe = Path.Combine(root, "setup", "SnapView-Setup.exe");
        if (File.Exists(setupExe))
        {
            long size = new FileInfo(setupExe).Length;
            Check("설치기 단일 exe 가 만들어짐", size > payloadSize,
                  (size / 1024 / 1024.0).ToString("0.##") + " MB");
        }

        Check("제거 프로그램 이름이 정해져 있음", Installer.UninstallerName == "uninstall.exe");
        Check("버전이 읽힘", !string.IsNullOrEmpty(Installer.Version), Installer.Version);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SnapView.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static string SizeMb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
}
