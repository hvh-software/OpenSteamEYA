using System.Diagnostics;
using Microsoft.Win32;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamPathService
{
    public SteamPaths GetSteamPaths()
    {
        AppLog.Info("开始定位 Steam 安装目录。");
        string? installPath = null;
        string? firstExistingPath = null;

        foreach (var (source, candidate) in EnumerateInstallPathCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                AppLog.Info($"  候选[{source}]：(空)");
                continue;
            }

            var directory = candidate.Trim().TrimEnd('\\', '/');
            var exists = Directory.Exists(directory);
            var hasExe = exists && File.Exists(Path.Combine(directory, "steam.exe"));
            AppLog.Info($"  候选[{source}]：\"{directory}\" 存在={exists} 含steam.exe={hasExe}");

            if (!exists)
            {
                continue;
            }

            firstExistingPath ??= directory;

            // 优先选真正含 steam.exe 的目录，避免命中搬迁/重装后残留的空目录。
            if (hasExe)
            {
                installPath = directory;
                break;
            }
        }

        installPath ??= firstExistingPath;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            AppLog.Error("未找到任何存在的 Steam 安装目录（所有候选均不存在）。");
            throw new InvalidOperationException("没有找到 Steam 安装目录。");
        }

        var usedFallback = !File.Exists(Path.Combine(installPath, "steam.exe"));
        AppLog.Info(
            $"选定 Steam 安装目录：\"{installPath}\"{(usedFallback ? "（回退：该目录无 steam.exe）" : string.Empty)}");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            AppLog.Error("无法获取 LocalAppData 目录。");
            throw new InvalidOperationException("没有找到 LocalAppData 目录。");
        }

        var paths = new SteamPaths(
            installPath,
            Path.Combine(localAppData, "Steam", "local.vdf"),
            Path.Combine(installPath, "config"));
        AppLog.Info($"config 目录=\"{paths.ConfigPath}\"  local.vdf=\"{paths.LocalVdfPath}\"");
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
