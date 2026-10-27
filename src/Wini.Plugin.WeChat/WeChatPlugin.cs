using WindowsIsland.Contracts;

namespace Wini.Plugin.WeChat;

public sealed class WeChatPlugin : IIslandPlugin, IPluginPolling, IPluginStatusProvider, IPluginRetry
{
    private IWeChatHost? _host;
    private WeChatHostStatus _state = new(new(PluginConnectionState.NotRunning), false);
    private bool _started, _disposed;
    private int _retry, _generation;

    public string Id => "wini_plugin_wechat";
    public string DisplayName => "微信";
    public PluginStatus Status => _state.Status;
    public bool CanRetry => _started && !_disposed && _state.CanRetry;

    public Task InitializeAsync(IPluginContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (context.PluginId != Id) throw new ArgumentException("Plugin context identity mismatch.", nameof(context));
        _host = context.GetService<IWeChatHost>() ?? throw new NotSupportedException("WeChat host service is unavailable.");
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        var generation = Volatile.Read(ref _generation);
        var state = await (_host ?? throw new InvalidOperationException()).StartAsync();
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        _state = state;
        _started = true;
    }

    public async Task PollAsync()
    {
        if (!_started || _disposed || _host is not { } host) return;
        var generation = Volatile.Read(ref _generation);
        var state = Interlocked.Exchange(ref _retry, 0) != 0 ? await host.RetryAsync() : await host.PollAsync();
        if (_started && !_disposed && generation == Volatile.Read(ref _generation)) _state = state;
    }

    public void Retry() { if (CanRetry) Interlocked.Exchange(ref _retry, 1); }

    public Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        _started = false;
        Interlocked.Exchange(ref _retry, 0);
        _state = new(new(PluginConnectionState.NotRunning), false);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _host = null;
    }
}
