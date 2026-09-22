using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace SnapView.Editor
{
    public partial class EditorWindow
    {
        private DispatcherTimer? _saveToastTimer;

        private void ShowSavedToast(string path, bool project = false)
        {
            if (_saveToastTimer == null)
            {
                _saveToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
                _saveToastTimer.Tick += (_, _) => HideSavedToast();
                Closed += (_, _) => HideSavedToast();
            }
            _saveToastTimer.Stop();
            SaveToastMessage.Text = project ? "프로젝트를 저장했습니다" : "저장했습니다";
            SaveToastPath.Text = path;
            SaveToast.Visibility = Visibility.Visible;
            // Announce the result without moving keyboard focus away from editing.
            if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                UIElementAutomationPeer.CreatePeerForElement(SaveToastMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            _saveToastTimer.Start();
        }

        private void HideSavedToast()
        {
            _saveToastTimer?.Stop();
            SaveToast.Visibility = Visibility.Collapsed;
        }
    }
}
