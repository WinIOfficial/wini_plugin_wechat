using WindowsIsland.Contracts;
using Wini.Plugin.WeChat.Services;

namespace Wini.Plugin.WeChat;

public sealed class WeChatPlugin : IIslandPlugin, IPluginPolling, IPluginStatusProvider, IPluginRetry, IPluginConversationPolicy
{
    private IPluginContext? _context;
    private IPluginDesktop? _desktop;
    private string? _avatarDirectory;
    private WeChatService? _service;
    private CancellationTokenSource? _shutdown;
    private Task? _run;
    private bool _disposed;
    private int _generation;
    public string Id => "wini_plugin_wechat";
    public string DisplayName => "微信";
    public PluginStatus Status => _service?.Status ?? new(PluginConnectionState.NotRunning);
    public bool CanRetry => !_disposed && _run is not null && Status.Reason is PluginStatusReason.ConnectionFailed or PluginStatusReason.VersionNotSupported;
    public IReadOnlyList<PluginConversationRule> ConversationRules => _service?.Rules ?? [];

    public Task InitializeAsync(IPluginContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (context.PluginId != Id) throw new ArgumentException("Plugin context identity mismatch.", nameof(context));
        _desktop = context.GetService<IPluginDesktop>() ?? throw new NotSupportedException("Desktop service is unavailable.");
        _context = context;
        _avatarDirectory = context.GetService<IPluginStorage>() is { } storage ? Path.Combine(storage.DataDirectory, "avatars") : null;
        return Task.CompletedTask;
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run is not null) return Task.CompletedTask;
        var context = _context ?? throw new InvalidOperationException();
        var generation = Volatile.Read(ref _generation);
        _shutdown = new();
        var token = _shutdown.Token;
        var service = _service = new(_desktop ?? throw new InvalidOperationException(), notification =>
        {
            if (!_disposed && !token.IsCancellationRequested && generation == Volatile.Read(ref _generation)) context.PublishNotification(notification);
        }, _avatarDirectory, error => context.Diagnostics.Write(new PluginDiagnostic(Id,"Listener",
            AdditionalInformation: new PluginDiagnosticInfo(ExceptionType:error.GetType().FullName,HResult:error.HResult))));
        _run = Task.Run(() => service.RunAsync(token));
        return Task.CompletedTask;
    }

    public Task PollAsync() => _run is { IsFaulted: true } failed ? failed : Task.CompletedTask;
    public void Retry() { if (CanRetry) _service?.Retry(); }
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        var shutdown = _shutdown; var run = _run;
        _shutdown = null; _run = null; _service = null;
        if (shutdown is null) return;
        shutdown.Cancel();
        try { if (run is not null) await run.ConfigureAwait(false); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { shutdown.Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _context = null; _desktop = null;
    }
}
