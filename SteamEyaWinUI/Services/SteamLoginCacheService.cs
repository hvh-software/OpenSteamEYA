using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamLoginCacheService
{
    private const string AppFolderName = "SteamEYA";
    private const string CacheFileName = "cached-login.json";
    private const string AvatarFolderName = "cached-avatars";

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    // 缓存文件是进程级单一资源（路径固定），所有「读-改-写」必须串行，否则后台头像刷新与
    // 用户在已缓存账号页的删除/清空会互相覆盖（丢失刚缓存的账号 / 已删账号复活）。临界区全为
    // 同步代码（网络抓取在锁外），故用普通 lock 即可。
    private static readonly object FileLock = new();

    // 一次刷新对 steamcommunity 的并发抓取上限，避免一次性开太多连接触发 429（与 AccountHistoryService 一致）。
    private const int MaxProfileFetchConcurrency = 4;

    public SteamLoginCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppFolderPath = Path.Combine(appData, AppFolderName);
        CacheFilePath = Path.Combine(AppFolderPath, CacheFileName);
        AvatarFolderPath = Path.Combine(AppFolderPath, AvatarFolderName);
    }

    public string AppFolderPath { get; }

    public string CacheFilePath { get; }

    public string AvatarFolderPath { get; }

    public IReadOnlyList<CachedSteamLoginAccount> LoadAll()
    {
        lock (FileLock)
        {
            return NormalizeAccounts(ReadDocument().Accounts);
        }
    }

    public void MarkEyaLogin(string accountName, string steamId)
    {
        var marker = new CachedSteamLoginAccount
        {
            AccountName = accountName.Trim(),
            SteamId = steamId.Trim(),
            CachedAt = DateTimeOffset.Now
        };

        if (!IsUsable(marker))
        {
            return;
        }

        lock (FileLock)
        {
            var document = ReadDocument();
            var existing = FindExisting(document.EyaAccounts, marker);
            if (existing is null)
            {
                document.EyaAccounts.Add(marker);
            }
            else
            {
                existing.AccountName = marker.AccountName;
                existing.SteamId = marker.SteamId;
                existing.CachedAt = marker.CachedAt;
            }

            document.EyaAccounts = NormalizeAccounts(document.EyaAccounts).ToList();
            WriteDocument(document);
        }
    }

    public bool IsEyaLogin(CachedSteamLoginAccount account)
    {
        lock (FileLock)
        {
            return FindExisting(ReadDocument().EyaAccounts, account) is not null;
        }
    }

    public IReadOnlyList<CachedSteamLoginAccount> SaveMany(IEnumerable<CachedSteamLoginAccount> accounts)
    {
        var saved = new List<CachedSteamLoginAccount>();
        lock (FileLock)
        {
            var document = ReadDocument();
            foreach (var account in accounts)
            {
                account.AccountName = account.AccountName.Trim();
                account.SteamId = account.SteamId.Trim();

                if (!IsUsable(account))
                {
                    continue;
                }

                account.CachedAt = DateTimeOffset.Now;
                var existing = FindExisting(document.Accounts, account);
                if (existing is null)
                {
                    document.Accounts.Add(account);
                    saved.Add(account);
                    continue;
                }

                existing.AccountName = account.AccountName;
                existing.SteamId = account.SteamId;
                existing.CachedAt = account.CachedAt;
                existing.PersonaName = string.IsNullOrWhiteSpace(account.PersonaName)
                    ? existing.PersonaName
                    : account.PersonaName;
                existing.AvatarUrl = string.IsNullOrWhiteSpace(account.AvatarUrl)
                    ? existing.AvatarUrl
                    : account.AvatarUrl;
                existing.AvatarPath = string.IsNullOrWhiteSpace(account.AvatarPath)
                    ? existing.AvatarPath
                    : account.AvatarPath;
                existing.ConnectCacheToken = string.IsNullOrWhiteSpace(account.ConnectCacheToken)
                    ? existing.ConnectCacheToken
                    : account.ConnectCacheToken;
                saved.Add(existing);
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }

        return saved;
    }

    public async Task<int> RefreshProfilesAsync(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        var targets = accounts
            .Where(IsUsable)
            .GroupBy(account => account.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        // 网络抓取在锁外并发进行，但用信号量限流，避免一次性对 steamcommunity 开太多连接触发 429。
        using var throttle = new SemaphoreSlim(MaxProfileFetchConcurrency);
        var results = await Task.WhenAll(targets.Select(async account =>
        {
            await throttle.WaitAsync();
            try
            {
                var profile = await TryGetSteamProfileAsync(account.SteamId);
                var avatarPath = !string.IsNullOrWhiteSpace(profile?.AvatarUrl)
                    ? await TryDownloadAvatarAsync(account.SteamId, account.AccountName, profile.AvatarUrl)
                    : null;
                return (Account: account, Profile: profile, AvatarPath: avatarPath);
            }
            finally
            {
                throttle.Release();
            }
        }));

        // 抓取耗时可能数秒，期间用户可能已删除/清空账号；故在锁内基于「当前」磁盘状态合并回写，
        // 而非把抓取前读到的旧快照整体覆盖（否则会让已删账号复活、丢失并发写入）。
        var updatedCount = 0;
        lock (FileLock)
        {
            var document = ReadDocument();
            foreach (var (account, profile, avatarPath) in results)
            {
                if (profile is null)
                {
                    continue;
                }

                var item = FindExisting(document.Accounts, account);
                if (item is null)
                {
                    continue;
                }

                var wrote = false;
                if (!string.IsNullOrWhiteSpace(profile.PersonaName))
                {
                    item.PersonaName = profile.PersonaName;
                    wrote = true;
                }

                if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
                {
                    var urlChanged = !string.Equals(item.AvatarUrl, profile.AvatarUrl, StringComparison.OrdinalIgnoreCase);
                    item.AvatarUrl = profile.AvatarUrl;
                    if (avatarPath is not null)
                    {
                        item.AvatarPath = avatarPath;
                    }
                    else if (urlChanged)
                    {
                        // 头像已换但本次下载失败：清空本地路径让 UI 走新 URL 远程回退，
                        // 否则本地旧图优先、新头像永远不显示。
                        item.AvatarPath = null;
                    }

                    wrote = true;
                }

                // 只计实际写入资料的账号：抓到但字段全空（什么都没写）不算「已同步」，避免提示数虚高。
                if (wrote)
                {
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                document.Accounts = NormalizeAccounts(document.Accounts).ToList();
                WriteDocument(document);
            }
        }

        return updatedCount;
    }

    public int Delete(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return 0;
        }

        var keys = accounts.Select(account => account.CacheKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed;
        lock (FileLock)
        {
            var document = ReadDocument();
            removed = document.Accounts.RemoveAll(account => keys.Contains(account.CacheKey));
            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }

        foreach (var account in accounts)
        {
            TryDeleteFile(account.AvatarPath);
        }

        return removed;
    }

    public int ClearAll()
    {
        int count;
        List<CachedSteamLoginAccount> clearedAvatars;
        lock (FileLock)
        {
            var document = ReadDocument();
            count = NormalizeAccounts(document.Accounts).Count;
            clearedAvatars = document.Accounts.ToList();
            WriteDocument(new CachedSteamLoginDocument
            {
                EyaAccounts = NormalizeAccounts(document.EyaAccounts).ToList()
            });
        }

        foreach (var account in clearedAvatars)
        {
            TryDeleteFile(account.AvatarPath);
        }

        return count;
    }

    private CachedSteamLoginDocument ReadDocument()
    {
        if (!File.Exists(CacheFilePath))
        {
            return new CachedSteamLoginDocument();
        }

        try
        {
            var json = File.ReadAllText(CacheFilePath);
            using var jsonDocument = JsonDocument.Parse(json);
            if (jsonDocument.RootElement.ValueKind == JsonValueKind.Object &&
                jsonDocument.RootElement.TryGetProperty("accounts", out _))
            {
                var document = JsonSerializer.Deserialize(
                    json,
                    SteamLoginCacheJsonContext.Default.CachedSteamLoginDocument)
                    ?? new CachedSteamLoginDocument();
                document.Accounts ??= [];
                document.EyaAccounts ??= [];
                return document;
            }

            var legacyAccount = JsonSerializer.Deserialize(
                json,
                SteamLoginCacheJsonContext.Default.CachedSteamLoginAccount);
            return legacyAccount is not null && IsUsable(legacyAccount)
                ? new CachedSteamLoginDocument { Accounts = [legacyAccount] }
                : new CachedSteamLoginDocument();
        }
        catch (JsonException)
        {
            return new CachedSteamLoginDocument();
        }
        catch (IOException)
        {
            return new CachedSteamLoginDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return new CachedSteamLoginDocument();
        }
    }

    private void WriteDocument(CachedSteamLoginDocument document)
    {
        var directory = Path.GetDirectoryName(CacheFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            document,
            SteamLoginCacheJsonContext.Default.CachedSteamLoginDocument);

        // 原子写入：先写临时文件再 Move 覆盖，崩溃或并发读取只会看到完整的旧文件或完整的新文件，
        // 不会读到 File.WriteAllText「先截断后写入」中途的空/半截 JSON。
        var tempPath = CacheFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, CacheFilePath, overwrite: true);
    }

    private static CachedSteamLoginAccount? FindExisting(
        IEnumerable<CachedSteamLoginAccount> accounts,
        CachedSteamLoginAccount target)
    {
        return accounts.FirstOrDefault(account =>
            string.Equals(account.CacheKey, target.CacheKey, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<CachedSteamLoginAccount> NormalizeAccounts(
        IEnumerable<CachedSteamLoginAccount> accounts)
    {
        return accounts
            .Where(IsUsable)
            .GroupBy(account => account.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(account => account.CachedAt).First())
            .OrderByDescending(account => account.CachedAt)
            .ToList();
    }

    private static bool IsUsable(CachedSteamLoginAccount? account)
    {
        return account is not null &&
            !string.IsNullOrWhiteSpace(account.AccountName) &&
            !string.IsNullOrWhiteSpace(account.SteamId);
    }

    private static async Task<SteamProfileData?> TryGetSteamProfileAsync(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        try
        {
            // nc 一次性参数穿透边缘缓存：?xml=1 被 CDN 缓存最长 1 小时（缓存键含查询串，唯一参数必回源），
            // 否则改名/换头像后的刷新会抓到旧快照并把记录「倒退」回旧值。
            var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId.Trim())}" +
                $"?xml=1&nc={DateTimeOffset.UtcNow.Ticks}";
            var xml = await DefaultHttpClient.GetStringAsync(url);
            var document = XDocument.Parse(xml);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            return new SteamProfileData(
                root.Element("steamID")?.Value,
                root.Element("avatarFull")?.Value ?? root.Element("avatarMedium")?.Value);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn($"抓取 Steam 资料失败（{steamId}）：{ex.Message}");
            return null;
        }
        catch (TaskCanceledException)
        {
            AppLog.Warn($"抓取 Steam 资料超时（{steamId}）。");
            return null;
        }
        catch (System.Xml.XmlException)
        {
            // 不存在/被封禁的账号返回 200 + HTML 错误页，非 XML。
            AppLog.Warn($"Steam 资料响应非 XML（{steamId}），已跳过。");
            return null;
        }
    }

    private async Task<string?> TryDownloadAvatarAsync(
        string steamId,
        string accountName,
        string avatarUrl)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return null;
        }

        string? tempPath = null;
        try
        {
            using var response = await DefaultHttpClient.GetAsync(avatarUri);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 || !LooksLikeImage(bytes))
            {
                // 拦下捕获门户/劫持网络返回的 200+HTML「头像」：这类坏文件落盘后 UI 解码必失败。
                return null;
            }

            Directory.CreateDirectory(AvatarFolderPath);
            var avatarPath = Path.Combine(AvatarFolderPath, $"{GetSafeAvatarKey(steamId, accountName)}.jpg");
            // 原子写：先写临时文件再整体替换，避免 UI 线程在覆盖期间读到半截 JPEG 或撞上写入中的文件。
            // 临时名带随机后缀：登录后台刷新与手动刷新可并发下载同一账号，固定名会互撞静默失败。
            tempPath = avatarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, avatarPath, overwrite: true);
            return avatarPath;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            TryDeleteFile(tempPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            return null;
        }
    }

    // JPEG(FF D8 FF) / PNG(89 50 4E 47) / GIF(47 49 46)——头像 CDN 只会返回这三类图片。
    private static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
            (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
            (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46));

    private static string GetSafeAvatarKey(string steamId, string accountName)
    {
        var value = string.IsNullOrWhiteSpace(steamId) ? accountName : steamId;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record SteamProfileData(string? PersonaName, string? AvatarUrl);
}

internal sealed class CachedSteamLoginDocument
{
    public int Version { get; set; } = 1;

    public List<CachedSteamLoginAccount> Accounts { get; set; } = [];

    public List<CachedSteamLoginAccount> EyaAccounts { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(CachedSteamLoginDocument))]
[JsonSerializable(typeof(CachedSteamLoginAccount))]
internal sealed partial class SteamLoginCacheJsonContext : JsonSerializerContext;
