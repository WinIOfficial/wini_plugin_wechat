using WindowsIsland.Contracts;
using Wini.Plugin.WeChat;

static void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}

using var unavailable = new WeChatPlugin();
var rejected = false;
try { await unavailable.InitializeAsync(new Context(null)); } catch (NotSupportedException) { rejected = true; }
Check(rejected, "Missing host capability fails without attempting desktop access");
var host = new Host();
using var plugin = new WeChatPlugin();
await plugin.InitializeAsync(new Context(host));
await plugin.StartAsync(); await plugin.StartAsync();
Check(host.Starts == 1 && plugin.CanRetry, "Start is idempotent and exposes host retry state");
plugin.Retry(); plugin.Retry(); await plugin.PollAsync();
Check(host.Retries == 1, "Repeated retry clicks coalesce into one host request");
await plugin.StopAsync(); plugin.Retry(); await plugin.PollAsync();
Check(host.Retries == 1 && host.Polls == 0 && !plugin.CanRetry, "Stopped plugin does not call the host");
await plugin.StartAsync(); await plugin.PollAsync();
Check(host.Starts == 2 && host.Polls == 1, "Lifecycle restart resumes host polling");
plugin.Dispose(); await plugin.PollAsync();
Check(host.Polls == 1, "Disposed plugin rejects late polling");
Check(typeof(WeChatPlugin).Assembly.GetReferencedAssemblies().All(a => a.Name != "WindowsIsland"), "Plugin has no host assembly dependency");

var delayed = new DelayedHost();
using var pending = new WeChatPlugin();
await pending.InitializeAsync(new Context(delayed));
var starting = pending.StartAsync();
await pending.StopAsync();
delayed.Completion.SetResult(new(new(PluginConnectionState.Connected), true));
await starting;
Check(pending.Status.State == PluginConnectionState.NotRunning && !pending.CanRetry,
    "A delayed start response cannot restore a stopped generation");

sealed class Host : IWeChatHost
{
    public int Starts, Polls, Retries;
    private static Task<WeChatHostStatus> State() => Task.FromResult(new WeChatHostStatus(new(PluginConnectionState.Connected), true));
    public Task<WeChatHostStatus> StartAsync() { Starts++; return State(); }
    public Task<WeChatHostStatus> PollAsync() { Polls++; return State(); }
    public Task<WeChatHostStatus> RetryAsync() { Retries++; return State(); }
}

sealed class Context(IWeChatHost? host) : IPluginContext
{
    public string PluginId => "wini_plugin_wechat";
    public IPluginSettings Settings => throw new NotSupportedException();
    public IPluginDiagnostics Diagnostics => throw new NotSupportedException();
    public T? GetService<T>() where T : class => host as T;
    public void PublishNotification(PluginNotification notification) => throw new InvalidOperationException("Host owns notification delivery.");
}

sealed class DelayedHost : IWeChatHost
{
    public TaskCompletionSource<WeChatHostStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<WeChatHostStatus> StartAsync() => Completion.Task;
    public Task<WeChatHostStatus> PollAsync() => throw new InvalidOperationException();
    public Task<WeChatHostStatus> RetryAsync() => throw new InvalidOperationException();
}
