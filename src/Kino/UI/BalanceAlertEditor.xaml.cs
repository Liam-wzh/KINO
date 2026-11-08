using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kino.Models;

namespace Kino.UI
{
    public partial class BalanceAlertEditor : UserControl
    {
        public Func<string> SendTestNotification { get; set; }
        public BalanceAlertEditor() { InitializeComponent(); SetOptions(new BalanceAlertOptions()); }
        public void SetOptions(BalanceAlertOptions options)
        {
            var copy = (options ?? new BalanceAlertOptions()).Clone(); copy.Normalize();
            AlertEnabledBox.IsChecked = copy.Enabled; ThresholdBox.Text = copy.ThresholdUsd.ToString(CultureInfo.InvariantCulture);
            CooldownBox.Text = copy.CooldownMinutes.ToString(CultureInfo.InvariantCulture);
        }
        public BalanceAlertOptions ReadOptions() => BalanceAlertOptions.FromInput(AlertEnabledBox.IsChecked == true, ThresholdBox.Text, CooldownBox.Text);
        private void TestClicked(object sender, RoutedEventArgs e)
        {
            var error = SendTestNotification == null ? "预览模式不发送系统通知。" : SendTestNotification();
            TestMessage.Text = error ?? "测试通知已提交给 Windows；若未显示，请检查系统通知与勿扰设置。";
        }
    }
}
