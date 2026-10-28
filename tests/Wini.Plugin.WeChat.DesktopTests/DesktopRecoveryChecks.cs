using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WindowsIsland.Contracts;
using WindowsIsland.PluginIsolation;
using WindowsIsland.Plugins;

internal static class DesktopRecoveryChecks
{
    public static async Task RunAsync(PluginDesktopBroker desktop, IsolationRpc rpc, string executable, string directory,
        ConcurrentQueue<PluginNotification> notifications, Action<bool, string> check)
    {
        var module = Path.Combine(directory, "Weixin.dll");
        AccessibilityChecks.WriteFixtureModule(module);
        using var fixture = ListenerChecks.FixtureClient.Start(executable, module);
        var id = fixture.Process.Id;
        var initial = (await desktop.FindClientsAsync()).Single(client => client.ProcessId == id);
        check(initial.Modules.Length == 0, "A client can be discovered before its declared module is loaded");
        fixture.Send("accessibility-load");
        var client = (await desktop.FindClientsAsync()).Single(client => client.ProcessId == id);
        check(client.Modules.Any(value => value.Name == "Weixin.dll"), "Desktop mediation refreshes modules loaded after discovery");
        await ConnectedAsync();
        check((await desktop.ReadMemoryAsync(new(id, "Weixin.dll", 0x2021, 1)))[0] == 1,
            "The LPAC implementation locates and enables the synthetic accessibility gate through bounded IPC");
        check((await desktop.ReadModuleAsync(new(id, "Weixin.dll", 0x621, 1)))[0] == 0,
            "Enabling accessibility changes client memory without modifying its module file");
        check(!await desktop.SetFlagAsync(new(id, "Weixin.dll", 0x2021, 0, [new(0, [0, 0])])),
            "Mismatched module anchors cannot change a flag");
        var rejected = false;
        try { await desktop.SetFlagAsync(new(id, "Weixin.dll", 0x2021, 2, [new(0, [0x4d, 0x5a])])); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "A declared module does not permit non-boolean writes");
        rejected = false;
        try { await desktop.ReadMemoryAsync(new(id, "Weixin.dll", client.Modules.Single().ImageSize, 1)); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Reads beyond a declared module image are rejected");
        await Task.Delay(700);
        check(notifications.IsEmpty, "Delayed accessibility establishes a silent message baseline");
        fixture.Send("accessibility-reset");
        await UntilAsync(async () => (await desktop.ReadMemoryAsync(new(id, "Weixin.dll", 0x2021, 1)))[0] == 1);
        await ConnectedAsync();
        await Task.Delay(700);
        check(notifications.IsEmpty, "Recovering a reset accessibility gate does not replay history");
        fixture.Send("incoming:辅助访问恢复后的消息");
        await UntilAsync(() => Task.FromResult(!notifications.IsEmpty));
        check(notifications.TryDequeue(out var message) && message.Body == "辅助访问恢复后的消息",
            "New messages arrive after the LPAC implementation restores accessibility");
        await rpc.CallAsync<WorkerDescription>("stop");
        var screenReader = 0;
        if (!SystemParametersInfo(0x46, 0, ref screenReader, 0)) throw new InvalidOperationException("Cannot read the screen reader state.");
        await desktop.DisposeAsync();
        fixture.Send(screenReader == 0 ? "accessibility-expect-off" : "accessibility-expect-on");
        check(true, "Revocation restores the guarded flag while preserving an active system screen reader");

        Task ConnectedAsync() => UntilAsync(async () => (await rpc.CallAsync<WorkerDescription>("poll"))?.Status.State == PluginConnectionState.Connected);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline) { if (await condition()) return; await Task.Delay(100); }
        throw new TimeoutException("The accessibility recovery fixture did not reach the expected state.");
    }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);
}
