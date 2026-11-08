using System.Windows;
using System.Windows.Controls;
using Kino.Models;

namespace Kino.UI
{
    public partial class SitesWindow : Wpf.Ui.Controls.FluentWindow
    {
        private sealed class SiteRow
        {
            public SiteProfile Profile { get; set; }
            public string Title { get; set; }
            public string Summary => Profile.Summary;
        }
        private AppSettings _settings;
        private bool _closed, _updating;
        private string _deleteId;
        public Func<AppSettings, string> SaveSettings { get; set; }
        public Func<string, string> TestNotification { get; set; }
        internal ConnectionWindow EditorWindow { get; private set; }
        internal BalanceAlertWindow AlertWindow { get; private set; }
        internal bool IsEditing => EditorWindow != null || AlertWindow != null;
        private SiteProfile Selected => (SitesList.SelectedItem as SiteRow)?.Profile;

        public SitesWindow(AppSettings settings)
        {
            InitializeComponent();
            Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16));
            UpdateSettings(settings);
            Closed += (_, __) => { _closed = true; EditorWindow?.Close(); AlertWindow?.Close(); };
        }
        internal void UpdateSettings(AppSettings settings, string selectedId = null)
        {
            selectedId = selectedId ?? Selected?.Id;
            _settings = settings.Clone(); _settings.CaptureActiveSite();
            var rows = _settings.Sites.Select(s => new SiteRow { Profile = s, Title = s.DisplayName + (s.Id == _settings.ActiveSiteId ? " · 当前" : "") }).ToList();
            SitesList.ItemsSource = rows;
            SitesList.SelectedItem = rows.FirstOrDefault(r => r.Profile.Id == selectedId) ?? rows.FirstOrDefault(r => r.Profile.Id == _settings.ActiveSiteId) ?? rows.FirstOrDefault();
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDetails();
        }
        private void SiteSelected(object sender, SelectionChangedEventArgs e) => UpdateDetails();
        private void UpdateDetails()
        {
            if (DetailsPanel == null) return;
            _updating = true; _deleteId = null; DeletePrompt.Visibility = Visibility.Collapsed;
            var site = Selected; DetailsPanel.Visibility = site == null ? Visibility.Collapsed : Visibility.Visible;
            ProfileNameBox.Text = site?.DisplayName ?? ""; ProtocolText.Text = site?.ProtocolLabel ?? ""; AddressText.Text = site?.BaseUrl ?? "";
            ActiveLabel.Text = site?.Id == _settings?.ActiveSiteId ? "● 当前正在监测" : "已保存 · 可切换";
            UseSiteButton.IsEnabled = site != null && site.Id != _settings.ActiveSiteId;
            AlertSummary.Text = site?.BalanceAlert?.Enabled == true ? "低余额提醒已开启 · " + site.BalanceAlert.CooldownMinutes + " 分钟内不重复提醒" : "低余额提醒未开启";
            RenameButton.IsEnabled = false; _updating = false;
        }
        private void NameChanged(object sender, TextChangedEventArgs e)
        { if (!_updating && RenameButton != null) RenameButton.IsEnabled = Selected != null && !string.IsNullOrWhiteSpace(ProfileNameBox.Text) && ProfileNameBox.Text.Trim() != Selected.SiteName; }
        private bool Commit(AppSettings candidate, string selectedId, string message)
        {
            if (_closed) return false;
            var error = SaveSettings == null ? "暂时无法保存，请重新打开站点管理。" : SaveSettings(candidate);
            if (error != null) { ResultText.Text = error; return false; }
            UpdateSettings(candidate, selectedId); ResultText.Text = message; return true;
        }
        private void RenameClicked(object sender, RoutedEventArgs e)
        {
            if (Selected == null || string.IsNullOrWhiteSpace(ProfileNameBox.Text)) return;
            var candidate = _settings.Clone(); var site = candidate.Sites.First(s => s.Id == Selected.Id);
            site.SiteName = ProfileNameBox.Text.Trim();
            if (site.Id == candidate.ActiveSiteId) site.ApplyTo(candidate);
            Commit(candidate, site.Id, "站点名称已更新。");
        }
        private void UseClicked(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            var candidate = _settings.Clone(); if (!candidate.SelectSite(Selected.Id)) return;
            Commit(candidate, Selected.Id, "已切换到 " + Selected.DisplayName + "。");
        }
        private void DeleteClicked(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            _deleteId = Selected.Id; DeletePromptText.Text = "删除「" + Selected.DisplayName + "」及其本机凭据？";
            DeletePrompt.Visibility = Visibility.Visible;
        }
        private void CancelDeleteClicked(object sender, RoutedEventArgs e) { _deleteId = null; DeletePrompt.Visibility = Visibility.Collapsed; }
        private void ConfirmDeleteClicked(object sender, RoutedEventArgs e)
        {
            if (_deleteId == null) return;
            var candidate = _settings.Clone(); if (!candidate.RemoveSite(_deleteId)) return;
            Commit(candidate, candidate.ActiveSiteId, candidate.Sites.Count == 0 ? "站点已删除。点击新增站点重新连接。" : "站点已删除。");
        }
        private void AddClicked(object sender, RoutedEventArgs e) => BeginAddSite();
        internal void BeginAddSite() => OpenEditor(null);
        private void EditClicked(object sender, RoutedEventArgs e) { if (Selected != null) OpenEditor(Selected.Id); }
        private void OpenEditor(string id)
        {
            if (AlertWindow != null) { AlertWindow.Activate(); return; }
            if (EditorWindow != null) { EditorWindow.Activate(); return; }
            var activeId = _settings.ActiveSiteId;
            var draft = id == null ? _settings.NewSiteDraft() : _settings.Clone();
            if (id != null && !draft.SelectSite(id)) return;
            var editor = new ConnectionWindow(draft) { Owner = this };
            editor.ConfigureSiteEditor(id != null);
            editor.SaveSettings = candidate =>
            {
                if (_closed) return "站点管理已关闭，连接未保存。";
                candidate.CaptureActiveSite();
                if (id != null && id != activeId) candidate.SelectSite(activeId);
                return Commit(candidate, id ?? candidate.ActiveSiteId, id == null ? "站点已添加并设为当前。" : "连接已更新。") ? null : ResultText.Text;
            };
            EditorWindow = editor; ManagerContent.IsEnabled = false;
            editor.Closed += (_, __) => { EditorWindow = null; ManagerContent.IsEnabled = true; if (!_closed) Activate(); };
            editor.Show(); editor.Activate();
        }
        private void BalanceAlertClicked(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            if (EditorWindow != null) { EditorWindow.Activate(); return; }
            if (AlertWindow != null) { AlertWindow.Activate(); return; }
            var id = Selected.Id; var active = _settings.ActiveSiteId;
            var draft = _settings.Clone(); if (!draft.SelectSite(id)) return;
            var window = new BalanceAlertWindow(draft) { Owner = this };
            window.AlertEditor.SendTestNotification = () => TestNotification == null ? "预览模式不发送系统通知。" : TestNotification(draft.SiteName);
            window.SaveSettings = candidate =>
            {
                if (_closed) return "站点管理已关闭，提醒设置未保存。";
                candidate.CaptureActiveSite(); candidate.SelectSite(active);
                return Commit(candidate, id, "提醒设置已保存。仅检查当前正在监测的站点。") ? null : ResultText.Text;
            };
            AlertWindow = window; ManagerContent.IsEnabled = false;
            window.Closed += (_, __) => { AlertWindow = null; ManagerContent.IsEnabled = true; if (!_closed) Activate(); };
            window.Show(); window.Activate();
        }
    }
}
