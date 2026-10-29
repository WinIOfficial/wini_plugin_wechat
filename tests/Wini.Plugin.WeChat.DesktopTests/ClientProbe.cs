using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using WindowsIsland.Contracts;
using Wini.Plugin.WeChat.Services;

internal static class ClientProbe
{
    // Read-only diagnosis: names, bodies, identifiers and portraits stay in memory.
    public static async Task<int> RunAsync()
    {
        var readers = new Dictionary<int, (WeChatReader Reader, WeChatTracker Tracker)>();
        var desktop = new ReadOnlyDesktop();
        for (var sample = 0; sample < 120; sample++)
        {
            foreach (var process in Process.GetProcessesByName("Weixin").Concat(Process.GetProcessesByName("WeChat")))
            {
                using (process)
                {
                    if (process.MainWindowHandle == 0) continue;
                    try
                    {
                        var tree = Read(process.Id, process.MainWindowHandle);
                        if (!readers.TryGetValue(process.Id, out var pair))
                            readers[process.Id] = pair = (new(desktop), new());
                        var snapshot = await pair.Reader.ReadAsync(tree, default);
                        var arrivals = pair.Tracker.Update(snapshot, tree.ObservedAt);
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            Sample = sample, Nodes = tree.Nodes.Length, snapshot.HasSessions, snapshot.HasMessages,
                            Previews = snapshot.Previews.Count, Unread = snapshot.Previews.Count(p => p.Unread > 0),
                            UnmutedUnread = snapshot.Previews.Count(p => p.Unread > 0 && !p.Muted),
                            Recent = snapshot.Previews.Count(p => WeChatText.Recent(p.Stamp, tree.ObservedAt)),
                            Incoming = snapshot.Messages.Count(m => m.Direction == WeChatDirection.Incoming),
                            Outgoing = snapshot.Messages.Count(m => m.Direction == WeChatDirection.Outgoing),
                            Arrivals = arrivals.Count
                        }));
                    }
                    catch (Exception error)
                    { Console.WriteLine(JsonSerializer.Serialize(new { Sample = sample, Error = error.GetType().Name })); }
                }
            }
            await Task.Delay(500);
        }
        return 0;
    }

    private static DesktopTree Read(int processId, nint window)
    {
        var nodes = new List<DesktopNode>();
        var queue = new Queue<(AutomationElement Element, int Parent, int Depth)>();
        queue.Enqueue((AutomationElement.FromHandle(window), -1, 0));
        var walker = TreeWalker.ControlViewWalker;
        var timer = Stopwatch.StartNew();
        while (queue.TryDequeue(out var item))
        {
            if (nodes.Count >= 2048 || timer.ElapsedMilliseconds > 3000) throw new IOException("Snapshot limit.");
            var info = item.Element.Current;
            if (info.ProcessId != processId) continue;
            var bounds = info.BoundingRectangle;
            var index = nodes.Count;
            nodes.Add(new(item.Parent, info.IsPassword ? "" : info.Name, info.AutomationId, info.ClassName,
                info.ControlType.Id, info.ItemStatus, info.ItemType, string.Join('/', item.Element.GetRuntimeId()),
                bounds.IsEmpty ? default : new(bounds.X, bounds.Y, bounds.Width, bounds.Height), info.IsOffscreen));
            if (item.Depth >= 24) continue;
            for (var child = walker.GetFirstChild(item.Element); child is not null; child = walker.GetNextSibling(child))
            {
                if (queue.Count + nodes.Count >= 2048) throw new IOException("Snapshot limit.");
                queue.Enqueue((child, index, item.Depth + 1));
            }
        }
        return new("probe", nodes.ToArray()) { ObservedAt = DateTimeOffset.Now };
    }

    private sealed class ReadOnlyDesktop : IPluginDesktop
    {
        public Task<byte[]?> ReadImageAsync(DesktopImage request, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public Task<DesktopClient[]> FindClientsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<DesktopTree> ReadTreeAsync(int processId, long window, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadModuleAsync(DesktopRead request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadMemoryAsync(DesktopRead request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> SetFlagAsync(DesktopFlag request, CancellationToken token = default) => throw new NotSupportedException();
    }
}
