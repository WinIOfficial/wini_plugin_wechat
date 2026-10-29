using WindowsIsland.Contracts;

internal sealed class DesktopFixture : IPluginDesktop
{
    public int Calls, ImageCalls;
    public int Unread = 1;
    public string Body = "已有消息", Direction = "Incoming", Conversation = "会话";
    public bool Muted, Blocked;
    public byte[]? Avatar;
    private readonly List<(int Id,string Body,string Direction)> _history = [];
    private int _lastUnread = -1;
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default)
    {
        Interlocked.Increment(ref Calls); Entered.TrySetResult();
        if (Blocked) await Release.Task.WaitAsync(token);
        return [new(42, 1, "fixture", "1.0.0", [],
            [new(100, "WeChatMainWndForPC", new(0, 0, 800, 600), 0, 0, false, true, false, false, 1)])];
    }
    public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default)
    {
        var row = $"{Conversation}\n{Body}\n{DateTimeOffset.Now:HH:mm}\n{Unread}条未读消息" + (Muted ? "\n消息免打扰" : "");
        DesktopNode Node(int parent, string name, string id, int type, string runtime, DesktopRect bounds, string status = "") =>
            new(parent, name, id, type == 50007 && id.StartsWith("session") ? "mmui::ChatSessionCell" : "Fixture",
                type, status, "", runtime, bounds, false);
        if (_lastUnread != Unread) { _history.Add((Unread,Body,Direction)); _lastUnread = Unread; }
        return Task.FromResult(new DesktopTree("fixture", [
            new(-1,"客户端","","WeChatMainWndForPC",50032,"","","root",new(0,0,800,600),false),
            Node(0,"会话","session_list",50008,"sessions",new(0,0,260,500)),
            Node(1,row,"session_item_"+Conversation,50007,"session",new(0,0,260,68)),
            Node(0,Conversation,"chat_input_field",50004,"input",new(260,500,540,100)),
            Node(0,"消息","chat_message_list",50008,"messages",new(260,0,540,500)),
            .._history.Select((message,index) => Node(4,message.Body,"",50007,"message/"+message.Id,
                new(260,index*68,540,68),message.Direction))
        ]) { ObservedAt = DateTimeOffset.Now });
    }
    public Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default)
    { Interlocked.Increment(ref ImageCalls); return Task.FromResult(Avatar); }
    public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) => throw new UnauthorizedAccessException();
    public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => throw new UnauthorizedAccessException();
    public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default) => throw new UnauthorizedAccessException();
}

internal sealed class TestContext(IPluginDesktop? desktop) : IPluginContext
{
    public readonly List<PluginNotification> Notifications = [];
    public string PluginId => "wini_plugin_wechat";
    public IPluginSettings Settings => throw new NotSupportedException();
    public IPluginDiagnostics Diagnostics => throw new NotSupportedException();
    public T? GetService<T>() where T : class => desktop as T;
    public void PublishNotification(PluginNotification notification) { lock (Notifications) Notifications.Add(notification); }
}
