using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class AccountHistoryService
{
    private const string AppFolderName = "SteamEYA";
    private const string HistoryFolderName = "history";
    private const string HistoryFileName = "accounts.json";
    private const string AvatarFolderName = "avatars";

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    public AccountHistoryService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppFolderPath = Path.Combine(appData, AppFolderName);
        HistoryFolderPath = Path.Combine(AppFolderPath, HistoryFolderName);
        HistoryFilePath = Path.Combine(HistoryFolderPath, HistoryFileName);
        AvatarFolderPath = Path.Combine(AppFolderPath, AvatarFolderName);
    }

    public string AppFolderPath { get; }

    public string HistoryFolderPath { get; }

    public string HistoryFilePath { get; }

    public string AvatarFolderPath { get; }

    public IReadOnlyList<SteamAccountHistoryItem> Load()
    {
        var document = ReadDocument();
        return NormalizeAccounts(document.Accounts);
    }

    public async Task<SteamAccountHistoryItem?> GetProfilePreviewAsync(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt)
    {
        var profile = await TryGetSteamProfileAsync(steamId);
        if (profile is null)
        {
            return null;
        }

        var avatarPath = !string.IsNullOrWhiteSpace(profile.AvatarUrl)
            ? await TryDownloadAvatarAsync(steamId, accountName, profile.AvatarUrl)
            : null;

        return new SteamAccountHistoryItem
        {
            AccountName = accountName,
            SteamId = steamId,
            EyaToken = eyaToken,
            TokenExpiresAt = tokenExpiresAt,
            PersonaName = profile.PersonaName,
            AvatarUrl = profile.AvatarUrl,
            AvatarPath = avatarPath
        };
    }

    public async Task SaveLoginAsync(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt)
    {
        accountName = accountName.Trim();
        steamId = steamId.Trim();
        eyaToken = eyaToken.Trim();

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException("账户名称不能为空。", nameof(accountName));
        }

        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new ArgumentException("EYA 令牌不能为空。", nameof(eyaToken));
        }

        var document = ReadDocument();
        var item = FindExisting(document.Accounts, steamId, accountName);
        if (item is null)
        {
            item = new SteamAccountHistoryItem();
            document.Accounts.Add(item);
        }

        item.AccountName = accountName;
        item.SteamId = steamId;
        item.EyaToken = eyaToken;
        item.TokenExpiresAt = tokenExpiresAt;
        item.LastLoginAt = DateTimeOffset.Now;

        document.Accounts = NormalizeAccounts(document.Accounts).ToList();
        WriteDocument(document);

        var profile = await TryGetSteamProfileAsync(steamId);
        if (profile is not null)
        {
            if (!string.IsNullOrWhiteSpace(profile.PersonaName))
            {
                item.PersonaName = profile.PersonaName;
            }

            if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
            {
                item.AvatarUrl = profile.AvatarUrl;
                item.AvatarPath = await TryDownloadAvatarAsync(steamId, accountName, profile.AvatarUrl)
                    ?? item.AvatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
    }

    public void SaveCsAccountStatus(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt,
        CsPremierScoreResult score,
        SteamTokenOnlineValidationResult jwtValidation)
    {
        accountName = accountName.Trim();
        steamId = steamId.Trim();
        eyaToken = eyaToken.Trim();

        if (string.IsNullOrWhiteSpace(accountName) ||
            string.IsNullOrWhiteSpace(steamId) ||
            string.IsNullOrWhiteSpace(eyaToken))
        {
            return;
        }

        var document = ReadDocument();
        var item = FindExisting(document.Accounts, steamId, accountName);
        if (item is null)
        {
            item = new SteamAccountHistoryItem
            {
                AccountName = accountName,
                SteamId = steamId,
                EyaToken = eyaToken,
                TokenExpiresAt = tokenExpiresAt
            };
            document.Accounts.Add(item);
        }

        item.AccountName = accountName;
        item.SteamId = steamId;
        item.EyaToken = eyaToken;
        item.TokenExpiresAt = tokenExpiresAt;
        item.CompetitiveScore = score.DisplayText;
        item.AccountStatus = score.StatusText;
        item.JwtAvailable = jwtValidation.IsValid;
        item.JwtStatus = jwtValidation.IsValid ? "有效" : "无效";
        item.JwtValidatedAt = DateTimeOffset.Now;
        item.PremierScore = score.PremierRanking is null ? null : checked((int)score.PremierRanking.RankId);
        item.PremierWins = score.PremierRanking is null ? null : checked((int)score.PremierRanking.Wins);
        item.PremierScoreUpdatedAt = DateTimeOffset.Now;
        item.CooldownSeconds = score.PenaltySeconds;
        item.CooldownReason = score.PenaltyReason;
        item.GcVacBanned = score.GcVacBannedOrUnknown;
        item.CsPlayerLevel = score.PlayerLevel;
        item.InCsMatch = score.InMatch;
        item.CsStatusUpdatedAt = DateTimeOffset.Now;

        document.Accounts = NormalizeAccounts(document.Accounts).ToList();
        WriteDocument(document);
    }

    /// <summary>合并导入账号（按 SteamID/账户名去重覆盖），导入不更新“上次登录”。</summary>
    public (int Added, int Updated) ImportAccounts(IReadOnlyList<AccountImportEntry> entries)
    {
        var document = ReadDocument();
        var added = 0;
        var updated = 0;

        foreach (var entry in entries)
        {
            var accountName = entry.AccountName.Trim();
            var steamId = entry.SteamId.Trim();
            var eyaToken = entry.EyaToken.Trim();
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(eyaToken))
            {
                continue;
            }

            var item = FindExisting(document.Accounts, steamId, accountName);
            if (item is null)
            {
                item = new SteamAccountHistoryItem();
                document.Accounts.Add(item);
                added++;
            }
            else
            {
                updated++;
            }

            item.AccountName = accountName;
            item.SteamId = steamId;
            item.EyaToken = eyaToken;
            item.TokenExpiresAt = entry.TokenExpiresAt;
        }

        document.Accounts = NormalizeAccounts(document.Accounts).ToList();
        WriteDocument(document);
        return (added, updated);
    }

    /// <summary>批量补全昵称与头像（网络失败逐个忽略），返回成功更新的账号数。</summary>
    public async Task<int> RefreshProfilesAsync(IReadOnlyCollection<string> steamIds)
    {
        var distinctIds = steamIds
            .Where(steamId => !string.IsNullOrWhiteSpace(steamId))
            .Select(steamId => steamId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctIds.Count == 0)
        {
            return 0;
        }

        var results = await Task.WhenAll(distinctIds.Select(async steamId =>
        {
            var profile = await TryGetSteamProfileAsync(steamId);
            var avatarPath = !string.IsNullOrWhiteSpace(profile?.AvatarUrl)
                ? await TryDownloadAvatarAsync(steamId, steamId, profile.AvatarUrl)
                : null;
            return (SteamId: steamId, Profile: profile, AvatarPath: avatarPath);
        }));

        var document = ReadDocument();
        var updatedCount = 0;
        foreach (var (steamId, profile, avatarPath) in results)
        {
            if (profile is null)
            {
                continue;
            }

            var item = document.Accounts.FirstOrDefault(account =>
                string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>删除指定账号（含本地头像缓存），返回实际移除的条数。</summary>
    public int DeleteAccounts(IReadOnlyCollection<SteamAccountHistoryItem> accounts)
    {
        if (accounts.Count == 0)
        {
            return 0;
        }

        var keys = accounts.Select(GetAccountKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var document = ReadDocument();
        var removed = document.Accounts.RemoveAll(account => keys.Contains(GetAccountKey(account)));
        document.Accounts = NormalizeAccounts(document.Accounts).ToList();
        WriteDocument(document);

        foreach (var account in accounts)
        {
            TryDeleteFile(account.AvatarPath);
        }

        return removed;
    }

    /// <summary>清空全部历史账号与头像缓存，返回清空前的账号数。</summary>
    public int ClearAll()
    {
        var document = ReadDocument();
        var count = NormalizeAccounts(document.Accounts).Count;
        WriteDocument(new AccountHistoryDocument());

        try
        {
            if (Directory.Exists(AvatarFolderPath))
            {
                foreach (var file in Directory.EnumerateFiles(AvatarFolderPath))
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return count;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // 头像可能正被界面上的 BitmapImage 占用，删不掉就留着，不影响记录删除。
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

    private AccountHistoryDocument ReadDocument()
    {
        if (!File.Exists(HistoryFilePath))
        {
            return new AccountHistoryDocument();
        }

        try
        {
            var json = File.ReadAllText(HistoryFilePath);
            var document = JsonSerializer.Deserialize(json, AccountHistoryJsonContext.Default.AccountHistoryDocument)
                ?? new AccountHistoryDocument();
            document.Accounts ??= [];
            return document;
        }
        catch (JsonException)
        {
            return new AccountHistoryDocument();
        }
        catch (IOException)
        {
            return new AccountHistoryDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return new AccountHistoryDocument();
        }
    }

    private static SteamAccountHistoryItem? FindExisting(
        IEnumerable<SteamAccountHistoryItem> accounts,
        string steamId,
        string accountName)
    {
        if (!string.IsNullOrWhiteSpace(steamId))
        {
            var bySteamId = accounts.FirstOrDefault(account =>
                string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
            if (bySteamId is not null)
            {
                return bySteamId;
            }
        }

        return accounts.FirstOrDefault(account =>
            string.Equals(account.AccountName, accountName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<SteamAccountHistoryItem> NormalizeAccounts(
        IEnumerable<SteamAccountHistoryItem> accounts)
    {
        return accounts
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.AccountName) &&
                !string.IsNullOrWhiteSpace(account.EyaToken))
            .GroupBy(GetAccountKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(account => account.LastLoginAt)
                .First())
            .OrderByDescending(account => account.LastLoginAt)
            .ToList();
    }

    private static string GetAccountKey(SteamAccountHistoryItem account)
    {
        return string.IsNullOrWhiteSpace(account.SteamId)
            ? $"name:{account.AccountName}"
            : $"id:{account.SteamId}";
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
        catch (WebException)
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

    private void WriteDocument(AccountHistoryDocument document)
    {
        Directory.CreateDirectory(HistoryFolderPath);
        var json = JsonSerializer.Serialize(document, AccountHistoryJsonContext.Default.AccountHistoryDocument);

        // 先写临时文件再原子替换，避免进程中断导致 accounts.json 半截损坏、下次保存清空全部历史。
        var tempPath = HistoryFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, HistoryFilePath, overwrite: true);
    }

    private sealed record SteamProfileData(string? PersonaName, string? AvatarUrl);
}

internal sealed class AccountHistoryDocument
{
    public int Version { get; set; } = 1;

    public List<SteamAccountHistoryItem> Accounts { get; set; } = [];
}

// JsonSerializerDefaults.Web 与旧版反射序列化保持一致：camelCase 属性名、大小写不敏感读取，
// 保证现有 accounts.json 在 AOT 下继续可读写。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AccountHistoryDocument))]
internal sealed partial class AccountHistoryJsonContext : JsonSerializerContext;
