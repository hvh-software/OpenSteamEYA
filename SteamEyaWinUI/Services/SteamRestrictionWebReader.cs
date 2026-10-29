using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SteamEyaWinUI.Services;

/// <summary>网页兜底读到的限制状态。字段语义与 GC 一致：null = 未知，0 = 确认无，正数 = 剩余秒数。</summary>
internal sealed record SteamRestrictionSnapshot(uint? CooldownSeconds, bool? VacBanned);

/// <summary>
/// 冷却 / VAC 的网页兜底读取器：GC 的 MatchmakingGC2ClientHello(9110) 没答时，用已登录 CM 会话换出的
/// <see cref="SteamWebSession"/> 去 help.steampowered.com 取：
/// <list type="bullet">
/// <item>冷却页 /zh-cn/wizard/HelpWithGameIssue/?appid=730&amp;issueid=131</item>
/// <item>VAC 页 /zh-cn/wizard/VacBans</item>
/// </list>
/// 三条设计约束，正是「一路失败两列一起卡在待查询」的成因，必须同时满足：
/// <list type="number">
/// <item>两页互不短路：冷却页读不到只让冷却未知，VAC 页照读（反之亦然）。</item>
/// <item>被重定向到 /login 说明 Web 会话没生效，不能当成「确认无限制」；此时重建一次会话重试。</item>
/// <item>「确认无」必须有页面证据（冷却区块存在但没有到期时间 / VAC 页正常打开），读不到一律判未知。</item>
/// </list>
/// </summary>
internal static class SteamRestrictionWebReader
{
    private const string CooldownUrl =
        "https://help.steampowered.com/zh-cn/wizard/HelpWithGameIssue/?appid=730&issueid=131";
    private const string VacUrl =
        "https://help.steampowered.com/zh-cn/wizard/VacBans";

    // 冷却页里包住「冷却到期时间」的那一段；没有到期时间但有这个区块 = 明确无活跃冷却。
    private const string CooldownSectionMarker = "help_game_cooldown";
    private const string CooldownExpirationMarker = "help_game_cooldown_expirationtime\">";
    private const int MaxRedirects = 6;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        // 手动跟 3xx：必须知道最终落在 help 页还是 /login，才能判断会话是否真的生效。
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// 读一次冷却 + VAC。全部失败时返回 (null, null)，由调用方继续保持未知（不写脏数据）。
    /// <paramref name="refreshSession"/> 允许在会话失效时重建一次；传 null 表示不重试。
    /// </summary>
    public static async Task<SteamRestrictionSnapshot> ReadAsync(
        SteamWebSession session,
        Func<CancellationToken, Task<SteamWebSession>>? refreshSession,
        CancellationToken cancellationToken)
    {
        var cooldown = await ReadCooldownAsync(session, cancellationToken);
        var vac = await ReadVacAsync(session, cancellationToken);

        if ((cooldown.NeedAuthenticatedSession || vac.NeedAuthenticatedSession) && refreshSession is not null)
        {
            AppLog.Warn("网页兜底：help.steampowered.com 会话未生效（被导向登录页），重建 Web 会话后重试一次。");
            try
            {
                var fresh = await refreshSession(cancellationToken);
                if (cooldown.NeedAuthenticatedSession)
                {
                    cooldown = await ReadCooldownAsync(fresh, cancellationToken);
                }

                if (vac.NeedAuthenticatedSession)
                {
                    vac = await ReadVacAsync(fresh, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"网页兜底：重建 Web 会话失败，保持未知：{ex.Message}");
            }
        }

        return new SteamRestrictionSnapshot(cooldown.Seconds, vac.VacBanned);
    }

    private readonly record struct CooldownProbe(uint? Seconds, bool NeedAuthenticatedSession);

    private readonly record struct VacProbe(bool? VacBanned, bool NeedAuthenticatedSession);

    private static async Task<CooldownProbe> ReadCooldownAsync(
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var (html, finalUri) = await GetHtmlAsync(CooldownUrl, session, cancellationToken);
            if (!IsAuthenticatedHelpPage(finalUri))
            {
                AppLog.Warn($"网页兜底：冷却页被重定向到 {finalUri.AbsoluteUri}，会话未生效。");
                return new CooldownProbe(null, true);
            }

            if (!html.Contains(CooldownSectionMarker, StringComparison.OrdinalIgnoreCase))
            {
                // 页面能打开但找不到冷却区块：多半是 Steam 改了文案/结构，判未知而不是「无冷却」。
                AppLog.Warn("网页兜底：冷却页已认证但未找到冷却区块，保持冷却未知。");
                return new CooldownProbe(null, false);
            }

            var endText = ExtractCooldownExpiration(html);
            if (string.IsNullOrWhiteSpace(endText))
            {
                // 已认证 + 冷却区块存在 + 没有到期时间：明确无活跃冷却，不能继续当未知。
                return new CooldownProbe(0, false);
            }

            var endsAt = ParseCooldownEnd(html, endText);
            if (!endsAt.HasValue)
            {
                AppLog.Warn($"网页兜底：冷却到期时间“{endText}”解析失败（页面未给出 g_ServerTime），保持未知。");
                return new CooldownProbe(null, false);
            }

            var remainingSeconds = (endsAt.Value - DateTimeOffset.Now).TotalSeconds;
            return new CooldownProbe(remainingSeconds > 0 ? (uint)Math.Ceiling(remainingSeconds) : 0, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"网页兜底：冷却页读取失败，保持未知：{ex.Message}");
            return new CooldownProbe(null, false);
        }
    }

