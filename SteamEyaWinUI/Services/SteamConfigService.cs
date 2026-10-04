using System.Globalization;
using Microsoft.Win32;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamConfigService
{
    public IReadOnlyList<CachedSteamLoginAccount> GetLoginAccounts(SteamPaths paths)
    {
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        var loginUsers = VdfDocument.LoadOrEmpty(loginUsersPath);
        var accounts = new List<CachedSteamLoginAccount>();

        var activeAccount = GetActiveLoginAccount(paths, loginUsers);
        if (activeAccount is not null)
        {
            accounts.Add(activeAccount);
        }

        accounts.AddRange(GetLoginUsersAccounts(loginUsers));
        accounts.AddRange(GetConfigAccounts(Path.Combine(paths.ConfigPath, "config.vdf")));

        var normalized = NormalizeLoginAccounts(accounts);
        PopulateConnectCacheTokens(paths, normalized);
        return normalized;
    }

    public void UpdateLoginFiles(
        SteamPaths paths,
        string accountName,
        string steamId,
        string encryptedJwt,
        string accountCrc32)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        UpdateConfigVdf(configPath, accountName, steamId);
        AppLog.Info($"已写入 config.vdf（{FileLength(configPath)} 字节）：\"{configPath}\"");

        UpdateLoginUsersVdf(loginUsersPath, accountName, steamId);
        AppLog.Info($"已写入 loginusers.vdf（{FileLength(loginUsersPath)} 字节）：\"{loginUsersPath}\"");

        UpdateLocalVdf(paths.LocalVdfPath, accountCrc32, encryptedJwt);
        AppLog.Info($"已写入 local.vdf（{FileLength(paths.LocalVdfPath)} 字节）：\"{paths.LocalVdfPath}\"");
    }

    public void RestoreLoginFiles(SteamPaths paths, CachedSteamLoginAccount account)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        UpdateConfigVdf(configPath, account.AccountName, account.SteamId);
        AppLog.Info($"已恢复 config.vdf（{FileLength(configPath)} 字节）：\"{configPath}\"");

        RestoreLoginUsersVdf(loginUsersPath, account);
        AppLog.Info($"已恢复 loginusers.vdf（{FileLength(loginUsersPath)} 字节）：\"{loginUsersPath}\"");

        // 写回原账号的 ConnectCache 令牌：有它 Steam 才能免密自动登录；缺失则只能预选账号、仍需手动登录。
        if (!string.IsNullOrWhiteSpace(account.ConnectCacheToken))
        {
            UpdateLocalVdf(
                paths.LocalVdfPath,
                Crc32.ComputeSteamAccountKey(account.AccountName),
                account.ConnectCacheToken);
            AppLog.Info($"已恢复 local.vdf ConnectCache（{FileLength(paths.LocalVdfPath)} 字节）：\"{paths.LocalVdfPath}\"");
        }
        else
        {
            AppLog.Warn("缓存账号缺少 ConnectCache 令牌，恢复后 Steam 可能需要手动登录。");
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    private static void UpdateConfigVdf(string path, string accountName, string steamId)
    {
        // 对齐 SteamEYA_GUI.exe（sub_140003640）：config.vdf 从零生成、整体覆盖，
        // 绝不读取/合并旧文件。旧实现用 LoadOrEmpty 读出用户原有 config.vdf
        // （常有 20KB+），再经我们手写的 VDF 解析/序列化往返一遍——只要某处结构
        // 往返后被破坏，Steam 启动时读不动 config.vdf 就会把它重置，连带忽略我们
        // 写入 loginusers.vdf/local.vdf 的自动登录，停在登录界面。这正是「上号流程
        // 全部成功、Steam 进程也起来了，却没自动登录」且只在部分机器复现的根因
        // （取决于该机 config.vdf 里有没有我们解析器处理不好的内容）。参考二进制
        // 干脆只写下面的最小模板，彻底规避往返破坏。
        var config = new Dictionary<string, object>(StringComparer.Ordinal);
        var steam = EnsurePath(config, "InstallConfigStore", "Software", "Valve", "Steam");

        steam["AutoUpdateWindowEnabled"] = "0";
        steam["MTBF"] = Random.Shared.Next(100000000, 999999999).ToString();

        // Steam「下载」页的着色器预缓存开关：每次换号重建配置时显式禁用，避免恢复为默认开启。
        var shaderCacheManager = EnsureObject(steam, "ShaderCacheManager");
        shaderCacheManager["DisableShaderCache"] = "1";

        var accounts = EnsureObject(steam, "Accounts");
        accounts[accountName] = new Dictionary<string, object>
        {
            ["SteamID"] = steamId
        };

        VdfDocument.Save(path, config);
    }

    private static void UpdateLoginUsersVdf(string path, string accountName, string steamId)
    {
        var loginUsers = VdfDocument.LoadOrEmpty(path);
        var users = EnsureObject(loginUsers, "users");

        foreach (var user in users.Values.OfType<Dictionary<string, object>>())
        {
            user["MostRecent"] = "0";
        }

        users[steamId] = new Dictionary<string, object>
        {
            ["AccountName"] = accountName,
            ["PersonaName"] = accountName,
            ["RememberPassword"] = "1",
            ["WantsOfflineMode"] = "0",
            ["SkipOfflineModeWarning"] = "0",
            ["AllowAutoLogin"] = "1",
            ["MostRecent"] = "1",
            ["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };

        VdfDocument.Save(path, loginUsers);
    }

    private static void RestoreLoginUsersVdf(string path, CachedSteamLoginAccount account)
    {
        var loginUsers = VdfDocument.LoadOrEmpty(path);
        var users = EnsureObject(loginUsers, "users");

        foreach (var user in users.Values.OfType<Dictionary<string, object>>())
        {
            user["MostRecent"] = "0";
        }

        var restoredUser = EnsureObject(users, account.SteamId);
        restoredUser["AccountName"] = account.AccountName;
        // 优先保留 loginusers.vdf 里既有昵称，其次用缓存到的 Steam 昵称，最后才退回登录名。
        var existingPersona = GetString(restoredUser, "PersonaName");
        restoredUser["PersonaName"] = !string.IsNullOrWhiteSpace(existingPersona)
            ? existingPersona
            : !string.IsNullOrWhiteSpace(account.PersonaName)
                ? account.PersonaName
                : account.AccountName;
        restoredUser["RememberPassword"] = "1";
        restoredUser["WantsOfflineMode"] = "0";
        restoredUser["SkipOfflineModeWarning"] = "0";
        restoredUser["AllowAutoLogin"] = "1";
        restoredUser["MostRecent"] = "1";
        restoredUser["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

        VdfDocument.Save(path, loginUsers);
    }

    private static void UpdateLocalVdf(string path, string accountCrc32, string encryptedJwt)
    {
        var local = VdfDocument.LoadOrEmpty(path);
        var connectCache = EnsurePath(
            local,
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");

        connectCache[accountCrc32] = encryptedJwt;
        VdfDocument.Save(path, local);
    }

    private static Dictionary<string, object> EnsurePath(
        Dictionary<string, object> root,
        params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            current = EnsureObject(current, key);
        }

        return current;
    }

    private static Dictionary<string, object> EnsureObject(
        Dictionary<string, object> parent,
        string key)
    {
        if (parent.TryGetValue(key, out var value) && value is Dictionary<string, object> existing)
        {
            return existing;
        }

        var created = new Dictionary<string, object>(StringComparer.Ordinal);
        parent[key] = created;
        return created;
    }

    private static CachedSteamLoginAccount? GetActiveLoginAccount(
        SteamPaths paths,
        Dictionary<string, object> loginUsers)
    {
        var accountId = ReadActiveUserAccountId();
        if (!accountId.HasValue)
        {
            return null;
        }

        var steamId = ToSteam64(accountId.Value);
        var accountName = FindAccountNameBySteamId(loginUsers, steamId) ??
            FindAccountNameBySteamId(Path.Combine(paths.ConfigPath, "config.vdf"), steamId) ??
            ReadSteamRegistryString("AutoLoginUser");

        if (string.IsNullOrWhiteSpace(accountName))
        {
            AppLog.Warn($"找到活动 Steam 用户 {steamId}，但无法解析其账户名。");
            return null;
        }

        return new CachedSteamLoginAccount
        {
            AccountName = accountName,
            SteamId = steamId,
            CachedAt = DateTimeOffset.Now
        };
    }

    // 在 EYA 登录覆盖 local.vdf 之前，把每个账号的 ConnectCache 令牌（crc32(账户名)+"1"）抓出来随账号缓存，
    // 恢复时写回 local.vdf 才能让 Steam 免密自动登录。读不到（账号已被 Steam 忘记/令牌已轮换）时保持 null，
    // 恢复退化为仅预选账号。local.vdf 解析失败时 GetPath 返回 null，整体跳过，best-effort。
    private static void PopulateConnectCacheTokens(
        SteamPaths paths,
        IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return;
        }

        var connectCache = GetPath(
            VdfDocument.LoadOrEmpty(paths.LocalVdfPath),
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");
        if (connectCache is null)
        {
            return;
        }

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.AccountName))
            {
                continue;
            }

            var token = GetString(connectCache, Crc32.ComputeSteamAccountKey(account.AccountName));
            if (!string.IsNullOrWhiteSpace(token))
            {
                account.ConnectCacheToken = token;
            }
        }
    }

    // internal：SteamAccountNameService 离线补全 CS2 来源账号显示名时复用同一份解析。
    internal static IEnumerable<CachedSteamLoginAccount> GetLoginUsersAccounts(
        Dictionary<string, object> loginUsers)
    {
        if (!TryGetUsers(loginUsers, out var users))
        {
            yield break;
        }

        foreach (var (steamId, value) in users)
        {
            if (value is not Dictionary<string, object> user)
            {
                continue;
            }

            var accountName = GetString(user, "AccountName");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            yield return new CachedSteamLoginAccount
            {
                AccountName = accountName,
                SteamId = steamId,
                PersonaName = GetString(user, "PersonaName"),
                CachedAt = DateTimeOffset.Now
            };
        }
    }

    // internal：同 GetLoginUsersAccounts，供 SteamAccountNameService 复用。
    internal static IEnumerable<CachedSteamLoginAccount> GetConfigAccounts(string configPath)
    {
        var config = VdfDocument.LoadOrEmpty(configPath);
        var steam = GetPath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam is null || VdfDocument.GetObject(steam, "Accounts") is not { } accounts)
        {
            yield break;
        }

        foreach (var (accountName, value) in accounts)
        {
            if (value is not Dictionary<string, object> account)
            {
                continue;
            }

            var steamId = GetString(account, "SteamID");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            yield return new CachedSteamLoginAccount
            {
                AccountName = accountName,
                SteamId = steamId,
                CachedAt = DateTimeOffset.Now
            };
        }
    }

    private static IReadOnlyList<CachedSteamLoginAccount> NormalizeLoginAccounts(
        IEnumerable<CachedSteamLoginAccount> accounts)
    {
        return accounts
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.AccountName) &&
                !string.IsNullOrWhiteSpace(account.SteamId))
            .GroupBy(account => account.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryGetUsers(
        Dictionary<string, object> loginUsers,
        out Dictionary<string, object> users)
    {
        if (VdfDocument.GetObject(loginUsers, "users") is { } existingUsers)
        {
            users = existingUsers;
            return true;
        }

        users = [];
        return false;
    }

    private static string? GetString(Dictionary<string, object> values, string key)
    {
        return VdfDocument.GetValue(values, key)?.ToString();
    }

    private static uint? ReadActiveUserAccountId()
    {
        return ReadSteamRegistryUInt32(@"Software\Valve\Steam\ActiveProcess", "ActiveUser") ??
            ReadSteamRegistryUInt32(@"Software\Valve\Steam", "ActiveUser");
    }

    private static string ToSteam64(uint accountId)
    {
        const ulong individualAccountUniverseBase = 76561197960265728UL;
        return (individualAccountUniverseBase + accountId).ToString(CultureInfo.InvariantCulture);
    }

    private static string? FindAccountNameBySteamId(
        Dictionary<string, object> loginUsers,
        string steamId)
    {
        if (!TryGetUsers(loginUsers, out var users) ||
            !users.TryGetValue(steamId, out var value) ||
            value is not Dictionary<string, object> user)
        {
            return null;
        }

        return GetString(user, "AccountName");
    }

    private static string? FindAccountNameBySteamId(string configPath, string steamId)
    {
        var config = VdfDocument.LoadOrEmpty(configPath);
        var steam = GetPath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam is null || VdfDocument.GetObject(steam, "Accounts") is not { } accounts)
        {
            return null;
        }

        foreach (var (accountName, value) in accounts)
        {
            if (value is Dictionary<string, object> account &&
                string.Equals(GetString(account, "SteamID"), steamId, StringComparison.OrdinalIgnoreCase))
            {
                return accountName;
            }
        }

        return null;
    }

    private static Dictionary<string, object>? GetPath(
        Dictionary<string, object> root,
        params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            if (VdfDocument.GetObject(current, key) is not { } child)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    private static uint? ReadSteamRegistryUInt32(string keyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(keyPath);
            var value = key?.GetValue(valueName);
            return value switch
            {
                int intValue when intValue > 0 => unchecked((uint)intValue),
                uint uintValue when uintValue > 0 => uintValue,
                long longValue when longValue is > 0 and <= uint.MaxValue => (uint)longValue,
                string stringValue when uint.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSteamRegistryString(string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
