using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wini.Plugin.WeChat.Services;

internal sealed class WeChatAvatarCache(string? directory = null, string scope = "")
{
    internal sealed record Entry(byte[] Bytes, DateTimeOffset ReadAt);
    private readonly Dictionary<(string Key, string Conversation), Entry?> _entries = [];
    private bool _pruned;

    public Entry? Get((string Key, string Conversation) key)
    {
        if (_entries.TryGetValue(key, out var entry)) return entry;
        try
        {
            var path = FilePath(key);
            if (path is not null && new FileInfo(path) is { Exists: true, Length: > 0 and <= 65536 } file)
            {
                var bytes = File.ReadAllBytes(path);
                if (ValidPng(bytes)) entry = new(bytes, ModifiedUtc(file));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
        Remember(key, entry);
        return entry;
    }

    public void Store((string Key, string Conversation) key, byte[] bytes)
    {
        var unchanged = _entries.TryGetValue(key, out var previous) && previous is not null
            && previous.Bytes.AsSpan().SequenceEqual(bytes);
        Remember(key, new(bytes, DateTimeOffset.UtcNow));
        if (directory is null) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (!_pruned)
            {
                _pruned = true;
                var files = new DirectoryInfo(directory).EnumerateFiles("*.png").Select(file => (File: file, Modified: ModifiedUtc(file)))
                    .OrderByDescending(item => item.Modified).ToArray();
                for (var index = 0; index < files.Length; index++)
                    if (index >= 384 || DateTime.UtcNow - files[index].Modified > TimeSpan.FromDays(7)) files[index].File.Delete();
            }
            var path = FilePath(key)!;
            if (unchanged && File.Exists(path)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return; }
            temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Remember((string Key, string Conversation) key, Entry? entry)
    {
        if (_entries.Count >= 128 && !_entries.ContainsKey(key)) _entries.Remove(_entries.Keys.First());
        _entries[key] = entry;
    }

    private string? FilePath((string Key, string Conversation) key) => directory is null ? null
        : Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"v2|{scope.Length}:{scope}{key.Key.Length}:{key.Key}{key.Conversation.Length}:{key.Conversation}"))) + ".png");

    private static DateTime ModifiedUtc(FileInfo file)
    {
        if (!OperatingSystem.IsWindows()) return file.LastWriteTimeUtc;
        using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileTime(handle, 0, 0, out var written)) throw new IOException("Avatar cache timestamp is unavailable.");
        return DateTime.FromFileTimeUtc(written);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTime(SafeFileHandle file, nint created, nint accessed, out long written);

    internal static bool ValidPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12,4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) != 64 || BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) != 64) return false;
        var offset = 8;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > (uint)(bytes.Length - offset - 12)) return false;
            uint crc = 0xffffffff;
            foreach (var value in bytes.Slice(offset + 4, checked((int)length) + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 8 + (int)length)..])) return false;
            var ended = bytes.Slice(offset + 4,4).SequenceEqual("IEND"u8);
            offset += checked((int)length) + 12;
            if (ended) return length == 0 && offset == bytes.Length;
        }
        return false;
    }
    public void Clear() => _entries.Clear();
}
