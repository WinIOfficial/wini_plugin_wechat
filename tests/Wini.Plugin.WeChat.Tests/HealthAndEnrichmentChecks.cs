using Wini.Plugin.WeChat.Services;
using WindowsIsland.Contracts;

internal static class HealthAndEnrichmentChecks
{
    public static void Run(Action<bool,string> check)
    {
        var health = new WeChatConnectionHealth();
        check(health.StatusKey(false, 0) == "NotRunning" && health.StatusKey(false, 1) == "NotConnected",
            "WeChat reports not running without a client and not connected before any failure threshold");
        health.Failed(7, unsupported: true);
        health.Failed(7, unsupported: true);
        check(health.StatusKey(false, 1) == "NotConnected", "Isolated accessibility failures do not degrade the WeChat state");
        health.Failed(7, unsupported: true);
        check(health.StatusKey(false, 1) == "WeChatUnsupported", "Consecutive unsupported failures degrade WeChat to version not supported");
        check(health.StatusKey(true, 1) == "Connected", "A readable WeChat client reports connected even after earlier failures");
        health.Retry();
        check(health.StatusKey(false, 1) == "NotConnected", "Retry clears the failure state for one more attempt");
        health.Failed(7, unsupported: true);
        check(health.StatusKey(false, 1) == "WeChatUnsupported", "A failed retry degrades again immediately");
        health.Succeeded(7);
        health.Failed(8, unsupported: false);
        health.Failed(8, unsupported: false);
        health.Failed(8, unsupported: false);
        check(health.StatusKey(false, 2) == "WeChatConnectFailed", "Access failures are reported separately from unsupported versions");
        health.Remove(8);
        check(health.StatusKey(false, 1) == "NotConnected", "A closed client discards its failure history");
        check(WeChatConnectionHealth.IsUnsupported("AccessorMismatch") && !WeChatConnectionHealth.IsUnsupported("OpenProcess"),
            "Signature mismatches are version incompatibilities while process access is not");

        var automatic = new PluginNotification("wini_plugin_wechat","小林",null,"新消息",DateTimeOffset.Now,DeduplicationKey:"event/1");
        var now = DateTimeOffset.UtcNow;
        var session = new WeChatPreview("stable-session", automatic.ConversationName, automatic.Body, 1, "刚刚");
        var enrichmentQueue = new WeChatAvatarEnrichment();
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session], now).Count == 0, "A missing avatar remains pending without delaying the message");
        var completed = enrichmentQueue.Update([session with { Avatar = [8, 9], Body = "本人在其他设备上的回复" }], now.AddSeconds(1));
        check(completed.Count == 1 && completed[0].IsMetadataUpdate && completed[0].DeduplicationKey == automatic.DeduplicationKey
            && completed[0].CreatedAt == automatic.CreatedAt && completed[0].Body == automatic.Body,
            "Late portraits preserve the original event and body even after a remote-device reply changes the preview");
        check(enrichmentQueue.Update([session with { Avatar = [8, 9] }], now.AddSeconds(2)).Count == 0,
            "A pending avatar is completed only once");
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session with { Conversation = "另一会话", Avatar = [8, 9] }], now.AddSeconds(1)).Count == 0,
            "A recycled row cannot supply another conversation's avatar");
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session with { Muted = true, Avatar = [8, 9] }], now.AddSeconds(1)).Count == 0,
            "Enabling mute cancels pending avatar enrichment");
        enrichmentQueue.Remember(automatic, [session], now);
        check(enrichmentQueue.Update([session with { Avatar = [8, 9] }], now.AddSeconds(6)).Count == 0,
            "Avatar waiting is bounded to the notification display duration");
    }
}
