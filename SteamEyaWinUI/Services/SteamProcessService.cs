using System.Diagnostics;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class SteamProcessService
{
    public void EnsureSteamStopped(SteamPaths paths, IProgress<string>? progress)
    {
        if (!IsSteamRunning())
        {
            AppLog.Info("Steam 未在运行，无需关闭。");
            return;
        }

        AppLog.Info("检测到 Steam 正在运行，尝试关闭...");
        progress?.Report("Steam 正在运行，正在关闭...");

        var steamExe = Path.Combine(paths.InstallPath, "steam.exe");
        if (File.Exists(steamExe))
        {
            try
            {
                using var shutdown = Process.Start(new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = false
                }.WithArguments("-shutdown"));

                shutdown?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"调用 steam.exe -shutdown 失败：{ex.Message}");
            }
        }
        else
        {
            AppLog.Warn($"关闭 Steam 时找不到 steam.exe：\"{steamExe}\"");
        }

        for (var i = 0; i < 10; i++)
        {
            if (!IsSteamRunning())
            {
                AppLog.Info("Steam 已退出。");
                return;
            }

            Thread.Sleep(1000);
        }

        AppLog.Warn("Steam 未在 10 秒内退出，强制结束相关进程。");
        progress?.Report("Steam 未及时退出，正在结束相关进程...");
        KillProcesses("steam");
        KillProcesses("steamwebhelper");
    }

    public void LaunchSteamWithLogin(SteamPaths paths, string accountName)
    {
        var steamExe = Path.Combine(paths.InstallPath, "steam.exe");
        if (!File.Exists(steamExe))
        {
            AppLog.Error($"启动失败：找不到 steam.exe：\"{steamExe}\"");
            throw new InvalidOperationException($"没有找到 Steam 可执行文件：{steamExe}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = paths.InstallPath,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-login");
        startInfo.ArgumentList.Add(accountName);

        AppLog.Info($"启动 Steam：\"{steamExe}\" -login {accountName}");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                AppLog.Error("Process.Start 返回 null，系统未创建 Steam 进程。");
                throw new InvalidOperationException("Steam 启动失败（系统未创建进程）。");
            }

            AppLog.Info($"已启动 Steam 进程，PID={process.Id}。");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Win32Exception（被杀软拦截、文件不可执行、权限不足等）原始信息对诊断很关键。
            AppLog.Error($"启动 Steam 抛出异常：\"{steamExe}\"", ex);
            throw new InvalidOperationException($"启动 Steam 失败：{ex.Message}", ex);
        }
    }

    private static bool IsSteamRunning()
    {
        return Process.GetProcessesByName("steam").Length > 0 ||
            Process.GetProcessesByName("steamwebhelper").Length > 0;
    }

    private static void KillProcesses(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
                catch
                {
                    // Best effort. The caller will surface launch/write failures if Steam keeps files locked.
                }
            }
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
