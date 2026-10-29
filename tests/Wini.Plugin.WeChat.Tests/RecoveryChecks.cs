using WindowsIsland.Contracts;
using Wini.Plugin.WeChat.Services;

internal static class RecoveryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var desktop = new RecoveringDesktop();
        var notifications = new List<PluginNotification>();
        var service = new WeChatService(desktop, notifications.Add);
        await service.PollAsync(default);
        desktop.Available = false;
        await service.PollAsync(default);
        check(desktop.FlagWrites == 1, "A rejected accessibility read invokes verified gate recovery instead of only dropping the window");
        desktop.Rows.Unread++; desktop.Rows.Body = "恢复期间的历史消息";
        await service.PollAsync(default);
        check(service.Status.State == PluginConnectionState.Connected && notifications.Count == 0,
            "Recovery reconnects with a silent baseline and does not replay inaccessible history");
        desktop.Rows.Unread++; desktop.Rows.Body = "恢复后的新消息";
        await service.PollAsync(default);
        check(notifications is [{ Body: "恢复后的新消息" }], "New arrivals are delivered after accessibility recovery");
        var policyReady = false;
        WeChatService? policyService = null;
        policyService = new(desktop, notification => policyReady = policyService!.Rules.Any(rule =>
            rule.Conversation == notification.ConversationName && !rule.Muted));
        await policyService.PollAsync(default);
        desktop.Rows.Conversation = "新出现的会话";
        desktop.Rows.Unread++; desktop.Rows.Body = "第一条消息";
        await policyService.PollAsync(default);
        check(policyReady, "A first-time conversation publishes its unmuted policy before emitting its first arrival");

        desktop.Available = false;
        for (var i = 0; i < 4; i++) await service.PollAsync(default);
        check(desktop.FlagWrites == 1 && service.Status.Reason == PluginStatusReason.ConnectionFailed,
            "Repeated provider failures respect gate retry throttling and expose a connection failure");
        service.Retry();
        await service.PollAsync(default);
        check(desktop.FlagWrites == 2, "An explicit retry immediately revalidates the existing accessibility session");

        var transient = new RecoveringDesktop { EnableSucceeded = false };
        var transientNotifications = new List<PluginNotification>();
        var transientService = new WeChatService(transient, transientNotifications.Add);
        await transientService.PollAsync(default);
        transient.Available = false;
        await transientService.PollAsync(default);
        transient.Rows.Unread++; transient.Rows.Body = "中断期间的消息"; transient.Available = true;
        await transientService.PollAsync(default);
        check(transientNotifications.Count == 0, "A provider that restores itself also establishes a silent recovery baseline");
        transient.Rows.Unread++; transient.Rows.Body = "自动恢复后的新消息";
        await transientService.PollAsync(default);
        check(transientNotifications is [{ Body: "自动恢复后的新消息" }], "Self-restoration resumes subsequent arrivals without replaying the gap");

        var timeoutDesktop = new RecoveringDesktop { Timeout = true };
        var reports = 0;
        var timeoutService = new WeChatService(timeoutDesktop, _ => { }, report: _ => reports++);
        using var shutdown = new CancellationTokenSource();
        var run = timeoutService.RunAsync(shutdown.Token);
        await timeoutDesktop.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(650);
        check(!run.IsCompleted && reports == 0, "A desktop RPC timeout does not permanently terminate the background listener");
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        check(timeoutService.Status.State == PluginConnectionState.NotRunning,
            "Cancellation still stops a retrying listener and clears its status");
    }

    private sealed class RecoveringDesktop : IPluginDesktop
    {
        private readonly byte[] _module = AccessibilityChecks.FixtureModule();
        public readonly DesktopFixture Rows = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Available = true, Timeout, EnableSucceeded = true;
        public int FlagWrites;
        public async Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default) =>
            (await Rows.FindClientsAsync(token)).Select(client => client with
            {
                Modules = [new("Weixin.dll", 0x180000000, 0x3000, _module.Length)]
            }).ToArray();
        public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default)
        {
            Entered.TrySetResult();
            if (Timeout) throw new OperationCanceledException("Synthetic desktop request timeout.");
            if (!Available) throw new InvalidOperationException("Synthetic provider restart.");
            return Rows.ReadTreeAsync(processId, window, token);
        }
        public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) =>
            Task.FromResult(_module.AsSpan((int)request.Offset, request.Count).ToArray());
        public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => ReadModuleAsync(request, token);
        public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default)
        { FlagWrites++; Available = EnableSucceeded; return Task.FromResult(EnableSucceeded); }
        public Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default) => Rows.ReadImageAsync(request, token);
    }
}
