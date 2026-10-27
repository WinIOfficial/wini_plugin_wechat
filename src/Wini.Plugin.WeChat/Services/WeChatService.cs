using WindowsIsland.Contracts;

namespace Wini.Plugin.WeChat.Services;

internal sealed class WeChatService(IPluginDesktop desktop, Action<PluginNotification> publish, string? avatarDirectory = null,
    Action<Exception>? report = null)
{
    private sealed class Watch(IPluginDesktop desktop, string? directory, string scope)
    {
        public readonly WeChatReader Reader = new(desktop, new(directory, scope));
        public readonly WeChatTracker Tracker = new();
        public readonly WeChatAvatarEnrichment Enrichment = new();
    }
    private readonly Dictionary<(int Process, long Started, long Window), Watch> _watches = [];
    private readonly Dictionary<int, (long Started, WeChatAccessibilitySession Session)> _accessibility = [];
    private readonly Dictionary<int, DateTimeOffset> _attempts = [];
    private readonly Dictionary<(int Process, long Window), (string Title, string Body)?> _popups = [];
    private readonly WeChatConversationRegistry _conversations = new();
    private readonly WeChatConnectionHealth _health = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private PluginStatus _status = new(PluginConnectionState.NotRunning);
    private PluginConversationRule[] _rules = [];
    private bool _baseline;
    private uint _sequence;
    private int _retry;
    public PluginStatus Status => Volatile.Read(ref _status);
    public IReadOnlyList<PluginConversationRule> Rules => Volatile.Read(ref _rules);
    public void Retry() => Interlocked.Exchange(ref _retry, 1);

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await PollAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            { Volatile.Write(ref _status, new(PluginConnectionState.Disconnected, PluginStatusReason.ConnectionFailed)); }
            catch (Exception error) { report?.Invoke(error); throw; }
            try { await Task.Delay(500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
        _conversations.Clear(); Volatile.Write(ref _rules, []);
        Volatile.Write(ref _status, new(PluginConnectionState.NotRunning));
    }

    internal async Task PollAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _retry, 0) != 0) { _attempts.Clear(); _health.Retry(); }
        var clients = await desktop.FindClientsAsync(token).ConfigureAwait(false);
        var windows = new HashSet<(int, long, long)>();
        var popups = new HashSet<(int, long)>();
        var connected = new HashSet<int>();
        foreach (var client in clients)
        {
            token.ThrowIfCancellationRequested();
            if (_accessibility.TryGetValue(client.ProcessId, out var current) && current.Started != client.StartedAt)
            {
                _accessibility.Remove(client.ProcessId); _attempts.Remove(client.ProcessId);
                _conversations.Remove(client.ProcessId);
            }
            foreach (var window in client.Windows)
            {
                var key = (client.ProcessId, client.StartedAt, window.Handle);
                if (IsMainWindow(window))
                {
                    windows.Add(key);
                    try
                    {
                        var tree = await desktop.ReadTreeAsync(client.ProcessId, window.Handle, token).ConfigureAwait(false);
                        if (!_watches.TryGetValue(key, out var watch)) _watches[key] = watch = new(desktop, avatarDirectory,
                            $"{client.ProcessId}/{client.StartedAt}/{window.Handle}");
                        var snapshot = await watch.Reader.ReadAsync(tree, token).ConfigureAwait(false);
                        if (!snapshot.HasSessions && !snapshot.HasMessages)
                        {
                            if (await EnableAccessibilityAsync(client, token).ConfigureAwait(false)) _watches.Remove(key);
                            continue;
                        }
                        connected.Add(client.ProcessId); _health.Succeeded(client.ProcessId);
                        _conversations.Update(client.ProcessId, snapshot.Previews);
                        if (snapshot.ConversationMuted is null)
                            snapshot = snapshot with { ConversationMuted = _conversations.IsMuted(snapshot.Conversation, client.ProcessId) };
                        foreach (var arrival in watch.Tracker.Update(snapshot, DateTimeOffset.Now))
                        {
                            token.ThrowIfCancellationRequested();
                            var notification = Notification(client, window, arrival.Conversation, arrival.Body,
                                PluginNotificationKind.ClientAutomation, arrival.Avatar);
                            watch.Enrichment.Remember(notification, snapshot.Previews, DateTimeOffset.UtcNow);
                            publish(notification);
                        }
                        foreach (var update in watch.Enrichment.Update(snapshot.Previews, DateTimeOffset.UtcNow))
                        { token.ThrowIfCancellationRequested(); publish(update); }
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
                    { _watches.Remove(key); _health.Failed(client.ProcessId, false); }
                }
                else if (IsPopup(window))
                {
                    var popupKey = (client.ProcessId, window.Handle); popups.Add(popupKey);
                    var tree = await desktop.ReadTreeAsync(client.ProcessId, window.Handle, token).ConfigureAwait(false);
                    var content = ReadPopup(tree);
                    var known = _popups.TryGetValue(popupKey, out var previous);
                    _popups[popupKey] = content;
                    if (!_baseline || content is null || known && previous == content) continue;
                    if (_conversations.IsMuted(content.Value.Title, client.ProcessId) is not false) continue;
                    token.ThrowIfCancellationRequested();
                    publish(Notification(client, window, content.Value.Title, content.Value.Body, PluginNotificationKind.ClientHook));
                }
            }
        }
        foreach (var key in _watches.Keys.Except(windows).ToArray()) _watches.Remove(key);
        foreach (var key in _popups.Keys.Except(popups).ToArray()) _popups.Remove(key);
        var ids = clients.Select(client => client.ProcessId).ToHashSet();
        foreach (var id in _accessibility.Keys.Where(id => !ids.Contains(id)).ToArray())
        { _accessibility.Remove(id); _attempts.Remove(id); _conversations.Remove(id); _health.Remove(id); }
        _baseline = true;
        Volatile.Write(ref _rules, _conversations.Rules());
        var status = _health.StatusKey(connected.Count > 0, clients.Length);
        Volatile.Write(ref _status, status switch
        {
            "Connected" => new(PluginConnectionState.Connected),
            "NotRunning" => new(PluginConnectionState.NotRunning),
            "WeChatUnsupported" => new(PluginConnectionState.Disconnected, PluginStatusReason.VersionNotSupported),
            "WeChatConnectFailed" => new(PluginConnectionState.Disconnected, PluginStatusReason.ConnectionFailed),
            _ => new(PluginConnectionState.Disconnected)
        });
    }

    private async Task<bool> EnableAccessibilityAsync(DesktopClient client, CancellationToken token)
    {
        if (_attempts.TryGetValue(client.ProcessId, out var next) && next > DateTimeOffset.UtcNow) return false;
        _attempts[client.ProcessId] = DateTimeOffset.UtcNow.AddSeconds(10);
        if (_accessibility.TryGetValue(client.ProcessId, out var active)
            && await active.Session.EnsureEnabledAsync(token).ConfigureAwait(false)) return true;
        var result = await WeChatAccessibilitySession.TryOpenAsync(desktop, client, token).ConfigureAwait(false);
        if (result.Session is not null)
        { _accessibility[client.ProcessId] = (client.StartedAt, result.Session); return true; }
        _health.Failed(client.ProcessId, WeChatConnectionHealth.IsUnsupported(result.Failure));
        return false;
    }

    private PluginNotification Notification(DesktopClient client, DesktopWindow window, string conversation, string body,
        PluginNotificationKind kind, byte[]? avatar = null)
    {
        var sequence = ++_sequence;
        return new("wini_plugin_wechat", conversation, null, body, DateTimeOffset.Now, client.Icon, avatar,
            $"{_instance}/{client.ProcessId}/{window.Handle}/{sequence}", client.ProcessId, "微信", "wechat", sequence, Kind: kind);
    }

    internal static bool IsMainWindow(DesktopWindow window) => window.ClassName is "WeChatMainWndForPC" or "ChatWnd"
        || window.ClassName.Contains("QWindowIcon", StringComparison.Ordinal) && (window.Style & 0x40000) != 0
        || window.ClassName.StartsWith("HwndWrapper[", StringComparison.Ordinal);

    internal static bool IsPopup(DesktopWindow window)
    {
        var width = window.Bounds.Width / window.Scale; var height = window.Bounds.Height / window.Scale;
        if (!window.Visible || window.Foreground || width is < 120 or > 640 || height is < 40 or > 380 || (window.Style & 0x70000) != 0
            || window.ClassName is "#32768" or "#32770" || window.ClassName.Contains("tooltip", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Contains("QTipLabel", StringComparison.OrdinalIgnoreCase)) return false;
        return window.ClassName.Contains("notification", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Contains("notify", StringComparison.OrdinalIgnoreCase) || window.ClassName.Contains("toast", StringComparison.OrdinalIgnoreCase)
            || (window.ExtendedStyle & 8) != 0 && ((window.ExtendedStyle & 0x80) != 0 || window.Owned);
    }

    internal static (string Title, string Body)? ReadPopup(DesktopTree tree)
    {
        static bool App(string value) => value.Equals("微信", StringComparison.OrdinalIgnoreCase)
            || value.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || value.Equals("Weixin", StringComparison.OrdinalIgnoreCase);
        var labels = tree.Nodes.Where(node => !node.Offscreen && node.ControlType is WeChatReader.Text or WeChatReader.Document)
            .Select(node => node.Name.Trim()).Where(text => text.Length > 0 && !App(text)).Distinct().Take(32).ToArray();
        if (labels.Length == 0) return null;
        var title = tree.Nodes.FirstOrDefault()?.Name.Trim() ?? "";
        return labels.Length == 1 ? (title.Length == 0 || App(title) ? "微信" : title, labels[0])
            : (labels[0], string.Join('\n', labels.Skip(1)));
    }
}
