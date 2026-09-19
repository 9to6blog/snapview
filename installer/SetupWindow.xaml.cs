using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace SnapViewSetup
{
    public partial class SetupWindow : Window
    {
        private readonly bool _uninstallMode;
        private readonly string? _presetPath;
        private bool _finished;

        internal SetupWindow(bool uninstallMode, string? presetPath = null)
        {
            _uninstallMode = uninstallMode;
            _presetPath = presetPath;
            InitializeComponent();

            LoadLogo();
            SubTitle.Text = $"화면 캡처 · 주석 편집 · 이미지 뷰어   v{Installer.Version}";

            if (_uninstallMode) PrepareUninstall();
            else PrepareInstall();
        }

        private void LoadLogo()
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/assets/app.ico"));
                if (info == null) return;

                using Stream s = info.Stream;
                var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                BitmapFrame best = decoder.Frames[0];
                foreach (BitmapFrame f in decoder.Frames)
                {
                    if (f.PixelWidth is >= 48 and <= 128 && f.PixelWidth > best.PixelWidth) best = f;
                }
                LogoImage.Source = best;
            }
            catch { }
        }

        // ================================================= 설치 화면

        private void PrepareInstall()
        {
            Title = "SnapView 설치";
            BtnGo.Content = "설치";

            string? existing = Installer.ReadInstallLocation();
            TbPath.Text = _presetPath ?? existing ?? Installer.DefaultDirectory;

            if (existing != null)
            {
                ExistingBox.Visibility = Visibility.Visible;
                ExistingText.Text =
                    $"이미 설치되어 있습니다.\n{existing}\n\n" +
                    "계속하면 그 자리에 덮어씁니다. 실행 중이면 자동으로 종료합니다.";
                BtnGo.Content = "업데이트";
            }
        }

        private void PrepareUninstall()
        {
            Title = "SnapView 제거";
            BtnGo.Content = "제거";
            FormPanel.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;

            string? dir = Installer.ReadInstallLocation();
            StatusTitle.Text = "SnapView 를 제거할까요?";
            StatusDetail.Text = dir == null
                ? "설치 정보를 찾지 못했습니다."
                : dir + "\n\n이 폴더와 바로가기, 등록 정보를 지웁니다.\n" +
                  "캡처한 이미지와 설정 파일은 지우지 않습니다.";

            if (dir == null) BtnGo.IsEnabled = false;
        }

        // ================================================= 동작

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "SnapView 를 설치할 폴더",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(TbPath.Text) ? TbPath.Text : Installer.DefaultDirectory
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            // 사용자가 고른 폴더 바로 밑이 아니라 그 안에 SnapView 폴더를 만든다.
            string picked = dlg.SelectedPath;
            TbPath.Text = string.Equals(Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar)),
                                        Installer.AppName, StringComparison.OrdinalIgnoreCase)
                ? picked
                : Path.Combine(picked, Installer.AppName);
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();

        private void OnBrandLink(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Installer.HomePage) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
        }

        private async void OnGo(object sender, RoutedEventArgs e)
        {
            if (_finished) { Close(); return; }

            if (_uninstallMode) { await RunUninstall(); return; }
            await RunInstall();
        }

        private async Task RunInstall()
        {
            string target = TbPath.Text.Trim();

            if (Installer.IsUnsafeTarget(target, out string reason))
            {
                MessageBox.Show(this, reason, "SnapView 설치",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = new InstallOptions
            {
                TargetDirectory = target,
                DesktopShortcut = CbDesktop.IsChecked == true,
                StartMenuShortcut = CbStartMenu.IsChecked == true,
                RunAtStartup = CbStartup.IsChecked == true,
                AssociateImages = CbAssoc.IsChecked == true,
                LaunchAfterInstall = CbLaunch.IsChecked == true
            };

            BeginWork("설치하는 중...");

            try
            {
                await Task.Run(() => Installer.Install(options, Report));

                Finish("설치가 끝났습니다",
                       Path.Combine(options.TargetDirectory, Installer.ExeName) +
                       (options.AssociateImages
                           ? "\n\n이미지 · 동영상을 연결 프로그램 목록에 올렸습니다.\n" +
                             "윈도우 설정 → 기본 앱 에서 SnapView 를 골라야 더블클릭으로 열립니다."
                           : ""));

                if (options.LaunchAfterInstall)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(
                            Path.Combine(options.TargetDirectory, Installer.ExeName))
                        { UseShellExecute = true });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Fail("설치하지 못했습니다", ex.Message);
            }
        }

        private async Task RunUninstall()
        {
            BeginWork("제거하는 중...");
            try
            {
                await Task.Run(() => Installer.Uninstall(Report));
                Finish("제거가 끝났습니다", "그동안 고마웠습니다.");
            }
            catch (Exception ex)
            {
                Fail("제거하지 못했습니다", ex.Message);
            }
        }

        private void BeginWork(string title)
        {
            FormPanel.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            StatusTitle.Text = title;
            StatusDetail.Text = "";
            BtnGo.IsEnabled = false;
            BtnCancel.IsEnabled = false;
        }

        private void Report(string message)
            => Dispatcher.Invoke(() => StatusDetail.Text = message);

        private void Finish(string title, string detail)
        {
            _finished = true;
            StatusTitle.Text = title;
            StatusDetail.Text = detail;
            BtnGo.Content = "닫기";
            BtnGo.IsEnabled = true;
            BtnCancel.Visibility = Visibility.Collapsed;
        }

        private void Fail(string title, string detail)
        {
            _finished = true;
            StatusTitle.Text = title;
            StatusDetail.Text = detail;
            BtnGo.Content = "닫기";
            BtnGo.IsEnabled = true;
            BtnCancel.IsEnabled = true;
        }
    }
}
