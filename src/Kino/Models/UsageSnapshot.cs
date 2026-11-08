using System.Globalization;

namespace Kino.Models
{
    public sealed class UsagePoint
    {
        public DateTime Time { get; set; }
        public decimal Spend { get; set; }
        public long Tokens { get; set; }
        public long Requests { get; set; }
    }

    public sealed class ModelUsage
    {
        public string Name { get; set; }
        public decimal? Spend { get; set; }
        public long Tokens { get; set; }
        public long Requests { get; set; }
    }

    public sealed class UsageSnapshot
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public decimal? Balance { get; set; }
        public string BalanceLabel { get; set; } = "可用余额";
        public decimal? TodaySpend { get; set; }
        public long? TodayTokens { get; set; }
        public long? TodayRequests { get; set; }
        public double? LatencyMs { get; set; }
        public double? AverageDurationMs { get; set; }
        public bool Connected { get; set; }
        public bool IsDemo { get; set; }
        public bool IsStale { get; set; }
        public string Scope { get; set; } = "账户统计";
        public string Note { get; set; } = "";
        public string Error { get; set; } = "";
        public string HistoryLabel { get; set; } = "今日消费 · 按小时";
        public string ModelPeriodLabel { get; set; } = "今日 Token";
        public List<UsagePoint> History { get; set; } = new List<UsagePoint>();
        public List<ModelUsage> Models { get; set; } = new List<ModelUsage>();

        public UsageSnapshot AsStale(string error) => new UsageSnapshot
        {
            UpdatedAt = UpdatedAt, Balance = Balance, BalanceLabel = BalanceLabel,
            TodaySpend = TodaySpend, TodayTokens = TodayTokens, TodayRequests = TodayRequests,
            AverageDurationMs = AverageDurationMs, LatencyMs = null, Connected = false,
            IsDemo = IsDemo, IsStale = true, Scope = Scope, Note = Note, Error = error,
            HistoryLabel = HistoryLabel, ModelPeriodLabel = ModelPeriodLabel, History = History, Models = Models
        };
    }

    public static class Metrics
    {
        public static IReadOnlyList<MetricKind> All { get; } = Enum.GetValues(typeof(MetricKind)).Cast<MetricKind>().ToArray();
        public static string Name(MetricKind kind)
        {
            switch (kind)
            {
                case MetricKind.Balance: return "可用余额";
                case MetricKind.TodaySpend: return "今日消费";
                case MetricKind.TodayTokens: return "今日 Token";
                case MetricKind.Latency: return "接口延迟";
                case MetricKind.TodayRequests: return "今日请求";
                default: return "连接状态";
            }
        }
        public static string ShortName(MetricKind kind)
        {
            switch (kind)
            {
                case MetricKind.Balance: return "余额";
                case MetricKind.TodaySpend: return "今日";
                case MetricKind.TodayTokens: return "Token";
                case MetricKind.Latency: return "延迟";
                case MetricKind.TodayRequests: return "请求";
                default: return "状态";
            }
        }
        public static string Value(MetricKind kind, UsageSnapshot data, bool hideAmounts = false)
        {
            if (kind == MetricKind.Connection) return data == null ? "待连接" : data.IsDemo ? "演示" : data.Connected ? "已连接" : "已断开";
            if (data == null) return "—";
            if (hideAmounts && (kind == MetricKind.Balance || kind == MetricKind.TodaySpend)) return "••••";
            switch (kind)
            {
                case MetricKind.Balance: return Money(data.Balance);
                case MetricKind.TodaySpend: return Money(data.TodaySpend);
                case MetricKind.TodayTokens: return data.TodayTokens.HasValue ? Compact(data.TodayTokens.Value) : "—";
                case MetricKind.Latency: return data.LatencyMs.HasValue ? Math.Round(data.LatencyMs.Value).ToString(CultureInfo.InvariantCulture) + " ms" : "—";
                case MetricKind.TodayRequests: return data.TodayRequests.HasValue ? Compact(data.TodayRequests.Value) : "—";
                default: return "—";
            }
        }
        public static string Money(decimal? amount) => amount.HasValue ? "$" + amount.Value.ToString("N2", CultureInfo.InvariantCulture) : "—";
        public static string Compact(long amount)
        {
            if (amount >= 1000000000) return (amount / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (amount >= 1000000) return (amount / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (amount >= 1000) return (amount / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return amount.ToString(CultureInfo.InvariantCulture);
        }
    }
}
