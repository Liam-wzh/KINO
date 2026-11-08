using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Kino.Interop;
using Kino.Models;
using Kino.Services;
using Kino.UI;
using static Kino.Interop.NativeMethods;

namespace Kino
{
    public partial class App : Application
    {
        private readonly bool _uiSmokeTest;
        private Mutex _mutex;
        private SettingsStore _store;
        private MonitorController _controller;
        private ApiTransport _transport;
        private LowBalanceAlerts _balanceAlerts;
        private AlertHistoryStore _alertHistory;
        private TaskbarHost _taskbar;
        private FlyoutWindow _flyout;
        private SettingsWindow _settingsWindow;
        private ConnectionWindow _connectionWindow;
        private SitesWindow _sitesWindow;
        private DispatcherTimer _refreshTimer, _lifecycleTimer;
        private System.Windows.Forms.NotifyIcon _tray;
        private NativeMethods.Rect _lastAnchor;
        private bool _preview, _diagnostic, _openingSettings, _connectionPreview, _notificationPreview;
        private DateTime _lastClosed;
        private string _sentinel;

        static App()
        {
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
        }
        public App() { }
        internal App(bool uiSmokeTest) { _uiSmokeTest = uiSmokeTest; }
        protected override void OnStartup(StartupEventArgs e)
        {
            if (_uiSmokeTest) return;
            base.OnStartup(e);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _connectionPreview = e.Args.Contains("--connect-preview");
            _notificationPreview = e.Args.Contains("--notification-preview");
            if (_notificationPreview)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notification-preview.log"), "starting\n"); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            _preview = e.Args.Contains("--preview") || _connectionPreview || _notificationPreview; _diagnostic = e.Args.Contains("--diagnose");
            if (!_preview && !_diagnostic)
            {
                _mutex = new Mutex(true, "Local\\KINO.Taskbar." + Environment.UserName, out var created);
                if (!created) { MessageBox.Show("KINO 已在运行。可点击任务栏信息或托盘图标打开卡片。", ProductBrand.Name); Shutdown(); return; }
            }
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Write("UI exception: " + args.Exception.GetType().Name + " 0x" + args.Exception.HResult.ToString("X8"));
                args.Handled = true;
                MessageBox.Show("界面遇到错误，请退出后重试。诊断信息保存在本机 KINO 用户目录。", ProductBrand.Name);
            };
            _store = new SettingsStore();
            var settings = _connectionPreview ? new AppSettings() : _preview || _diagnostic ? AppSettings.DemoSettings() : _store.Load();
            ThemeService.Apply(settings.Theme);
            _transport = new ApiTransport(); _controller = new MonitorController(new UsageClient(_transport), settings);
            _alertHistory = new AlertHistoryStore();
            _balanceAlerts = new LowBalanceAlerts(_preview || _diagnostic ? null : _alertHistory.Load());
            _balanceAlerts.KeepSites(_controller.Settings.Sites.Select(s => s.Id));
            _controller.Changed += ControllerChanged;
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(settings.RefreshSeconds) };
            _refreshTimer.Tick += async (_, __) => { if (!_controller.IsRefreshing && !_controller.Settings.RequiresConnectionSetup) await _controller.RefreshAsync(); };
            _refreshTimer.Start();
            _sentinel = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shutdown.sentinel");
            if (File.Exists(_sentinel)) File.Delete(_sentinel);
            _lifecycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            var ticks = 0;
            _lifecycleTimer.Tick += (_, __) =>
            {
                if (File.Exists(_sentinel)) { File.Delete(_sentinel); Shutdown(); return; }
                if ((_diagnostic || _notificationPreview) && ++ticks >= (_notificationPreview ? 20 : 8)) { Log.Write("Diagnostic completed"); Shutdown(); }
            };
            _lifecycleTimer.Start();
            SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
            if (!_preview)
            {
                _taskbar = new TaskbarHost(settings);
                _taskbar.Clicked += ToggleFlyout;
                _taskbar.ContextRequested += ShowMenu;
                _taskbar.AvailabilityChanged += value => { if (_tray != null) _tray.Text = value ? "KINO · 点击查看 AI 用量" : "KINO · 任务栏连接中，点击查看卡片"; };
                _taskbar.Start();
                if (!_diagnostic) CreateTray();
            }
            else if (_notificationPreview) CreateTray();
            Log.Write("Started " + (_preview ? "preview" : _diagnostic ? "diagnostic" : "normal") + "; provider=" + settings.Provider);
            _ = StartAsync();
        }

        private async Task StartAsync()
        {
            if (_connectionPreview) { ShowConnectionGuide(); return; }
            if (!_preview && !_diagnostic && _controller.Settings.RequiresConnectionSetup)
            {
                ShowConnectionGuide();
                return;
            }
            await _controller.RefreshAsync();
            if (_notificationPreview) { ShowTestNotification("演示站点"); return; }
            if (_preview || (!_diagnostic && _controller.Settings.Provider == ProviderKind.Demo)) ShowFlyout(DefaultAnchor(), _preview);
            if (!_preview && !string.IsNullOrEmpty(_store.LoadWarning)) MessageBox.Show(_store.LoadWarning, ProductBrand.Name);
        }
        private void CreateTray()
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/kino.ico"));
            using (var stream = resource.Stream)
            using (var icon = new System.Drawing.Icon(stream))
                _tray = new System.Windows.Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "KINO · 点击查看 AI 用量", Visible = true };
            _tray.MouseClick += (_, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleFlyout(DefaultAnchor());
                else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu(DefaultAnchor());
            }));
            _tray.BalloonTipShown += (_, __) => RecordNotificationPreview("shown");
            _tray.BalloonTipClicked += (_, __) => Dispatcher.BeginInvoke(new Action(() => { RecordNotificationPreview("clicked"); ShowFlyout(DefaultAnchor(), _preview); }));
        }
        private void ControllerChanged()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ControllerChanged)); return; }
            _taskbar?.Publish(_controller.Settings, _controller.Snapshot);
            _flyout?.Update(_controller.Settings, _controller.Snapshot, _controller.IsRefreshing);
            if (_preview || _diagnostic || _tray == null) return;
            var alert = _balanceAlerts.Observe(_controller.Settings, _controller.Snapshot, _controller.IsRefreshing);
            if (alert != null && SendNotification(alert.Title, alert.Message) == null)
            {
                _balanceAlerts.MarkSent(alert); SaveAlertHistory();
            }
        }
        private string SendNotification(string title, string message)
        {
            if ((_preview && !_notificationPreview) || _diagnostic) return "预览模式不发送系统通知。";
            if (_tray == null || !_tray.Visible) return "托盘图标尚未准备好，请稍后重试。";
            try
            {
                _tray.ShowBalloonTip(8000, title, message, System.Windows.Forms.ToolTipIcon.Warning); RecordNotificationPreview("submitted"); return null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            { Log.Write("System notification could not be submitted"); return "无法提交系统通知，请检查 Windows 通知设置后重试。"; }
        }
        private string ShowTestNotification(string siteName) => SendNotification("KINO · 通知测试", "这是「" + LowBalanceAlerts.NotificationName(siteName) + "」的测试提醒。\n点击查看当前站点的用量卡片。");
        private void RecordNotificationPreview(string status)
        {
            if (!_notificationPreview) return;
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notification-preview.log"), status + "\n"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
        private void SaveAlertHistory()
        {
            try { _alertHistory.Save(_balanceAlerts.History); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { Log.Write("Notification cooldown could not be saved"); }
        }
        private void ToggleFlyout(NativeMethods.Rect anchor)
        {
            if (_flyout != null) { _flyout.Close(); return; }
            if ((DateTime.UtcNow - _lastClosed).TotalMilliseconds < 180) return;
            ShowFlyout(anchor);
        }
        private void ShowFlyout(NativeMethods.Rect anchor, bool preview = false)
        {
            if (_connectionWindow != null) { _connectionWindow.Activate(); return; }
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            if (_sitesWindow != null) { _sitesWindow.Activate(); return; }
            if (_flyout != null) { _flyout.Activate(); return; }
            _lastAnchor = anchor;
            var window = new FlyoutWindow(); _flyout = window;
            window.SettingsRequested += ShowSettings;
            window.SiteSwitchRequested += SwitchSite;
            window.AddSiteRequested += ShowNewSite;
            window.ManageSitesRequested += ShowSites;
            window.RefreshRequested += async () => { if (!_controller.IsRefreshing) await _controller.RefreshAsync(); };
            window.PrivacyRequested += () =>
            {
                var candidate = _controller.Settings.Clone(); candidate.HideAmounts = !candidate.HideAmounts;
                var error = ApplySettings(candidate);
                if (error != null) MessageBox.Show(window, error, ProductBrand.Name);
            };
            window.Closed += (_, __) => { if (ReferenceEquals(_flyout, window)) _flyout = null; _lastClosed = DateTime.UtcNow; if (_preview && !_openingSettings) Shutdown(); };
            window.Update(_controller.Settings, _controller.Snapshot, _controller.IsRefreshing);
            window.Open(anchor, preview);
        }
        private void ShowSettings() => ShowSettingsFor(_controller.Settings);
        private void ShowSettingsFor(AppSettings initial)
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            _openingSettings = true;
            _sitesWindow?.Close();
            _connectionWindow?.Close();
            _flyout?.Close();
            _settingsWindow = new SettingsWindow(initial) { SaveSettings = ApplySettings, TestNotification = ShowTestNotification };
            _settingsWindow.ConnectionGuideRequested += ShowConnectionGuide;
            _settingsWindow.ManageSitesRequested += ShowSites;
            _settingsWindow.Closed += (_, __) => { _settingsWindow = null; if (_preview && !_openingSettings) Shutdown(); };
            _settingsWindow.Show(); _settingsWindow.Activate();
            _openingSettings = false;
        }
        private void ShowConnectionGuide() => OpenConnectionGuide(_controller.Settings);
        private void ShowNewSite()
        {
            if (_sitesWindow != null) { _sitesWindow.Activate(); _sitesWindow.BeginAddSite(); return; }
            OpenConnectionGuide(_controller.Settings.NewSiteDraft());
        }
        private void OpenConnectionGuide(AppSettings initial)
        {
            if (_connectionWindow != null) { _connectionWindow.Activate(); return; }
            _openingSettings = true;
            _settingsWindow?.Close(); _flyout?.Close();
            var window = new ConnectionWindow(initial) { SaveSettings = ApplySettings };
            _connectionWindow = window;
            window.AdvancedSettingsRequested += () => ShowSettingsFor(initial);
            window.Closed += (_, __) =>
            {
                if (ReferenceEquals(_connectionWindow, window)) _connectionWindow = null;
                if (window.IsCompleted) Dispatcher.BeginInvoke(new Action(() => ShowFlyout(DefaultAnchor(), _preview)));
                else if (_preview && !_openingSettings) Shutdown();
            };
            window.Show(); window.Activate();
            _openingSettings = false;
        }
        private string ApplySettings(AppSettings candidate)
        {
            candidate = candidate.Clone(); candidate.CaptureActiveSite();
            try { if (!_preview && !_diagnostic) _store.Save(candidate); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.Cryptography.CryptographicException)
            { return "无法保存设置，请检查本机用户目录权限。"; }
            _controller.ApplySettings(candidate); ThemeService.Apply(candidate.Theme);
            if (_balanceAlerts.KeepSites(candidate.Sites.Select(s => s.Id))) SaveAlertHistory();
            _refreshTimer.Interval = TimeSpan.FromSeconds(candidate.RefreshSeconds);
            _sitesWindow?.UpdateSettings(candidate);
            ControllerChanged();
            if (!candidate.RequiresConnectionSetup) _ = _controller.RefreshAsync();
            return null;
        }
        private bool CanSwitchSites => _settingsWindow == null && _connectionWindow == null && _sitesWindow?.IsEditing != true;
        private void SwitchSite(string id)
        {
            if (!CanSwitchSites || id == _controller.Settings.ActiveSiteId) return;
            var candidate = _controller.Settings.Clone();
            if (!candidate.SelectSite(id)) return;
            var error = ApplySettings(candidate);
            if (error != null) MessageBox.Show(error, ProductBrand.Name);
        }
        private void ShowSites()
        {
            if (_sitesWindow != null) { _sitesWindow.Activate(); return; }
            _openingSettings = true; _settingsWindow?.Close(); _connectionWindow?.Close(); _flyout?.Close();
            var window = new SitesWindow(_controller.Settings) { SaveSettings = ApplySettings, TestNotification = ShowTestNotification };
            _sitesWindow = window;
            window.Closed += (_, __) => { if (ReferenceEquals(_sitesWindow, window)) _sitesWindow = null; if (_preview && !_openingSettings) Shutdown(); };
            window.Show(); window.Activate(); _openingSettings = false;
        }
        private void ShowMenu(NativeMethods.Rect anchor)
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, typeof(ContextMenu));
            AddMenu(menu, "查看用量", () => ShowFlyout(anchor));
            AddMenu(menu, "立即刷新", () => { if (!_controller.IsRefreshing) _ = _controller.RefreshAsync(); });
            AddMenu(menu, "设置", ShowSettings);
            var sites = new MenuItem { Header = "切换站点" };
            SiteMenus.Populate(sites, _controller.Settings, SwitchSite, ShowNewSite, ShowSites, CanSwitchSites);
            menu.Items.Add(sites);
            AddMenu(menu, "管理站点", ShowSites);
            menu.Items.Add(new Separator());
            AddMenu(menu, "退出 KINO", Shutdown);
            GetCursorPos(out var cursor);
            var scale = NativeMethods.Dpi(FindWindow("Shell_TrayWnd", null)) / 96d;
            menu.Placement = PlacementMode.AbsolutePoint; menu.HorizontalOffset = cursor.X / scale; menu.VerticalOffset = cursor.Y / scale;
            menu.IsOpen = true;
        }
        private static void AddMenu(ContextMenu menu, string title, Action action)
        {
            var item = new MenuItem { Header = title }; item.Click += (_, __) => action(); menu.Items.Add(item);
        }
        private static NativeMethods.Rect DefaultAnchor()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var rect))
                return new NativeMethods.Rect { Left = rect.Right - 440, Right = rect.Right - 32, Top = rect.Top, Bottom = rect.Bottom };
            var area = SystemParameters.WorkArea;
            return new NativeMethods.Rect { Left = (int)area.Right - 440, Right = (int)area.Right - 25, Top = (int)area.Bottom, Bottom = (int)area.Bottom + 40 };
        }
        private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => { if (_controller != null) { ThemeService.Apply(_controller.Settings.Theme); ControllerChanged(); } }));
        }
        protected override void OnExit(ExitEventArgs e)
        {
            if (_uiSmokeTest) { base.OnExit(e); return; }
            SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
            _refreshTimer?.Stop(); _lifecycleTimer?.Stop(); _controller?.Dispose();
            _taskbar?.Dispose(); _transport?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); }
            if (_mutex != null) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _mutex.Dispose(); }
            Log.Write("Stopped"); base.OnExit(e);
        }
    }
}
