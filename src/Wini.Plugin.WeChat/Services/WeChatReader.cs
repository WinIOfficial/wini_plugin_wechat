using WindowsIsland.Contracts;

namespace Wini.Plugin.WeChat.Services;

internal sealed class WeChatReader(IPluginDesktop desktop, WeChatAvatarCache? avatarCache = null)
{
    internal const int Button = 50000, Edit = 50004, Image = 50006, ListItem = 50007, List = 50008, Text = 50020, Document = 50030;
    private readonly WeChatAvatarCache _avatars = avatarCache ?? new();

    public async Task<WeChatSnapshot> ReadAsync(DesktopTree tree, CancellationToken token)
    {
        var nodes = tree.Nodes;
        if (nodes.Length == 0) return new("", [], [], false, false, null);
        var sessions = Array.FindIndex(nodes, node => node.ControlType == List
            && (WeChatText.IdMatches(node.AutomationId, "session_list") || node.Name is "会话" or "聊天" or "Chats"));
        var messages = Array.FindIndex(nodes, node => node.ControlType == List
            && (WeChatText.IdMatches(node.AutomationId, "chat_message_list") || node.Name is "消息" or "Messages"));
        var input = Array.FindIndex(nodes, node => WeChatText.IdMatches(node.AutomationId, "chat_input_field")
            || node.ControlType == Edit && node.Name.Length > 0 && node.Name is not ("搜索" or "Search") && nodes[0].ClassName == "WeChatMainWndForPC");
        var title = Array.FindIndex(nodes, node => WeChatText.IdMatches(node.AutomationId, "current_chat_name_label"));
        var conversation = input >= 0 ? nodes[input].Name.Trim() : title >= 0 ? nodes[title].Name.Trim()
            : nodes[0].ClassName == "ChatWnd" ? nodes[0].Name.Trim() : "";
        var previews = new List<WeChatPreview>();
        if (sessions >= 0)
        {
            foreach (var index in Enumerable.Range(0, nodes.Length).Where(index => nodes[index].Parent == sessions && nodes[index].ControlType == ListItem).Take(80))
            {
                var row = nodes[index];
                if (row.ClassName.Contains("Fold", StringComparison.Ordinal)) continue;
                var labels = row.ClassName.StartsWith("mmui::", StringComparison.Ordinal) ? []
                    : Descendants(nodes, index).Where(i => nodes[i].ControlType == Text).Select(i => nodes[i].Name.Trim()).Where(s => s.Length > 0).Take(8).ToArray();
                if (WeChatText.ParsePreview(row.AutomationId, row.Name, labels) is not { } preview) continue;
                previews.Add(preview with { Avatar = await AvatarAsync(tree, index, preview, nodes[sessions].Bounds, token).ConfigureAwait(false) });
            }
        }
        var rows = new List<WeChatMessage>();
        if (messages >= 0)
        {
            foreach (var index in Enumerable.Range(0, nodes.Length).Where(i => nodes[i].Parent == messages && nodes[i].ControlType == ListItem).TakeLast(80))
            {
                var row = nodes[index];
                if (row.ClassName == "mmui::ChatItemView" || string.IsNullOrWhiteSpace(row.Name)) continue;
                var body = row.Name.Trim(); if (body.Length > 4096) body = body[..4096];
                var direction = Direction(row.ItemStatus, row.ItemType); var sender = "";
                if (!row.ClassName.StartsWith("mmui::", StringComparison.Ordinal))
                {
                    foreach (var button in Descendants(nodes, index).Select(i => nodes[i]).Where(node => node.ControlType == Button))
                    {
                        var bounds = button.Bounds;
                        if (bounds.IsEmpty || row.Bounds.Width <= 0 || bounds.Width is < 20 or > 120
                            || Math.Abs(bounds.Width - bounds.Height) > 3 || button.Name.Length == 0) continue;
                        var position = (bounds.X - row.Bounds.X) / row.Bounds.Width;
                        if (position < 0.35) { direction = WeChatDirection.Incoming; sender = button.Name.Trim(); }
                        else if (position > 0.65) direction = WeChatDirection.Outgoing;
                        break;
                    }
                }
                rows.Add(new(row.RuntimeId, sender, body, direction));
            }
        }
        return new(conversation, previews, rows, sessions >= 0, messages >= 0,
            previews.FirstOrDefault(preview => preview.Conversation == conversation)?.Muted);
    }

    private async Task<byte[]?> AvatarAsync(DesktopTree tree, int index, WeChatPreview preview, DesktopRect viewport, CancellationToken token)
    {
        var key = (preview.Key, preview.Conversation);
        var cached = _avatars.Get(key);
        if (cached is not null && DateTimeOffset.UtcNow - cached.ReadAt < TimeSpan.FromMinutes(2)) return cached.Bytes;
        var row = tree.Nodes[index];
        var image = Descendants(tree.Nodes, index).FirstOrDefault(i =>
        {
            var node = tree.Nodes[i]; var rect = node.Bounds;
            return (node.ControlType == Image || node.ControlType == Button && (node.AutomationId.Contains("avatar", StringComparison.OrdinalIgnoreCase)
                || node.Name is "头像" or "Avatar")) && !rect.IsEmpty && rect.Width is >= 20 and <= 256
                && Math.Abs(rect.Width - rect.Height) <= 4 && row.Bounds.Contains(rect) && rect.Right <= row.Bounds.X + row.Bounds.Width * 0.4;
        }, -1);
        var bytes = image >= 0 ? await desktop.ReadImageAsync(new(tree.Token, image), token).ConfigureAwait(false) : null;
        var bounds = image >= 0 ? tree.Nodes[image].Bounds : ConversationBounds(row.ClassName, row.Bounds, row.AutomationId);
        if (bytes is null && !row.Offscreen && viewport.Contains(bounds))
            bytes = await desktop.ReadImageAsync(new(tree.Token, index, bounds), token).ConfigureAwait(false);
        if (bytes is not null)
        {
            _avatars.Store(key, bytes);
        }
        return bytes ?? cached?.Bytes;
    }

    internal static DesktopRect ConversationBounds(string className, DesktopRect row, string automationId = "")
    {
        var session = className == "mmui::ChatSessionCell" || className.StartsWith("mmui::", StringComparison.Ordinal)
            && automationId.Split('.').Any(part => part.StartsWith("session_item_", StringComparison.Ordinal));
        if (!session || row.IsEmpty || row.Height is < 40 or > 240 || row.Width < row.Height * 2) return default;
        var size = Math.Round(row.Height * 40 / 68);
        return new(row.X + Math.Round(row.Height * 12 / 68), row.Y + Math.Round((row.Height - size) / 2), size, size);
    }

    internal static IEnumerable<int> Descendants(DesktopNode[] nodes, int parent)
    {
        for (var index = parent + 1; index < nodes.Length; index++)
        {
            var ancestor = nodes[index].Parent;
            while (ancestor > parent) ancestor = nodes[ancestor].Parent;
            if (ancestor == parent) yield return index;
        }
    }
    private static WeChatDirection Direction(string status, string type)
    {
        var value = (status + " " + type).Trim();
        return value.Equals("Incoming", StringComparison.OrdinalIgnoreCase) || value is "收到" or "接收" ? WeChatDirection.Incoming
            : value.Equals("Outgoing", StringComparison.OrdinalIgnoreCase) || value is "发出" or "发送" ? WeChatDirection.Outgoing : WeChatDirection.Unknown;
    }
}
