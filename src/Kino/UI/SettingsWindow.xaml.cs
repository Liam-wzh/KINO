using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Kino.Models;
using Kino.Services;

namespace Kino.UI
{
    public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
    {
        private sealed class Option { public MetricKind Kind; public bool Enabled; }
        private readonly AppSettings _original;
        private readonly List<Option> _taskbar, _card;
        private readonly CancellationTokenSource _testCancellation = new CancellationTokenSource();
        private bool _loaded, _dirty;
        public Func<AppSettings, string> SaveSettings { get; set; }
        public event Action ConnectionGuideRequested;
        public event Action ManageSitesRequested;
        public Func<string, string> TestNotification { get; set; }
        internal Func<Uri, bool> OpenLink { get; set; } = BrowserLauncher.Open;

        public SettingsWindow(AppSettings settings)
        {
            _original = settings.Clone();
            _taskbar = BuildOptions(settings.TaskbarMetrics); _card = BuildOptions(settings.CardMetrics);
            InitializeComponent();
            VersionText.Text = ProductBrand.Name + "  " + ProductBrand.Version;
            Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16));
            ProviderBox.SelectedIndex = (int)settings.Provider; Sub2AuthBox.SelectedIndex = (int)settings.Sub2Auth;
            SiteNameBox.Text = settings.SiteName; UrlBox.Text = settings.BaseUrl; TokenBox.Password = settings.Token;
            UserIdBox.Text = settings.NewApiUserId == 0 ? "" : settings.NewApiUserId.ToString();
            QuotaBox.Text = settings.QuotaPerUnit.ToString(CultureInfo.InvariantCulture);
            AutoQuotaBox.IsChecked = settings.AutoQuotaPerUnit; ChartBox.IsChecked = settings.ShowChart; ModelsBox.IsChecked = settings.ShowModels;
            ThemeBox.SelectedIndex = (int)settings.Theme; IntervalBox.Text = settings.RefreshSeconds.ToString();
            OffsetBox.Text = settings.TaskbarOffset.ToString(); PrivacyBox.IsChecked = settings.HideAmounts;
            AlertEditor.SetOptions(settings.BalanceAlert); AlertSiteLabel.Text = "当前站点：" + settings.SiteName;
            AlertEditor.SendTestNotification = () => TestNotification == null ? "预览模式不发送系统通知。" : TestNotification(SiteNameBox.Text);
            _loaded = true; Navigation.SelectedIndex = 0; UpdateProvider(); RebuildOptions();
            AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, __) => _dirty = true));
            TokenBox.PasswordChanged += (_, __) => _dirty = true;
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, args) => { if (args.OriginalSource is ComboBox) _dirty = true; }));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, __) => _dirty = true));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, __) => _dirty = true));
            Closed += (_, __) => _testCancellation.Cancel();
        }

        private static List<Option> BuildOptions(IReadOnlyList<MetricKind> selected) => selected.Concat(Metrics.All.Where(m => !selected.Contains(m)))
            .Select(m => new Option { Kind = m, Enabled = selected.Contains(m) }).ToList();

        private void ProviderChanged(object sender, SelectionChangedEventArgs e)
        { if (_loaded) { ClearPreviousCredentials(); UpdateProvider(); } }
        private void AddressChanged(object sender, TextChangedEventArgs e)
        { if (_loaded) ClearPreviousCredentials(); }
        private void ClearPreviousCredentials()
        {
            TokenBox.Clear(); UserIdBox.Clear(); AutoQuotaBox.IsChecked = true; QuotaBox.Text = "500000";
            TestResult.Text = "站点或认证方式已更改，请重新填写对应凭据。";
        }
        private void UpdateProvider()
        {
            var provider = (ProviderKind)ProviderBox.SelectedIndex;
            CredentialsPanel.Visibility = provider == ProviderKind.Demo ? Visibility.Collapsed : Visibility.Visible;
            NewApiPanel.Visibility = provider == ProviderKind.NewApi ? Visibility.Visible : Visibility.Collapsed;
            Sub2AuthPanel.Visibility = provider == ProviderKind.Sub2Api ? Visibility.Visible : Visibility.Collapsed;
            if (provider == ProviderKind.NewApi)
            {
                TokenLabel.Text = "用户访问令牌";
                TokenHelp.Text = "在站点的个人设置 / 安全设置中创建用户访问令牌。新版本需勾选 profile:read 和 usage:read。模型调用用的 sk- Key 无法读取账户信息。";
                ConnectionHint.Text = "读取账户余额与数据看板。今日消费、Token 和请求数需要站点开启数据看板，并授予读取权限。";
            }
            else if (provider == ProviderKind.Sub2Api && Sub2AuthBox.SelectedIndex == 0)
            {
                TokenLabel.Text = "API Key";
                TokenHelp.Text = "填写 Sub2API 中生成的 API Key。用量按当前 Key 统计，余额按钱包、Key 限额或订阅展示。";
                ConnectionHint.Text = "使用 /v1/usage 查询。旧版本可能未提供全部统计，缺少的字段显示为 —。";
            }
            else if (provider == ProviderKind.Sub2Api)
            {
                TokenLabel.Text = "账户访问令牌（JWT）";
                TokenHelp.Text = "填写已登录账户的访问令牌。令牌到期后需重新填写；此版本不保存密码或自动续期。";
                ConnectionHint.Text = "读取整个账户的余额、今日消费、Token 和模型统计。今日口径由站点时区决定。";
            }
            else ConnectionHint.Text = "使用内置演示数据预览任务栏和卡片，不产生网络请求。连接真实站点后，演示数据会被清除。";
        }

        private void PageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded || !(Navigation.SelectedItem is ListBoxItem item)) return;
            var page = int.Parse((string)item.Tag);
            ConnectionPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
            DisplayPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
            AppearancePage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
            AlertsPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageTitle.Text = new[] { "站点连接", "显示内容", "外观与刷新", "余额提醒" }[page];
            PageDescription.Text = new[] { "连接你的中转站，在任务栏查看用量。", "让你关心的信息，出现在合适的位置。", "按你的使用习惯，调整这张小卡片。", "为当前站点设置提醒，及时关注可用余额。" }[page];
            e.Handled = true;
        }

        private void RebuildOptions()
        {
            BuildRows(TaskbarOptions, _taskbar); BuildRows(CardOptions, _card); UpdatePreview();
        }
        private void BuildRows(StackPanel host, List<Option> options)
        {
            host.Children.Clear();
            foreach (var option in options)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Height = 36 };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var check = new CheckBox { Content = Metrics.Name(option.Kind), IsChecked = option.Enabled, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                check.Click += (_, __) => { option.Enabled = check.IsChecked == true; UpdatePreview(); };
                row.Children.Add(check);
                var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                arrows.Children.Add(Arrow("\uE70E", options, option, -1)); arrows.Children.Add(Arrow("\uE70D", options, option, 1));
                Grid.SetColumn(arrows, 1); row.Children.Add(arrows); host.Children.Add(row);
            }
        }
        private Button Arrow(string glyph, List<Option> options, Option option, int direction)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 9 },
                Style = (Style)FindResource("ChromeButtonStyle"), Width = 22, Height = 26,
                ToolTip = direction < 0 ? "上移" : "下移", IsEnabled = options.IndexOf(option) + direction >= 0 && options.IndexOf(option) + direction < options.Count
            };
            button.Click += (_, __) =>
            {
                _dirty = true; var index = options.IndexOf(option); options.RemoveAt(index); options.Insert(index + direction, option); RebuildOptions();
            };
            return button;
        }
        private void UpdatePreview()
        {
            var selected = _taskbar.Where(o => o.Enabled).Select(o => o.Kind).ToList();
            var demo = UsageClient.Demo();
            PreviewLine1.Text = string.Join("    ", selected.Where((_, i) => i % 2 == 0).Select(m => Metrics.ShortName(m) + " " + Metrics.Value(m, demo, PrivacyBox.IsChecked == true)));
            PreviewLine2.Text = string.Join("    ", selected.Where((_, i) => i % 2 == 1).Select(m => Metrics.ShortName(m) + " " + Metrics.Value(m, demo, PrivacyBox.IsChecked == true)));
        }

        private AppSettings ReadSettings()
        {
            var settings = _original.Clone();
            settings.Provider = (ProviderKind)ProviderBox.SelectedIndex; settings.Sub2Auth = (Sub2AuthKind)Math.Max(0, Sub2AuthBox.SelectedIndex);
            settings.SiteName = SiteNameBox.Text; settings.BaseUrl = UrlBox.Text; settings.Token = TokenBox.Password;
            settings.ProtectedToken = "";
            if (settings.Provider == ProviderKind.Demo) settings.Token = "";
            if (!int.TryParse(string.IsNullOrWhiteSpace(UserIdBox.Text) ? "0" : UserIdBox.Text, out var userId) || userId < 0)
                throw new ArgumentException("用户 ID 必须是大于或等于 0 的整数。");
            settings.NewApiUserId = userId;
            if (!decimal.TryParse(QuotaBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var quota) || quota <= 0)
                throw new ArgumentException("额度换算比例必须大于 0。");
            settings.QuotaPerUnit = quota; settings.AutoQuotaPerUnit = AutoQuotaBox.IsChecked == true;
            if (!int.TryParse(IntervalBox.Text, out var interval) || interval < 15 || interval > 3600)
                throw new ArgumentException("刷新间隔请填写 15–3600 秒。");
            settings.RefreshSeconds = interval;
            if (!int.TryParse(OffsetBox.Text, out var offset) || offset < 0 || offset > 1200)
                throw new ArgumentException("偏移请填写 0–1200 DIP。");
            settings.TaskbarOffset = offset;
            settings.Theme = (ThemeKind)ThemeBox.SelectedIndex; settings.HideAmounts = PrivacyBox.IsChecked == true;
            settings.ShowChart = ChartBox.IsChecked == true; settings.ShowModels = ModelsBox.IsChecked == true;
            settings.BalanceAlert = AlertEditor.ReadOptions();
            settings.TaskbarMetrics = _taskbar.Where(o => o.Enabled).Select(o => o.Kind).ToList();
            settings.CardMetrics = _card.Where(o => o.Enabled).Select(o => o.Kind).ToList();
            if (settings.TaskbarMetrics.Count == 0) throw new ArgumentException("任务栏至少保留一个显示项。");
            settings.Normalize();
            var error = settings.ValidateConnection();
            if (error != null) throw new ArgumentException(error);
            return settings;
        }

        private async void TestClicked(object sender, RoutedEventArgs e)
        {
            AppSettings candidate;
            try { candidate = ReadSettings(); }
            catch (ArgumentException ex) { TestResult.Text = ex.Message; return; }
            TestButton.IsEnabled = false; TestResult.Text = "正在连接…";
            try
            {
                using (var transport = new ApiTransport())
                {
                    var data = await new UsageClient(transport).FetchAsync(candidate, _testCancellation.Token);
                    TestResult.Text = (data.IsDemo ? "演示模式正常" : "连接成功") + " · " + data.Scope + "\n" + data.BalanceLabel + " " + Metrics.Value(MetricKind.Balance, data, candidate.HideAmounts) +
                        (string.IsNullOrEmpty(data.Note) ? "" : "\n" + data.Note);
                }
            }
            catch (OperationCanceledException) { if (!_testCancellation.IsCancellationRequested) TestResult.Text = "连接超时，请检查网络与站点地址。"; }
            catch (ApiException ex) { TestResult.Text = ex.Message; }
            catch (HttpRequestException) { TestResult.Text = "无法连接站点，请检查网络和地址。"; }
            catch (Exception) { TestResult.Text = "无法读取接口数据，请检查服务端版本与返回格式。"; }
            finally { TestButton.IsEnabled = true; }
        }
        private void SaveClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidate = ReadSettings();
                var error = SaveSettings?.Invoke(candidate);
                if (error != null) { SaveMessage.Text = error; return; }
                Close();
            }
            catch (ArgumentException ex) { SaveMessage.Text = ex.Message; }
        }
        private void CancelClicked(object sender, RoutedEventArgs e) => Close();
        private void GuideClicked(object sender, RoutedEventArgs e) => ConnectionGuideRequested?.Invoke();
        private void ManageSitesClicked(object sender, RoutedEventArgs e)
        {
            if (_dirty) { SaveMessage.Text = "请先保存或取消当前修改，再打开站点管理。"; return; }
            ManageSitesRequested?.Invoke();
        }
        private void SecurityClicked(object sender, RoutedEventArgs e)
        {
            var candidate = _original.Clone(); candidate.BaseUrl = UrlBox.Text.Trim(); candidate.Provider = ProviderKind.NewApi;
            candidate.Token = "url-validation-placeholder";
            if (candidate.ValidateConnection() != null) { TestResult.Text = "请先填写有效的 HTTPS 站点地址。"; return; }
            if (!OpenLink(ApiTransport.Endpoint(candidate, "security"))) TestResult.Text = "无法打开浏览器，请在站点网页进入安全设置。";
        }
    }
}
