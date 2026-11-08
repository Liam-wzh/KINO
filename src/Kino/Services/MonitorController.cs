using System.Net.Http;
using Kino.Models;

namespace Kino.Services
{
    public sealed class MonitorController : IDisposable
    {
        private readonly UsageClient _client;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly List<CancellationTokenSource> _retired = new List<CancellationTokenSource>();
        private CancellationTokenSource _cancellation = new CancellationTokenSource();
        private int _generation;
        private bool _disposed;
        public AppSettings Settings { get; private set; }
        public UsageSnapshot Snapshot { get; private set; }
        public bool IsRefreshing { get; private set; }
        public event Action Changed;

        public MonitorController(UsageClient client, AppSettings settings)
        { _client = client; Settings = settings.Clone(); Settings.CaptureActiveSite(); }
        public void ApplySettings(AppSettings settings)
        {
            var candidate = settings.Clone(); candidate.CaptureActiveSite();
            var changedConnection = !Settings.SameConnection(candidate);
            _generation++;
            _cancellation.Cancel();
            _retired.Add(_cancellation);
            _cancellation = new CancellationTokenSource();
            Settings = candidate;
            if (changedConnection) Snapshot = null;
            Changed?.Invoke();
        }

        public async Task RefreshAsync()
        {
            if (_disposed) return;
            var generation = _generation;
            var cancellation = _cancellation.Token;
            var settings = Settings.Clone();
            try
            {
                await _gate.WaitAsync(cancellation);
            }
            catch (OperationCanceledException) { return; }
            try
            {
                if (_disposed || generation != _generation) return;
                IsRefreshing = true;
                Changed?.Invoke();
                var snapshot = await _client.FetchAsync(settings, cancellation);
                if (generation == _generation && !_disposed) Snapshot = snapshot;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (OperationCanceledException) { SetError(generation, "连接超时，请稍后重试。"); }
            catch (ApiException ex) { SetError(generation, ex.Message); }
            catch (HttpRequestException) { SetError(generation, "无法连接站点，请检查网络和站点地址。"); }
            catch (Exception) { SetError(generation, "无法读取用量数据，请检查服务端返回格式。"); }
            finally
            {
                IsRefreshing = false;
                _gate.Release();
                if (!_disposed) Changed?.Invoke();
            }
        }

        private void SetError(int generation, string message)
        {
            if (_disposed || generation != _generation) return;
            Snapshot = Snapshot?.AsStale(message) ?? new UsageSnapshot { Error = message };
        }

        public void Dispose()
        {
            _disposed = true;
            _cancellation.Cancel();
            // A request may still be unwinding. Cancellation sources stay alive until the process exits.
        }
    }
}
