using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class AccountHistoryService
{
    private const string AppFolderName = "SteamEYA";
    private const string HistoryFolderName = "history";
    private const string HistoryFileName = "accounts.json";
    private const string AvatarFolderName = "avatars";

    // RefreshProfilesAsync 批量抓取时的并发上限，避免导入几百账号瞬间打开数百连接触发 429。
    private const int MaxProfileFetchConcurrency = 4;

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    // 串行化所有「读-改-写整文件」临界区。异步路径（SaveLoginAsync）用 await WaitAsync 不阻塞 UI 线程；
    // 同步路径（SaveCsAccountStatus/ImportAccounts/DeleteAccounts/ClearAll）用 Wait——其临界区内不含 await，
    // 持锁者必同步走完即释放，故 UI 线程上的 Wait 不会自死锁。
    // 与 RefreshProfilesAsync 内的限并发 SemaphoreSlim 是两把不同的锁，不要混用。
    private readonly SemaphoreSlim _fileGate = new(1, 1);

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
        DateTimeOffset? tokenExpiresAt,
        string? prefetchedPersonaName = null,
        string? prefetchedAvatarUrl = null,
        string? prefetchedAvatarPath = null)
    {
        accountName = accountName.Trim();
        steamId = steamId.Trim();
        eyaToken = eyaToken.Trim();

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException(Loc.T("Account_Error_AccountNameEmpty"), nameof(accountName));
        }

        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new ArgumentException(Loc.T("Account_Error_EyaTokenEmpty"), nameof(eyaToken));
        }

        // 传入预取资料即跳过对应的网络抓取。
        var needPersona = prefetchedPersonaName is null;
        var needAvatar = prefetchedAvatarPath is null;
        // 已有头像 URL（来自预取或后续抓取）就不必单为拿 URL 再抓 profile；
        // 但 needAvatar 且手里没有 URL 时仍须抓 profile 取 AvatarUrl，否则补不回头像（曾下载失败的账号）。
        var avatarUrl = string.IsNullOrWhiteSpace(prefetchedAvatarUrl) ? null : prefetchedAvatarUrl;
        var needProfileFetch = needPersona || (needAvatar && avatarUrl is null);
        var skipNetwork = !needProfileFetch && !needAvatar;

        // 临界区 1：读-改-写基本记录，并合并已预取到的 profile/头像字段。
        await _fileGate.WaitAsync();
        try
        {
            var document = ReadDocumentForWrite();
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

            if (!string.IsNullOrWhiteSpace(prefetchedPersonaName))
            {
                item.PersonaName = prefetchedPersonaName;
            }

            // 预取到 URL 也要落盘：它是本地头像文件被删/丢失后 AvatarImage 的回退来源。
            if (avatarUrl is not null)
            {
                item.AvatarUrl = avatarUrl;
            }

            if (!string.IsNullOrWhiteSpace(prefetchedAvatarPath))
            {
                item.AvatarPath = prefetchedAvatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }

        if (skipNetwork)
        {
            return;
        }

        // 网络请求在锁外进行，避免占着文件锁等数秒，期间后台 RefreshProfilesAsync 仍可写盘。
        var profile = needProfileFetch ? await TryGetSteamProfileAsync(steamId) : null;
        string? personaName = needPersona ? profile?.PersonaName : prefetchedPersonaName;
        if (avatarUrl is null)
        {
            avatarUrl = profile?.AvatarUrl;
        }

        string? avatarPath = prefetchedAvatarPath;
        if (needAvatar && !string.IsNullOrWhiteSpace(avatarUrl))
        {
            avatarPath = await TryDownloadAvatarAsync(steamId, accountName, avatarUrl);
        }

        var hasPersona = !string.IsNullOrWhiteSpace(personaName);
        var hasAvatarUrl = !string.IsNullOrWhiteSpace(avatarUrl);
        var hasAvatarPath = !string.IsNullOrWhiteSpace(avatarPath);
        if (!hasPersona && !hasAvatarUrl && !hasAvatarPath)
        {
            return;
        }

        // 临界区 2：重新读盘、按账号键重新定位条目，只合并 profile/头像字段，避免用陈旧 document 回滚他方写入。
        await _fileGate.WaitAsync();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, steamId, accountName);
            if (item is null)
            {
                return;
            }

            if (hasPersona)
            {
                item.PersonaName = personaName;
            }

            if (hasAvatarUrl)
            {
                item.AvatarUrl = avatarUrl;
            }

            if (hasAvatarPath)
            {
                item.AvatarPath = avatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    // personaName/avatarUrl/avatarPath：查询流程已顺带抓到的资料，非空白才覆盖（与 SaveLoginAsync 合并语义一致）。
    // 不传则保持存量不动——否则只查询过、从未登录的账号会永远缺昵称头像（抓到即丢弃、每次查询重复浪费）。
    public void SaveCsAccountStatus(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt,
        CsPremierScoreResult score,
        SteamTokenOnlineValidationResult jwtValidation,
        string? personaName = null,
        string? avatarUrl = null,
        string? avatarPath = null)
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

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
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

            if (!string.IsNullOrWhiteSpace(personaName))
            {
                item.PersonaName = personaName;
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                item.AvatarUrl = avatarUrl;
            }

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                item.AvatarPath = avatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>
    /// 个性化应用成功后，用已知的新昵称/头像直接写回本地记录——不依赖再抓：
    /// 社区资料接口有边缘缓存，改名后立刻抓取可能仍返回旧值、把记录「倒退」回去。
    /// <paramref name="avatarImageSourcePath"/> 非空时把该图片复制进头像库并更新 AvatarPath。
    /// </summary>
    public void UpdateProfileLocally(string steamId, string? personaName, string? avatarImageSourcePath)
    {
        steamId = steamId.Trim();
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return;
        }

        string? avatarPath = null;
        if (!string.IsNullOrWhiteSpace(avatarImageSourcePath) && File.Exists(avatarImageSourcePath))
        {
            string? tempPath = null;
            try
            {
                Directory.CreateDirectory(AvatarFolderPath);
                avatarPath = Path.Combine(AvatarFolderPath, $"{GetSafeAvatarKey(steamId, steamId)}.jpg");
                tempPath = avatarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(avatarImageSourcePath, tempPath, overwrite: true);
                File.Move(tempPath, avatarPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"复制个性化头像到头像库失败：{ex.Message}");
                TryDeleteFile(tempPath);
                avatarPath = null;
            }
        }

        if (string.IsNullOrWhiteSpace(personaName) && avatarPath is null)
        {
            return;
        }

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var item = document.Accounts.FirstOrDefault(account =>
                string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(personaName))
            {
                item.PersonaName = personaName.Trim();
            }

            if (avatarPath is not null)
            {
                item.AvatarPath = avatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>设置账号备注（trim；空白视为清空）。就地改写磁盘条目，其余字段保持不变。</summary>
    public void SetNote(SteamAccountHistoryItem account, string? note)
    {
        var normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, account.SteamId, account.AccountName);
            if (item is null)
            {
                return;
            }

            item.Note = normalized;
            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>批量把选中账号加入/移出某分组，返回实际改动的账号数。</summary>
    public int SetGroupMembership(
        IReadOnlyCollection<SteamAccountHistoryItem> accounts, string groupId, bool isMember)
    {
        if (accounts.Count == 0 || string.IsNullOrWhiteSpace(groupId))
        {
            return 0;
        }

        var keys = accounts.Select(GetAccountKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = 0;

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            foreach (var item in document.Accounts)
            {
                if (!keys.Contains(GetAccountKey(item)))
                {
                    continue;
                }

                item.GroupIds ??= [];
                if (isMember)
                {
                    if (!item.GroupIds.Contains(groupId))
                    {
                        item.GroupIds.Add(groupId);
                        changed++;
                    }
                }
                else if (item.GroupIds.Remove(groupId))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                document.Accounts = NormalizeAccounts(document.Accounts).ToList();
                WriteDocument(document);
            }

            return changed;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>删除分组时把该分组 ID 从所有账号移除（级联清理），返回受影响账号数。</summary>
    public int RemoveGroupFromAllAccounts(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return 0;
        }

        var changed = 0;

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            foreach (var item in document.Accounts)
            {
                if (item.GroupIds is { Count: > 0 } && item.GroupIds.Remove(groupId))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                document.Accounts = NormalizeAccounts(document.Accounts).ToList();
                WriteDocument(document);
            }

            return changed;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>合并导入账号（按 SteamID/账户名去重覆盖），导入不更新“上次登录”。</summary>
    public (int Added, int Updated) ImportAccounts(IReadOnlyList<AccountImportEntry> entries)
    {
        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
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
        finally
        {
            _fileGate.Release();
        }
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

        // 抓取前快照各账号当前 (PersonaName, AvatarUrl)：批量抓取可持续数分钟，期间个性化/登录兜底刷新
        // 可能已写入更新的资料；合并时若发现字段已不等于快照值（有并发新写入），跳过覆盖，
        // 避免用几分钟前抓到的旧资料把新值「倒退」回去（乐观并发检查）。
        var baseline = new Dictionary<string, (string? PersonaName, string? AvatarUrl)>(StringComparer.OrdinalIgnoreCase);
        await _fileGate.WaitAsync();
        try
        {
            foreach (var account in ReadDocument().Accounts)
            {
                baseline[account.SteamId] = (account.PersonaName, account.AvatarUrl);
            }
        }
        finally
        {
            _fileGate.Release();
        }

        // 限并发到 4：这把锁只管网络抓取节流，与文件互斥锁 _fileGate 是两把不同的锁，别混用。
        using var fetchThrottle = new SemaphoreSlim(MaxProfileFetchConcurrency, MaxProfileFetchConcurrency);
        var results = await Task.WhenAll(distinctIds.Select(async steamId =>
        {
            await fetchThrottle.WaitAsync();
            try
            {
                var profile = await TryGetSteamProfileAsync(steamId);
                var avatarPath = !string.IsNullOrWhiteSpace(profile?.AvatarUrl)
                    ? await TryDownloadAvatarAsync(steamId, steamId, profile.AvatarUrl)
                    : null;
                return (SteamId: steamId, Profile: profile, AvatarPath: avatarPath);
            }
            finally
            {
                fetchThrottle.Release();
            }
        }));

        await _fileGate.WaitAsync();
        try
        {
            var document = ReadDocumentForWrite();
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

                // 抓取期间该字段已被并发写入更新（与快照不符）时跳过覆盖：并发写入的值必然比本次
                // 抓取开始前的网络快照新，覆盖只会造成资料「倒退」。
                var snapshot = baseline.TryGetValue(steamId, out var value) ? value : default;
                var wrote = false;
                if (!string.IsNullOrWhiteSpace(profile.PersonaName) && item.PersonaName == snapshot.PersonaName)
                {
                    item.PersonaName = profile.PersonaName;
                    wrote = true;
                }

                if (!string.IsNullOrWhiteSpace(profile.AvatarUrl) && item.AvatarUrl == snapshot.AvatarUrl)
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

            return updatedCount;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>删除指定账号（含本地头像缓存），返回实际移除的条数。</summary>
    public int DeleteAccounts(IReadOnlyCollection<SteamAccountHistoryItem> accounts)
    {
        if (accounts.Count == 0)
        {
            return 0;
        }

        var keys = accounts.Select(GetAccountKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int removed;
        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            removed = document.Accounts.RemoveAll(account => keys.Contains(GetAccountKey(account)));
            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }

        foreach (var account in accounts)
        {
            TryDeleteFile(account.AvatarPath);
        }

        return removed;
    }

    /// <summary>清空全部历史账号与头像缓存，返回清空前的账号数。</summary>
    public int ClearAll()
    {
        int count;
        _fileGate.Wait();
        try
        {
            // 清空是纯覆盖写，不依赖旧内容，读失败时按 0 计数即可，无需 ReadDocumentForWrite 中止。
            var document = ReadDocument();
            count = NormalizeAccounts(document.Accounts).Count;
            WriteDocument(new AccountHistoryDocument());
        }
        finally
        {
            _fileGate.Release();
        }

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

    // 纯读取场景（Load 等只读 API）允许降级为空文档；读-改-写路径必须改用 ReadDocumentForWrite。
    private AccountHistoryDocument ReadDocument()
    {
        try
        {
            return ReadDocumentForWrite();
        }
        catch (Exception ex)
        {
            AppLog.Error("读取账号历史失败，按空文档降级（只读场景）。", ex);
            return new AccountHistoryDocument();
        }
    }

    // 供「读-改-写整文件覆盖」路径使用：区分「文件不存在」与「文件存在但读不了/解析失败」。
    // 后者抛出异常以中止本次覆盖写，绝不能拿一份空文档把全部历史账号与令牌覆盖掉。
    private AccountHistoryDocument ReadDocumentForWrite()
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("账号历史文件存在但无法读取，已中止本次保存以避免覆盖丢失数据。", ex);
            throw new InvalidOperationException(
                Loc.T("Account_Error_HistoryFileUnreadable"),
                ex);
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
        var normalized = accounts
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.AccountName) &&
                !string.IsNullOrWhiteSpace(account.EyaToken))
            .GroupBy(GetAccountKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(account => account.LastLoginAt)
                .First())
            .OrderByDescending(account => account.LastLoginAt)
            .ToList();

        // JSON 里显式的 "groupIds": null 会覆盖属性初始值；消费方（如批量分组 flyout）直接 .GroupIds.Contains 会 NRE。
        foreach (var account in normalized)
        {
            account.GroupIds ??= [];
        }

        return normalized;
    }

    /// <summary>账号去重键（SteamID 优先，缺失退化为账户名），供 HistoryPage 复用以保持选中态键一致。</summary>
    internal static string GetAccountKey(SteamAccountHistoryItem account)
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
            // 临时名带随机后缀：登录兜底刷新与手动刷新可并发下载同一账号，固定名会互撞静默失败。
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

    private void WriteDocument(AccountHistoryDocument document)
    {
        Directory.CreateDirectory(HistoryFolderPath);
        var json = JsonSerializer.Serialize(document, AccountHistoryJsonContext.Default.AccountHistoryDocument);

        // 先写临时文件再原子替换，避免进程中断导致 accounts.json 半截损坏、下次保存清空全部历史。
        // 临时文件名加随机后缀，避免多次写入间残留的 .tmp 撞名。
        var tempPath = HistoryFilePath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);

            var backupPath = HistoryFilePath + ".bak";
            if (File.Exists(HistoryFilePath))
            {
                // 覆盖前为旧文件保留一份 .bak：File.Replace 原子替换并自动留备份。
                File.Replace(tempPath, HistoryFilePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, HistoryFilePath);
            }
        }
        catch
        {
            // 替换失败（.bak 被占用等）时清掉临时文件，避免每次失败在 history 目录堆积 .tmp。
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 清理失败只残留临时文件，不影响正式文件，吞掉以保留原始异常。
            }

            throw;
        }
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
