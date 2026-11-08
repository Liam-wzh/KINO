using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Kino;
using Kino.Models;
using Kino.Services;
using Kino.UI;
using Kino.Interop;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Events;
using LiveChartsCore.SkiaSharpView;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/previews");
            Directory.CreateDirectory(output);
            var app = new App(uiSmokeTest: true); app.InitializeComponent();
            Motion.EnabledOverride = false;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            foreach (var dark in new[] { false, true })
            {
                var settings = AppSettings.DemoSettings(); settings.Theme = dark ? ThemeKind.Dark : ThemeKind.Light;
                typeof(App).Assembly.GetType("Kino.UI.ThemeService").GetMethod("Apply", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { settings.Theme });
                var name = dark ? "dark" : "light";
                CheckFlyoutOpening(settings, output, name, animations: true);
                CheckFlyoutOpening(settings, output, name, animations: false);
                var flyout = new FlyoutWindow();
                flyout.Update(settings, UsageClient.Demo(), false);
                var initialChart = (UsageChart)flyout.FindName("Chart"); initialChart.FocusPoint(0);
                if (initialChart.HoverText != "") throw new Exception("Unmeasured chart must ignore early pointer or focus input");
                ((Border)flyout.FindName("RootBorder")).Background = Background(dark);
                flyout.Left = -10000; flyout.Top = -10000; flyout.Show();
                Save(flyout, Path.Combine(output, "card-" + name + ".png"));
                CheckChartAndMotion(flyout, settings, output, name);
                // A fully hidden metric layout must also render without stale data or layout errors.
                settings.CardMetrics.Clear(); settings.ShowChart = false; settings.ShowModels = false;
                flyout.Update(settings, new UsageSnapshot { Error = "无法连接站点，请检查网络和站点地址。" }, false);
                Save(flyout, Path.Combine(output, "card-minimal-" + name + ".png")); flyout.Close();
                var window = new SettingsWindow(new AppSettings());
                window.Background = Background(dark); ((Grid)window.Content).Background = Background(dark);
                window.Left = -10000; window.Top = -10000; window.Show();
                var navigation = (ListBox)window.FindName("Navigation");
                for (var page = 0; page < 4; page++)
                {
                    navigation.SelectedIndex = page;
                    Save(window, Path.Combine(output, "settings-" + page + "-" + name + ".png"));
                }
                // Exercise an actual selection and save through the UI's public controls.
                var settingsToken = (PasswordBox)window.FindName("TokenBox");
                settingsToken.Password = "fake-existing-site-token";
                ((TextBox)window.FindName("UserIdBox")).Text = "42";
                ((TextBox)window.FindName("UrlBox")).Text = "https://custom.example.test";
                if (settingsToken.Password != "" || ((TextBox)window.FindName("UserIdBox")).Text != "") throw new Exception("Changing a site in settings must clear the previous credentials");
                settingsToken.Password = "fake-new-site-token";
                var provider = (ComboBox)window.FindName("ProviderBox"); provider.SelectedIndex = 2;
                if (settingsToken.Password != "") throw new Exception("Changing a platform in settings must clear the previous token");
                ((ComboBox)window.FindName("Sub2AuthBox")).SelectedIndex = 1;
                if (!((TextBlock)window.FindName("TokenLabel")).Text.Contains("JWT")) throw new Exception("Provider help did not update");
                provider.SelectedIndex = 0;
                var called = false;
                window.SaveSettings = selected => { called = selected.Provider == ProviderKind.Demo; return "Test: keep window open"; };
                ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (!called) throw new Exception("Save UI did not produce settings");
                window.Close();
                CheckConnectionGuide(dark, output);
                CheckSiteManagement(dark, output);
                CheckSiteMenus(dark);
                CheckBalanceAlerts(dark, output);
            }
            app.Shutdown(); Console.WriteLine("PASS WPF themes, charts, privacy, opening stability, onboarding, settings, multi-site switching and balance alert preferences. Previews: " + output); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static Brush Background(bool dark) => new SolidColorBrush(dark ? Color.FromRgb(35, 38, 38) : Color.FromRgb(245, 245, 245));
    private sealed class OpeningFrame
    {
        public double Milliseconds, Opacity, ContentY;
        public NativeMethods.Rect Bounds;
    }
    private static void CheckFlyoutOpening(AppSettings settings, string output, string theme, bool animations)
    {
        Motion.EnabledOverride = animations;
        var flyout = new FlyoutWindow(); flyout.Update(settings, UsageClient.Demo(), false);
        var root = (Border)flyout.FindName("RootBorder");
        var frames = new List<OpeningFrame>(); var elapsed = Stopwatch.StartNew();
        NativeMethods.GetWindowRect(NativeMethods.FindWindow("Shell_TrayWnd", null), out var anchor);
        var work = NativeMethods.MonitorAt(anchor).Work;
        EventHandler capture = (_, __) =>
        {
            if (!flyout.IsVisible || root.ActualWidth <= 0) return;
            NativeMethods.GetWindowRect(new WindowInteropHelper(flyout).Handle, out var bounds);
            if (bounds.Right <= work.Left || bounds.Left >= work.Right || bounds.Bottom <= work.Top || bounds.Top >= work.Bottom) return;
            frames.Add(new OpeningFrame { Milliseconds = elapsed.Elapsed.TotalMilliseconds, Opacity = root.Opacity,
                ContentY = root.TransformToAncestor(flyout).Transform(new Point()).Y, Bounds = bounds });
        };
        CompositionTarget.Rendering += capture;
        try
        {
            flyout.Open(anchor);
            WaitUntil(() => elapsed.ElapsedMilliseconds >= 650 && frames.Count >= 3, "Opening animation did not render");
            var lines = new List<string> { "milliseconds,opacity,contentY,left,top,width,height" };
            lines.AddRange(frames.Select(f => string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F4},{2:F4},{3},{4},{5},{6}",
                f.Milliseconds, f.Opacity, f.ContentY, f.Bounds.Left, f.Bounds.Top, f.Bounds.Width, f.Bounds.Height)));
            File.WriteAllLines(Path.Combine(output, "opening-" + theme + (animations ? "-animated" : "-reduced") + ".csv"), lines);
            if (frames.Max(f => f.ContentY) - frames.Min(f => f.ContentY) > .01)
                throw new Exception("Opening animation moved the content inside the stationary acrylic card");
            for (var i = 1; i < frames.Count; i++)
            {
                var previous = frames[i - 1]; var current = frames[i];
                if (current.Opacity + .01 < previous.Opacity) throw new Exception("Opening opacity went backwards after a visible frame");
                if (!animations && current.Opacity != 1) throw new Exception("Reduced motion must show the card fully opaque");
                if (current.Bounds.Left != previous.Bounds.Left || current.Bounds.Top != previous.Bounds.Top ||
                    current.Bounds.Width != previous.Bounds.Width || current.Bounds.Height != previous.Bounds.Height)
                    throw new Exception("Opening animation changed the window bounds between rendered frames");
            }
            if (root.Opacity < .999) throw new Exception("Opening animation left the content transparent");
            Console.WriteLine("PASS " + theme + (animations ? " animated" : " reduced-motion") + " opening frames: stationary content and bounds, monotonic opacity");
        }
        finally { CompositionTarget.Rendering -= capture; flyout.Close(); Motion.EnabledOverride = false; }
    }
    private static void CheckChartAndMotion(FlyoutWindow flyout, AppSettings settings, string output, string theme)
    {
        var chart = (UsageChart)flyout.FindName("Chart");
        var snapshot = UsageClient.Demo();
        chart.FocusPoint(13);
        if (!chart.HoverText.Contains(snapshot.History[13].Spend.ToString("0.########", CultureInfo.InvariantCulture)) || !chart.HoverText.Contains("Token"))
            throw new Exception("Hourly chart must expose the exact point cost and tokens");
        Save(flyout, Path.Combine(output, "card-details-" + theme + ".png"));
        var previousPlot = chart.Plot;
        var modelRows = (StackPanel)flyout.FindName("ModelRows");
        var oldModelDetail = (ToolTip)((Grid)modelRows.Children[0]).ToolTip;
        settings.HideAmounts = true; flyout.Update(settings, snapshot, false);
        if (chart.HoverText != "" || ReferenceEquals(previousPlot, chart.Plot) || !Equals(oldModelDetail.Content, ""))
            throw new Exception("Privacy must discard cached chart details and model tooltip amounts immediately");
        WaitForChart(chart); chart.FocusPoint(13);
        var hiddenValues = ((LineSeries<ObservablePoint>)chart.Plot.Series.Single()).Values.ToArray();
        if (!chart.HoverText.Contains("••••") || chart.HoverText.Contains("$") || hiddenValues.Any(p => Math.Abs(p.Y ?? 0) > 1) ||
            ((TextBlock)flyout.FindName("BalanceText")).Text != "••••" || ((TextBlock)flyout.FindName("ChartUnit")).Text != "相对趋势")
            throw new Exception("Hidden amounts must stay masked and chart values must use a relative scale");
        Save(flyout, Path.Combine(output, "card-private-" + theme + ".png"));

        settings.HideAmounts = false; flyout.Update(settings, snapshot, false); WaitForChart(chart);
        previousPlot = chart.Plot;
        var metrics = (System.Windows.Controls.Primitives.UniformGrid)flyout.FindName("MetricsGrid");
        var firstMetric = metrics.Children[0];
        snapshot.History[13].Spend += .25m; snapshot.Balance = 123m;
        Motion.EnabledOverride = true; flyout.Update(settings, snapshot, false);
        if (!ReferenceEquals(previousPlot, chart.Plot) || !ReferenceEquals(firstMetric, metrics.Children[0]) ||
            ((TextBlock)flyout.FindName("BalanceText")).Text != "$123.00")
            throw new Exception("Refresh must update the existing chart and metric views with the new values");
        var root = (Border)flyout.FindName("RootBorder"); Motion.PrepareReveal(root); Motion.Reveal(root);
        WaitForChart(chart);
        WaitUntil(() => root.Opacity > .999 && root.RenderTransform.Value.IsIdentity &&
            ((TextBlock)flyout.FindName("BalanceText")).Opacity > .999, "Animation did not settle at the final layout");
        settings.HideAmounts = true; flyout.Update(settings, snapshot, false);
        if (((TextBlock)flyout.FindName("BalanceText")).Opacity != 1 || ((TextBlock)flyout.FindName("BalanceText")).Text != "••••")
            throw new Exception("Masking an animated amount must cancel the fade immediately");
        Motion.EnabledOverride = false;
        var next = settings.Clone(); next.BaseUrl = "https://new.example.test";
        flyout.Update(next, null, false);
        if (chart.HoverText != "" || chart.Plot.Visibility != Visibility.Collapsed || ((TextBlock)flyout.FindName("BalanceText")).Text != "—" ||
            !(modelRows.Children[0] is TextBlock)) throw new Exception("Switching accounts must immediately clear every old data view");
        Save(flyout, Path.Combine(output, "card-empty-" + theme + ".png"));

        var daily = new[] { new UsagePoint { Time = DateTime.Today.AddDays(-2), Spend = .1m, Tokens = 10 },
            new UsagePoint { Time = DateTime.Today.AddDays(-1), Spend = .5m, Tokens = 50 }, new UsagePoint { Time = DateTime.Today, Spend = .2m, Tokens = 20 } };
        chart.Update(daily, false); WaitForChart(chart); chart.FocusPoint(1);
        if (!chart.HoverText.Contains("$0.5") || !chart.Plot.XAxes.Single().Labeler(1).Contains("/"))
            throw new Exception("Daily trends must use dates and the correct cost");
        chart.Update(new[] { new UsagePoint { Time = DateTime.Today, Spend = 0 } }, false);
        WaitForChart(chart); chart.FocusPoint(0);
        if (chart.Plot.Visibility != Visibility.Visible || !chart.HoverText.Contains("$0"))
            throw new Exception("A zero-cost point is valid data, not an unavailable history");
        chart.Update(Array.Empty<UsagePoint>(), false);
        if (chart.Plot.Visibility != Visibility.Collapsed || chart.HoverText != "") throw new Exception("Empty history must clear the previous plot");
    }
    private static void WaitForChart(UsageChart chart)
    {
        chart.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); chart.UpdateLayout();
        if (!chart.IsVisible || chart.Plot.Visibility != Visibility.Visible) return;
        var plot = chart.Plot; var finished = false;
        ChartEventHandler completed = _ => finished = true;
        plot.UpdateFinished += completed;
        try
        {
            plot.CoreChart.Update(new ChartUpdateParams { Throttling = false });
            WaitUntil(() => finished && plot.CoreCanvas.IsValid, "Chart drawing did not finish");
        }
        finally { plot.UpdateFinished -= completed; }
    }
    private static void WaitUntil(Func<bool> ready, string error)
    {
        if (ready()) return;
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, __) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!ready()) throw new Exception(error);
    }
    private static void CheckConnectionGuide(bool dark, string output)
    {
        var original = new AppSettings { Theme = dark ? ThemeKind.Dark : ThemeKind.Light };
        original.TaskbarMetrics.Reverse();
        var guide = new ConnectionWindow(original);
        guide.Background = Background(dark); ((Grid)guide.Content).Background = Background(dark);
        guide.Left = -10000; guide.Top = -10000; guide.Show();
        Save(guide, Path.Combine(output, "connect-" + (dark ? "dark" : "light") + ".png"));
        var url = (TextBox)guide.FindName("UrlBox");
        if (url.Text != "" || ((ComboBox)guide.FindName("PresetBox")).SelectedIndex != 0 || ((Button)guide.FindName("SecurityButton")).IsEnabled)
            throw new Exception("Fresh onboarding must leave the site unbound and the browser link disabled");
        Uri target = null;
        guide.OpenLink = uri => { target = uri; return true; };
        ((Button)guide.FindName("SecurityButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (target != null) throw new Exception("An empty address must not open a preset site");
        url.Text = "https://custom.example.test/relay/api/";
        ((Button)guide.FindName("SecurityButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (target?.AbsoluteUri != "https://custom.example.test/relay/security") throw new Exception("Security link must use the chosen site's deployment path");
        var verified = 0; var saved = 0; AppSettings applied = null;
        guide.VerifyConnection = (candidate, cancellation) => { verified++; return Task.FromResult(new UsageSnapshot { Connected = true, Balance = 1m }); };
        guide.SaveSettings = candidate => { saved++; applied = candidate; return "Test save failure"; };
        var token = (PasswordBox)guide.FindName("TokenBox"); var button = (Button)guide.FindName("ConnectButton");
        token.Password = "sk-not-a-real-key";
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(guide.PendingConnection);
        if (verified != 0 || saved != 0 || !((TextBlock)guide.FindName("ResultText")).Text.Contains("用户访问令牌")) throw new Exception("Model key must be rejected before verification or save");
        token.Password = "  nap_example_test_value  ";
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(guide.PendingConnection);
        if (!guide.IsVisible || guide.IsCompleted || saved != 1 || !button.IsEnabled) throw new Exception("Save failure must preserve the guide for a retry");
        guide.SaveSettings = candidate => { saved++; applied = candidate; return null; };
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(guide.PendingConnection);
        if (!guide.IsCompleted || saved != 2 || applied.Token != "nap_example_test_value" || applied.BaseUrl != "https://custom.example.test/relay/api" || applied.SiteName != "custom.example.test" ||
            applied.TaskbarMetrics[0] != original.TaskbarMetrics[0] || applied.Theme != original.Theme) throw new Exception("Verified connection must save normalized credentials and preserve preferences");

        var previous = new AppSettings { BaseUrl = "https://first.example.test", SiteName = "First site", Token = "nap_previous_fake_token", ProtectedToken = "fake-ciphertext", NewApiUserId = 42, AutoQuotaPerUnit = false, QuotaPerUnit = 100m };
        var switching = new ConnectionWindow(previous); AppSettings switched = null;
        switching.Left = -10000; switching.Top = -10000; switching.Show();
        switching.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        var switchingUrl = (TextBox)switching.FindName("UrlBox");
        var switchingToken = (PasswordBox)switching.FindName("TokenBox");
        if (switchingToken.Password != previous.Token || switchingUrl.Text != previous.BaseUrl) throw new Exception("Reopening the guide must preserve the saved connection");
        switchingUrl.Text = "https://second.example.test/subpath";
        if (switchingToken.Password != "" || ((TextBox)switching.FindName("UserIdBox")).Text != "") throw new Exception("Changing hosts must clear credentials before any request");
        switchingToken.Password = "nap_fake_second_site";
        var presets = (ComboBox)switching.FindName("PresetBox"); presets.SelectedIndex = 1;
        if (switchingUrl.Text != SitePreset.Available[0].BaseUrl || switchingToken.Password != "") throw new Exception("An explicitly selected preset may fill public details only");
        switchingUrl.Text = "https://second.example.test/subpath/v1";
        if (presets.SelectedIndex != 0) throw new Exception("Editing a preset must return to custom-site mode");
        switchingToken.Password = "nap_fake_previous_protocol";
        ((ComboBox)switching.FindName("ProviderBox")).SelectedIndex = 1;
        if (switchingToken.Password != "" || ((TextBlock)switching.FindName("TokenLabel")).Text != "API Key") throw new Exception("Platform switch must clear credentials and update instructions");
        var auth = (ComboBox)switching.FindName("Sub2AuthBox"); auth.SelectedIndex = 1;
        if (!((TextBlock)switching.FindName("TokenLabel")).Text.Contains("JWT")) throw new Exception("Account authentication instructions must be available in onboarding");
        switchingToken.Password = "fake-jwt-for-test"; auth.SelectedIndex = 0;
        if (switchingToken.Password != "") throw new Exception("Authentication switch must clear the previous credential");
        switching.OpenLink = uri => { target = uri; return true; };
        ((Button)switching.FindName("SecurityButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (target?.AbsoluteUri != "https://second.example.test/subpath/") throw new Exception("Sub2API browser link must use the chosen site");
        switchingToken.Password = "sk-fake-subscription-key";
        switching.VerifyConnection = (candidate, cancellation) => Task.FromResult(new UsageSnapshot { Connected = true, Balance = null });
        switching.SaveSettings = candidate => { switched = candidate; return null; };
        ((Button)switching.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(switching.PendingConnection);
        if (!switching.IsCompleted || switched?.Provider != ProviderKind.Sub2Api || switched.Sub2Auth != Sub2AuthKind.ApiKey || switched.Token != "sk-fake-subscription-key" ||
            switched.BaseUrl != "https://second.example.test/subpath/v1" || switched.ProtectedToken != "" || switched.NewApiUserId != 0 || !switched.AutoQuotaPerUnit || switched.QuotaPerUnit != 500000m)
            throw new Exception("A valid subscription key must connect without a monetary balance or previous-site credentials");
        if (previous.Token != "nap_previous_fake_token" || previous.BaseUrl != "https://first.example.test") throw new Exception("Editing the guide must not mutate the active saved connection");

        var pending = new TaskCompletionSource<UsageSnapshot>();
        var closing = new ConnectionWindow(new AppSettings { BaseUrl = "https://custom.example.test" }); var lateSave = false;
        var observedCancellation = CancellationToken.None;
        closing.VerifyConnection = (_, cancellation) => { observedCancellation = cancellation; return pending.Task; }; closing.SaveSettings = _ => { lateSave = true; return null; };
        closing.Left = -10000; closing.Top = -10000; closing.Show();
        closing.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        ((PasswordBox)closing.FindName("TokenBox")).Password = "nap_example_test_value";
        ((Button)closing.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (((StackPanel)closing.FindName("ConnectionForm")).IsEnabled || closing.PendingConnection.IsCompleted) throw new Exception("Connection inputs must be locked during verification");
        closing.Close(); pending.SetResult(new UsageSnapshot { Connected = true, Balance = 1m }); WaitFor(closing.PendingConnection);
        if (lateSave || !observedCancellation.IsCancellationRequested) throw new Exception("A closed guide must cancel verification and must not save a late response");

        var demo = new ConnectionWindow(new AppSettings()); AppSettings demoSettings = null;
        demo.VerifyConnection = (_, cancellation) => throw new Exception("Demo must not verify online");
        demo.SaveSettings = candidate => { demoSettings = candidate; return null; };
        demo.Left = -10000; demo.Top = -10000; demo.Show();
        demo.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        ((Button)demo.FindName("DemoButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!demo.IsCompleted || demoSettings?.Provider != ProviderKind.Demo || demoSettings.Token != "") throw new Exception("Demo choice must save without a token");
    }
    private static AppSettings SiteSettings(bool dark)
    {
        var settings = new AppSettings { SiteName = "工作站点", BaseUrl = "https://primary.example.test", Token = "nap_fake_primary", NewApiUserId = 42, Theme = dark ? ThemeKind.Dark : ThemeKind.Light, RefreshSeconds = 120 };
        settings.TaskbarMetrics.Reverse(); settings.CaptureActiveSite();
        var second = settings.NewSiteDraft(); second.SiteName = "备用站点"; second.BaseUrl = "https://backup.example.test";
        second.Provider = ProviderKind.Sub2Api; second.Sub2Auth = Sub2AuthKind.AccountToken; second.Token = "fake-backup-jwt"; second.CaptureActiveSite();
        second.SelectSite(settings.ActiveSiteId); return second;
    }
    private static void Click(Window window, string name) => ((ButtonBase)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void ShowOffscreen(Window window, bool dark)
    {
        window.Background = Background(dark); if (window.Content is Panel panel) panel.Background = Background(dark);
        window.Left = -10000; window.Top = -10000; window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }
    private static void CheckSiteManagement(bool dark, string output)
    {
        var settings = SiteSettings(dark); var primaryId = settings.ActiveSiteId; var backupId = settings.Sites[1].Id;
        var saved = settings.Clone(); var saves = 0; var reject = false;
        var manager = new SitesWindow(settings);
        manager.SaveSettings = candidate =>
        {
            saves++; if (reject) return "Test: save failed, retry";
            saved = candidate.Clone(); saved.CaptureActiveSite(); return null;
        };
        ShowOffscreen(manager, dark); var list = (ListBox)manager.FindName("SitesList");
        if (list.Items.Count != 2 || ((Button)manager.FindName("UseSiteButton")).IsEnabled) throw new Exception("Site management must select and mark the active profile");
        Save(manager, Path.Combine(output, "sites-" + (dark ? "dark" : "light") + ".png"));
        list.SelectedIndex = 1; reject = true; Click(manager, "UseSiteButton");
        if (saved.ActiveSiteId != primaryId || !((Button)manager.FindName("UseSiteButton")).IsEnabled || !((TextBlock)manager.FindName("ResultText")).Text.Contains("failed"))
            throw new Exception("A failed save must not switch the active profile or discard the retry");
        reject = false; Click(manager, "UseSiteButton");
        if (saved.ActiveSiteId != backupId || saved.Token != "fake-backup-jwt" || ((Button)manager.FindName("UseSiteButton")).IsEnabled) throw new Exception("Selecting a profile must restore its independent credentials");

        list.SelectedIndex = 0; Click(manager, "EditSiteButton"); var editor = manager.EditorWindow;
        editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (((PasswordBox)editor.FindName("TokenBox")).Password != "nap_fake_primary" || ((Grid)manager.FindName("ManagerContent")).IsEnabled)
            throw new Exception("Editing an inactive site must load only that site's credentials and lock management until it closes");
        ((TextBox)editor.FindName("UrlBox")).Text = "https://primary.example.test/relay";
        if (((PasswordBox)editor.FindName("TokenBox")).Password != "") throw new Exception("Editing a site's address must clear its old credential");
        ((PasswordBox)editor.FindName("TokenBox")).Password = "nap_fake_primary_updated";
        editor.VerifyConnection = (candidate, _) =>
        {
            if (candidate.ActiveSiteId != primaryId || candidate.Token != "nap_fake_primary_updated") throw new Exception("The wrong profile was passed to verification");
            return Task.FromResult(new UsageSnapshot { Connected = true });
        };
        reject = true; Click(editor, "ConnectButton"); WaitFor(editor.PendingConnection);
        if (editor.IsCompleted || manager.EditorWindow == null || saved.Sites[0].Token != "nap_fake_primary") throw new Exception("An editor must remain open when saving fails");
        reject = false; Click(editor, "ConnectButton"); WaitFor(editor.PendingConnection);
        if (!editor.IsCompleted || manager.EditorWindow != null || saved.ActiveSiteId != backupId || saved.Token != "fake-backup-jwt" || saved.Sites[0].Token != "nap_fake_primary_updated")
            throw new Exception("Saving an inactive profile must retain the currently monitored account");
        ((TextBox)manager.FindName("ProfileNameBox")).Text = "工作站点 · 已更新"; Click(manager, "RenameButton");
        if (saved.Sites[0].SiteName != "工作站点 · 已更新" || saved.ActiveSiteId != backupId) throw new Exception("Renaming an inactive profile must not activate it");

        var beforeCancel = saves; Click(manager, "AddSiteButton"); editor = manager.EditorWindow;
        editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (((TextBox)editor.FindName("UrlBox")).Text != "" || ((PasswordBox)editor.FindName("TokenBox")).Password != "" || ((TextBox)editor.FindName("UserIdBox")).Text != "")
            throw new Exception("Adding a site must start with empty address, token and account ID");
        editor.Close();
        if (saves != beforeCancel || saved.Sites.Count != 2 || saved.ActiveSiteId != backupId) throw new Exception("Canceling an addition must preserve the catalog");
        Click(manager, "AddSiteButton"); editor = manager.EditorWindow; editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        ((TextBox)editor.FindName("UrlBox")).Text = "https://third.example.test"; ((TextBox)editor.FindName("SiteNameBox")).Text = "新站点";
        ((PasswordBox)editor.FindName("TokenBox")).Password = "nap_fake_third";
        editor.VerifyConnection = (_, __) => Task.FromResult(new UsageSnapshot { Connected = true });
        Click(editor, "ConnectButton"); WaitFor(editor.PendingConnection);
        var thirdId = saved.ActiveSiteId;
        if (!editor.IsCompleted || saved.Sites.Count != 3 || thirdId == primaryId || thirdId == backupId || saved.Token != "nap_fake_third" ||
            saved.Theme != settings.Theme || saved.RefreshSeconds != 120 || saved.TaskbarMetrics[0] != MetricKind.Latency) throw new Exception("Adding a verified site must activate it and retain shared preferences");
        list.SelectedIndex = 0; var beforeDelete = saves; Click(manager, "DeleteSiteButton");
        if (saves != beforeDelete || ((Border)manager.FindName("DeletePrompt")).Visibility != Visibility.Visible) throw new Exception("Deletion requires its inline confirmation");
        Click(manager, "ConfirmDeleteButton");
        if (saved.Sites.Count != 2 || saved.ActiveSiteId != thirdId || saved.Sites.Any(s => s.Id == primaryId)) throw new Exception("Deleting an inactive profile must leave the active profile intact");
        list.SelectedIndex = 1; Click(manager, "DeleteSiteButton"); Click(manager, "ConfirmDeleteButton");
        if (saved.Sites.Count != 1 || saved.ActiveSiteId != backupId || saved.Token != "fake-backup-jwt") throw new Exception("Deleting the active profile must restore a remaining site's credentials");
        Click(manager, "DeleteSiteButton"); Click(manager, "ConfirmDeleteButton");
        if (saved.Sites.Count != 0 || !saved.RequiresConnectionSetup || saved.Token != "" || ((TextBlock)manager.FindName("EmptyText")).Visibility != Visibility.Visible)
            throw new Exception("Deleting the final site must clear credentials and show the empty state");
        Save(manager, Path.Combine(output, "sites-empty-" + (dark ? "dark" : "light") + ".png")); manager.Close();

        var closing = new SitesWindow(settings); var lateSave = false; var pending = new TaskCompletionSource<UsageSnapshot>(); var cancellation = CancellationToken.None;
        closing.SaveSettings = _ => { lateSave = true; return null; }; ShowOffscreen(closing, dark); Click(closing, "EditSiteButton"); editor = closing.EditorWindow;
        editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        editor.VerifyConnection = (_, token) => { cancellation = token; return pending.Task; };
        Click(editor, "ConnectButton"); closing.Close(); pending.SetResult(new UsageSnapshot { Connected = true }); WaitFor(editor.PendingConnection);
        if (lateSave || !cancellation.IsCancellationRequested) throw new Exception("Closing management must cancel its editor and reject a late response");

        var advanced = new SettingsWindow(settings); var opened = 0; advanced.ManageSitesRequested += () => opened++; ShowOffscreen(advanced, dark);
        Click(advanced, "ManageSitesButton"); if (opened != 1) throw new Exception("Fresh settings must allow opening site management");
        ((TextBox)advanced.FindName("SiteNameBox")).Text = "未保存的名称"; Click(advanced, "ManageSitesButton");
        if (opened != 1 || !((TextBlock)advanced.FindName("SaveMessage")).Text.Contains("保存")) throw new Exception("Site management must not silently discard unsaved settings"); advanced.Close();
        var demo = new ConnectionWindow(settings.NewSiteDraft()); AppSettings demoSettings = null; ShowOffscreen(demo, dark);
        demo.SaveSettings = candidate => { demoSettings = candidate.Clone(); demoSettings.CaptureActiveSite(); return null; }; Click(demo, "DemoButton");
        if (demoSettings?.Sites.Count != 3 || demoSettings.Provider != ProviderKind.Demo || demoSettings.Sites[0].Token != "nap_fake_primary" || demoSettings.Sites[1].Token != "fake-backup-jwt")
            throw new Exception("Choosing demo from a new-site guide must preserve real profiles without saving a blank draft");
        Console.WriteLine("PASS " + (dark ? "dark" : "light") + " site management: add, edit, rename, switch, save retry, delete and close cancellation");
    }
    private static void CheckSiteMenus(bool dark)
    {
        var settings = SiteSettings(dark); settings.Sites[1].SiteName = settings.SiteName;
        string selected = null; var added = false; var managed = false; var menu = new ContextMenu();
        SiteMenus.Populate(menu, settings, id => selected = id, () => added = true, () => managed = true);
        if (menu.Items.Count != 5 || !((MenuItem)menu.Items[0]).IsChecked || ((MenuItem)menu.Items[1]).IsChecked || ((TextBlock)((MenuItem)menu.Items[1]).Header).Text != settings.SiteName) throw new Exception("Quick switching must distinguish same-named profiles by stable IDs");
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ((MenuItem)menu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); ((MenuItem)menu.Items[4]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (selected != settings.Sites[1].Id || !added || !managed) throw new Exception("The shared quick menu must route profile, add and manage actions");
        var locked = new MenuItem(); SiteMenus.Populate(locked, settings, _ => { }, () => { }, () => { }, false);
        if (locked.Items.OfType<MenuItem>().Take(2).Any(item => item.IsEnabled)) throw new Exception("Quick switching must be disabled while editing a connection");

        var flyout = new FlyoutWindow(); flyout.Update(settings, UsageClient.Demo(), false);
        NativeMethods.GetWindowRect(NativeMethods.FindWindow("Shell_TrayWnd", null), out var anchor); flyout.Open(anchor);
        flyout.SiteSwitchRequested += id => { selected = id; var next = settings.Clone(); next.SelectSite(id); flyout.Update(next, null, true); };
        var oldPlot = ((UsageChart)flyout.FindName("Chart")).Plot;
        Click(flyout, "SiteMenuButton");
        var popup = (ContextMenu)typeof(FlyoutWindow).GetField("_siteMenu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flyout);
        flyout.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (!popup.IsOpen || !flyout.IsVisible) throw new Exception("An unpinned card must stay open while its site menu is expanded");
        ((MenuItem)popup.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (selected != settings.Sites[1].Id || ((TextBlock)flyout.FindName("BalanceText")).Text != "—" || ReferenceEquals(oldPlot, ((UsageChart)flyout.FindName("Chart")).Plot))
            throw new Exception("The card's switch action must immediately discard old balance and chart geometry");
        popup.IsOpen = false; flyout.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (!flyout.IsVisible) throw new Exception("Choosing a site must keep the card open to show the new account");
        flyout.Close();
        Console.WriteLine("PASS " + (dark ? "dark" : "light") + " quick menus and open-card account isolation");
    }
    private static void CheckBalanceAlerts(bool dark, string output)
    {
        var settings = SiteSettings(dark); var primaryId = settings.ActiveSiteId; var backupId = settings.Sites[1].Id;
        AppSettings saved = settings.Clone(); var saves = 0; var reject = false; string testedSite = null;
        var manager = new SitesWindow(settings);
        manager.SaveSettings = candidate => { saves++; if (reject) return "Test: retry save"; saved = candidate.Clone(); saved.CaptureActiveSite(); return null; };
        manager.TestNotification = name => { testedSite = name; return null; }; ShowOffscreen(manager, dark);
        ((ListBox)manager.FindName("SitesList")).SelectedIndex = 1; Click(manager, "BalanceAlertButton");
        var window = manager.AlertWindow; window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        var editor = (BalanceAlertEditor)window.FindName("AlertEditor");
        var enabled = (CheckBox)editor.FindName("AlertEnabledBox"); var threshold = (TextBox)editor.FindName("ThresholdBox"); var cooldown = (TextBox)editor.FindName("CooldownBox");
        if (!manager.IsEditing || ((Grid)manager.FindName("ManagerContent")).IsEnabled || enabled.IsChecked == true || threshold.IsEnabled || threshold.Text != "5" || cooldown.Text != "60")
            throw new Exception("Alert preferences must start disabled and lock management while editing a profile");
        enabled.IsChecked = true; threshold.Text = "0.50"; cooldown.Text = "120";
        ((Button)editor.FindName("TestNotificationButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (testedSite != "备用站点" || saves != 0 || !((TextBlock)editor.FindName("TestMessage")).Text.Contains("已提交")) throw new Exception("Testing a notification must use the selected site without saving preferences");
        threshold.Text = "-1"; Click(window, "SaveButton");
        if (saves != 0 || !window.IsVisible || !((TextBlock)window.FindName("SaveMessage")).Text.Contains("0 USD")) throw new Exception("Invalid thresholds must be rejected without changing the profile");
        threshold.Text = "0.50"; reject = true; Click(window, "SaveButton");
        if (!window.IsVisible || saved.Sites[1].BalanceAlert.Enabled || !((TextBlock)window.FindName("SaveMessage")).Text.Contains("retry")) throw new Exception("Failed saves must retain the editor and the previous alert preferences");
        window.Background = Background(dark); ((Grid)window.Content).Background = Background(dark); ((TextBlock)window.FindName("SaveMessage")).Text = "";
        Save(window, Path.Combine(output, "alerts-" + (dark ? "dark" : "light") + ".png"));
        reject = false; Click(window, "SaveButton");
        if (manager.IsEditing || saved.ActiveSiteId != primaryId || saved.BalanceAlert.Enabled || !saved.Sites[1].BalanceAlert.Enabled || saved.Sites[1].BalanceAlert.ThresholdUsd != .5m || saved.Sites[1].BalanceAlert.CooldownMinutes != 120)
            throw new Exception("Saving an inactive profile's alerts must preserve the active account and its independent preferences");
        Click(manager, "BalanceAlertButton"); window = manager.AlertWindow; window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); editor = (BalanceAlertEditor)window.FindName("AlertEditor");
        var reopened = editor.ReadOptions();
        if (!reopened.Enabled || reopened.ThresholdUsd != .5m || reopened.CooldownMinutes != 120) throw new Exception("Saved alert options must reopen correctly");
        ((CheckBox)editor.FindName("AlertEnabledBox")).IsChecked = false; var beforeCancel = saves; Click(window, "CancelButton");
        if (saves != beforeCancel || !saved.Sites[1].BalanceAlert.Enabled) throw new Exception("Canceling alert edits must keep saved preferences");
        Click(manager, "BalanceAlertButton"); window = manager.AlertWindow; window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        manager.Close(); Click(window, "SaveButton");
        if (saves != beforeCancel || window.IsVisible) throw new Exception("A closed management window must close its alert editor and prevent late saves");

        var advanced = new SettingsWindow(saved); ShowOffscreen(advanced, dark); ((ListBox)advanced.FindName("Navigation")).SelectedIndex = 3;
        editor = (BalanceAlertEditor)advanced.FindName("AlertEditor"); ((CheckBox)editor.FindName("AlertEnabledBox")).IsChecked = true;
        ((TextBox)editor.FindName("ThresholdBox")).Text = "2.25"; ((TextBox)editor.FindName("CooldownBox")).Text = "30";
        advanced.TestNotification = _ => "Test: notification unavailable"; ((Button)editor.FindName("TestNotificationButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!((TextBlock)editor.FindName("TestMessage")).Text.Contains("unavailable")) throw new Exception("Notification submission failures must be visible in preferences");
        AppSettings applied = null; advanced.SaveSettings = candidate => { applied = candidate; return "Test: stay open"; }; Click(advanced, "SaveButton");
        if (applied == null || !applied.BalanceAlert.Enabled || applied.BalanceAlert.ThresholdUsd != 2.25m || applied.BalanceAlert.CooldownMinutes != 30 || applied.ActiveSiteId != primaryId || !applied.Sites.Single(s => s.Id == backupId).BalanceAlert.Enabled)
            throw new Exception("The main settings window must edit only the current site's alert preferences");
        ((TextBlock)advanced.FindName("SaveMessage")).Text = "保存后应用于当前站点。";
        ((TextBlock)editor.FindName("TestMessage")).Text = "可先测试通知；显示效果取决于 Windows 通知与勿扰设置。";
        Save(advanced, Path.Combine(output, "settings-alerts-" + (dark ? "dark" : "light") + ".png")); advanced.Close();
        Console.WriteLine("PASS " + (dark ? "dark" : "light") + " alert preferences: per-site isolation, opt-in, validation, save retry, test routing and cancellation");
    }
    private static void WaitFor(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var timedOut = false;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, __) => { timedOut = true; frame.Continue = false; };
            task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
            if (timedOut) throw new Exception("Onboarding operation timed out");
        }
        task.GetAwaiter().GetResult();
    }
    private static void Save(Window window, string path)
    {
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        if (window is FlyoutWindow flyout) WaitForChart((UsageChart)flyout.FindName("Chart"));
        window.UpdateLayout();
        var width = (int)Math.Ceiling(window.ActualWidth); var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) throw new Exception("Window has no rendered size");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine(Path.GetFileName(path) + " " + width + "x" + height);
    }
}
