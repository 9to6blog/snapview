using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SnapView.Core;

namespace SnapView.Prefs
{
    public partial class AiSettingsWindow : Window
    {
        private readonly AiSettingsStore _store;
        private readonly AiPreferences _preferences;
        private AiProfile? _current;
        private bool _changing;
        internal AiSettingsWindow(AiSettingsStore? store = null, string? selectedProvider = null)
        {
            _store = store ?? new AiSettingsStore();
            _preferences = _store.Load();
            InitializeComponent();
            ProviderBox.ItemsSource = AiProvider.All;
            ProviderBox.SelectedItem = AiProvider.Find(selectedProvider ?? _preferences.SelectedProvider);
            Closed += (_, _) => KeyBox.Clear();
        }
        private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_changing || ProviderBox.SelectedItem is not AiProvider provider) return;
            try { CaptureDraft(); }
            catch (Exception ex)
            {
                Status.Text = SafeSettingsError(ex);
                _changing = true; ProviderBox.SelectedItem = _current?.Provider; _changing = false; return;
            }
            _current = _preferences.Get(provider.Id);
            KeyBox.Clear();
            ModelBox.ItemsSource = provider.Models; ModelBox.Text = _current.Model;
            ModelBox.IsEnabled = provider.Id != "stability";
            EndpointBox.Text = provider.Id == "compatible" ? _current.Endpoint : provider.Endpoint;
            EndpointBox.IsReadOnly = provider.Id != "compatible";
            ProviderNote.Text = provider.Note;
            UpdateKeyState(); Status.Text = "";
        }
        private void UpdateKeyState() => KeyState.Text = string.IsNullOrEmpty(_current?.ProtectedKey)
            ? "저장한 키가 없습니다. 해당 제공사의 API 키를 입력해 주세요."
            : "암호화된 키가 있습니다. 비워 두면 유지하고, 입력하면 교체합니다.";
        private void CaptureDraft()
        {
            if (_current == null) return;
            var draft = _current.Clone();
            draft.Model = ModelBox.Text.Trim();
            draft.Endpoint = EndpointBox.Text.Trim();
            string newKey = KeyBox.Password.Trim();
            if (draft.ProviderId == "compatible" && draft.Endpoint.Length == 0 && newKey.Length == 0 && string.IsNullOrEmpty(draft.ProtectedKey))
            { _current.Model = draft.Model; _current.Endpoint = ""; return; }
            draft.Endpoint = draft.BaseUri.AbsoluteUri;
            if (draft.Model.Length == 0) throw new AiRequestException("모델 ID를 입력해 주세요.");
            if (newKey.Length > 0) draft.SetKey(newKey);
            else if (!string.IsNullOrEmpty(draft.ProtectedKey) && draft.KeyScope != _current.KeyScope)
                throw new AiRequestException("API 주소를 바꿀 때는 새 주소에 사용할 키를 다시 입력해 주세요.");
            _current.Endpoint = draft.Endpoint; _current.Model = draft.Model; _current.ProtectedKey = draft.ProtectedKey;
            KeyBox.Clear();
        }
        private void OnSave(object sender, RoutedEventArgs e)
        {
            try
            {
                CaptureDraft();
                _preferences.SelectedProvider = _current?.ProviderId ?? "openai";
                _store.Save(_preferences); DialogResult = true;
            }
            catch (Exception ex) { Status.Text = SafeSettingsError(ex); }
        }
        private void OnDeleteKey(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _current.ProtectedKey = ""; KeyBox.Clear(); UpdateKeyState();
            Status.Text = "설정 저장을 누르면 이 제공사의 키가 삭제됩니다.";
        }
        private void OnDocumentation(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            try { Process.Start(new ProcessStartInfo(_current.Provider.Documentation) { UseShellExecute = true }); }
            catch { Status.Text = "브라우저에서 문서를 열지 못했습니다."; }
        }
        private static string SafeSettingsError(Exception ex) => ex is AiRequestException ? ex.Message
            : ex is System.Security.Cryptography.CryptographicException ? "키 암호화에 실패했습니다. 이 Windows 계정에서 다시 시도해 주세요."
            : "AI 설정을 저장하지 못했습니다. 설정 폴더의 읽기·쓰기 권한을 확인해 주세요.";
    }
}
