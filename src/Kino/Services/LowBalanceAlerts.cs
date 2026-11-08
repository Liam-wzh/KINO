using Kino.Models;

namespace Kino.Services
{
    public sealed class BalanceAlert
    {
        public string SiteId { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string Title => "低余额提醒 · KINO";
        public string Message { get; set; }
    }

    public sealed class LowBalanceAlerts
    {
        private readonly Dictionary<string, DateTimeOffset> _lastSent;
        private readonly Func<DateTimeOffset> _now;
        private UsageSnapshot _observed;
        private string _observedSite;
        public LowBalanceAlerts(IDictionary<string, DateTimeOffset> history = null, Func<DateTimeOffset> now = null)
        {
            _lastSent = history == null ? new Dictionary<string, DateTimeOffset>() : new Dictionary<string, DateTimeOffset>(history);
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }
        public BalanceAlert Observe(AppSettings settings, UsageSnapshot snapshot, bool refreshing)
        {
            if (refreshing) return null;
            if (ReferenceEquals(_observed, snapshot) && _observedSite == settings.ActiveSiteId) return null;
            _observed = snapshot; _observedSite = settings.ActiveSiteId;
            var options = settings.BalanceAlert;
            if (options == null || !options.Enabled || options.Validate() != null || string.IsNullOrEmpty(settings.ActiveSiteId) ||
                settings.Provider == ProviderKind.Demo || settings.RequiresConnectionSetup || snapshot == null || snapshot.IsDemo ||
                snapshot.IsStale || !snapshot.Connected || !string.IsNullOrEmpty(snapshot.Error) || !snapshot.Balance.HasValue ||
                snapshot.UpdatedAt == default || snapshot.Balance.Value > options.ThresholdUsd) return null;
            var now = _now().ToUniversalTime();
            if (_lastSent.TryGetValue(settings.ActiveSiteId, out var last) && now - last < TimeSpan.FromMinutes(options.CooldownMinutes)) return null;
            return new BalanceAlert { SiteId = settings.ActiveSiteId, CreatedAtUtc = now,
                Message = "「" + NotificationName(settings.SiteName) + "」的可用余额已达到提醒阈值。\n点击查看当前站点的用量卡片。" };
        }
        public void MarkSent(BalanceAlert alert) => _lastSent[alert.SiteId] = alert.CreatedAtUtc;
        public Dictionary<string, DateTimeOffset> History => new Dictionary<string, DateTimeOffset>(_lastSent);
        public bool KeepSites(IEnumerable<string> ids)
        {
            var keep = new HashSet<string>(ids); var removed = false;
            foreach (var id in _lastSent.Keys.Where(id => !keep.Contains(id)).ToArray()) { _lastSent.Remove(id); removed = true; }
            return removed;
        }
        public static string NotificationName(string name)
        {
            var clean = new string((name ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();
            return clean.Length == 0 ? "当前站点" : clean;
        }
    }
}
