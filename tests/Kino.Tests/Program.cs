using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kino.Models;
using Kino.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int _passed;
    private static async Task Main()
    {
        await Run("New API: auth, unit conversion, daily aggregation", NewApi);
        await Run("New API: disabled statistics remain unavailable", DisabledStatistics);
        await Run("New API: optional statistics error keeps balance", PartialStatistics);
        await Run("Sub2API: wallet and key-scoped usage", SubWallet);
        await Run("Sub2API: quota and subscription scopes", SubScopes);
        await Run("Sub2API: account JWT dashboard", SubAccount);
        await Run("Transport: auth, redirect and HTML failures", TransportFailures);
        await Run("New API: scoped authentication failures are actionable and sanitized", NewApiAuthFailures);
        await Run("Controller: stale state and account switch isolation", ControllerState);
        await Run("Controller: old in-flight request cannot publish after switch", Cancellation);
        await Run("Settings: encrypted round trip and corrupt-file recovery", SettingsRoundTrip);
        await Run("Settings: selection order, endpoint and validation", Validation);
        await Run("First run: neutral setup, custom sites, legacy preferences and key validation", FirstRun);
        await Run("Sites: independent drafts, stable identity, preferences and deletion", SiteCatalog);
        await Run("Sites: encrypted multi-provider persistence and final deletion", SiteStorage);
        await Run("Sites: legacy migration is read-only until save", SiteMigration);
        await Run("Sites: damaged credentials and catalog recovery", SiteRecovery);
        await Run("Sites: rapid switching cancels queued work and isolates identical connections", SiteSwitching);
        await Run("Alerts: threshold, unavailable balances and fresh real snapshots", AlertEligibility);
        await Run("Alerts: observation deduplication and per-site cooldown across restarts", AlertCooldown);
        await Run("Alerts: independent options, migration and validation", AlertSettings);
        await Run("Alerts: private, atomic cooldown history and recovery", AlertHistory);
        await Run("Alerts: controller refresh, failure and switching integration", AlertController);
        await Run("Demo: no HTTP calls; privacy formatting", Demo);
        Console.WriteLine("PASS: " + _passed + " checks");
    }
    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine("PASS " + name); _passed++; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + "\n" + ex); Environment.Exit(1); }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static AppSettings Settings(ProviderKind kind) => new AppSettings { Provider = kind, BaseUrl = "https://relay.example.test", Token = "fake-user-token", NewApiUserId = 42 };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { _send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => _send(request, cancellation);
    }
    private static HttpResponseMessage Json(string body, int status = 200) => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static ApiTransport Transport(Func<HttpRequestMessage, HttpResponseMessage> send) => new ApiTransport(new Handler((r, _) => Task.FromResult(send(r))));

    private static async Task NewApi()
    {
        var timestamp = new DateTimeOffset(DateTime.Today.AddHours(9)).ToUnixTimeSeconds();
        var requests = new List<string>();
        using (var transport = Transport(request =>
        {
            requests.Add(request.RequestUri.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/api/status")
            {
                Assert(request.Headers.Authorization == null, "Public status must not receive the token");
                return Json("{\"success\":true,\"data\":{\"quota_per_unit\":1000000,\"enable_data_export\":true}}");
            }
            Assert(request.Headers.Authorization?.Parameter == "fake-user-token", "Bearer token missing");
            Assert(request.Headers.GetValues("New-Api-User").Single() == "42", "New-Api-User missing");
            if (request.RequestUri.AbsolutePath == "/api/user/self") return Json("{\"success\":true,\"data\":{\"quota\":123450000}}");
            Assert(request.RequestUri.Query.Contains("start_timestamp="), "Daily date range missing");
            return Json("{\"success\":true,\"data\":[{\"quota\":1000000,\"token_used\":140,\"count\":2,\"created_at\":" + timestamp + ",\"model_name\":\"model-a\"},{\"quota\":250000,\"token_used\":60,\"count\":1,\"created_at\":" + timestamp + ",\"model_name\":\"model-a\"}]}");
        }))
        {
            var snapshot = await new UsageClient(transport).FetchAsync(Settings(ProviderKind.NewApi), CancellationToken.None);
            Assert(snapshot.Balance == 123.45m && snapshot.TodaySpend == 1.25m, "Incorrect money conversion");
            Assert(snapshot.TodayTokens == 200 && snapshot.TodayRequests == 3, "Incorrect daily totals");
            Assert(snapshot.History.Count == 24 && snapshot.History[9].Spend == 1.25m, "Hourly aggregation failed");
            Assert(snapshot.Models.Single().Tokens == 200, "Model aggregation failed");
            Assert(snapshot.Connected && snapshot.LatencyMs.HasValue && !snapshot.IsDemo, "Connection flags failed");
            Assert(requests.Count == 3, "Unexpected number of API requests");
        }
    }
    private static async Task DisabledStatistics()
    {
        using (var transport = Transport(r => r.RequestUri.AbsolutePath == "/api/status"
            ? Json("{\"success\":true,\"data\":{\"quota_per_unit\":500000,\"enable_data_export\":false}}")
            : Json("{\"success\":true,\"data\":{\"quota\":500000}}")))
        {
            var data = await new UsageClient(transport).FetchAsync(Settings(ProviderKind.NewApi), CancellationToken.None);
            Assert(data.Balance == 1 && data.TodaySpend == null && data.TodayTokens == null, "Disabled stats must not look like zero use");
        }
    }
    private static async Task PartialStatistics()
    {
        using (var transport = Transport(r => r.RequestUri.AbsolutePath == "/api/user/self" ? Json("{\"success\":true,\"data\":{\"quota\":500000}}") : Json("{}", 403)))
        {
            var data = await new UsageClient(transport).FetchAsync(Settings(ProviderKind.NewApi), CancellationToken.None);
            Assert(data.Connected && data.Balance == 1 && data.TodaySpend == null && data.Note.Length > 0, "Partial data should keep balance and explain missing stats");
        }
    }
    private static async Task SubWallet()
    {
        using (var transport = Transport(r =>
        {
            Assert(r.RequestUri.AbsolutePath == "/v1/usage", "Incorrect Sub2API key endpoint");
            return Json("{\"mode\":\"unrestricted\",\"isValid\":true,\"balance\":12.5,\"remaining\":12.5,\"planName\":\"钱包余额\",\"usage\":{\"today\":{\"actual_cost\":2.25,\"cost\":9,\"total_tokens\":12345,\"requests\":7},\"average_duration_ms\":2400},\"daily_usage\":[{\"date\":\"2026-11-08\",\"actual_cost\":2.25,\"total_tokens\":12345}],\"model_stats\":[{\"model\":\"m\",\"total_tokens\":12345,\"requests\":7,\"actual_cost\":2.25}]}");
        }))
        {
            var data = await new UsageClient(transport).FetchAsync(Settings(ProviderKind.Sub2Api), CancellationToken.None);
            Assert(data.Balance == 12.5m && data.TodaySpend == 2.25m, "Use actual cost, not standard cost");
            Assert(data.TodayTokens == 12345 && data.TodayRequests == 7, "Missing usage stats");
            Assert(data.Scope.Contains("Key") && data.BalanceLabel == "可用余额", "Incorrect key/wallet scope");
            Assert(data.AverageDurationMs == 2400 && data.LatencyMs != 2400, "Do not confuse round-trip with generation duration");
            Assert(data.History.Count == 1 && data.Models.Count == 1, "History/models missing");
        }
    }
    private static async Task SubScopes()
    {
        using (var transport = Transport(r => Json("{\"mode\":\"quota_limited\",\"isValid\":true,\"remaining\":5}")))
        {
            var data = await new UsageClient(transport).FetchAsync(Settings(ProviderKind.Sub2Api), CancellationToken.None);
            Assert(data.BalanceLabel == "Key 剩余额度" && data.TodaySpend == null, "Limited key must not be labeled as wallet balance");
        }
        using (var transport = Transport(r => Json("{\"mode\":\"unrestricted\",\"isValid\":true,\"planName\":\"Pro\"}")))
        {
            var data = await new UsageClient(transport).FetchAsync(Settings(ProviderKind.Sub2Api), CancellationToken.None);
            Assert(data.BalanceLabel == "订阅剩余额度" && data.Balance == null, "Subscription must not invent a balance");
        }
    }
    private static async Task SubAccount()
    {
        using (var transport = Transport(r =>
        {
            if (r.RequestUri.AbsolutePath.EndsWith("/auth/me")) return Json("{\"code\":0,\"data\":{\"balance\":15}}");
            if (r.RequestUri.AbsolutePath.EndsWith("/stats")) return Json("{\"code\":0,\"data\":{\"today_actual_cost\":1.2,\"today_tokens\":400,\"today_requests\":3}}");
            if (r.RequestUri.AbsolutePath.EndsWith("/trend")) return Json("{\"code\":0,\"data\":{\"trend\":[{\"date\":\"2026-11-08 09:00\",\"actual_cost\":1.2}]}}");
            return Json("{\"code\":0,\"data\":{\"models\":[{\"model\":\"demo\",\"total_tokens\":400}]}}");
        }))
        {
            var settings = Settings(ProviderKind.Sub2Api); settings.Sub2Auth = Sub2AuthKind.AccountToken;
            var data = await new UsageClient(transport).FetchAsync(settings, CancellationToken.None);
            Assert(data.Balance == 15 && data.TodaySpend == 1.2m && data.TodayTokens == 400 && data.Scope.Contains("账户"), "Account JWT mapping failed");
        }
    }
    private static async Task TransportFailures()
    {
        foreach (var status in new[] { 401, 403, 302, 404, 429, 500 })
        {
            using (var transport = Transport(r => Json("{\"message\":\"secret-token\"}", status)))
            {
                try { await transport.GetAsync(Settings(ProviderKind.NewApi), "api/user/self", CancellationToken.None); throw new Exception("Expected API error"); }
                catch (ApiException ex) { Assert(ex.Status == status && !ex.Message.Contains("secret-token"), "Incorrect or unsafe HTTP error"); }
            }
        }
        using (var transport = Transport(r => Json("<html>Login</html>")))
        {
            try { await transport.GetAsync(Settings(ProviderKind.NewApi), "api/user/self", CancellationToken.None); throw new Exception("Expected JSON error"); }
            catch (ApiException ex) { Assert(ex.Message.Contains("JSON"), "HTML error should explain invalid response"); }
        }
    }
    private static async Task NewApiAuthFailures()
    {
        var cases = new[]
        {
            new { Code = "AUTH_UNAUTHORIZED", Status = 401, Expected = "用户访问令牌无效" },
            new { Code = "ACCESS_TOKEN_EXPIRED", Status = 401, Expected = "已过期" },
            new { Code = "ACCESS_TOKEN_LEGACY_RETIRED", Status = 401, Expected = "旧版" },
            new { Code = "ACCESS_TOKEN_SCOPE_DENIED", Status = 403, Expected = "usage:read" },
            new { Code = "ACCESS_TOKEN_SCOPE_DENIED", Status = 200, Expected = "profile:read" }
        };
        foreach (var item in cases)
        {
            using (var transport = Transport(_ => Json("{\"success\":false,\"code\":\"" + item.Code + "\",\"message\":\"secret-token\",\"message_params\":{\"scope\":\"secret-token\"}}", item.Status)))
            {
                try { await transport.GetAsync(Settings(ProviderKind.NewApi), "api/user/self", CancellationToken.None); throw new Exception("Expected auth error"); }
                catch (ApiException ex)
                {
                    Assert(ex.Code == item.Code && ex.Status == item.Status && ex.Message.Contains(item.Expected), "Missing actionable auth explanation");
                    Assert(!ex.Message.Contains("secret-token"), "Server messages or scope parameters leaked");
                }
            }
        }
        foreach (var body in new[] { "{\"code\":\"secret-token\",\"message\":\"secret-token\"}", "<html>secret-token</html>", "[]" })
        {
            using (var transport = Transport(_ => Json(body, 401)))
            {
                try { await transport.GetAsync(Settings(ProviderKind.NewApi), "api/user/self", CancellationToken.None); throw new Exception("Expected auth error"); }
                catch (ApiException ex) { Assert(ex.Code == null && !ex.Message.Contains("secret-token"), "Unknown auth failures must stay sanitized"); }
            }
        }
    }
    private static async Task ControllerState()
    {
        var fail = false;
        using (var transport = Transport(r => fail ? Json("{}", 500) : Json("{\"mode\":\"unrestricted\",\"isValid\":true,\"balance\":42}")))
        using (var controller = new MonitorController(new UsageClient(transport), Settings(ProviderKind.Sub2Api)))
        {
            await controller.RefreshAsync(); var time = controller.Snapshot.UpdatedAt;
            fail = true; await controller.RefreshAsync();
            Assert(controller.Snapshot.Balance == 42 && controller.Snapshot.IsStale && !controller.Snapshot.Connected, "Failed request should retain stale data");
            Assert(controller.Snapshot.UpdatedAt == time && controller.Snapshot.LatencyMs == null, "Failure must not forge freshness");
            var settings = Settings(ProviderKind.Sub2Api); settings.Token = "different-user"; controller.ApplySettings(settings);
            Assert(controller.Snapshot == null, "Changing accounts must clear previous private data");
        }
    }
    private static async Task Cancellation()
    {
        var entered = new TaskCompletionSource<bool>();
        using (var transport = new ApiTransport(new Handler(async (_, cancellation) =>
        {
            entered.TrySetResult(true); await Task.Delay(30000, cancellation); return Json("{}");
        })))
        using (var controller = new MonitorController(new UsageClient(transport), Settings(ProviderKind.Sub2Api)))
        {
            var old = controller.RefreshAsync(); await entered.Task;
            controller.ApplySettings(AppSettings.DemoSettings());
            await Task.WhenAll(old, controller.RefreshAsync());
            Assert(controller.Snapshot.IsDemo && controller.Snapshot.Connected, "Cancelled account request overwrote new provider");
        }
    }
    private static Task SettingsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kino-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
            var settings = Settings(ProviderKind.NewApi); settings.SiteName = "测试中转站"; settings.Token = "sensitive-test-value";
            store.Save(settings); store.Save(settings);
            var raw = File.ReadAllText(path);
            Assert(!raw.Contains(settings.Token), "Token was saved as plaintext");
            var loaded = store.Load(); Assert(loaded.Token == settings.Token && loaded.SiteName == settings.SiteName, "Encrypted settings roundtrip failed");
            File.WriteAllText(path, "invalid JSON");
            Assert(store.Load().Provider == ProviderKind.Demo && store.LoadWarning != null && File.ReadAllText(path) == "invalid JSON", "Corrupt settings should not be overwritten");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        return Task.CompletedTask;
    }
    private static AppSettings MultiSiteSettings()
    {
        var settings = Settings(ProviderKind.NewApi); settings.SiteName = "同名站点";
        settings.Theme = ThemeKind.Dark; settings.HideAmounts = true; settings.RefreshSeconds = 120;
        settings.TaskbarMetrics.Reverse(); settings.CaptureActiveSite();
        var second = settings.NewSiteDraft();
        second.Provider = ProviderKind.Sub2Api; second.Sub2Auth = Sub2AuthKind.AccountToken;
        second.SiteName = "同名站点"; second.BaseUrl = "https://second.example.test/relay";
        second.Token = "fake-second-account-token"; second.QuotaPerUnit = 100m; second.AutoQuotaPerUnit = false; second.CaptureActiveSite();
        return second;
    }
    private static Task SiteCatalog()
    {
        var settings = MultiSiteSettings(); var active = settings.ActiveSiteId;
        Assert(settings.Sites.Count == 2 && settings.Sites.Select(s => s.Id).Distinct().Count() == 2, "Same display names must retain separate stable IDs");
        var clone = settings.Clone(); clone.Sites[0].Token = "fake-changed"; clone.TaskbarMetrics.Clear();
        Assert(settings.Sites[0].Token == "fake-user-token" && settings.TaskbarMetrics.Count > 0, "Cloning a catalog must isolate credentials and preferences");
        var draft = settings.NewSiteDraft();
        Assert(draft.Sites.Count == 2 && draft.ActiveSiteId != active && draft.BaseUrl == "" && draft.Token == "" && draft.ProtectedToken == "" && draft.NewApiUserId == 0 &&
            draft.Provider == ProviderKind.NewApi && draft.QuotaPerUnit == 500000m && draft.AutoQuotaPerUnit, "A new draft must retain the catalog without borrowing a connection");
        Assert(settings.SameConnection(settings.Clone()) && settings.ActiveSiteId == active && settings.Token == "fake-second-account-token", "Drafts must not change the saved active site");
        Assert(draft.NewSiteDraft().Sites.Count == 2, "Opening another draft must not save an unconfigured draft as a ghost profile");
        Assert(settings.SelectSite(settings.Sites[0].Id) && settings.Provider == ProviderKind.NewApi && settings.Token == "fake-user-token" && settings.NewApiUserId == 42 &&
            settings.Theme == ThemeKind.Dark && settings.HideAmounts && settings.RefreshSeconds == 120 && settings.TaskbarMetrics[0] == MetricKind.Latency, "Switching must restore only the selected connection and preserve global preferences");
        var before = settings.Clone();
        Assert(!settings.SelectSite("missing-id") && settings.SameConnection(before), "Unknown site IDs must leave the active connection intact");
        Assert(settings.RemoveSite(active) && settings.Sites.Count == 1 && settings.SameConnection(before), "Deleting an inactive site must keep the active connection");
        Assert(settings.RemoveSite(settings.ActiveSiteId) && settings.Sites.Count == 0 && settings.ActiveSiteId == "" && settings.RequiresConnectionSetup && settings.Token == "" && settings.ProtectedToken == "", "Deleting the last site must erase its active credentials without enabling demo");
        settings.CaptureActiveSite(); Assert(settings.Sites.Count == 0, "An empty catalog must not resurrect a deleted site");
        return Task.CompletedTask;
    }
    private static Task SiteStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kino-sites-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var settings = MultiSiteSettings(); var active = settings.ActiveSiteId;
            var demo = SiteProfile.Capture(AppSettings.DemoSettings(), Guid.NewGuid().ToString("N")); settings.Sites.Add(demo);
            store.Save(settings); var raw = File.ReadAllText(store.FilePath); var loaded = store.Load();
            Assert(loaded.Sites.Count == 3 && loaded.ActiveSiteId == active && loaded.Sub2Auth == Sub2AuthKind.AccountToken && loaded.Token == "fake-second-account-token", "Restart must restore all profiles and the selected authentication mode");
            Assert(!raw.Contains("fake-user-token") && !raw.Contains("fake-second-account-token") && loaded.Sites.Take(2).All(s => !string.IsNullOrEmpty(s.ProtectedToken)), "Every account credential must be encrypted");
            Assert(loaded.SelectSite(loaded.Sites[0].Id) && loaded.Token == "fake-user-token" && loaded.NewApiUserId == 42 && loaded.QuotaPerUnit == 500000m, "Profiles must restore independent tokens, IDs and quota ratios");
            loaded.SiteName = "重命名站点"; store.Save(loaded); loaded = store.Load();
            Assert(loaded.SiteName == "重命名站点" && loaded.Sites[1].SiteName == "同名站点" && loaded.Theme == ThemeKind.Dark && loaded.HideAmounts, "Saving the active site must keep other names and shared preferences");
            loaded.SelectSite(demo.Id); store.Save(loaded); loaded = store.Load();
            Assert(loaded.Provider == ProviderKind.Demo && loaded.Token == "" && loaded.Sites[1].Token == "fake-second-account-token", "An offline demo profile must not destroy account credentials");
            loaded.RemoveSite(demo.Id);
            Assert(loaded.ActiveSiteId == loaded.Sites.Last().Id && loaded.Token == "fake-second-account-token", "Deleting the active site must choose a remaining profile");
            foreach (var id in loaded.Sites.Select(s => s.Id).ToArray()) loaded.RemoveSite(id);
            store.Save(loaded); loaded = store.Load();
            Assert(loaded.Sites.Count == 0 && loaded.ActiveSiteId == "" && loaded.RequiresConnectionSetup && loaded.Token == "" && loaded.ProtectedToken == "", "Last-site deletion must persist after restart");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        return Task.CompletedTask;
    }
    private static Task SiteMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kino-migration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
            var legacy = Settings(ProviderKind.NewApi); legacy.SiteName = "原有站点"; legacy.ProtectedToken = SettingsStore.Protect(legacy.Token);
            legacy.Theme = ThemeKind.Light; legacy.TaskbarMetrics = new List<MetricKind> { MetricKind.Latency, MetricKind.Balance };
            var json = JObject.FromObject(legacy); json.Remove("Sites"); json.Remove("ActiveSiteId");
            var original = json.ToString(Formatting.Indented); File.WriteAllText(path, original, new UTF8Encoding(false));
            var loaded = store.Load();
            Assert(loaded.Sites.Count == 1 && loaded.ActiveSiteId == loaded.Sites[0].Id && loaded.Token == legacy.Token && loaded.Sites[0].NewApiUserId == 42 &&
                loaded.SiteName == legacy.SiteName && loaded.Theme == legacy.Theme && loaded.TaskbarMetrics.SequenceEqual(legacy.TaskbarMetrics), "Legacy accounts must migrate with decrypted credentials and preferences");
            Assert(File.ReadAllText(path) == original, "Loading an old configuration must not write to disk");
            var id = loaded.ActiveSiteId; store.Save(loaded); loaded = store.Load();
            Assert(loaded.Sites.Count == 1 && loaded.ActiveSiteId == id && loaded.Token == legacy.Token && !File.ReadAllText(path).Contains(legacy.Token), "The first explicit save must persist migration without duplicate sites or plaintext");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        return Task.CompletedTask;
    }
    private static Task SiteRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kino-site-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path); store.Save(MultiSiteSettings());
            var json = JObject.Parse(File.ReadAllText(path)); var profiles = (JArray)json["Sites"];
            profiles[0]["ProtectedToken"] = "damaged-test-ciphertext";
            File.WriteAllText(path, json.ToString()); var loaded = store.Load();
            Assert(loaded.Provider == ProviderKind.Sub2Api && loaded.Token == "fake-second-account-token" && loaded.Sites[0].Token == "" && store.LoadWarning != null, "One damaged credential must leave the other accounts usable");
            store.Save(loaded); loaded = store.Load();
            Assert(loaded.Sites[0].ProtectedToken == "damaged-test-ciphertext" && loaded.SelectSite(loaded.Sites[0].Id) && loaded.RequiresConnectionSetup && loaded.Provider != ProviderKind.Demo, "Saving another account must preserve unreadable ciphertext and never fill damaged sites with demo data");
            profiles[0]["Id"] = "duplicate"; profiles[1]["Id"] = "duplicate"; profiles.Add(JObject.FromObject(new SiteProfile { Id = "", Provider = ProviderKind.Demo })); profiles.Add(JValue.CreateNull());
            json["ActiveSiteId"] = "missing"; File.WriteAllText(path, json.ToString()); var original = File.ReadAllText(path); loaded = store.Load();
            Assert(loaded.Sites.Count == 3 && loaded.Sites.Select(s => s.Id).Distinct().Count() == 3 && loaded.Sites.All(s => !string.IsNullOrWhiteSpace(s.Id)) && loaded.ActiveSiteId == loaded.Sites[0].Id, "Invalid profile identities must be repaired without dropping valid sites");
            Assert(File.ReadAllText(path) == original, "Recovery on load must leave the source file intact");
            loaded.SelectSite(loaded.Sites[1].Id); store.Save(loaded); var valid = store.Load();
            valid.RemoveSite(valid.Sites[0].Id); store.Save(valid); store.Load(); Assert(store.LoadWarning == null, "A previous decryption warning must clear once the damaged profile is removed");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        return Task.CompletedTask;
    }
    private static async Task SiteSwitching()
    {
        var settings = Settings(ProviderKind.Sub2Api); settings.CaptureActiveSite();
        var identical = SiteProfile.Capture(settings, Guid.NewGuid().ToString("N")); settings.Sites.Add(identical);
        var third = identical.Clone(); third.Id = Guid.NewGuid().ToString("N"); third.BaseUrl = "https://third.example.test"; third.Token = "fake-third-token"; settings.Sites.Add(third);
        var started = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>();
        var calls = new List<string>(); var first = true; var oldCancellation = CancellationToken.None;
        using (var transport = new ApiTransport(new Handler(async (request, cancellation) =>
        {
            calls.Add(request.RequestUri.Host);
            if (first) { first = false; oldCancellation = cancellation; started.SetResult(true); await release.Task; }
            return Json("{\"mode\":\"unrestricted\",\"isValid\":true,\"balance\":" + (request.RequestUri.Host == "third.example.test" ? "3" : "42") + "}");
        })))
        using (var controller = new MonitorController(new UsageClient(transport), settings))
        {
            var old = controller.RefreshAsync(); await started.Task;
            var next = controller.Settings.Clone(); next.SelectSite(identical.Id); controller.ApplySettings(next); var skipped = controller.RefreshAsync();
            next = controller.Settings.Clone(); next.SelectSite(third.Id); controller.ApplySettings(next); var current = controller.RefreshAsync();
            Assert(controller.Snapshot == null && oldCancellation.IsCancellationRequested, "A quick switch must immediately clear data and cancel the old account request");
            release.SetResult(true); await Task.WhenAll(old, skipped, current);
            Assert(calls.SequenceEqual(new[] { "relay.example.test", "third.example.test" }) && controller.Snapshot.Balance == 3 && controller.Settings.ActiveSiteId == third.Id, "Only the final queued profile may issue a request and publish data");
            next = controller.Settings.Clone(); next.SelectSite(identical.Id); controller.ApplySettings(next); await controller.RefreshAsync();
            Assert(controller.Snapshot.Balance == 42, "Switching back must read the selected account again");
            next = controller.Settings.Clone(); next.SelectSite(settings.Sites[0].Id); controller.ApplySettings(next);
            Assert(controller.Snapshot == null, "Different profile IDs must clear cached data even when their credentials are identical");
            await controller.RefreshAsync(); var snapshot = controller.Snapshot; next = controller.Settings.Clone(); next.SiteName = "改名"; controller.ApplySettings(next);
            Assert(ReferenceEquals(snapshot, controller.Snapshot), "A label change must keep the current account snapshot");
        }
    }
    private static UsageSnapshot AlertSnapshot(decimal? balance) => new UsageSnapshot { Connected = true, UpdatedAt = DateTimeOffset.UtcNow, Balance = balance };
    private static AppSettings AlertSite()
    {
        var settings = Settings(ProviderKind.Sub2Api); settings.SiteName = "测试站点";
        settings.BalanceAlert.Enabled = true; settings.BalanceAlert.ThresholdUsd = 5m; settings.CaptureActiveSite(); return settings;
    }
    private static Task AlertEligibility()
    {
        var settings = AlertSite(); var alerts = new LowBalanceAlerts();
        Assert(alerts.Observe(settings, AlertSnapshot(5.01m), false) == null, "A balance above the threshold must not notify");
        foreach (var balance in new[] { 5m, 0m, -1m })
            Assert(alerts.Observe(settings, AlertSnapshot(balance), false) != null, "Equality, zero and negative balances must trigger when enabled");
        var cases = new[] { AlertSnapshot(null), new UsageSnapshot { Balance = 0m, UpdatedAt = DateTimeOffset.UtcNow },
            new UsageSnapshot { Balance = 0m, Connected = true }, AlertSnapshot(0).AsStale("offline"),
            new UsageSnapshot { Connected = true, IsDemo = true, Balance = 0, UpdatedAt = DateTimeOffset.UtcNow },
            new UsageSnapshot { Connected = true, Error = "failed", Balance = 0, UpdatedAt = DateTimeOffset.UtcNow } };
        foreach (var data in cases) Assert(alerts.Observe(settings, data, false) == null, "Missing, disconnected, stale, demo or errored data must not notify");
        Assert(alerts.Observe(settings, null, false) == null && alerts.Observe(settings, AlertSnapshot(0), true) == null, "Loading a site or starting a refresh must not notify");
        settings.BalanceAlert.Enabled = false; Assert(alerts.Observe(settings, AlertSnapshot(0), false) == null, "Alerts must be opt-in");
        settings.BalanceAlert.Enabled = true; settings.Provider = ProviderKind.Demo; Assert(alerts.Observe(settings, AlertSnapshot(0), false) == null, "A demo provider must not notify even if a malformed snapshot claims to be real");
        settings.Provider = ProviderKind.Sub2Api; settings.BalanceAlert.CooldownMinutes = 0; Assert(alerts.Observe(settings, AlertSnapshot(0), false) == null, "Invalid options must not generate alerts");
        settings.BalanceAlert.CooldownMinutes = 60; settings.Token = ""; Assert(alerts.Observe(settings, AlertSnapshot(0), false) == null, "An unconfigured account cannot generate an alert");
        return Task.CompletedTask;
    }
    private static Task AlertCooldown()
    {
        var now = DateTimeOffset.UtcNow; var settings = AlertSite(); var alerts = new LowBalanceAlerts(now: () => now);
        var snapshot = AlertSnapshot(1.234m); var first = alerts.Observe(settings, snapshot, false);
        Assert(first != null && !first.Message.Contains("1.234") && !first.Message.Contains("5") && !first.Message.Contains(settings.Token), "System notifications must not contain balances, thresholds or tokens");
        alerts.MarkSent(first);
        Assert(alerts.Observe(settings, snapshot, false) == null && alerts.Observe(settings, AlertSnapshot(0), false) == null, "UI updates and repeated low samples must respect cooldown");
        now = now.AddMinutes(59); Assert(alerts.Observe(settings, AlertSnapshot(0), false) == null, "Cooldown must last for the full configured interval");
        now = now.AddMinutes(1); var repeated = alerts.Observe(settings, AlertSnapshot(0), false);
        Assert(repeated != null, "A persistently low balance may notify at the interval boundary"); alerts.MarkSent(repeated);
        var second = settings.NewSiteDraft(); second.Provider = ProviderKind.Sub2Api; second.BaseUrl = "https://backup.example.test"; second.Token = "fake-backup-alert-token"; second.BalanceAlert.Enabled = true; second.CaptureActiveSite();
        var other = alerts.Observe(second, AlertSnapshot(0), false); Assert(other != null && other.SiteId != first.SiteId, "One site's cooldown must not suppress another site"); alerts.MarkSent(other);
        Assert(alerts.Observe(settings, AlertSnapshot(0), false) == null, "Switching back must preserve a site's cooldown");
        var restored = new LowBalanceAlerts(alerts.History, () => now);
        Assert(restored.Observe(settings, AlertSnapshot(0), false) == null, "Restarting must retain cooldown history");
        Assert(restored.KeepSites(new[] { second.ActiveSiteId }) && !restored.History.ContainsKey(first.SiteId), "Deleting a site must discard its cooldown history");
        var disabled = settings.Clone(); disabled.BalanceAlert.Enabled = false; var sample = AlertSnapshot(0); var fresh = new LowBalanceAlerts();
        Assert(fresh.Observe(disabled, sample, false) == null && fresh.Observe(settings, sample, false) == null && fresh.Observe(settings, AlertSnapshot(0), false) != null, "Enabling alerts must wait for a new successful refresh rather than replaying cached data");
        Assert(LowBalanceAlerts.NotificationName("站点\r\nname\t") == "站点name" && LowBalanceAlerts.NotificationName("") == "当前站点", "Notification names must remain a single short line");
        return Task.CompletedTask;
    }
    private static Task AlertSettings()
    {
        var settings = AlertSite(); var clone = settings.Clone(); clone.BalanceAlert.ThresholdUsd = 12; clone.Sites[0].BalanceAlert.Enabled = false;
        Assert(settings.BalanceAlert.ThresholdUsd == 5 && settings.Sites[0].BalanceAlert.Enabled, "Cloning must isolate nested alert options");
        var draft = settings.NewSiteDraft(); Assert(!draft.BalanceAlert.Enabled && draft.BalanceAlert.ThresholdUsd == 5 && settings.BalanceAlert.Enabled, "A new profile must start with alerts disabled");
        settings.BalanceAlert.ThresholdUsd = .5m; settings.BalanceAlert.CooldownMinutes = 180; settings.CaptureActiveSite();
        var second = settings.NewSiteDraft(); second.Provider = ProviderKind.Sub2Api; second.BaseUrl = "https://second.example.test"; second.Token = "fake-second-alert-token"; second.CaptureActiveSite();
        var directory = Path.Combine(Path.GetTempPath(), "kino-alert-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json")); store.Save(second); var loaded = store.Load();
            Assert(!loaded.BalanceAlert.Enabled && loaded.SelectSite(settings.ActiveSiteId) && loaded.BalanceAlert.Enabled && loaded.BalanceAlert.ThresholdUsd == .5m && loaded.BalanceAlert.CooldownMinutes == 180, "Each site's alert preferences must survive restart and switching");
            var before = loaded.Clone(); loaded.BalanceAlert.ThresholdUsd = 1; Assert(loaded.SameConnection(before), "Editing alerts must not clear the current account snapshot");
            var legacy = JObject.FromObject(settings); legacy.Remove("BalanceAlert"); foreach (var profile in legacy["Sites"]) ((JObject)profile).Remove("BalanceAlert");
            var migrated = JsonConvert.DeserializeObject<AppSettings>(legacy.ToString()); Assert(!migrated.BalanceAlert.Enabled && !migrated.Sites[0].BalanceAlert.Enabled, "Existing users must not receive unsolicited alerts after upgrade");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        Assert(BalanceAlertOptions.FromInput(true, "0", "1").ThresholdUsd == 0 && BalanceAlertOptions.FromInput(true, "0.50", "10080").CooldownMinutes == 10080, "Zero, decimals and documented cooldown boundaries must be accepted");
        foreach (var input in new[] { new[] { "-1", "60" }, new[] { "1,5", "60" }, new[] { "five", "60" }, new[] { "1", "0" }, new[] { "1", "10081" }, new[] { "1", "1.5" } })
        {
            var rejected = false; try { BalanceAlertOptions.FromInput(true, input[0], input[1]); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid or ambiguous alert inputs must be rejected");
        }
        var invalid = new BalanceAlertOptions { Enabled = true, ThresholdUsd = -1 }; invalid.Normalize(); Assert(!invalid.Enabled && invalid.ThresholdUsd == 5, "Malformed stored options must reset to an opt-in default");
        return Task.CompletedTask;
    }
    private static Task AlertHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kino-alert-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "alert-history.json"); var store = new AlertHistoryStore(path);
            Assert(store.Load().Count == 0 && !File.Exists(path), "Loading missing cooldown history must not write a file");
            var now = DateTimeOffset.UtcNow; var history = new Dictionary<string, DateTimeOffset> { ["fake-site-id"] = now }; store.Save(history); store.Save(history);
            Assert(store.Load().Single().Value == now && !File.Exists(path + ".tmp"), "Cooldown history must support atomic replacement");
            var data = JObject.Parse(File.ReadAllText(path)); Assert(data.Properties().Count() == 1 && data.Property("fake-site-id") != null, "Only site IDs and send times may be persisted");
            history["future"] = now.AddYears(10); history["empty-date"] = default; store.Save(history); Assert(store.Load().Count == 1, "Invalid future timestamps must not suppress alerts indefinitely");
            File.WriteAllText(path, "bad history"); Assert(store.Load().Count == 0 && File.ReadAllText(path) == "bad history", "A damaged history file must fail safely without changing account settings or its source");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        return Task.CompletedTask;
    }
    private static async Task AlertController()
    {
        var settings = AlertSite(); var mode = 0; var emitted = new List<string>(); var alerts = new LowBalanceAlerts();
        using (var transport = Transport(_ => mode == 1 ? Json("{}", 500) : Json(mode == 2 ? "{\"mode\":\"subscription\",\"isValid\":true}" : "{\"mode\":\"unrestricted\",\"isValid\":true,\"balance\":0.5}")))
        using (var controller = new MonitorController(new UsageClient(transport), settings))
        {
            controller.Changed += () => { var alert = alerts.Observe(controller.Settings, controller.Snapshot, controller.IsRefreshing); if (alert != null) { emitted.Add(alert.SiteId); alerts.MarkSent(alert); } };
            await controller.RefreshAsync(); Assert(emitted.Count == 1, "A successful low-balance refresh must notify once");
            var metadata = controller.Settings.Clone(); metadata.SiteName = "改名"; controller.ApplySettings(metadata); await controller.RefreshAsync(); Assert(emitted.Count == 1, "Saving preferences and refreshing must not replay an alert");
            var second = controller.Settings.NewSiteDraft(); second.Provider = ProviderKind.Sub2Api; second.BaseUrl = "https://second.example.test"; second.Token = "fake-controller-alert-token"; second.BalanceAlert.Enabled = true; second.CaptureActiveSite();
            controller.ApplySettings(second); mode = 1; await controller.RefreshAsync(); mode = 2; await controller.RefreshAsync(); Assert(emitted.Count == 1, "Failed or balance-less requests on a new site must not alert using the previous site's balance");
            mode = 0; await controller.RefreshAsync(); Assert(emitted.Count == 2 && emitted.Last() == second.ActiveSiteId, "The new site's successful balance must use its own cooldown");
        }
    }
    private static Task Validation()
    {
        var settings = Settings(ProviderKind.NewApi);
        settings.TaskbarMetrics = new List<MetricKind> { MetricKind.Latency, MetricKind.Balance, MetricKind.Latency, (MetricKind)999 };
        settings.Normalize(); Assert(settings.TaskbarMetrics.SequenceEqual(new[] { MetricKind.Latency, MetricKind.Balance }), "Order/dedup failed");
        settings.BaseUrl = "https://relay.example.test/subpath/api/";
        Assert(ApiTransport.Endpoint(settings, "api/user/self").AbsoluteUri == "https://relay.example.test/subpath/api/user/self", "API prefix/subpath failed");
        settings.BaseUrl = "https://relay.example.test/?token=secret"; Assert(settings.ValidateConnection() != null, "Do not accept credentials in URL query");
        settings.BaseUrl = "http://untrusted.example.test"; Assert(settings.ValidateConnection() != null, "Remote credentials should use HTTPS");
        settings.BaseUrl = "http://127.0.0.1:8080"; Assert(settings.ValidateConnection() == null, "Local development should work");
        return Task.CompletedTask;
    }
    private static async Task Demo()
    {
        using (var transport = Transport(r => throw new Exception("Demo must not use network")))
        {
            var data = await new UsageClient(transport).FetchAsync(AppSettings.DemoSettings(), CancellationToken.None);
            Assert(data.IsDemo && Metrics.Value(MetricKind.Balance, data, true) == "••••", "Demo/privacy failed");
            Assert(Metrics.Value(MetricKind.TodayTokens, data) == "1.25M", "Compact numbers failed");
        }
    }
    private static async Task FirstRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kino-first-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var fresh = store.Load();
            Assert(fresh.Provider == ProviderKind.NewApi && fresh.BaseUrl == "" && fresh.RequiresConnectionSetup && fresh.Token == "", "Fresh install must require an explicitly chosen site without credentials");
            using (var transport = Transport(_ => throw new Exception("An unconfigured installation must not contact any preset site")))
            {
                try { await new UsageClient(transport).FetchAsync(fresh, CancellationToken.None); throw new Exception("Expected URL validation"); }
                catch (ApiException ex) { Assert(ex.Message.Contains("HTTPS"), "An unconfigured site must fail local URL validation"); }
            }
            var custom = Settings(ProviderKind.Sub2Api); custom.Theme = ThemeKind.Dark; custom.TaskbarMetrics.Reverse();
            store.Save(custom);
            var upgraded = store.Load();
            Assert(upgraded.Provider == ProviderKind.Sub2Api && upgraded.BaseUrl == custom.BaseUrl && upgraded.Token == custom.Token &&
                upgraded.Theme == custom.Theme && upgraded.TaskbarMetrics.SequenceEqual(custom.TaskbarMetrics) && !upgraded.RequiresConnectionSetup, "Upgrades must preserve a user's existing connection and preferences");
            store.Save(AppSettings.DemoSettings());
            Assert(!store.Load().RequiresConnectionSetup, "Choosing demo must persist across restarts");
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        var invalid = new AppSettings { BaseUrl = "https://custom.example.test", Token = "sk-not-a-real-key" };
        using (var transport = Transport(_ => throw new Exception("A model key must be rejected before any HTTP call")))
        {
            try { await new UsageClient(transport).FetchAsync(invalid, CancellationToken.None); throw new Exception("Expected model key rejection"); }
            catch (ApiException ex) { Assert(ex.Message.Contains("用户访问令牌"), "Model key rejection must explain the correct credential"); }
        }
        invalid.Provider = ProviderKind.Sub2Api;
        Assert(invalid.ValidateConnection() == null, "Sub2API must still accept model API keys");
        invalid.Normalize();
        Assert(invalid.SiteName == "custom.example.test", "An unnamed custom site must use its own host as the display name");
    }
}
