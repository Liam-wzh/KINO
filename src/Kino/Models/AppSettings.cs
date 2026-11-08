using Newtonsoft.Json;

namespace Kino.Models
{
    public enum ProviderKind { Demo, NewApi, Sub2Api }
    public enum Sub2AuthKind { ApiKey, AccountToken }
    public enum ThemeKind { System, Light, Dark }
    public enum MetricKind { Balance, TodaySpend, TodayTokens, Latency, TodayRequests, Connection }

    public sealed class AppSettings
    {
        public ProviderKind Provider { get; set; } = ProviderKind.NewApi;
        public Sub2AuthKind Sub2Auth { get; set; } = Sub2AuthKind.ApiKey;
        public string SiteName { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string ProtectedToken { get; set; } = "";
        [JsonIgnore] public string Token { get; set; } = "";
        public int NewApiUserId { get; set; }
        public decimal QuotaPerUnit { get; set; } = 500000m;
        public bool AutoQuotaPerUnit { get; set; } = true;
        public BalanceAlertOptions BalanceAlert { get; set; } = new BalanceAlertOptions();
        public int RefreshSeconds { get; set; } = 60;
        public ThemeKind Theme { get; set; } = ThemeKind.System;
        public bool HideAmounts { get; set; }
        public int TaskbarOffset { get; set; }
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<MetricKind> TaskbarMetrics { get; set; } = new List<MetricKind>
            { MetricKind.Balance, MetricKind.TodaySpend, MetricKind.TodayTokens, MetricKind.Latency };
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<MetricKind> CardMetrics { get; set; } = new List<MetricKind>
            { MetricKind.Balance, MetricKind.TodaySpend, MetricKind.TodayTokens, MetricKind.TodayRequests, MetricKind.Latency, MetricKind.Connection };
        public bool ShowChart { get; set; } = true;
        public bool ShowModels { get; set; } = true;
        public string ActiveSiteId { get; set; } = "";
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<SiteProfile> Sites { get; set; } = new List<SiteProfile>();
        [JsonIgnore] public bool RequiresConnectionSetup => Provider != ProviderKind.Demo && ValidateConnection() != null;
        public static AppSettings DemoSettings() => new AppSettings { Provider = ProviderKind.Demo, SiteName = "演示站点" };

        public AppSettings Clone() => new AppSettings
        {
            Provider = Provider, Sub2Auth = Sub2Auth, SiteName = SiteName, BaseUrl = BaseUrl,
            Token = Token, ProtectedToken = ProtectedToken, NewApiUserId = NewApiUserId,
            QuotaPerUnit = QuotaPerUnit, AutoQuotaPerUnit = AutoQuotaPerUnit,
            BalanceAlert = (BalanceAlert ?? new BalanceAlertOptions()).Clone(),
            RefreshSeconds = RefreshSeconds, Theme = Theme, HideAmounts = HideAmounts,
            TaskbarOffset = TaskbarOffset, TaskbarMetrics = new List<MetricKind>(TaskbarMetrics),
            CardMetrics = new List<MetricKind>(CardMetrics), ShowChart = ShowChart, ShowModels = ShowModels,
            ActiveSiteId = ActiveSiteId, Sites = (Sites ?? new List<SiteProfile>()).Where(s => s != null).Select(s => s.Clone()).ToList()
        };

        public void CaptureActiveSite()
        {
            Normalize();
            Sites = (Sites ?? new List<SiteProfile>()).Where(s => s != null).ToList();
            // A fresh install or deletion of the final site remains unconfigured.
            if (Provider != ProviderKind.Demo && string.IsNullOrEmpty(BaseUrl) && string.IsNullOrEmpty(Token) &&
                string.IsNullOrEmpty(ProtectedToken) && !Sites.Any(s => s.Id == ActiveSiteId)) return;
            if (string.IsNullOrEmpty(ActiveSiteId)) ActiveSiteId = Guid.NewGuid().ToString("N");
            var index = Sites.FindIndex(s => s.Id == ActiveSiteId);
            var profile = SiteProfile.Capture(this, ActiveSiteId);
            if (index < 0) Sites.Add(profile); else Sites[index] = profile;
        }

        public bool SelectSite(string id)
        {
            var site = Sites?.FirstOrDefault(s => s.Id == id);
            if (site == null) return false;
            ActiveSiteId = site.Id; site.ApplyTo(this); Normalize(); return true;
        }

        public AppSettings NewSiteDraft()
        {
            var draft = Clone(); draft.CaptureActiveSite();
            draft.ActiveSiteId = Guid.NewGuid().ToString("N");
            new SiteProfile().ApplyTo(draft);
            return draft;
        }

        public bool RemoveSite(string id)
        {
            var index = Sites.FindIndex(s => s.Id == id);
            if (index < 0) return false;
            Sites.RemoveAt(index);
            if (ActiveSiteId == id)
            {
                if (Sites.Count > 0) SelectSite(Sites[Math.Min(index, Sites.Count - 1)].Id);
                else { ActiveSiteId = ""; new SiteProfile().ApplyTo(this); Normalize(); }
            }
            return true;
        }

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(ProviderKind), Provider)) Provider = ProviderKind.Demo;
            if (!Enum.IsDefined(typeof(Sub2AuthKind), Sub2Auth)) Sub2Auth = Sub2AuthKind.ApiKey;
            if (!Enum.IsDefined(typeof(ThemeKind), Theme)) Theme = ThemeKind.System;
            BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/');
            SiteName = string.IsNullOrWhiteSpace(SiteName)
                ? (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var site) ? site.Host : "我的 AI 中转站")
                : SiteName.Trim();
            Token = (Token ?? "").Trim();
            if (Token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) Token = Token.Substring(7).Trim();
            RefreshSeconds = Math.Max(15, Math.Min(3600, RefreshSeconds));
            TaskbarOffset = Math.Max(0, Math.Min(1200, TaskbarOffset));
            QuotaPerUnit = QuotaPerUnit > 0 ? QuotaPerUnit : 500000m;
            BalanceAlert = BalanceAlert ?? new BalanceAlertOptions(); BalanceAlert.Normalize();
            TaskbarMetrics = CleanMetrics(TaskbarMetrics);
            CardMetrics = CleanMetrics(CardMetrics);
            if (TaskbarMetrics.Count == 0) TaskbarMetrics.Add(MetricKind.Balance);
        }

        private static List<MetricKind> CleanMetrics(List<MetricKind> metrics) => (metrics ?? new List<MetricKind>())
            .Where(m => Enum.IsDefined(typeof(MetricKind), m)).Distinct().ToList();

        public string ValidateConnection()
        {
            if (Provider == ProviderKind.Demo) return null;
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return "请输入有效的 HTTPS 站点地址，例如 https://api.example.com。";
            if (string.IsNullOrWhiteSpace(Token)) return "请填写用户访问令牌或 API Key。";
            if (Token.IndexOfAny(new[] { '\r', '\n' }) >= 0) return "令牌中不能包含换行。";
            if (Provider == ProviderKind.NewApi && Token.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                return "这是模型调用 Key。请在站点安全设置中创建用户访问令牌，并授予 profile:read 与 usage:read。";
            return null;
        }

        public bool SameConnection(AppSettings other) => other != null && ActiveSiteId == other.ActiveSiteId && Provider == other.Provider &&
            Sub2Auth == other.Sub2Auth && BaseUrl == other.BaseUrl && Token == other.Token &&
            NewApiUserId == other.NewApiUserId && QuotaPerUnit == other.QuotaPerUnit && AutoQuotaPerUnit == other.AutoQuotaPerUnit;
    }
}
