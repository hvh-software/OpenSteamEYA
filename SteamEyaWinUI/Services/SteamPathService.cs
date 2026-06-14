using System.Diagnostics;
using Microsoft.Win32;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamPathService
{
    /// <summary>
    /// 自动探测 Steam 安装目录：按候选源顺序返回第一个确实含 steam.exe 的目录；都找不到则返回 null
    /// （不再退回「存在但没有 steam.exe」的残留目录——那只会把清晰的失败拖到启动 steam.exe 时才暴露）。
    /// 解析/缓存/失败弹框的总编排见 <see cref="SteamPathCoordinator"/>。
    /// </summary>
    public string? AutoDetectInstallPath()
    {
        AppLog.Info("开始自动探测 Steam 安装目录。");

        foreach (var (source, candidate) in EnumerateInstallPathCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                AppLog.Info($"  候选[{source}]：(空)");
                continue;
            }

            var directory = candidate.Trim().TrimEnd('\\', '/');
            var hasExe = ContainsSteamExe(directory);
            AppLog.Info($"  候选[{source}]：\"{directory}\" 含steam.exe={hasExe}");

            if (hasExe)
            {
                AppLog.Info($"自动探测选定 Steam 安装目录：\"{directory}\"");
                return directory;
            }
        }

        AppLog.Warn("未能自动探测到含 steam.exe 的 Steam 安装目录。");
        return null;
    }

    /// <summary>目录是否是有效的 Steam 安装根目录（存在且含 steam.exe）。空路径或异常一律视为否。</summary>
    public static bool ContainsSteamExe(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(directory.Trim().TrimEnd('\\', '/'), "steam.exe"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把路径规整成磁盘上的真实大小写 + 反斜杠。HKCU\Software\Valve\Steam\SteamPath 是
    /// 全小写正斜杠（如 c:/program files (x86)/steam），直接显示很别扭。逐段用目录枚举取真实大小写；
    /// 取不到（段不存在/无权限）则至少修正分隔符并大写盘符。Windows 路径大小写不敏感，仅为美观/一致。
    /// </summary>
    public static string NormalizeInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = FixSeparators(path);
        try
        {
            var root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root))
            {
                return normalized;
            }

            var current = root.ToUpperInvariant(); // 盘符大写：c:\ → C:\
            foreach (var segment in normalized[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var realName = segment;
                try
                {
                    var matches = new DirectoryInfo(current).GetFileSystemInfos(segment);
                    if (matches.Length > 0)
                    {
                        realName = matches[0].Name; // 磁盘上的真实大小写
                    }
                }
                catch
                {
                    // 段不存在/无权限/含通配符——保留原样继续。
                }

                current = Path.Combine(current, realName);
            }

            return current.TrimEnd('\\');
        }
        catch
        {
            return normalized;
        }

        static string FixSeparators(string p)
        {
            var trimmed = p.Trim().TrimEnd('\\', '/');
            try
            {
                // GetFullPath 把正斜杠转反斜杠并规整；大小写不变。
                return Path.GetFullPath(trimmed).TrimEnd('\\');
            }
            catch
            {
                return trimmed.Replace('/', '\\');
            }
        }
    }

    /// <summary>由已确定的安装目录构造完整路径集合（config 目录 + local.vdf）。</summary>
    public SteamPaths BuildPaths(string installPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            AppLog.Error("无法获取 LocalAppData 目录。");
            throw new InvalidOperationException(Loc.T("Steam_Error_LocalAppDataNotFound"));
        }

        var normalized = installPath.Trim().TrimEnd('\\', '/');
        var paths = new SteamPaths(
            normalized,
            Path.Combine(localAppData, "Steam", "local.vdf"),
            Path.Combine(normalized, "config"));
        AppLog.Info($"使用 Steam 安装目录=\"{normalized}\"  config 目录=\"{paths.ConfigPath}\"  local.vdf=\"{paths.LocalVdfPath}\"");
        return paths;
    }

    // 候选顺序对齐 SteamEYA_GUI.exe，并补全它没覆盖到但更可靠的来源。
    // HKCU\Software\Valve\Steam\SteamPath 是 Steam 自己每次启动都会写的权威值，
    // 原实现完全没读它，导致 HKLM InstallPath 缺失/过期的用户（免管理员安装、
    // 搬盘后注册表未更新、机器级协议注册在 HKLM 而非 HKCU）找不到 Steam。
    private static IEnumerable<(string Source, string? Path)> EnumerateInstallPathCandidates()
    {
        yield return ("HKCU SteamPath", ReadRegistryString(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamPath"));

        var steamExe = ReadRegistryString(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamExe");
        yield return ("HKCU SteamExe", string.IsNullOrWhiteSpace(steamExe) ? null : Path.GetDirectoryName(steamExe));

        yield return ("ProgramFiles(x86)", GetDefaultInstallPath("ProgramFiles(x86)"));
        yield return ("ProgramFiles", GetDefaultInstallPath("ProgramFiles"));

        yield return ("HKLM64 InstallPath", ReadRegistryString(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath"));
        yield return ("HKLM32 InstallPath", ReadRegistryString(
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            @"SOFTWARE\Valve\Steam",
            "InstallPath"));

        yield return ("运行进程", GetInstallPathFromRunningProcess());

        yield return ("steam协议(HKCU)", GetInstallPathFromProtocolRegistry(RegistryHive.CurrentUser));
        yield return ("steam协议(HKLM)", GetInstallPathFromProtocolRegistry(RegistryHive.LocalMachine));
    }

    private static string? GetDefaultInstallPath(string environmentVariable)
    {
        var root = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "Steam");
    }

    private static string? GetInstallPathFromRunningProcess()
    {
        foreach (var processName in new[] { "steam", "steamwebhelper" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            return Path.GetDirectoryName(fileName);
                        }
                    }
                    catch
                    {
                        // Some processes deny MainModule access; other candidates usually cover this.
                    }
                }
            }
        }

        return null;
    }

    private static string? GetInstallPathFromProtocolRegistry(RegistryHive hive)
    {
        var command = ReadRegistryString(
            hive,
            RegistryView.Default,
            @"Software\Classes\steam\Shell\Open\Command",
            "");

        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var exePath = ExtractExecutablePath(command);
        return string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
    }

    private static string? ReadRegistryString(
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var endQuote = command.IndexOf('"', 1);
            return endQuote > 1 ? command[1..endQuote] : null;
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? command[..(exeIndex + 4)] : null;
    }
}
