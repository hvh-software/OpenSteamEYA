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
        // 对齐 SteamEYA_GUI.exe（sub_140003640）：config.vdf 从零生成、整体覆盖，
        // 绝不读取/合并旧文件。旧实现用 LoadOrEmpty 读出用户原有 config.vdf
        // （常有 20KB+），再经我们手写的 VDF 解析/序列化往返一遍——只要某处结构
        // 往返后被破坏，Steam 启动时读不动 config.vdf 就会把它重置，连带忽略我们
        // 写入 loginusers.vdf/local.vdf 的自动登录，停在登录界面。这正是「上号流程
        // 全部成功、Steam 进程也起来了，却没自动登录」且只在部分机器复现的根因
        // （取决于该机 config.vdf 里有没有我们解析器处理不好的内容）。参考二进制
        // 干脆只写下面这三项最小模板，彻底规避往返破坏。
        var config = new Dictionary<string, object>(StringComparer.Ordinal);
        var steam = EnsurePath(config, "InstallConfigStore", "Software", "Valve", "Steam");

        steam["AutoUpdateWindowEnabled"] = "0";
        steam["MTBF"] = Random.Shared.Next(100000000, 999999999).ToString();

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
