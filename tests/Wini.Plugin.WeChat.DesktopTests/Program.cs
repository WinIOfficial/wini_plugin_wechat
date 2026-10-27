using System.Collections.Concurrent;
using System.Drawing;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsIsland.Contracts;
using WindowsIsland.Plugins;
using WindowsIsland.PluginIsolation;
using Wini.Plugin.WeChat.Services;

if (args.Length >= 3 && args[0] == "--fixture") return WeChatFixture.Run(args[1],args[2],args.Length > 3 ? args[3] : null);
if (args.Length != 2 || args[0] != "--package") throw new ArgumentException("Expected --package <zip>.");
var checks = 0;
void Check(bool value,string name) { if(!value) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS "+name); }
var root=Path.Combine(AppContext.BaseDirectory,"verification-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var code=Path.Combine(root,"code"); ZipFile.ExtractToDirectory(Path.GetFullPath(args[1]),code);
var manifest=JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(Path.Combine(code,"manifest.json")),new JsonSerializerOptions(JsonSerializerDefaults.Web))!
    with {SourceKind=PluginSourceKind.Official};
var hostRoot=Path.Combine(root,"host-state"); Directory.CreateDirectory(hostRoot);
var executable=Path.Combine(AppContext.BaseDirectory,"WeChat.exe"); File.Copy(Environment.ProcessPath!,executable,true);
var notifications=new ConcurrentQueue<PluginNotification>();
DesktopTree? lastTree=null;
await using var desktop=new PluginDesktopBroker(manifest,hostRoot);
var fixture=ListenerChecks.FixtureClient.Start(executable);
SandboxProcess? worker=null;
IsolationRpc? rpc=null;
try
{
    var clients=await desktop.FindClientsAsync();
    Check(clients.Any(client=>client.ProcessId==fixture.Process.Id),"Desktop mediation discovers only the declared fixture client");
    lastTree=await desktop.ReadTreeAsync(fixture.Process.Id,fixture.Window.ToInt64());
    Check(lastTree.Nodes.Any(node=>node.AutomationId=="session_list"),"The native broker reads the real WPF accessibility provider");
    Check(await desktop.ReadImageAsync(new(lastTree.Token,0,new(-100,-100,40,40))) is null,"Image capture rejects coordinates outside the bound element");
    var denied=false;
    try { await desktop.ReadTreeAsync(Environment.ProcessId,fixture.Window.ToInt64()); } catch(UnauthorizedAccessException){denied=true;}
    Check(denied,"A forged process identifier cannot access another window");
    denied=false;
    try { await desktop.ReadMemoryAsync(new(fixture.Process.Id,"other.dll",0,1)); } catch(UnauthorizedAccessException){denied=true;}
    Check(denied,"Undeclared modules are inaccessible");

    worker=new(new(manifest,hostRoot,code,Path.Combine(root,"data")));
    rpc=new(worker.Input,worker.Output,async(method,payload,token)=>
    {
        switch(method)
        {
            case "desktop": return await DesktopRpc.DispatchAsync(desktop,payload.Deserialize<WorkerDesktopRequest>(IsolationRpc.Json)!,token);
            case "notification":
                var notification=payload.Deserialize<PluginNotification>(IsolationRpc.Json)!;
                if(notification.OriginProcessId is {} pid && !desktop.OwnsProcess(pid)) throw new UnauthorizedAccessException();
                notifications.Enqueue(notification); return null;
            case "diagnostic": return null;
            default: throw new InvalidDataException("Unexpected worker request.");
        }
    });
    await worker.VerifyReadyAsync(rpc);
    await rpc.CallAsync<WorkerDescription>("initialize",new WorkerInitialization(manifest,Path.Combine(root,"data"),manifest.Permissions,null,"0.1.0",code));
    await rpc.CallAsync<WorkerDescription>("start");
    await UntilAsync(async()=> (await rpc.CallAsync<WorkerDescription>("poll"))?.Status.State==PluginConnectionState.Connected);
    Check(worker.Sid.StartsWith("S-1-15-2-",StringComparison.Ordinal),"The independent implementation runs in a verified LPAC worker");
    await QuietAsync("Initial unread sessions and message history are not replayed");
    fixture.Send("incoming:迁移后的实时消息");
    await UntilAsync(()=>Task.FromResult(notifications.Count==1));
    notifications.TryDequeue(out var first);
    Check(first is {ConversationName:"示例会话",Body:"迁移后的实时消息",Kind:PluginNotificationKind.ClientAutomation}
        && first.OriginProcessId==fixture.Process.Id,"The LPAC plugin parses real client rows and emits the new message");
    Check(IsColor(first?.SenderAvatar,Color.CornflowerBlue),"Personal portraits survive the mediated image path");
    await QuietAsync("Repeated desktop snapshots do not duplicate a message");
    fixture.Send("incoming:迁移后的实时消息");
    await UntilAsync(()=>Task.FromResult(notifications.Count==1)); notifications.Clear();
    Check(true,"An identical incoming message with a new row identity is delivered again");
    fixture.Send("remote-outgoing:来自其他设备的本人回复");
    await QuietAsync("Remote-device replies with old unread counts remain silent");
    fixture.Send("outgoing:发出的消息"); await QuietAsync("Outgoing rows remain silent");
    fixture.Send("newchat:首次会话消息");
    await UntilAsync(()=>Task.FromResult(notifications.Count==1)); notifications.TryDequeue(out var pending);
    Check(pending is {ConversationName:"首次出现的会话",SenderAvatar:null},"A newly discovered conversation does not inherit another portrait");
    fixture.Send("newchat-avatar");
    await UntilAsync(()=>Task.FromResult(notifications.Count>=1));
    Check(notifications.TryDequeue(out var enriched) && enriched.IsMetadataUpdate && enriched.DeduplicationKey==pending!.DeduplicationKey
        && enriched.SenderAvatar is {Length:>0},"An available portrait enriches the original event through LPAC without replaying it");
    notifications.Clear();
    fixture.Send("mute:免打扰内容"); await QuietAsync("Muted conversations remain silent");
    var description=await rpc.CallAsync<WorkerDescription>("poll");
    Check(description?.ConversationRules?.Any(rule=>rule.Muted)==true,"The worker exports bounded mute rules without receiving other sources' bodies");
    fixture.Send("unmute");
    await Task.Delay(600); notifications.Clear();
    fixture.Send("group:群聊消息");
    await UntilAsync(()=>Task.FromResult(notifications.Count>=1));
    Check(notifications.TryDequeue(out var group) && IsColor(group.SenderAvatar,Color.ForestGreen),"A screenshot-backed group portrait is associated with its own conversation");
    notifications.Clear();

    var tree=await desktop.ReadTreeAsync(fixture.Process.Id,fixture.Window.ToInt64());
    var row=Array.FindIndex(tree.Nodes,node=>node.AutomationId.Contains("session_item_示例群",StringComparison.Ordinal));
    fixture.Send("avatar-recycle");
    await Task.Delay(200);
    Check(row>=0 && await desktop.ReadImageAsync(new(tree.Token,row,WeChatReader.ConversationBounds(tree.Nodes[row].ClassName,
        tree.Nodes[row].Bounds,tree.Nodes[row].AutomationId))) is null,"A recycled accessible row invalidates an earlier capture token");
    fixture.Send("avatar-reset"); await Task.Delay(650); notifications.Clear();

    await rpc.CallAsync<WorkerDescription>("stop");
    fixture.Send("incoming:停用期间消息");
    await QuietAsync("Stopping the LPAC plugin cancels its outstanding work");
    await rpc.CallAsync<WorkerDescription>("start");
    await UntilAsync(async()=> (await rpc.CallAsync<WorkerDescription>("poll"))?.Status.State==PluginConnectionState.Connected);
    await QuietAsync("Re-enabling creates a new silent baseline");
    fixture.Send("incoming:重新开启后消息");
    await UntilAsync(()=>Task.FromResult(notifications.Count>=1)); notifications.Clear();
    Check(true,"New messages arrive after re-enabling");
    fixture.Send("quit"); fixture.Process.WaitForExit(5000);
    await UntilAsync(async()=> (await rpc.CallAsync<WorkerDescription>("poll"))?.Status.State==PluginConnectionState.NotRunning);
    Check(true,"A closed client releases stale window state");
    await rpc.CallAsync<WorkerDescription>("stop");
    await desktop.DisposeAsync();
    denied=false;
    try{await desktop.FindClientsAsync();}catch(OperationCanceledException){denied=true;}catch(ObjectDisposedException){denied=true;}
    Check(denied,"Revoked native desktop services reject subsequent access");
    Console.WriteLine($"{checks} LPAC desktop integration checks passed.");
    return 0;
}
finally
{
    fixture.Dispose();
    rpc?.Dispose();
    worker?.Dispose();
    if(worker is not null) await worker.ExitTask;
    var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(hostRoot).ToUpperInvariant()+"\n"+manifest.Id)))[..40].ToLowerInvariant();
    DeleteAppContainerProfile("WindowsIsland.Plugin."+identity);
}

async Task UntilAsync(Func<Task<bool>> condition)
{
    var until=DateTimeOffset.UtcNow.AddSeconds(12);
    while(DateTimeOffset.UtcNow<until){if(await condition()) return; await Task.Delay(100);}
    throw new TimeoutException("The desktop fixture did not reach the expected state.");
}
async Task QuietAsync(string name){await Task.Delay(850);Check(notifications.IsEmpty,name);}
static bool IsColor(byte[]? bytes,Color expected)
{
    if(bytes is not {Length:>0})return false;
    using var stream=new MemoryStream(bytes);using var image=new Bitmap(stream);var pixel=image.GetPixel(8,8);
    return image.Width==64&&image.Height==64&&Math.Abs(pixel.R-expected.R)<=5&&Math.Abs(pixel.G-expected.G)<=5&&Math.Abs(pixel.B-expected.B)<=5;
}
[DllImport("userenv.dll",CharSet=CharSet.Unicode)] static extern int DeleteAppContainerProfile(string name);
