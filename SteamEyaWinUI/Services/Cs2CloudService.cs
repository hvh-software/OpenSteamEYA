using System.Diagnostics;
using System.Globalization;
using System.IO;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

/// <summary>一份要强推的 CS2 配置文件（云文件名 + 内容字节）。</summary>
internal sealed record Cs2CfgFile(string Name, byte[] Data);

/// <summary>候选「设置来源」账号：其本地 userdata 下存在 CS2 配置。</summary>
internal sealed record Cs2SettingsSource(string SteamId64, uint AccountId);

/// <summary>强推结果：是否成功、写入文件数、失败原因、「该账号未开启账号级 Steam 云」标记，以及部分写入失败的文件数。</summary>
internal sealed record Cs2CloudPushResult(
    bool Ok, int Pushed, string? Error, bool AccountCloudDisabled = false, int PartialFailed = 0);

/// <summary>
/// CS2（AppID 730）设置的「云强推」同步（issue #10）。
///
/// 思路：准星/灵敏度/视角/键位等是 Source2 客户端 convar，跨账号复制走官方 SDK 云强推：
///   1. 从「来源账号」的 userdata/&lt;accountId&gt;/730/<b>remote</b> 读取 cs2_user_*.vcfg——这里是云端文件的本地镜像，
///      文件名即云端文件名（实测：云端为 cs2_user_convars.vcfg / cs2_user_keys.vcfg，与 local/cfg 里的
///      cs2_user_convars_0_slot0.vcfg 名字不同，故必须读 remote 而不是 local/cfg）；
///   2. 目标账号登录后，用 Steamworks 云 API（<see cref="SteamworksNative"/>）以相同文件名 FileWrite 强推到
///      「当前登录账号」的 CS2 云端——由 Steam 负责下发落地，我们不手搓云逻辑。
/// 注意：画面设置 cs2_video.txt 不在云端（属整机本地文件），云同步无法覆盖它。
///
/// Steamworks 调用不在本进程执行：Steam 会把「以 730 身份 SteamAPI_Init 的进程」当作 CS2 游戏进程，
/// 该进程不退出「正在运行」状态就不清除，点「停止」还会被 Steam 强杀。故 ForcePush 只负责把待推文件
/// 落到临时目录，再拉起自身 exe 的短命辅助进程（<see cref="Cs2CloudPushWorker"/>）完成 init/写云/退出。
///
/// 前提：目标账号拥有 CS2、Steam 已登录、运行目录有 steam_api64.dll（见 SteamworksNative）。
/// 全程 best-effort：任何失败只记日志/提示，绝不中断上号主流程。
/// </summary>
internal sealed class Cs2CloudService
{
    private const string Cs2AppFolder = "730";
    // 个人账号 SteamID64 = 该基准 + 32 位 accountId（accountId 即 userdata 目录名）。
    private const ulong SteamId64Base = 76561197960265728UL;

    // 一次只跑一个推送辅助进程：登录后台推送与设置页「立即推送」并发时串行执行，
    // 避免两个 730 身份的 Steamworks 会话同时写同名云文件、结果无法归因。
    private static readonly object PushGate = new();

    // 登录路径推送的代际号（last-writer-wins）：每次新登录推送自增；持锁中的旧推送轮询发现代际已更新
    // 就立即杀掉辅助进程让位。否则连续切号时，旧账号的推送（其目标账号已被登出、注定失败）会白占锁
    // 几十秒，把唯一能成功的最新账号推送饿到超时放弃。「立即推送」不参与代际抢占。
    private static long _loginPushGeneration;

    /// <summary>枚举「已上云过 CS2 设置」的候选来源账号（remote 下有 cs2_user_*.vcfg），供设置页来源下拉。</summary>
    public IReadOnlyList<Cs2SettingsSource> EnumerateSources(string? userdataPath)
    {
        var result = new List<Cs2SettingsSource>();
        if (string.IsNullOrWhiteSpace(userdataPath) || !Directory.Exists(userdataPath))
        {
            return result;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(userdataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"枚举 userdata 目录失败：{userdataPath}，{ex.Message}");
            return result;
        }

        foreach (var dir in dirs)
        {
            if (!uint.TryParse(Path.GetFileName(dir), out var accountId) || accountId == 0)
            {
                continue;
            }

            if (!HasCs2Config(RemoteDir(userdataPath, accountId)))
            {
                continue;
            }

            var steamId64 = (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);
            result.Add(new Cs2SettingsSource(steamId64, accountId));
        }

        return result;
    }

