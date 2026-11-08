using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Kino.Interop;
using Kino.Models;
using static Kino.Interop.NativeMethods;

namespace Kino.UI
{
    public partial class FlyoutWindow : Window
    {
        private AppSettings _settings;
        private NativeMethods.Rect _anchor;
        private bool _preview, _ready;
        private ContextMenu _siteMenu;
        private readonly Dictionary<MetricKind, TextBlock> _metricValues = new Dictionary<MetricKind, TextBlock>();
        private MetricKind[] _metricOrder = Array.Empty<MetricKind>();
        private readonly List<ModelRow> _modelRows = new List<ModelRow>();
        private string[] _modelNames = Array.Empty<string>();

        private sealed class ModelRow
        {
            public TextBlock Value;
            public ScaleTransform Scale;
            public ToolTip Detail;
            public double Proportion;
        }
        public bool IsPinned => PinButton.IsChecked == true;
        public event Action SettingsRequested;
        public event Action RefreshRequested;
        public event Action PrivacyRequested;
        public event Action<string> SiteSwitchRequested;
        public event Action AddSiteRequested;
        public event Action ManageSitesRequested;

        public FlyoutWindow()
        {
            InitializeComponent();
            Left = -10000; Top = -10000; Topmost = true;
            SourceInitialized += (_, __) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow);
                var corner = 2; DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
            };
            SizeChanged += (_, __) => { if (_ready && !IsPinned && !_preview) PositionAtAnchor(); };
            if (Environment.OSVersion.Version.Build < 22000)
            {
                Acrylic.MaterialMode = FluentWpfCore.Interop.MaterialType.None;
                AllowsTransparency = true;
            }
        }

        public void Update(AppSettings settings, UsageSnapshot snapshot, bool refreshing)
        {
            var connectionChanged = _settings == null || !_settings.SameConnection(settings);
            var privacyChanged = _settings != null && _settings.HideAmounts != settings.HideAmounts;
            var animate = IsVisible && !connectionChanged && !privacyChanged;
            _settings = settings.Clone();
            SiteText.Text = settings.SiteName;
            var balanceVisible = settings.CardMetrics.Contains(MetricKind.Balance);
            BalanceSection.Visibility = balanceVisible ? Visibility.Visible : Visibility.Collapsed;
            BalanceLabel.Visibility = balanceVisible ? Visibility.Visible : Visibility.Collapsed;
            BalanceLabel.Text = snapshot?.BalanceLabel ?? "可用余额";
            Motion.SetText(BalanceText, Metrics.Value(MetricKind.Balance, snapshot, settings.HideAmounts), animate && !settings.HideAmounts);
            PrivacyButton.IsChecked = settings.HideAmounts;
            PrivacyIcon.Text = settings.HideAmounts ? "\uED1A" : "\uE890";
            StatusText.Text = snapshot == null ? "等待连接" : snapshot.IsDemo ? "演示模式" : snapshot.IsStale ? "连接中断" : snapshot.Connected ? "已连接" : "未连接";
            var statusVisible = settings.CardMetrics.Contains(MetricKind.Connection) ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Visibility = StatusDot.Visibility = statusVisible;
            StatusDot.Fill = (Brush)FindResource(snapshot?.Connected == true ? "KinoAccentBrush" : "KinoWarningBrush");
            ScopeText.Text = snapshot?.Scope ?? (settings.Provider == ProviderKind.Demo ? "演示数据 · 未连接真实账户" : "等待获取账户数据");
            var metricOrder = settings.CardMetrics.Where(m => m != MetricKind.Balance && m != MetricKind.Connection).ToArray();
            if (connectionChanged || !_metricOrder.SequenceEqual(metricOrder))
            {
                MetricsGrid.Children.Clear(); _metricValues.Clear(); _metricOrder = metricOrder;
                foreach (var kind in metricOrder) MetricsGrid.Children.Add(CreateMetric(kind));
            }
            foreach (var metric in _metricValues)
                Motion.SetText(metric.Value, Metrics.Value(metric.Key, snapshot, settings.HideAmounts), animate && !settings.HideAmounts);
            MetricsGrid.Visibility = MetricsGrid.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ChartSection.Visibility = settings.ShowChart ? Visibility.Visible : Visibility.Collapsed;
            ChartLabel.Text = snapshot?.HistoryLabel ?? "消费趋势";
            ChartUnit.Text = settings.HideAmounts ? "相对趋势" : "USD";
            Chart.Update(snapshot?.History ?? new List<UsagePoint>(), settings.HideAmounts, connectionChanged);
            ModelsSection.Visibility = settings.ShowModels ? Visibility.Visible : Visibility.Collapsed;
            ModelsPeriodText.Text = snapshot?.ModelPeriodLabel ?? "Token";
            RenderModels(snapshot, settings.HideAmounts, animate, connectionChanged || privacyChanged);
            var notice = snapshot?.Error;
            if (string.IsNullOrEmpty(notice)) notice = snapshot?.Note;
            if (snapshot == null && settings.RequiresConnectionSetup) notice = "此站点尚未连接，请在站点管理中编辑连接，或新增一个站点。";
            NoticeText.Text = notice ?? "";
            NoticeBorder.Visibility = string.IsNullOrEmpty(notice) ? Visibility.Collapsed : Visibility.Visible;
            NoticeText.Foreground = (Brush)FindResource(string.IsNullOrEmpty(snapshot?.Error) ? "KinoMutedBrush" : "KinoWarningBrush");
            UpdatedText.Text = refreshing ? "正在更新…" : snapshot == null || snapshot.UpdatedAt == default ? "尚未更新" : (snapshot.IsStale ? "上次成功 " : "更新于 ") + snapshot.UpdatedAt.ToString(snapshot.UpdatedAt.LocalDateTime.Date == DateTime.Today ? "HH:mm:ss" : "MM/dd HH:mm:ss");
            RefreshButton.IsEnabled = !refreshing;
            ConsoleButton.IsEnabled = settings.Provider != ProviderKind.Demo && Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _);
            ConsoleButton.Opacity = ConsoleButton.IsEnabled ? 1 : .4;
            Acrylic.IsDarkMode = ThemeService.IsDark;
            Acrylic.CompositonColor = (Color)ColorConverter.ConvertFromString(ThemeService.IsDark ? "#E6222525" : "#E6F5F5F5");
            if (Environment.OSVersion.Version.Build < 22000) RootBorder.Background = new SolidColorBrush(ThemeService.IsDark ? Color.FromRgb(35, 38, 38) : Color.FromRgb(245, 245, 245));
        }

        private Border CreateMetric(MetricKind kind)
        {
            var panel = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
            var label = new TextBlock { Text = Metrics.Name(kind), FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "KinoMutedBrush");
            var value = new TextBlock { FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
            _metricValues.Add(kind, value);
            panel.Children.Add(label); panel.Children.Add(value);
            var border = new Border { Child = panel, Margin = new Thickness(4), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
            border.SetResourceReference(BackgroundProperty, "KinoSurfaceBrush");
            border.SetResourceReference(BorderBrushProperty, "KinoBorderBrush");
            if (kind == MetricKind.Latency) border.ToolTip = "查询用量接口的完整 HTTP 往返耗时，含网络与服务端处理。\n不是模型首 Token 延迟。";
            else if (kind == MetricKind.TodayTokens) border.ToolTip = "采用平台提供的 Token 统计口径；不同平台对缓存 Token 的计算可能不同。";
            return border;
        }

        private void RenderModels(UsageSnapshot snapshot, bool hide, bool animate, bool reset)
        {
            var models = snapshot?.Models ?? new List<ModelUsage>();
            var visible = models.Take(3).ToArray();
            var names = visible.Select(m => m.Name).ToArray();
            var rebuild = reset || !_modelNames.SequenceEqual(names) || ModelRows.Children.Count == 0;
            foreach (var row in _modelRows)
                if (rebuild) { row.Detail.IsOpen = false; row.Detail.Content = ""; }
            if (rebuild) { ModelRows.Children.Clear(); _modelRows.Clear(); _modelNames = names; }
            if (models.Count == 0)
            {
                if (rebuild)
                {
                    var empty = new TextBlock { Text = "暂无模型统计", FontSize = 11, Margin = new Thickness(0, 3, 0, 8) };
                    empty.SetResourceReference(ForegroundProperty, "KinoMutedBrush"); ModelRows.Children.Add(empty);
                }
                return;
            }
            var total = Math.Max(1L, models.Sum(m => m.Tokens));
            var colors = new[] { "#239681", "#6489B6", "#A494BF" };
            for (var index = 0; index < visible.Length; index++)
            {
                var model = visible[index];
                if (rebuild) _modelRows.Add(CreateModelRow(model.Name, colors[index]));
                var view = _modelRows[index];
                Motion.SetText(view.Value, Metrics.Compact(model.Tokens), animate);
                view.Proportion = model.Tokens <= 0 ? 0 : Math.Min(1, Math.Max(4d / 344, (double)model.Tokens / total));
                if (animate) Motion.Animate(view.Scale, ScaleTransform.ScaleXProperty, view.Proportion, 280);
                else { view.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); view.Scale.ScaleX = view.Proportion; }
                view.Detail.Content = model.Name + "\n" + model.Requests.ToString("N0") + " 次请求 · " + (hide ? "••••" : Metrics.Money(model.Spend));
            }
        }

        private ModelRow CreateModelRow(string modelName, string color)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition()); row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), HorizontalAlignment = HorizontalAlignment.Left };
            var name = new TextBlock { Text = modelName, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            var value = new TextBlock { FontSize = 11.5 };
            value.SetResourceReference(ForegroundProperty, "KinoMutedBrush");
            Grid.SetColumn(name, 1); Grid.SetColumn(value, 2);
            row.Children.Add(dot); row.Children.Add(name); row.Children.Add(value);
            var scale = new ScaleTransform(0, 1);
            var bar = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = dot.Fill, HorizontalAlignment = HorizontalAlignment.Left, Width = 344, RenderTransform = scale, Opacity = .45, VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetRow(bar, 1); Grid.SetColumn(bar, 1); Grid.SetColumnSpan(bar, 2); row.Children.Add(bar);
            var detail = new ToolTip(); row.ToolTip = detail;
            ModelRows.Children.Add(row);
            return new ModelRow { Value = value, Scale = scale, Detail = detail };
        }

        internal void Open(NativeMethods.Rect anchor, bool preview = false)
        {
            _anchor = anchor; _preview = preview;
            if (preview) PinButton.IsChecked = true;
            var monitor = MonitorAt(anchor);
            if (monitor.Work.Height > 0) MaxHeight = Math.Max(200, monitor.Work.Height * 96d / NativeMethods.Dpi(FindWindow("Shell_TrayWnd", null)) - 20);
            Motion.PrepareReveal(RootBorder);
            Show(); UpdateLayout(); _ready = true; PositionAtAnchor();
            Motion.Reveal(RootBorder);
            Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }
        private void PositionAtAnchor()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var dpi = NativeMethods.Dpi(handle) / 96d;
            var width = (int)Math.Ceiling(ActualWidth * dpi);
            var height = (int)Math.Ceiling(ActualHeight * dpi);
            var info = MonitorAt(_anchor);
            var work = info.Work;
            if (work.Width == 0) return;
            var gap = (int)(10 * dpi);
            var vertical = _anchor.Height > _anchor.Width;
            var x = _anchor.Right - width;
            var y = _anchor.Top - height - gap;
            if (vertical) { x = _anchor.Left > info.Monitor.Left + info.Monitor.Width / 2 ? _anchor.Left - width - gap : _anchor.Right + gap; y = _anchor.Bottom - height; }
            else if (_anchor.Top < info.Monitor.Top + info.Monitor.Height / 2) y = _anchor.Bottom + gap;
            x = Math.Max(work.Left + gap, Math.Min(x, work.Right - width - gap));
            y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - height - gap));
            SetWindowPos(handle, new IntPtr(-1), x, y, width, height, SwpNoActivate);
        }
        private void OnDeactivated(object sender, EventArgs e)
        {
            if (!IsPinned && _ready) Dispatcher.BeginInvoke(new Action(() => { if (!IsActive && IsVisible && !IsPinned && _siteMenu?.IsOpen != true) Close(); }));
        }
        private void SiteMenuClicked(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            var menu = new ContextMenu { PlacementTarget = SiteMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
            SiteMenus.Populate(menu, _settings, id => SiteSwitchRequested?.Invoke(id), () => AddSiteRequested?.Invoke(), () => ManageSitesRequested?.Invoke());
            _siteMenu = menu;
            menu.Closed += (_, __) => { if (ReferenceEquals(_siteMenu, menu)) _siteMenu = null; OnDeactivated(this, EventArgs.Empty); };
            menu.IsOpen = true;
        }
        private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
        private void HeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsPinned && e.LeftButton == MouseButtonState.Pressed && !(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase))
            {
                var source = e.OriginalSource as DependencyObject;
                while (source != null) { if (source is System.Windows.Controls.Primitives.ButtonBase) return; source = VisualTreeHelper.GetParent(source); }
                DragMove();
            }
        }
        private void PinClicked(object sender, RoutedEventArgs e) { }
        private void SettingsClicked(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
        private void RefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
        private void PrivacyClicked(object sender, RoutedEventArgs e) => PrivacyRequested?.Invoke();
        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
        private void ConsoleClicked(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _settings.Provider == ProviderKind.Demo) return;
            var uri = Services.ApiTransport.Endpoint(_settings, _settings.Provider == ProviderKind.NewApi ? "console" : "dashboard");
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { MessageBox.Show(this, "无法打开默认浏览器，请手动访问你的站点。", ProductBrand.Name); }
        }
    }
}
