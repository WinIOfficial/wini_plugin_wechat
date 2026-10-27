using System.Buffers.Binary;
using WindowsIsland.Contracts;

namespace Wini.Plugin.WeChat.Services;

internal enum WeChatAccessibilityFailure
{
    None, ModuleNotFound, UnsupportedImage, ImageSizeMismatch, OpenProcess,
    MemoryLayout, HeaderMismatch, AccessorMismatch, ConsumerMismatch, ReadFlag, InvalidFlag, WriteFlag
}

// The worker owns the client-specific binary matching. Only bounded operations cross IPC.
internal sealed class WeChatAccessibilitySession(IPluginDesktop desktop, DesktopClient client,
    DesktopModule module, WeChatAccessibilityLocator.Location location)
{
    public static async Task<(WeChatAccessibilitySession? Session, string Failure)> TryOpenAsync(
        IPluginDesktop desktop, DesktopClient client, CancellationToken token)
    {
        var module = client.Modules.FirstOrDefault(value => value.Name.Equals("Weixin.dll", StringComparison.OrdinalIgnoreCase));
        if (module is null) return (null, "ModuleNotFound");
        using var stream = new ModuleStream(desktop, client.ProcessId, module, token);
        var location = WeChatAccessibilityLocator.Find(stream);
        if (location is null) return (null, "UnsupportedImage");
        if (location.ImageSize != module.ImageSize) return (null, "ImageSizeMismatch");
        var session = new WeChatAccessibilitySession(desktop, client, module, location);
        return await session.EnsureEnabledAsync(token).ConfigureAwait(false) ? (session, "None") : (null, "WriteFlag");
    }

    public async Task<bool> EnsureEnabledAsync(CancellationToken token)
    {
        var header = await desktop.ReadMemoryAsync(new(client.ProcessId, module.Name, 0, location.Header.Bytes.Length), token).ConfigureAwait(false);
        if (!WeChatAccessibilityLocator.MatchesLoadedHeader(location, header, module.ImageBase)) return false;
        // The broker compares exactly the header accepted by the plugin, including relocation.
        return await desktop.SetFlagAsync(new(client.ProcessId, module.Name, location.FlagRva, 1,
            [new(0, header), new(location.ReaderWriter.Rva, location.ReaderWriter.Bytes),
                new(location.Notification.Rva, location.Notification.Bytes)]), token).ConfigureAwait(false);
    }

    private sealed class ModuleStream(IPluginDesktop desktop, int processId, DesktopModule module, CancellationToken token) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => module.FileLength;
        public override long Position { get => _position; set { if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; } }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(Math.Min(buffer.Length, 1024 * 1024), Length - _position);
            if (count == 0) return 0;
            var bytes = desktop.ReadModuleAsync(new(processId, module.Name, _position, count), token).GetAwaiter().GetResult();
            if (bytes.Length != count) throw new EndOfStreamException();
            bytes.CopyTo(buffer); _position += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) { Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset; return Position; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