    /// <summary>
    /// 读取来源账号 remote 下的云端 CS2 设置文件（cs2_user_convars.vcfg / cs2_user_keys.vcfg）作为待强推内容；
    /// 文件名即云端名，直接原样 FileWrite 即可。无来源/无配置返回空。
    /// </summary>
    public IReadOnlyList<Cs2CfgFile> ReadSourceCfgFiles(string userdataPath, string? sourceSteamId64)
    {
        if (string.IsNullOrWhiteSpace(sourceSteamId64) || !TryAccountId(sourceSteamId64, out var accountId))
        {
            return Array.Empty<Cs2CfgFile>();
        }

        var remoteDir = RemoteDir(userdataPath, accountId);
        if (!Directory.Exists(remoteDir))
        {
            return Array.Empty<Cs2CfgFile>();
        }

        var result = new List<Cs2CfgFile>();
        try
        {
            // 只取用户设置（convars=准星/灵敏度/视角等、keys=键位）；跳过 socache.dt/voice_ban.dt 等账号级状态。
            foreach (var path in Directory.GetFiles(remoteDir, "cs2_user_*.vcfg"))
            {
                try
                {
                    result.Add(new Cs2CfgFile(Path.GetFileName(path), File.ReadAllBytes(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"读取来源 CS2 云文件失败：{path}，{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"枚举来源 CS2 remote 目录失败：{remoteDir}，{ex.Message}");
        }

        return result;
    }

    /// <summary>登录时调用（放后台线程）：把来源账号配置强推到刚登录的目标账号云端。全程吞异常。</summary>
    public void PushSourceForLogin(
        SteamPaths paths, string? sourceSteamId64, string targetSteamId64, IProgress<string>? progress)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSteamId64) ||
                !TryAccountId(sourceSteamId64, out var sourceId) ||
                !TryAccountId(targetSteamId64, out var targetId) ||
                sourceId == targetId)
            {
                return;
            }

            var files = ReadSourceCfgFiles(paths.UserdataPath, sourceSteamId64);
            if (files.Count == 0)
            {
                return;
            }

            progress?.Report(Loc.T("Cs2Cloud_Progress_Pushing"));
            // 登录后 Steam 需数秒才登好，ForcePush 内部会重试等待；
            // 传入目标 SteamID：写云前核对当前登录账号确实是它，登错/登成别的账号时宁可放弃也不覆盖别人云端。
            var result = ForcePush(files, maxWaitSeconds: 40, expectedSteamId64: targetSteamId64);
            progress?.Report(DescribeResult(result));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CS2 云推送（登录时）失败，忽略：{ex.Message}");
        }
    }

    /// <summary>设置页「立即推送」：把来源账号配置强推到当前登录账号云端（假设 Steam 已在运行/登录）。</summary>
    public Cs2CloudPushResult PushSourceNow(SteamPaths paths, string? sourceSteamId64)
    {
        if (string.IsNullOrWhiteSpace(sourceSteamId64))
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSourceSelected"));
        }

        var files = ReadSourceCfgFiles(paths.UserdataPath, sourceSteamId64);
        if (files.Count == 0)
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSource"));
        }

