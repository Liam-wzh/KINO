using Newtonsoft.Json;

namespace Kino.Models
{
    public sealed class SiteProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SiteName { get; set; } = "";
        public ProviderKind Provider { get; set; } = ProviderKind.NewApi;
        public Sub2AuthKind Sub2Auth { get; set; }
        public string BaseUrl { get; set; } = "";
        public string ProtectedToken { get; set; } = "";
        [JsonIgnore] public string Token { get; set; } = "";
        public int NewApiUserId { get; set; }
        public decimal QuotaPerUnit { get; set; } = 500000m;
        public bool AutoQuotaPerUnit { get; set; } = true;
        public BalanceAlertOptions BalanceAlert { get; set; } = new BalanceAlertOptions();
        [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(SiteName) ? "未命名站点" : SiteName;
        [JsonIgnore] public string ProtocolLabel => Provider == ProviderKind.Demo ? "演示模式" : Provider == ProviderKind.NewApi ? "New API" :
            Sub2Auth == Sub2AuthKind.AccountToken ? "Sub2API · 账户" : "Sub2API · API Key";
        [JsonIgnore] public string Summary => ProtocolLabel + (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? " · " + uri.Host : "");

        public static SiteProfile Capture(AppSettings settings, string id) => new SiteProfile
        {
            Id = id, SiteName = settings.SiteName, Provider = settings.Provider, Sub2Auth = settings.Sub2Auth,
            BaseUrl = settings.BaseUrl, Token = settings.Token, ProtectedToken = settings.ProtectedToken,
            NewApiUserId = settings.NewApiUserId, QuotaPerUnit = settings.QuotaPerUnit, AutoQuotaPerUnit = settings.AutoQuotaPerUnit,
            BalanceAlert = (settings.BalanceAlert ?? new BalanceAlertOptions()).Clone()
        };
        public void ApplyTo(AppSettings settings)
        {
            settings.Provider = Provider; settings.Sub2Auth = Sub2Auth; settings.SiteName = SiteName; settings.BaseUrl = BaseUrl;
            settings.Token = Token; settings.ProtectedToken = ProtectedToken; settings.NewApiUserId = NewApiUserId;
            settings.QuotaPerUnit = QuotaPerUnit; settings.AutoQuotaPerUnit = AutoQuotaPerUnit;
            settings.BalanceAlert = (BalanceAlert ?? new BalanceAlertOptions()).Clone();
        }
        public SiteProfile Clone()
        {
            var copy = (SiteProfile)MemberwiseClone(); copy.BalanceAlert = (BalanceAlert ?? new BalanceAlertOptions()).Clone(); return copy;
        }
    }
}
