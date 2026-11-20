namespace RemoteMan.Core.Integrations.Zabbix;

/// <summary>
/// Polls Zabbix in the background: problems every <see cref="Interval"/>, the host list every
/// few minutes. Raises <see cref="Updated"/> with a fresh snapshot, or <see cref="Failed"/>
/// with a readable reason and keeps trying.
/// </summary>
public sealed class ZabbixMonitor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ZabbixClient>> _connect;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private ZabbixClient? _client;
    private IReadOnlyList<ZabbixHost>? _hosts;
    private DateTimeOffset _hostsAt;
    private bool _forceHosts;
    private Task? _loop;

    /// <param name="connect">Makes a connected client. Called again after a failure, so a jump host tunnel can be rebuilt.</param>
    public ZabbixMonitor(Func<CancellationToken, Task<ZabbixClient>> connect)
    {
        _connect = connect;
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HostListInterval { get; init; } = TimeSpan.FromMinutes(5);
    public ZabbixSeverity MinimumSeverity { get; init; } = ZabbixSeverity.Warning;
    public ZabbixMatchOptions MatchOptions { get; init; } = new();
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public ZabbixSnapshot? Latest { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<ZabbixSnapshot>? Updated;
    public event EventHandler<string>? Failed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Poll now, including the host list (after an import or a link change).</summary>
    public void RefreshNow()
    {
        _forceHosts = true;
        _wake.Release();
    }

    /// <summary>One poll. Public so tests and "Test connection" can drive it without the loop.</summary>
    public async Task<ZabbixSnapshot> PollAsync(CancellationToken ct = default)
    {
        _client ??= await _connect(ct);
        var now = Clock();
        if (_hosts is null || _forceHosts || now - _hostsAt >= HostListInterval)
        {
            _hosts = await _client.GetHostsAsync(ct);
            _hostsAt = now;
            _forceHosts = false;
        }
        var problems = await _client.GetProblemsAsync(MinimumSeverity, ct);
        var snap = new ZabbixSnapshot(_hosts, problems, now, MatchOptions);
        Latest = snap;
        LastError = null;
        return snap;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = Interval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await PollAsync(ct);
                Updated?.Invoke(this, snap);
                delay = Interval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _client?.Dispose();
                _client = null;
                LastError = ex is ZabbixApiException ? ex.Message : $"Zabbix: {ex.Message}";
                Failed?.Invoke(this, LastError);
                // Back off on repeated failures, up to five minutes.
                delay = TimeSpan.FromSeconds(Math.Min(300, Math.Max(Interval.TotalSeconds, delay.TotalSeconds * 2)));
            }
            try { await _wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        _client?.Dispose();
        _cts.Dispose();
    }
}
