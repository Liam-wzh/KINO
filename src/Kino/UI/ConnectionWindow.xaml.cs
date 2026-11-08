using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Kino.Models;
using Kino.Services;

namespace Kino.UI
{
    public partial class ConnectionWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly AppSettings _original;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private bool _loaded, _updatingPreset, _targetChanged, _connecting, _closed;
        private string _actionLabel = "验证并连接";
        public Func<AppSettings, string> SaveSettings { get; set; }
        public bool IsCompleted { get; private set; }
        public event Action AdvancedSettingsRequested;
        internal Func<AppSettings, CancellationToken, Task<UsageSnapshot>> VerifyConnection { get; set; } = VerifyAsync;
        internal Func<Uri, bool> OpenLink { get; set; } = BrowserLauncher.Open;
        internal Task PendingConnection { get; private set; } = Task.CompletedTask;

        public ConnectionWindow(AppSettings settings)
        {
            _original = settings.Clone();
            InitializeComponent();
            Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16));
            PresetBox.ItemsSource = new[] { new SitePreset("自定义站点", "", ProviderKind.NewApi) }.Concat(SitePreset.Available);
            PresetBox.SelectedIndex = 0;
            ProviderBox.SelectedIndex = settings.Provider == ProviderKind.Sub2Api ? 1 : 0;
            Sub2AuthBox.SelectedIndex = (int)settings.Sub2Auth;
            UrlBox.Text = settings.BaseUrl; SiteNameBox.Text = settings.SiteName;
            if (settings.Provider != ProviderKind.Demo)
            {
                TokenBox.Password = settings.Token;
                UserIdBox.Text = settings.NewApiUserId > 0 ? settings.NewApiUserId.ToString() : "";
            }
            _loaded = true; UpdateProvider();
            Closed += (_, __) => { _closed = true; _cancellation.Cancel(); TokenBox.Clear(); };
        }

        private ProviderKind SelectedProvider => ProviderBox.SelectedIndex == 1 ? ProviderKind.Sub2Api : ProviderKind.NewApi;

        internal void ConfigureSiteEditor(bool editing)
        {
            Title = editing ? "编辑站点 · KINO" : "新增站点 · KINO";
            IntroTitle.Text = editing ? "编辑站点连接" : "添加一个中转站";
            _actionLabel = editing ? "验证并保存" : "验证并连接"; ConnectButton.Content = _actionLabel;
            DemoButton.Visibility = AdvancedButton.Visibility = Visibility.Collapsed;
        }

        private AppSettings Candidate()
        {
            var settings = _original.Clone();
            settings.Provider = SelectedProvider;
            settings.Sub2Auth = (Sub2AuthKind)Math.Max(0, Sub2AuthBox.SelectedIndex);
            settings.SiteName = SiteNameBox.Text; settings.BaseUrl = UrlBox.Text;
            settings.Token = TokenBox.Password; settings.ProtectedToken = "";
            settings.NewApiUserId = 0;
            if (settings.Provider == ProviderKind.NewApi)
            {
                if (!int.TryParse(string.IsNullOrWhiteSpace(UserIdBox.Text) ? "0" : UserIdBox.Text, out var userId) || userId < 0)
                    throw new ArgumentException("用户 ID 请填写非负整数，或留空。");
                settings.NewApiUserId = userId;
            }
            if (_targetChanged) { settings.AutoQuotaPerUnit = true; settings.QuotaPerUnit = 500000m; }
            settings.Normalize();
            var error = settings.ValidateConnection();
            if (error != null) throw new ArgumentException(error);
            return settings;
        }

        private void ConnectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded || _updatingPreset) return;
            ClearPreviousCredentials(); PresetBox.SelectedIndex = 0; UpdateProvider();
        }

        private void AddressChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded || _updatingPreset) return;
            ClearPreviousCredentials(); SiteNameBox.Clear(); PresetBox.SelectedIndex = 0; UpdateProvider();
        }

        private void ClearPreviousCredentials()
        {
            _targetChanged = true; TokenBox.Clear(); UserIdBox.Clear();
            ResultBorder.Visibility = Visibility.Collapsed;
        }

        private void PresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded || PresetBox.SelectedIndex <= 0 || !(PresetBox.SelectedItem is SitePreset preset)) return;
            _updatingPreset = true;
            try
            {
                ClearPreviousCredentials();
                ProviderBox.SelectedIndex = preset.Provider == ProviderKind.Sub2Api ? 1 : 0;
                UrlBox.Text = preset.BaseUrl; SiteNameBox.Text = preset.Name;
            }
            finally { _updatingPreset = false; }
            UpdateProvider();
        }

        private void UpdateProvider()
        {
            var newApi = SelectedProvider == ProviderKind.NewApi;
            Sub2AuthPanel.Visibility = newApi ? Visibility.Collapsed : Visibility.Visible;
            ScopePanel.Visibility = NewApiOptions.Visibility = newApi ? Visibility.Visible : Visibility.Collapsed;
            SecurityButton.Content = newApi ? "打开安全设置  ↗" : "打开站点  ↗";
            SecurityButton.IsEnabled = TryBrowserTarget(out _);
            if (newApi)
            {
                TokenLabel.Text = "用户访问令牌";
                CredentialHelp.Text = "在站点的个人设置 / 安全设置中创建用户访问令牌。新版本勾选下列两项读取权限；各站点入口可能不同。";
                TokenHint.Text = "新式令牌通常以 nap_ 开头。模型调用用的 sk- Key 无法读取账户信息。";
            }
            else if (Sub2AuthBox.SelectedIndex == 1)
            {
                TokenLabel.Text = "账户访问令牌（JWT）";
                CredentialHelp.Text = "使用已登录账户的访问令牌，读取整个账户的余额与用量。";
                TokenHint.Text = "JWT 到期后需重新填写。KINO 不保存网页登录密码。";
            }
            else
            {
                TokenLabel.Text = "API Key";
                CredentialHelp.Text = "在站点生成 API Key，通过 /v1/usage 读取当前 Key 的用量。";
                TokenHint.Text = "支持 sk- Key。订阅或旧版本未提供的金额字段将显示为 —。";
            }
        }

        private bool TryBrowserTarget(out Uri target)
        {
            var settings = new AppSettings { Provider = SelectedProvider, BaseUrl = UrlBox.Text.Trim(), Token = "link-validation-placeholder" };
            target = null;
            if (settings.ValidateConnection() != null) return false;
            target = ApiTransport.Endpoint(settings, settings.Provider == ProviderKind.NewApi ? "security" : "");
            return true;
        }

        private void TokenChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded || _connecting || _closed) return;
            if (SelectedProvider == ProviderKind.NewApi && TokenBox.Password.Trim().StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                ShowResult("这是模型调用 Key。请在所填站点创建用户访问令牌。");
            else ResultBorder.Visibility = Visibility.Collapsed;
        }

        private async void ConnectClicked(object sender, RoutedEventArgs e)
        {
            if (_connecting || _closed) return;
            PendingConnection = ConnectAsync();
            await PendingConnection;
        }

        private async Task ConnectAsync()
        {
            AppSettings candidate;
            try { candidate = Candidate(); }
            catch (ArgumentException ex) { ShowResult(ex.Message); return; }
            SetConnecting(true);
            ShowResult("正在验证连接并读取用量…");
            try
            {
                var snapshot = await VerifyConnection(candidate, _cancellation.Token);
                if (_closed || _cancellation.IsCancellationRequested) return;
                if (!snapshot.Connected || snapshot.IsDemo)
                    throw new ApiException("账户未能完成验证，请检查凭据与权限后重试。");
                if (SaveSettings == null) { ShowResult("暂时无法保存连接设置，请重新打开向导。"); return; }
                var error = SaveSettings(candidate);
                if (error != null) { ShowResult(error); return; }
                IsCompleted = true;
                Close();
            }
            catch (OperationCanceledException) { if (!_closed) ShowResult("连接超时，请检查网络后重试。"); }
            catch (ApiException ex) { if (!_closed) ShowResult(ex.Message); }
            catch (HttpRequestException) { if (!_closed) ShowResult("无法连接站点，请检查网络与地址后重试。"); }
            catch (Exception) { if (!_closed) ShowResult("暂时无法验证账户，请稍后重试。"); }
            finally { if (!_closed) SetConnecting(false); }
        }

        private static async Task<UsageSnapshot> VerifyAsync(AppSettings settings, CancellationToken cancellation)
        {
            using (var transport = new ApiTransport())
                return await new UsageClient(transport).FetchAsync(settings, cancellation).ConfigureAwait(false);
        }

        private void SetConnecting(bool value)
        {
            _connecting = value;
            ConnectionForm.IsEnabled = DemoButton.IsEnabled = AdvancedButton.IsEnabled = ConnectButton.IsEnabled = !value;
            ConnectButton.Content = value ? "正在验证…" : _actionLabel;
        }
        private void ShowResult(string message) { ResultText.Text = message; ResultBorder.Visibility = Visibility.Visible; }
        private void SecurityClicked(object sender, RoutedEventArgs e)
        {
            if (!TryBrowserTarget(out var target)) { ShowResult("请先填写有效的 HTTPS 站点地址。"); return; }
            if (!OpenLink(target)) ShowResult("未能打开浏览器，请手动访问所填站点。");
        }
        private void AdvancedClicked(object sender, RoutedEventArgs e) => AdvancedSettingsRequested?.Invoke();
        private void DemoClicked(object sender, RoutedEventArgs e)
        {
            var settings = _original.Clone();
            settings.Provider = ProviderKind.Demo; settings.Token = settings.ProtectedToken = ""; settings.NewApiUserId = 0;
            if (settings.Sites.Count > 0)
            {
                settings = _original.NewSiteDraft();
                var existing = settings.Sites.FirstOrDefault(s => s.Provider == ProviderKind.Demo);
                if (existing != null) settings.SelectSite(existing.Id);
                settings.Provider = ProviderKind.Demo;
            }
            settings.BaseUrl = settings.Token = settings.ProtectedToken = ""; settings.NewApiUserId = 0; settings.SiteName = "演示站点";
            if (SaveSettings == null) { ShowResult("暂时无法保存设置，请重新打开向导。"); return; }
            var error = SaveSettings(settings);
            if (error != null) { ShowResult(error); return; }
            IsCompleted = true; Close();
        }
    }
}
