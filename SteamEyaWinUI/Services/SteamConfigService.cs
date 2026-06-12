using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamConfigService
{
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
        var config = VdfDocument.LoadOrEmpty(path);
        var steam = EnsurePath(config, "InstallConfigStore", "Software", "Valve", "Steam");

        steam["AutoUpdateWindowEnabled"] = "0";
        steam["ipv6check_http_state"] = "bad";
        steam["ipv6check_udp_state"] = "bad";
        steam["ShaderCacheManager"] = new Dictionary<string, object>
        {
            ["HasCurrentBucket"] = "1",
            ["CurrentBucketGPU"] = "b4799b250d4196b0;36174e7cc31a08f9",
            ["CurrentBucketDriver"] = "W2:c18b09d9c69329b41cdbbf3de627bc9f;W2:ee32edf67d134b7cc2ec0cdecbd00037"
        };
        steam["RecentWebSocket443Failures"] = "";
        steam["RecentWebSocketNon443Failures"] = "";
        steam["RecentUDPFailures"] = "";
        steam["RecentTCPFailures"] = "";
        steam["CellIDServerOverride"] = "170";
        steam["MTBF"] = Random.Shared.Next(100000000, 999999999).ToString();
        steam["cip"] = "02000000507a6c24d6e96c6b00004021a356";
        steam["SurveyDate"] = "2017-10-22";
        steam["SurveyDateVersion"] = "-1724767764117155760";
        steam["SurveyDateType"] = "3";
        steam["Rate"] = "30000";

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
}
