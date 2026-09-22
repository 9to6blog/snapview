using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SnapView.Core;
using SnapView.Prefs;

namespace SnapView.Editor
{
    public partial class AiImageDialog : Window
    {
        private readonly BitmapSource _whole;
        private readonly BitmapSource? _region;
        private readonly Rect _regionBounds;
        private readonly AiSettingsStore _store;
        private AiPreferences _preferences = new();
        private AiProfile? _profile;
        private CancellationTokenSource? _request;
        private bool _loading, _closed;
        internal BitmapSource? Result { get; private set; }
        internal Rect ResultBounds { get; private set; }
        internal string ResultProvider { get; private set; } = "AI";

        internal AiImageDialog(BitmapSource whole, Int32Rect? region = null, AiSettingsStore? store = null)
        {
            _whole = whole; _store = store ?? new AiSettingsStore();
            if (region is Int32Rect r && r.Width > 1 && r.Height > 1)
            {
                _region = new CroppedBitmap(whole, r); _region.Freeze();
                _regionBounds = new Rect(r.X, r.Y, r.Width, r.Height);
            }
            InitializeComponent();
            SourceBox.Items.Add("현재 캔버스 전체");
            if (_region != null) SourceBox.Items.Add("선택 영역만");
            SourceBox.SelectedIndex = _region == null ? 0 : 1;
            LoadSettings();
            Closing += (_, _) => { _closed = true; _request?.Cancel(); };
        }
        private void LoadSettings(string? selected = null)
        {
            _loading = true;
            try
            {
                _preferences = _store.Load();
                ProviderBox.ItemsSource = AiProvider.All;
                ProviderBox.SelectedItem = AiProvider.Find(selected ?? _preferences.SelectedProvider);
                UpdateConnection();
            }
            catch { Status.Text = "AI 설정을 읽지 못했습니다. 설정 파일과 접근 권한을 확인해 주세요."; SendButton.IsEnabled = false; }
            finally { _loading = false; }
        }
        private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
        { if (!_loading) { ClearResult(); UpdateConnection(); } }
        private void UpdateConnection()
        {
            if (ProviderBox.SelectedItem is not AiProvider provider) return;
            _profile = _preferences.Get(provider.Id).Clone();
            string host;
            try { host = _profile.BaseUri.GetLeftPart(UriPartial.Authority); }
            catch { host = "HTTPS 주소를 설정해 주세요"; }
            Destination.Text = $"{provider.Name} · {_profile.Model}  →  {host}";
            bool ready = !string.IsNullOrEmpty(_profile.ProtectedKey);
            ConnectionState.Text = ready ? "저장한 키를 사용합니다. 아래 전송 버튼을 눌러야 요청이 시작됩니다." : "연결 설정에서 이 제공사의 API 키를 저장해 주세요.";
            SendButton.IsEnabled = ready;
        }
        private void OnSettings(object sender, RoutedEventArgs e)
        {
            try
            {
                string? id = _profile?.ProviderId;
                var window = new AiSettingsWindow(_store, id) { Owner = this };
                if (window.ShowDialog() == true) { ClearResult(); LoadSettings(); Status.Text = "연결 설정을 저장했습니다."; }
            }
            catch { Status.Text = "AI 설정을 열지 못했습니다. 설정 파일과 접근 권한을 확인해 주세요."; }
        }
        private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
        { SourcePreview.Source = SourceBox.SelectedIndex == 1 ? _region : _whole; ClearResult(); }
        private void ClearResult()
        {
            Result = null; ResultPreview.Source = null; ApplyButton.IsEnabled = false;
            ResultPlaceholder.Visibility = Visibility.Visible; ResultLabel.Text = "처리 결과";
        }
        private void SetBusy(bool busy)
        {
            ProviderBox.IsEnabled = SourceBox.IsEnabled = SettingsButton.IsEnabled = PromptBox.IsEnabled = !busy;
            SendButton.IsEnabled = !busy && !string.IsNullOrEmpty(_profile?.ProtectedKey);
            ApplyButton.IsEnabled = !busy && Result != null;
            CancelRequest.Visibility = Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CancelRequest.IsEnabled = busy;
        }
        private async void OnSend(object sender, RoutedEventArgs e)
        {
            if (_request != null || _profile == null) return;
            string prompt = PromptBox.Text.Trim();
            if (prompt.Length == 0) { Status.Text = "이미지를 어떻게 바꿀지 입력해 주세요."; PromptBox.Focus(); return; }
            ClearResult(); SetBusy(true);
            var cancellation = new CancellationTokenSource(); _request = cancellation;
            BitmapSource input = SourceBox.SelectedIndex == 1 && _region != null ? _region : _whole;
            Rect bounds = SourceBox.SelectedIndex == 1 ? _regionBounds : new Rect(0, 0, _whole.PixelWidth, _whole.PixelHeight);
            AiProfile profile = _profile.Clone();
            try
            {
                if (profile.ProviderId == "stability" && (input.PixelWidth < 64 || input.PixelHeight < 64 ||
                    (long)input.PixelWidth * input.PixelHeight > 9437184 || (double)input.PixelWidth / input.PixelHeight > 2.5 || (double)input.PixelHeight / input.PixelWidth > 2.5))
                    throw new AiRequestException("Stability AI 구조 변환은 각 변 64 px 이상, 9 MP 이하, 비율 1:2.5–2.5:1 이미지가 필요합니다. 크기를 줄이거나 영역을 선택해 주세요.");
                string key = profile.ReadKey();
                Status.Text = "전송할 이미지를 준비하고 있습니다…";
                byte[] png = await Task.Run(() => ImageIO.EncodePng(input), cancellation.Token);
                using var client = new AiImageClient();
                var progress = new Progress<string>(message => { if (!_closed && !cancellation.IsCancellationRequested) Status.Text = message; });
                byte[] output = await client.EditAsync(profile, key, png, prompt, progress, cancellation.Token);
                BitmapSource image = await Task.Run(() => AiImageClient.DecodeImage(output), cancellation.Token);
                if (_closed || cancellation.IsCancellationRequested) return;
                Result = image; ResultBounds = bounds; ResultProvider = profile.Provider.Name;
                ResultPreview.Source = image; ResultPlaceholder.Visibility = Visibility.Collapsed;
                ResultLabel.Text = $"처리 결과 · {image.PixelWidth} × {image.PixelHeight} px";
                Status.Text = "처리했습니다. 결과를 확인한 뒤 새 레이어로 추가하세요.";
            }
            catch (OperationCanceledException)
            {
                if (!_closed) Status.Text = cancellation.IsCancellationRequested
                    ? "요청 대기를 중단했습니다. 이미 접수된 처리는 제공사에서 완료·과금될 수 있습니다."
                    : "8분 안에 완료되지 않아 대기를 중단했습니다. 재시도 전에 제공사 처리 내역을 확인해 주세요.";
            }
            catch (Exception ex) { if (!_closed) Status.Text = AiImageClient.ErrorMessage(ex); }
            finally
            {
                _request = null; cancellation.Dispose();
                if (!_closed) SetBusy(false);
            }
        }
        private void OnCancelRequest(object sender, RoutedEventArgs e)
        { _request?.Cancel(); CancelRequest.IsEnabled = false; Status.Text = "취소하고 있습니다…"; }
        private void OnClose(object sender, RoutedEventArgs e) => Close();
        private void OnApply(object sender, RoutedEventArgs e) { if (Result != null) DialogResult = true; }
    }
}
