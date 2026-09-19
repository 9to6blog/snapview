using System;
using System.Windows;

namespace SnapViewSetup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool uninstall = HasFlag(e.Args, "uninstall") || HasFlag(e.Args, "u");
            bool silent = HasFlag(e.Args, "S") || HasFlag(e.Args, "silent");
            string? preset = ParseTargetDirectory();

            if (silent)
            {
                RunSilent(uninstall, preset);
                Shutdown();
                return;
            }

            new SetupWindow(uninstall, preset).Show();
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args)
            {
                string t = a.TrimStart('/', '-');
                if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 설치 폴더는 <c>/D=C:\some path\SnapView</c> 로 준다.
        /// <b>argv 가 아니라 명령줄 원문에서 읽는다</b> — 그러지 않으면 공백이 든 경로가
        /// 잘려서 "C:\some" 같은 폴더가 생긴다(WinPurge 설치기에서 실제로 겪은 일).
        /// </summary>
        private static string? ParseTargetDirectory()
        {
            string line = Environment.CommandLine;
            int at = line.IndexOf("/D=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            string rest = line[(at + 3)..].Trim();
            if (rest.Length == 0) return null;

            // NSIS 관례대로 /D= 는 맨 뒤에 오고 따옴표를 쓰지 않는다. 붙어 있어도 벗겨 준다.
            if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
                rest = rest[1..^1];

            return rest.Trim().TrimEnd('\\');
        }

        private static void RunSilent(bool uninstall, string? preset)
        {
            try
            {
                if (uninstall)
                {
                    Installer.Uninstall(_ => { });
                    return;
                }

                var options = new InstallOptions();
                if (!string.IsNullOrEmpty(preset)) options.TargetDirectory = preset;
                options.LaunchAfterInstall = false;

                if (Installer.IsUnsafeTarget(options.TargetDirectory, out string reason))
                    throw new InvalidOperationException(reason);

                Installer.Install(options, _ => { });
            }
            catch (Exception ex)
            {
                // 조용히 돌리라고 했으니 창은 안 띄우고 종료 코드로만 알린다.
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }
    }
}
