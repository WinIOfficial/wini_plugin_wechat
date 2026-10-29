using WindowsIsland.Contracts;
using Wini.Plugin.WeChat;
using Wini.Plugin.WeChat.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++; Console.WriteLine("PASS " + name);
}
WeChatChecks.Run(Check);
checks += AccessibilityChecks.Run();
HealthAndEnrichmentChecks.Run(Check);
AvatarCacheChecks.Run(Check);
await RecoveryChecks.RunAsync(Check);

using (var unavailable = new WeChatPlugin())
{
    var rejected = false;
    try { await unavailable.InitializeAsync(new TestContext(null)); } catch (NotSupportedException) { rejected = true; }
    Check(rejected, "Missing desktop mediation fails without direct desktop access");
}
var desktop = new DesktopFixture();
var notifications = new List<PluginNotification>();
var service = new WeChatService(desktop, notifications.Add);
await service.PollAsync(default);
Check(notifications.Count == 0 && service.Status.State == PluginConnectionState.Connected, "Initial accessible snapshot establishes a silent baseline");
desktop.Body = "新消息"; desktop.Unread++;
await service.PollAsync(default);
Check(notifications is [{ ConversationName: "会话", Body: "新消息", OriginProcessId: 42, AppId: "wechat" }], "The plugin parses an incoming message and retains its mediated client identity");
await service.PollAsync(default);
Check(notifications.Count == 1, "Repeated snapshots do not replay a message");
desktop.Unread++;
await service.PollAsync(default);
Check(notifications.Count == 2 && notifications[0].DeduplicationKey != notifications[1].DeduplicationKey, "Repeated message text with a new event remains distinct");
desktop.Muted = true; desktop.Unread++; desktop.Body = "免打扰消息";
await service.PollAsync(default);
Check(notifications.Count == 2 && service.Rules.Single().Muted, "Muted conversations suppress messages and publish cross-source mute state");
desktop.Muted = false; desktop.Direction = "Outgoing"; desktop.Unread++; desktop.Body = "发出的消息";
await service.PollAsync(default);
Check(notifications.Count == 2, "Outgoing messages are not presented as incoming notifications");
desktop.Direction = "Incoming"; desktop.Unread++; desktop.Body = "等待头像";
await service.PollAsync(default);
var pending = notifications.Last();
desktop.Avatar = [1, 2, 3];
await service.PollAsync(default);
Check(notifications.Last() is { IsMetadataUpdate: true, SenderAvatar.Length: 3 }
    && notifications.Last().DeduplicationKey == pending.DeduplicationKey, "Delayed avatars enrich the original event without a new message identity");
Check(WeChatReader.ConversationBounds("mmui::ChatSessionCell", new(0,0,260,68)) == new DesktopRect(12,14,40,40),
    "Portrait bounds remain derived from WeChat session rows");
var bounds = new DesktopRect(0,0,300,100);
Check(bounds.Contains(new(12,14,40,40)) && !bounds.Contains(new(-1,0,40,40)) && !bounds.Contains(default), "Empty and outside image bounds are rejected");

var blocked = new DesktopFixture { Blocked = true };
var context = new TestContext(blocked);
using var plugin = new WeChatPlugin();
await plugin.InitializeAsync(context);
await plugin.StartAsync(); await plugin.StartAsync();
await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(blocked.Calls == 1, "Repeated start creates only one background listener");
await plugin.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
blocked.Release.TrySetResult();
Check(context.Notifications.Count == 0 && plugin.Status.State == PluginConnectionState.NotRunning
    && plugin.ConversationRules.Count == 0, "Stop cancels pending desktop work and removes old conversation state");
blocked.Blocked = false;
await plugin.StartAsync();
await Task.Delay(100);
await plugin.StopAsync();
Check(blocked.Calls >= 2 && context.Notifications.Count == 0, "Re-enable creates a new silent baseline");
Check(typeof(WeChatPlugin).Assembly.GetReferencedAssemblies().All(assembly => assembly.Name != "WindowsIsland"),
    "The plugin has no host implementation assembly dependency");
Console.WriteLine($"{checks} WeChat plugin checks passed.");
