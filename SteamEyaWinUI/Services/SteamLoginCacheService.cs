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
        return NormalizeAccounts(ReadDocument().Accounts);
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

    public bool IsEyaLogin(CachedSteamLoginAccount account)
    {
        return FindExisting(ReadDocument().EyaAccounts, account) is not null;
    }

    public IReadOnlyList<CachedSteamLoginAccount> SaveMany(IEnumerable<CachedSteamLoginAccount> accounts)
    {
        var document = ReadDocument();
        var saved = new List<CachedSteamLoginAccount>();
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
            saved.Add(existing);
        }

        document.Accounts = NormalizeAccounts(document.Accounts).ToList();
        WriteDocument(document);
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

        var results = await Task.WhenAll(targets.Select(async account =>
        {
            var profile = await TryGetSteamProfileAsync(account.SteamId);
            var avatarPath = !string.IsNullOrWhiteSpace(profile?.AvatarUrl)
                ? await TryDownloadAvatarAsync(account.SteamId, account.AccountName, profile.AvatarUrl)
                : null;
            return (Account: account, Profile: profile, AvatarPath: avatarPath);
        }));

        var document = ReadDocument();
        var updatedCount = 0;
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

            if (!string.IsNullOrWhiteSpace(profile.PersonaName))
            {
                item.PersonaName = profile.PersonaName;
            }

            if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
            {
                item.AvatarUrl = profile.AvatarUrl;
                item.AvatarPath = avatarPath ?? item.AvatarPath;
            }

            updatedCount++;
        }

        if (updatedCount > 0)
        {
            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
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
        var document = ReadDocument();
        var removed = document.Accounts.RemoveAll(account => keys.Contains(account.CacheKey));
        document.Accounts = NormalizeAccounts(document.Accounts).ToList();
        WriteDocument(document);

        foreach (var account in accounts)
        {
            TryDeleteFile(account.AvatarPath);
        }

        return removed;
    }

    public int ClearAll()
    {
        var document = ReadDocument();
        var count = NormalizeAccounts(document.Accounts).Count;
        foreach (var account in document.Accounts)
        {
            TryDeleteFile(account.AvatarPath);
        }

        WriteDocument(new CachedSteamLoginDocument
        {
            EyaAccounts = NormalizeAccounts(document.EyaAccounts).ToList()
        });
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
        File.WriteAllText(CacheFilePath, json);
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
            var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId.Trim())}?xml=1";
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
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (System.Xml.XmlException)
        {
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

        try
        {
            using var response = await DefaultHttpClient.GetAsync(avatarUri);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return null;
            }

            Directory.CreateDirectory(AvatarFolderPath);
            var avatarPath = Path.Combine(AvatarFolderPath, $"{GetSafeAvatarKey(steamId, accountName)}.jpg");
            await File.WriteAllBytesAsync(avatarPath, bytes);
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
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

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
