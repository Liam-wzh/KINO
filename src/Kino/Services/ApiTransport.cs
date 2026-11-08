using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Kino.Models;

namespace Kino.Services
{
    public sealed class ApiException : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public ApiException(string message, int status = 0, string code = null) : base(message) { Status = status; Code = code; }
    }

    public sealed class ApiResult
    {
        public JToken Data { get; set; }
        public double ElapsedMs { get; set; }
    }

    public sealed class ApiTransport : IDisposable
    {
        private readonly HttpClient _client;
        public ApiTransport(HttpMessageHandler handler = null)
        {
            _client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        }

        public static Uri Endpoint(AppSettings settings, string route)
        {
            // Accept either the site root or its conventional API prefix without doubling it.
            var root = settings.BaseUrl.TrimEnd('/');
            var suffixes = settings.Provider == ProviderKind.NewApi ? new[] { "/api" } : new[] { "/api/v1", "/v1" };
            foreach (var suffix in suffixes)
                if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { root = root.Substring(0, root.Length - suffix.Length); break; }
            return new Uri(root + "/" + route.TrimStart('/'));
        }

        public async Task<ApiResult> GetAsync(AppSettings settings, string route, CancellationToken cancellation, bool authenticate = true)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(settings, route)))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd(ProductBrand.Name + "/" + ProductBrand.Version);
                if (authenticate)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
                    if (settings.Provider == ProviderKind.NewApi && settings.NewApiUserId > 0)
                        request.Headers.Add("New-Api-User", settings.NewApiUserId.ToString(CultureInfo.InvariantCulture));
                }
                var watch = Stopwatch.StartNew();
                using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation).ConfigureAwait(false))
                {
                    watch.Stop();
                    var status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                    {
                        if (status == 401 || status == 403)
                        {
                            if (settings.Provider == ProviderKind.NewApi)
                            {
                                try
                                {
                                    var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                    var knownError = NewApiError(JToken.Parse(errorBody), status);
                                    if (knownError != null) throw knownError;
                                }
                                catch (JsonException) { }
                            }
                            throw new ApiException("认证失败或权限不足，请检查令牌、用户 ID 及接口权限。", status);
                        }
                        if (status == 404) throw new ApiException("未找到用量接口，请检查站点地址、平台类型及服务端版本。", status);
                        if (status == 429) throw new ApiException("请求过于频繁，请增大刷新间隔。", status);
                        if (status >= 300 && status < 400) throw new ApiException("站点返回重定向，请直接填写最终的 HTTPS 站点地址。", status);
                        throw new ApiException("服务器返回 HTTP " + status + "，请稍后重试。", status);
                    }
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    JToken json;
                    try { json = JToken.Parse(body); }
                    catch (JsonException) { throw new ApiException("接口没有返回 JSON，请确认填写的是中转站地址。", status); }
                    if (!(json is JObject)) throw new ApiException("接口返回了无法识别的数据。", status);
                    var success = json["success"];
                    if (success != null && success.Type == JTokenType.Boolean && !success.Value<bool>())
                        throw (settings.Provider == ProviderKind.NewApi ? NewApiError(json, status) : null)
                            ?? new ApiException("接口拒绝请求，请检查访问令牌及用户 ID。", status);
                    var code = json["code"];
                    if (code != null && code.Type == JTokenType.Integer && code.Value<long>() != 0)
                        throw new ApiException("接口返回业务错误，请检查凭据与接口权限。", status);
                    // Never pass arbitrary server messages to the UI: they may contain credentials.
                    return new ApiResult { Data = json["data"] ?? json, ElapsedMs = watch.Elapsed.TotalMilliseconds };
                }
            }
        }

        private static ApiException NewApiError(JToken json, int status)
        {
            if (!(json is JObject) || json["code"]?.Type != JTokenType.String) return null;
            var code = json["code"].Value<string>();
            // Only recognized codes reach the UI; server messages and scope names may echo secrets.
            var message = code switch
            {
                "AUTH_UNAUTHORIZED" => "用户访问令牌无效，请在站点安全设置中重新生成。模型调用用的 sk- Key 无法读取账户信息。",
                "ACCESS_TOKEN_EXPIRED" => "用户访问令牌已过期，请在站点安全设置中重新生成并更新本机设置。",
                "ACCESS_TOKEN_LEGACY_RETIRED" => "站点已停用旧版访问令牌，请在安全设置中创建新的用户访问令牌。",
                "ACCESS_TOKEN_SCOPE_DENIED" => "用户访问令牌缺少读取权限，请授予 profile:read（用户资料）与 usage:read（用量统计）。",
                "AUTH_TOKEN_EXPIRED" => "账户访问令牌已过期，请改用安全设置中创建的用户访问令牌。",
                "AUTH_SESSION_REVOKED" => "此账户会话已失效，请重新创建用户访问令牌。",
                "AUTH_USER_DISABLED" => "此账户已被停用，请联系站点管理员。",
                "AUTH_SESSION_REQUIRED" => "此接口要求网页登录会话，请确认站点版本支持用户访问令牌读取账户数据。",
                _ => null
            };
            return message == null ? null : new ApiException(message, status, code);
        }
        public void Dispose() => _client.Dispose();
    }

    internal static class JsonValues
    {
        public static decimal? Decimal(JToken token, string path) => token?.SelectToken(path)?.Type is JTokenType.Integer or JTokenType.Float
            ? token.SelectToken(path).Value<decimal>() : (decimal?)null;
        public static long? Long(JToken token, string path) => Decimal(token, path) is decimal n ? (long)n : (long?)null;
        public static double? Double(JToken token, string path) => Decimal(token, path) is decimal n ? (double)n : (double?)null;
        public static string Text(JToken token, string path) => token?.SelectToken(path)?.Value<string>();
        public static IEnumerable<JToken> Array(JToken token) => token is JArray array ? array : Enumerable.Empty<JToken>();
    }
}