    private static async Task<VacProbe> ReadVacAsync(
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var (html, finalUri) = await GetHtmlAsync(VacUrl, session, cancellationToken);
            if (!IsAuthenticatedHelpPage(finalUri))
            {
                AppLog.Warn($"网页兜底：VAC 页被重定向到 {finalUri.AbsoluteUri}，会话未生效。");
                return new VacProbe(null, true);
            }

            if (IsRedLockPermanentVac(html))
            {
                return new VacProbe(true, false);
            }

            if (html.Contains("Counter-Strike 2", StringComparison.OrdinalIgnoreCase))
            {
                return new VacProbe(true, false);
            }

            if (!html.Contains("VAC", StringComparison.OrdinalIgnoreCase))
            {
                // 连 VAC 字样都没有，说明这不是 VAC 向导页（登录墙/维护页），不能判「无标记」。
                AppLog.Warn("网页兜底：VAC 页已认证但内容不像 VAC 向导，保持 VAC 未知。");
                return new VacProbe(null, false);
            }

            return new VacProbe(false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"网页兜底：VAC 页读取失败，保持未知：{ex.Message}");
            return new VacProbe(null, false);
        }
    }

    /// <summary>取页面并手动跟 3xx，返回最终地址（用于判断是否被导向登录页）。</summary>
    private static async Task<(string Html, Uri FinalUri)> GetHtmlAsync(
        string url,
        SteamWebSession session,
        CancellationToken cancellationToken)
    {
        var current = new Uri(url);
        for (var redirect = 0; redirect < MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 SteamEYA");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(cancellationToken), current);
        }

        throw new InvalidOperationException("Steam 验证页面重定向次数过多。");
    }

    /// <summary>help.steampowered.com 被重定向到 /login 时说明 Web 会话没生效，不能当成「无限制」。</summary>
    private static bool IsAuthenticatedHelpPage(Uri uri) =>
        uri.Host.Equals("help.steampowered.com", StringComparison.OrdinalIgnoreCase) &&
        !uri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractCooldownExpiration(string html)
    {
        var start = html.IndexOf(CooldownExpirationMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += CooldownExpirationMarker.Length;
        var end = html.IndexOf("</span>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : WebUtility.HtmlDecode(html[start..end]).Trim();
    }

    /// <summary>
    /// 页面给的到期时间是太平洋时间且不带年份；用页面里的 g_ServerTime 作为基准，
    /// 在 ±1 年里取距离服务器时间最近的一年，已结束的冷却保持为过去时间而不是滚成新的假倒计时。
    /// </summary>
    private static DateTimeOffset? ParseCooldownEnd(string html, string cooldownText)
    {
        var serverMatch = Regex.Match(html, @"g_ServerTime\s*=\s*(\d+)");
        if (!serverMatch.Success ||
            !long.TryParse(
                serverMatch.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return null;
        }

        var serverUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        TimeZoneInfo pacific;
        try
        {
            pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch
        {
            return null;
        }

        var serverPacific = TimeZoneInfo.ConvertTime(serverUtc, pacific);
        var match = Regex.Match(
            cooldownText,
            @"(\d+)\s*月\s*(\d+)\s*日\s*(上午|下午)\s*(\d+):(\d+)");
        if (!match.Success)
        {
            return null;
        }

        var month = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var period = match.Groups[3].Value;
        var hour = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        if (period == "下午" && hour != 12)
        {
            hour += 12;
        }
        else if (period == "上午" && hour == 12)
        {
            hour = 0;
        }

        DateTimeOffset? best = null;
        var bestDistance = TimeSpan.MaxValue;
        for (var year = serverPacific.Year - 1; year <= serverPacific.Year + 1; year++)
        {
            try
            {
                var unspecified = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
                var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, pacific);
                var candidate = new DateTimeOffset(utc, TimeSpan.Zero);
                var distance = (candidate - serverUtc).Duration();
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            catch (ArgumentException)
            {
                // 太平洋夏令时跳过的时间点：换相邻年份继续试即可。
            }
        }

        return best?.ToLocalTime();
    }

    /// <summary>Steam 对疑似被盗账号会显示「资料被强制设为私密」「账号锁定」的红锁提示，按永久 VAC 处理。</summary>
    private static bool IsRedLockPermanentVac(string vacHtml) =>
        vacHtml.Contains("暂时强制将您的个人资料设置为私密状态", StringComparison.Ordinal) ||
        vacHtml.Contains("怀疑您的帐户被他人登录过", StringComparison.Ordinal) ||
        vacHtml.Contains("暫時強制將您的個人資料設定為私密狀態", StringComparison.Ordinal) ||
        vacHtml.Contains("懷疑您的帳戶被他人登入過", StringComparison.Ordinal) ||
        vacHtml.Contains("账号锁定", StringComparison.Ordinal) ||
        vacHtml.Contains("帐号锁定", StringComparison.Ordinal) ||
        vacHtml.Contains("账户锁定", StringComparison.Ordinal) ||
        vacHtml.Contains("帳號鎖定", StringComparison.Ordinal) ||
        vacHtml.Contains("帳戶鎖定", StringComparison.Ordinal) ||
        vacHtml.Contains("account locked", StringComparison.OrdinalIgnoreCase);
}
