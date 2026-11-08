using System.Globalization;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using Kino.Models;
using static Kino.Services.JsonValues;

namespace Kino.Services
{
    public sealed class UsageClient
    {
        private readonly ApiTransport _transport;
        public UsageClient(ApiTransport transport) { _transport = transport; }

        public async Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (settings.Provider == ProviderKind.Demo) return Demo();
            var error = settings.ValidateConnection();
            if (error != null) throw new ApiException(error);
            var result = settings.Provider == ProviderKind.NewApi
                ? await NewApiAsync(settings, cancellation).ConfigureAwait(false)
                : settings.Sub2Auth == Sub2AuthKind.ApiKey
                    ? await Sub2KeyAsync(settings, cancellation).ConfigureAwait(false)
                    : await Sub2AccountAsync(settings, cancellation).ConfigureAwait(false);
            result.Connected = true;
            result.UpdatedAt = DateTimeOffset.Now;
            return result;
        }

        private async Task<UsageSnapshot> NewApiAsync(AppSettings settings, CancellationToken cancellation)
        {
            var userTask = _transport.GetAsync(settings, "api/user/self", cancellation);
            var statusTask = OptionalAsync(settings, "api/status", cancellation, false);
            // Observe every task even if one request fails.
            await Task.WhenAll(userTask, statusTask).ConfigureAwait(false);
            var user = userTask.Result;
            var status = statusTask.Result?.Data;
            var quota = Decimal(user.Data, "quota");
            if (!quota.HasValue) throw new ApiException("用户接口缺少额度字段，请确认使用 New API 用户访问令牌。\n不要填用于模型调用的 sk- Key。");
            var unit = settings.AutoQuotaPerUnit ? Decimal(status, "quota_per_unit") ?? settings.QuotaPerUnit : settings.QuotaPerUnit;
            if (unit <= 0) throw new ApiException("站点额度换算比例无效，请在设置中填写正确的比例。");
            var result = new UsageSnapshot { Balance = quota / unit, LatencyMs = user.ElapsedMs, Scope = "账户统计 · USD" };
            if (settings.AutoQuotaPerUnit && !Decimal(status, "quota_per_unit").HasValue)
                result.Note = "未取得站点额度比例，暂按设置中的比例换算。";
            if (status?["enable_data_export"]?.Type == JTokenType.Boolean && !status["enable_data_export"].Value<bool>())
            {
                result.Note = "站点未开启数据看板，今日用量暂不可用。";
                return result;
            }
            var start = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
            var end = DateTimeOffset.Now.ToUnixTimeSeconds();
            var daily = await OptionalAsync(settings, $"api/data/self?start_timestamp={start}&end_timestamp={end}", cancellation).ConfigureAwait(false);
            if (!(daily?.Data is JArray rows))
            {
                result.Note = "余额已更新；今日统计未能获取，请检查数据看板是否开启及令牌权限。";
                return result;
            }
            result.TodaySpend = 0m;
            result.TodayTokens = 0;
            result.TodayRequests = 0;
            var points = new Dictionary<int, UsagePoint>();
            var models = new Dictionary<string, ModelUsage>();
            foreach (var row in rows)
            {
                var spend = (Decimal(row, "quota") ?? 0m) / unit;
                var tokens = Long(row, "token_used") ?? 0;
                var requests = Long(row, "count") ?? 0;
                result.TodaySpend += spend;
                result.TodayTokens += tokens;
                result.TodayRequests += requests;
                var timestamp = Long(row, "created_at");
                if (timestamp.HasValue)
                {
                    var hour = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).LocalDateTime.Hour;
                    if (!points.TryGetValue(hour, out var point)) points[hour] = point = new UsagePoint { Time = DateTime.Today.AddHours(hour) };
                    point.Spend += spend; point.Tokens += tokens; point.Requests += requests;
                }
                var name = Text(row, "model_name") ?? "未知模型";
                if (!models.TryGetValue(name, out var model)) models[name] = model = new ModelUsage { Name = name, Spend = 0m };
                model.Spend += spend; model.Tokens += tokens; model.Requests += requests;
            }
            result.History = Enumerable.Range(0, 24).Select(h => points.TryGetValue(h, out var p) ? p : new UsagePoint { Time = DateTime.Today.AddHours(h) }).ToList();
            result.Models = models.Values.OrderByDescending(m => m.Spend).ToList();
            return result;
        }

        private async Task<UsageSnapshot> Sub2KeyAsync(AppSettings settings, CancellationToken cancellation)
        {
            var date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var response = await _transport.GetAsync(settings, "v1/usage?days=7&start_date=" + date + "&end_date=" + date, cancellation).ConfigureAwait(false);
            var data = response.Data;
            if (data["isValid"]?.Type == JTokenType.Boolean && !data["isValid"].Value<bool>())
                throw new ApiException("此 API Key 已失效，请检查 Key 的状态。");
            if (data["mode"] == null && data["remaining"] == null && data["balance"] == null && data["usage"] == null)
                throw new ApiException("用量接口返回的格式无法识别，请检查 Sub2API 服务端版本。");
            var result = new UsageSnapshot
            {
                Balance = Decimal(data, "balance") ?? Decimal(data, "remaining"),
                TodaySpend = Decimal(data, "usage.today.actual_cost"),
                TodayTokens = Long(data, "usage.today.total_tokens"),
                TodayRequests = Long(data, "usage.today.requests"),
                AverageDurationMs = Double(data, "usage.average_duration_ms"),
                LatencyMs = response.ElapsedMs,
                Scope = "当前 API Key 用量 · USD · 站点时区",
                ModelPeriodLabel = DateTime.Today.ToString("MM/dd") + " Token",
                HistoryLabel = "最近 7 天消费 · 当前 Key"
            };
            if (Text(data, "mode") == "quota_limited") result.BalanceLabel = "Key 剩余额度";
            else if (data["subscription"] != null || (Text(data, "planName") is string plan && plan != "钱包余额")) result.BalanceLabel = "订阅剩余额度";
            foreach (var row in Array(data["daily_usage"]))
            {
                if (DateTime.TryParse(Text(row, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    result.History.Add(new UsagePoint { Time = time, Spend = Decimal(row, "actual_cost") ?? 0m, Tokens = Long(row, "total_tokens") ?? 0, Requests = Long(row, "requests") ?? 0 });
            }
            result.Models = ParseModels(data["model_stats"]);
            if (!result.TodaySpend.HasValue) result.Note = "当前服务端未提供今日用量，已保留可用的余额信息。";
            if (data["subscription"] != null && !result.Balance.HasValue) result.Note = "订阅剩余额度未由服务端提供。";
            return result;
        }

        private async Task<UsageSnapshot> Sub2AccountAsync(AppSettings settings, CancellationToken cancellation)
        {
            var userTask = _transport.GetAsync(settings, "api/v1/auth/me", cancellation);
            var statsTask = OptionalAsync(settings, "api/v1/usage/dashboard/stats", cancellation);
            await Task.WhenAll(userTask, statsTask).ConfigureAwait(false);
            var user = userTask.Result;
            var stats = statsTask.Result?.Data;
            var balance = Decimal(user.Data, "balance");
            if (!balance.HasValue) throw new ApiException("账户接口缺少余额字段，请检查 Sub2API 账户访问令牌。");
            var result = new UsageSnapshot
            {
                Balance = balance, TodaySpend = Decimal(stats, "today_actual_cost"),
                TodayTokens = Long(stats, "today_tokens"), TodayRequests = Long(stats, "today_requests"),
                AverageDurationMs = Double(stats, "average_duration_ms"), LatencyMs = user.ElapsedMs,
                Scope = "账户统计 · USD · 站点时区",
                HistoryLabel = DateTime.Today.ToString("MM/dd") + " 消费 · 按小时",
                ModelPeriodLabel = DateTime.Today.ToString("MM/dd") + " Token"
            };
            var date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var query = "?start_date=" + date + "&end_date=" + date;
            var trendTask = OptionalAsync(settings, "api/v1/usage/dashboard/trend" + query + "&granularity=hour", cancellation);
            var modelTask = OptionalAsync(settings, "api/v1/usage/dashboard/models" + query, cancellation);
            await Task.WhenAll(trendTask, modelTask).ConfigureAwait(false);
            foreach (var row in Array(trendTask.Result?.Data?["trend"]))
            {
                if (DateTime.TryParse(Text(row, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    result.History.Add(new UsagePoint { Time = time, Spend = Decimal(row, "actual_cost") ?? 0m, Tokens = Long(row, "total_tokens") ?? 0, Requests = Long(row, "requests") ?? 0 });
            }
            result.Models = ParseModels(modelTask.Result?.Data?["models"]);
            if (stats == null) result.Note = "余额已更新，账户统计接口暂不可用。";
            return result;
        }

        private static List<ModelUsage> ParseModels(JToken token) => Array(token).Select(row => new ModelUsage
        {
            Name = Text(row, "model") ?? Text(row, "model_name") ?? "未知模型",
            Spend = Decimal(row, "actual_cost"), Tokens = Long(row, "total_tokens") ?? 0,
            Requests = Long(row, "requests") ?? 0
        }).OrderByDescending(m => m.Tokens).ToList();

        private async Task<ApiResult> OptionalAsync(AppSettings settings, string path, CancellationToken cancellation, bool authenticate = true)
        {
            try { return await _transport.GetAsync(settings, path, cancellation, authenticate).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
            catch (ApiException) { return null; }
            catch (HttpRequestException) { return null; }
        }

        public static UsageSnapshot Demo()
        {
            var values = new[] { 0.02m, 0.01m, 0m, 0.01m, 0.02m, 0.04m, 0.06m, 0.10m, 0.23m, 0.19m, 0.31m, 0.26m, 0.18m, 0.34m, 0.21m, 0.14m, 0.24m, 0.18m, 0.32m, 0.28m, 0.17m, 0.12m, 0.10m, 0.04m };
            var result = new UsageSnapshot
            {
                Balance = 124.86m, TodaySpend = 3.42m, TodayTokens = 1248500,
                TodayRequests = 186, LatencyMs = 128, AverageDurationMs = 2150,
                UpdatedAt = DateTimeOffset.Now, Connected = true, IsDemo = true,
                Scope = "演示数据 · 未连接真实账户",
                Note = "当前为演示模式，可在设置中连接 New API 或 Sub2API。"
            };
            var total = values.Sum();
            for (var hour = 0; hour < 24; hour++)
                result.History.Add(new UsagePoint { Time = DateTime.Today.AddHours(hour), Spend = values[hour] / total * 3.42m, Tokens = (long)(values[hour] / total * 1248500), Requests = (long)(values[hour] / total * 186) });
            result.Models.Add(new ModelUsage { Name = "claude-sonnet", Spend = 1.86m, Tokens = 624000, Requests = 72 });
            result.Models.Add(new ModelUsage { Name = "gpt-4.1", Spend = 1.12m, Tokens = 432000, Requests = 86 });
            result.Models.Add(new ModelUsage { Name = "gemini-flash", Spend = .44m, Tokens = 192500, Requests = 28 });
            return result;
        }
    }
}
