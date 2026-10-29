using System.Security.Cryptography;
using System.Text;
using Wini.Plugin.WeChat.Services;

internal static class AvatarCacheChecks
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wini-avatar-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAmElEQVR4nO3QMREAIBDAsBeLJWSiAWRkoEP2Xmftc382OkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAO0BKTBSs4ygT3MAAAAASUVORK5CYII=");
            check(WeChatAvatarCache.ValidPng(bytes), "The synthetic cache portrait is a complete 64px PNG");
            var key = (Key: "session", Conversation: "会话");
            const string scope = "fixture";
            var oldName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{scope.Length}:{scope}{key.Key.Length}:{key.Key}{key.Conversation.Length}:{key.Conversation}"))) + ".png";
            var oldPath = Path.Combine(directory, oldName);
            File.WriteAllBytes(oldPath, bytes);
            var cache = new WeChatAvatarCache(directory, scope);
            check(cache.Get(key) is null && File.Exists(oldPath), "Old portrait encodings are skipped without deleting existing files");
            cache.Store(key, bytes);
            check(new WeChatAvatarCache(directory, scope).Get(key)?.Bytes.SequenceEqual(bytes) == true,
                "Fresh portraits survive a listener restart with the updated cache format");
            check(new WeChatAvatarCache(directory, "other-client").Get(key) is null,
                "Portrait cache versions retain client scope isolation");
        }
        finally { Directory.Delete(directory, true); }
    }
}
