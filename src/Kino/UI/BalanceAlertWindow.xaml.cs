using System.Windows;
using Kino.Models;

namespace Kino.UI
{
    public partial class BalanceAlertWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly AppSettings _original;
        private bool _closed;
        public Func<AppSettings, string> SaveSettings { get; set; }
        public BalanceAlertWindow(AppSettings settings)
        {
            _original = settings.Clone(); InitializeComponent();
            Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16));
            SiteLabel.Text = "站点：" + settings.SiteName; AlertEditor.SetOptions(settings.BalanceAlert);
            Closed += (_, __) => _closed = true;
        }
        private void SaveClicked(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            try
            {
                var candidate = _original.Clone(); candidate.BalanceAlert = AlertEditor.ReadOptions(); candidate.CaptureActiveSite();
                var error = SaveSettings == null ? "暂时无法保存，请重新打开提醒设置。" : SaveSettings(candidate);
                if (error != null) { SaveMessage.Text = error; return; } Close();
            }
            catch (ArgumentException ex) { SaveMessage.Text = ex.Message; }
        }
        private void CancelClicked(object sender, RoutedEventArgs e) => Close();
    }
}