        return ForcePush(files, maxWaitSeconds: 4);
    }

    /// <summary>把推送结果转成用户可见文案（区分成功 / 成功但账号云关闭 / 失败）。</summary>
    public static string DescribeResult(Cs2CloudPushResult result)
    {
        if (!result.Ok)
        {
            return Loc.Tf("Cs2Cloud_Progress_Failed_Format", result.Error ?? string.Empty);
        }

        if (result.PartialFailed > 0)
        {
            return Loc.Tf("Cs2Cloud_Progress_Partial_Format", result.Pushed, result.Pushed + result.PartialFailed);
        }

        return result.AccountCloudDisabled
            ? Loc.Tf("Cs2Cloud_Progress_DoneNoCloud_Format", result.Pushed)
            : Loc.Tf("Cs2Cloud_Progress_Done_Format", result.Pushed);
    }

    /// <summary>
    /// 把给定文件强推到「当前登录账号」的 CS2(730) 云端：文件落到临时目录后交给短命辅助进程执行
    /// （Steamworks init 必须在会立即退出的进程里做，否则 Steam 一直显示 CS2「正在运行」，见类注释）。
    /// <paramref name="maxWaitSeconds"/> 内辅助进程以 2s 间隔重试 SteamAPI_Init（等 Steam 登录就绪）。
    /// <paramref name="expectedSteamId64"/> 非空时，写云前核对当前登录账号确为该 SteamID，
    /// 不符则视为「还没登到目标账号」继续等待，超时也绝不写到别的账号（防覆盖他人云端设置）。
    /// 「立即推送」传 null，语义即推给当前登录的任意账号。
    /// </summary>
    public Cs2CloudPushResult ForcePush(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, string? expectedSteamId64 = null)
    {
        if (files.Count == 0)
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSource"));
        }

        // 登录路径（带目标账号）先领代际号：持锁中的旧登录推送会在 ~1s 内察觉并让位，
        // 因此下面的 TryEnter 对最新登录推送几乎必然成功。
        long? generation = null;
        if (!string.IsNullOrWhiteSpace(expectedSteamId64))
        {
            generation = Interlocked.Increment(ref _loginPushGeneration);
        }

        // 限时抢锁而非无限期排队：登录后台推送可能持锁几十秒（辅助进程要等 Steam 登录就绪），
        // 「立即推送」若在此期间无限期阻塞会表现为分钟级无反馈假死。抢不到就明确告知用户稍后再试。
        if (!Monitor.TryEnter(PushGate, TimeSpan.FromSeconds(maxWaitSeconds)))
        {
            AppLog.Warn("另一次 CS2 云推送正在进行，本次放弃等待。");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_PushBusy"));
        }

        try
        {
            return RunPushHelper(files, maxWaitSeconds, expectedSteamId64, generation);
        }
        finally
        {
            Monitor.Exit(PushGate);
        }
    }

    // 本推送是否已被更新的登录推送取代（generation=null 表示不参与抢占）。
    private static bool IsSuperseded(long? generation) =>
        generation is { } gen && Interlocked.Read(ref _loginPushGeneration) != gen;

    // 写 payload → 拉起自身 exe 的辅助进程 → 等其退出 → 解析 result.txt。临时目录 finally 里清理。
    private static Cs2CloudPushResult RunPushHelper(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, string? expectedSteamId64, long? generation)
    {
        // 排队等锁期间已有更新的登录到来：本推送的目标账号注定登不上，直接让位不再拉进程。
        if (IsSuperseded(generation))
        {
            AppLog.Info("CS2 云推送已被更新的登录取代（未启动辅助进程）。");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_Superseded"));
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            AppLog.Warn("拿不到当前进程 exe 路径，无法启动 CS2 云推送辅助进程。");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperLaunchFailed"));
        }

        // GUI 在推送期间被关闭时（登录推送是后台 Task，finally 不保证执行），payload 目录会孤儿化，
        // 这里顺手清扫历史残留。1 小时阈值远超辅助进程约 70s 的生命周期，不会误删另一实例的活目录。
        SweepStalePayloadDirs();

        var payloadDir = Path.Combine(Path.GetTempPath(), "SteamEYA", "cs2push-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(payloadDir);
            foreach (var file in files)
            {
                // Name 来自来源目录枚举、理应是纯文件名；再过一遍 GetFileName 防路径拼接意外。
                File.WriteAllBytes(Path.Combine(payloadDir, Path.GetFileName(file.Name)), file.Data);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // steam_appid.txt 兜底按工作目录解析，故辅助进程工作目录固定为 exe 目录。
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Cs2CloudPushWorker.CommandLineSwitch);
            startInfo.ArgumentList.Add(payloadDir);
            startInfo.ArgumentList.Add(maxWaitSeconds.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(expectedSteamId64)
                ? Cs2CloudPushWorker.NoExpectedSteamIdToken
                : expectedSteamId64.Trim());

            AppLog.Info($"启动 CS2 云推送辅助进程：{files.Count} 个文件，maxWait={maxWaitSeconds}s。");
            using var helper = Process.Start(startInfo);
            if (helper is null)
            {
                AppLog.Warn("Process.Start 返回 null，系统未创建 CS2 云推送辅助进程。");
                return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperLaunchFailed"));
            }

            // 1s 步长轮询等待：辅助进程自带 maxWaitSeconds 的重试等待，这里再给退出与写结果留余量；
            // 期间发现已有更新的登录推送（代际号变化）就立即杀掉本辅助进程让位——它的目标账号已被登出，
            // 继续等只会白占锁把最新推送饿死。
            var deadline = Environment.TickCount64 + (maxWaitSeconds + 30) * 1000L;
            while (!helper.WaitForExit(1000))
            {
                if (IsSuperseded(generation))
                {
                    AppLog.Info("已有更新的登录推送到来，终止本次 CS2 云推送辅助进程让位。");
                    TryKillHelper(helper);
                    return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_Superseded"));
                }

                if (Environment.TickCount64 >= deadline)
                {
                    AppLog.Warn("CS2 云推送辅助进程超时未退出，强制结束。");
                    TryKillHelper(helper);
                    return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperTimeout"));
                }
            }

            return ReadHelperResult(Path.Combine(payloadDir, Cs2CloudPushWorker.ResultFileName));
        }
        catch (Exception ex)
        {
            AppLog.Error("调度 CS2 云推送辅助进程失败。", ex);
            return new Cs2CloudPushResult(false, 0, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(payloadDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                AppLog.Warn($"清理 CS2 云推送临时目录失败：{payloadDir}，{ex.Message}");
            }
        }
    }

    // 删除 %TEMP%\SteamEYA 下创建时间超过 1 小时的 cs2push-* 残留目录。全程 best-effort。
    private static void SweepStalePayloadDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "SteamEYA");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(root, "cs2push-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                    {
                        Directory.Delete(dir, recursive: true);
                        AppLog.Info($"已清扫残留 CS2 推送临时目录：{dir}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"清扫残留 CS2 推送临时目录失败：{dir}，{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"枚举 CS2 推送临时目录失败：{ex.Message}");
        }
    }

    private static void TryKillHelper(Process helper)
    {
        try
        {
            helper.Kill(entireProcessTree: true);
            helper.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"结束 CS2 云推送辅助进程失败：{ex.Message}");
        }
    }

    // 解析辅助进程写回的 key=value 结果文件；errorKey 在此翻译成本地化文案（辅助进程不做本地化）。
    private static Cs2CloudPushResult ReadHelperResult(string resultPath)
    {
        string[] lines;
        try
        {
            if (!File.Exists(resultPath))
            {
                AppLog.Warn("CS2 云推送辅助进程已退出但未留下结果文件（可能崩溃或被安全软件拦截）。");
                return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperNoResult"));
            }

            lines = File.ReadAllLines(resultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"读取 CS2 云推送结果文件失败：{ex.Message}");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperNoResult"));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart('\uFEFF'); // 容错 BOM，避免首行键名带上不可见字符。
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        var ok = values.GetValueOrDefault("ok") == "1";
        _ = int.TryParse(
            values.GetValueOrDefault("pushed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pushed);
        _ = int.TryParse(
            values.GetValueOrDefault("partialFailed"), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var partialFailed);
        var accountCloudDisabled = values.GetValueOrDefault("accountCloudDisabled") == "1";

        string? error = null;
        if (!ok)
        {
            var errorKey = values.GetValueOrDefault("errorKey");
            error = !string.IsNullOrWhiteSpace(errorKey)
                ? Loc.T(errorKey)
                : values.GetValueOrDefault("errorText") is { Length: > 0 } text
                    ? text
                    : Loc.T("Cs2Cloud_Error_HelperNoResult");
        }

        return new Cs2CloudPushResult(ok, pushed, error, accountCloudDisabled, partialFailed);
    }

    // 账号的 CS2 云文件本地镜像目录：userdata/<accountId>/730/remote（文件名即云端名）。
    private static string RemoteDir(string userdataPath, uint accountId) => Path.Combine(
        userdataPath, accountId.ToString(CultureInfo.InvariantCulture), Cs2AppFolder, "remote");

    private static bool HasCs2Config(string remoteDir)
    {
        try
        {
            return Directory.Exists(remoteDir) && Directory.EnumerateFiles(remoteDir, "cs2_user_*.vcfg").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // userdata 目录名 = SteamID64 低 32 位（accountId）。
    private static bool TryAccountId(string steamId64, out uint accountId)
    {
        accountId = 0;
        if (!ulong.TryParse(steamId64.Trim(), out var id))
        {
            return false;
        }

        accountId = (uint)(id & 0xFFFFFFFF);
        return accountId != 0;
    }

}
