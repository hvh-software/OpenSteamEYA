using System.Globalization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class CsPremierScoreService
{
    private const uint Cs2AppId = 730;
    private const uint ClientHello = 4006;
    private const uint ClientWelcome = 4004;
    private const uint ClientRequestPlayersProfile = 9127;
    private const uint PlayersProfile = 9128;
    private const uint MatchmakingClient2GCHello = 9109;
    private const uint MatchmakingGC2ClientHello = 9110;
    private const uint PremierRankTypeId = 11;
    private const uint CsClientVersion = 2_000_244;

    // 9110 可能在 GC welcome 前后主动下发；等待必须覆盖握手窗口，接收层也会缓存早到消息。
    // 仍保留“断开 GC 再重连”循环触发（与 cooldown.js 一致：6 轮、每轮等 11 秒、
    // 断开后 2.5 秒再重连），另设总时限兜底防止单轮 GC welcome 重试拖长整体耗时。
    private const int MaxHelloCycles = 6;
    private static readonly TimeSpan HelloWaitTimeout = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan CachedHelloPollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GcReconnectDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan HelloTotalBudget = TimeSpan.FromSeconds(100);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<CsPremierScoreResult> QueryAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
        {
            throw new InvalidOperationException("Steam64 格式不正确，无法查询优先分。");
        }

        var accountId = GetAccountId(steamId64);
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);

        try
        {
            var helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);
            await cmClient.SetGamesPlayedAsync([Cs2AppId], cancellationToken);
            await ConnectToGameCoordinatorAsync(cmClient, cancellationToken);

            // 冷却/VAC 只能从 GC 的 MatchmakingGC2ClientHello(9110) 拿：PlayersProfile 对自己
            // 账号的 penalty 字段永远为空。9110 waiter 已在进 730 前挂好，避免 welcome 阶段
            // 主动下发的 9110 被错过；这里再发 9109 请求，随后照常请求 PlayersProfile 取优先分/等级。
            await cmClient.SendGcProtobufMessageAsync(
                Cs2AppId,
                MatchmakingClient2GCHello,
                [],
                cancellationToken);

            var profileTask = cmClient.WaitForGcMessageAsync(
                Cs2AppId,
                PlayersProfile,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            await cmClient.SendGcProtobufMessageAsync(
                Cs2AppId,
                ClientRequestPlayersProfile,
                EncodePlayersProfileRequest(accountId),
                cancellationToken);

            var profileMessage = await profileTask;
            var profile = DecodePlayersProfile(accountId, profileMessage.Payload);

            var helloData = await helloTask;
            helloData ??= await WaitForMatchmakingHelloAsync(
                cmClient,
                CachedHelloPollTimeout,
                cancellationToken);
            var helloDeadline = DateTimeOffset.UtcNow + HelloTotalBudget;

            for (var cycle = 2;
                helloData is null && cycle <= MaxHelloCycles && DateTimeOffset.UtcNow < helloDeadline;
                cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.SetGamesPlayedAsync([], cancellationToken);
                    await Task.Delay(GcReconnectDelay, cancellationToken);
                    helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);
                    await cmClient.SetGamesPlayedAsync([Cs2AppId], cancellationToken);
                    await ConnectToGameCoordinatorAsync(cmClient, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // GC 重连失败：优先分已拿到，冷却按未知返回。
                    break;
                }

                await cmClient.SendGcProtobufMessageAsync(
                    Cs2AppId,
                    MatchmakingClient2GCHello,
                    [],
                    cancellationToken);
                helloData = await helloTask;
                helloData ??= await WaitForMatchmakingHelloAsync(
                    cmClient,
                    CachedHelloPollTimeout,
                    cancellationToken);
            }

            var premier = profile.Rankings.FirstOrDefault(ranking =>
                ranking.RankTypeId == PremierRankTypeId);

            return new CsPremierScoreResult(
                steamId,
                accountId,
                premier,
                profile.Rankings,
                helloData?.PenaltySeconds,
                helloData?.PenaltyReason,
                helloData?.VacBanned,
                profile.PlayerLevel,
                profile.InMatch);
        }
        finally
        {
            try
            {
                await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
            }
            catch
            {
                // Best-effort cleanup before logoff.
            }
        }
    }

    /// <summary>等待一轮 9110；超时返回 null（由调用方断开 GC 重连再试）。9110 与 PlayersProfile
    /// 的账号条目是同一个 proto 消息类型，直接复用 DecodeAccountProfile。</summary>
    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        return await WaitForMatchmakingHelloAsync(cmClient, HelloWaitTimeout, cancellationToken);
    }

    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await cmClient.WaitForGcMessageAsync(
                Cs2AppId,
                MatchmakingGC2ClientHello,
                timeout,
                cancellationToken,
                cacheUnmatched: true);
            return DecodeAccountProfile(message.Payload);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task ConnectToGameCoordinatorAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var welcomeTask = cmClient.WaitForGcMessageAsync(
                Cs2AppId,
                ClientWelcome,
                TimeSpan.FromSeconds(4),
                cancellationToken);

            await cmClient.SendGcProtobufMessageAsync(
                Cs2AppId,
                ClientHello,
                EncodeClientHello(),
                cancellationToken);

            try
            {
                await welcomeTask;
                return;
            }
            catch (TimeoutException)
            {
                // The GC often needs more than one hello after launching CS2.
            }
        }

        throw new TimeoutException("连接 CS2 Game Coordinator 超时，暂时无法查询优先分。");
    }

    private static byte[] EncodeClientHello()
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, CsClientVersion);
            writer.WriteUInt32(3, 0);
            writer.WriteUInt32(4, 0);
            writer.WriteUInt32(9, 0);
        });
    }

    private static byte[] EncodePlayersProfileRequest(uint accountId)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(3, accountId);
            writer.WriteUInt32(4, 32);
        });
    }

    private static CsAccountProfile DecodePlayersProfile(uint requestedAccountId, byte[] body)
    {
        var profiles = new List<CsAccountProfile>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    profiles.Add(DecodeAccountProfile(reader.ReadLengthDelimited(wireType)));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        var profile = profiles.FirstOrDefault(value => value.AccountId == requestedAccountId)
            ?? profiles.FirstOrDefault();

        if (profile is null)
        {
            throw new InvalidOperationException("CS2 GC 没有返回账号 Profile。");
        }

        return profile;
    }

    private static CsAccountProfile DecodeAccountProfile(byte[] body)
    {
        uint accountId = 0;
        uint penaltySeconds = 0;
        uint penaltyReason = 0;
        var vacBanned = 0;
        int? playerLevel = null;
        var inMatch = false;
        var rankings = new List<CsRankingInfo>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    inMatch = true;
                    reader.Skip(wireType);
                    break;

                case 4:
                    penaltySeconds = (uint)reader.ReadVarint(wireType);
                    break;

                case 5:
                    penaltyReason = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    vacBanned = (int)reader.ReadVarint(wireType);
                    break;

                case 7:
                case 20:
                    rankings.Add(DecodeRanking(reader.ReadLengthDelimited(wireType)));
                    break;

                case 17:
                    playerLevel = (int)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsAccountProfile(
            accountId,
            rankings,
            penaltySeconds,
            penaltyReason,
            vacBanned,
            playerLevel,
            inMatch);
    }

    private static CsRankingInfo DecodeRanking(byte[] body)
    {
        uint rankTypeId = 0;
        uint rankId = 0;
        uint wins = 0;
        uint? mapId = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    rankId = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    wins = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    rankTypeId = (uint)reader.ReadVarint(wireType);
                    break;

                case 13:
                    mapId ??= DecodePerMapRankMapId(reader.ReadLengthDelimited(wireType));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsRankingInfo(rankTypeId, rankId, wins, mapId);
    }

    private static uint? DecodePerMapRankMapId(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return (uint)reader.ReadVarint(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private static uint GetAccountId(ulong steamId64)
    {
        return (uint)(steamId64 & 0xFFFFFFFF);
    }

    private sealed record CsAccountProfile(
        uint AccountId,
        IReadOnlyList<CsRankingInfo> Rankings,
        uint PenaltySeconds,
        uint PenaltyReason,
        int VacBanned,
        int? PlayerLevel,
        bool InMatch);
}
