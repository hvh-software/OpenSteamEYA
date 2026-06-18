using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed partial class SteamWorkshopService
{
    private const uint AppId = 730;
    private const int MaxConcurrentUnsubscribes = 5;

    private readonly JwtTokenService _jwtTokenService = new();

    // 关闭自动重定向：steamLoginSecure（含 access token）是手动加在 Cookie 头上的，
    // .NET 自动跳转只剥 Authorization 不剥手动 Cookie 头，3xx 指向任意域名时 token 会原样外泄。
    // 改为手动跟随，只允许 steamcommunity.com 内部的 https 跳转（见 SendFollowingRedirectsAsync）。
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const int MaxRedirects = 3;

    public async Task<int> ClearSubscriptionsAsync(
        string eyaToken,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(Loc.T("Workshop_Progress_ValidatingToken"));
        var token = _jwtTokenService.Validate(eyaToken);

        await using var cmClient = new SteamCmClient(HttpClient);

        progress?.Report(Loc.T("Workshop_Progress_Connecting"));
        await cmClient.ConnectAndLogOnAsync(eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.Tf("Workshop_Progress_LoggedIn_Format", token.SteamId));
        progress?.Report(Loc.T("Workshop_Progress_GettingWebSession"));
        var (cookieHeader, sessionId) = await GetWebSessionAsync(cmClient, eyaToken, token.SteamId, cancellationToken);

        progress?.Report(Loc.T("Workshop_Progress_GettingSubscriptions"));
        var (ids, titles) = await EnumerateSubscriptionsAsync(token.SteamId, cookieHeader, cancellationToken);

        if (ids.Count == 0)
        {
            progress?.Report(Loc.T("Workshop_Progress_NoSubscriptions"));
            return 0;
        }

        progress?.Report(Loc.Tf("Workshop_Progress_FoundSubscriptions_Format", ids.Count));

        var unsubscribed = 0;
        var processed = 0;
        using var throttle = new SemaphoreSlim(MaxConcurrentUnsubscribes);

        var tasks = ids.Select(id =>
        {
            var title = titles.GetValueOrDefault(id, "");
            return UnsubscribeOneAsync(id, title);
        }).ToArray();

        await Task.WhenAll(tasks);

        async Task UnsubscribeOneAsync(string id, string title)
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = await UnsubscribeViaWebAsync(id, cookieHeader, sessionId, cancellationToken);
                var current = Interlocked.Increment(ref processed);

                if (ok)
                {
                    Interlocked.Increment(ref unsubscribed);
                    progress?.Report(Loc.Tf("Workshop_Progress_ItemUnsubscribed_Format",
                        current, ids.Count, id, title));
                }
                else
                {
                    progress?.Report(Loc.Tf("Workshop_Progress_ItemFailed_Format",
                        current, ids.Count, id, title, "HTTP"));
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        progress?.Report(Loc.Tf("Workshop_Progress_Done_Format", unsubscribed, ids.Count - unsubscribed));
        return unsubscribed;
    }

    private static async Task<(string cookieHeader, string sessionId)> GetWebSessionAsync(
        SteamCmClient cmClient,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        var accessToken = await cmClient.GenerateAccessTokenForAppAsync(refreshToken, cancellationToken);
        var steamLoginSecure = Uri.EscapeDataString($"{steamId}||{accessToken}");
        var sessionId = RandomHex(12);
        var clientSessionId = RandomHex(8);

        var cookieHeader = $"steamLoginSecure={steamLoginSecure}; sessionid={sessionId}; clientsessionid={clientSessionId}";
        return (cookieHeader, sessionId);
    }

    private static async Task<bool> UnsubscribeViaWebAsync(
        string fileId,
        string cookieHeader,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://steamcommunity.com/sharedfiles/unsubscribe");
        request.Headers.Add("Cookie", cookieHeader);
        request.Headers.Add("User-Agent", "Mozilla/5.0");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["appid"] = AppId.ToString(),
            ["id"] = fileId,
            ["sessionid"] = sessionId
        });

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        return (int)response.StatusCode < 400;
    }

    private static async Task<(List<string> ids, Dictionary<string, string> titles)> EnumerateSubscriptionsAsync(
        string steamId,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        var titles = new Dictionary<string, string>();
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"https://steamcommunity.com/profiles/{steamId}/myworkshopfiles/"
                + $"?appid={AppId}&browsefilter=mysubscriptions&numperpage=30&p={page}&l=english";

            using var response = await SendFollowingRedirectsAsync(
                HttpMethod.Get,
                new Uri(url),
                cookieHeader,
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            foreach (Match match in SubscriptionIdRegex().Matches(html))
            {
                var id = match.Groups[1].Value;
                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            foreach (Match match in SubscriptionTitleRegex().Matches(html))
            {
                titles[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            }

            var totalMatch = TotalEntriesRegex().Match(html);
            var total = totalMatch.Success
                ? int.Parse(totalMatch.Groups[1].Value.Replace(",", "", StringComparison.Ordinal))
                : ids.Count;

            if (ids.Count >= total || !html.Contains("workshopItemPreviewHolder", StringComparison.Ordinal))
            {
                break;
            }

            page++;
            await Task.Delay(400, cancellationToken);
        }

        return (ids, titles);
    }

    private static string RandomHex(int byteCount)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }

    /// <summary>
    /// 发送请求并手动跟随重定向。仅跟随指向 steamcommunity.com（或其子域）的 https 跳转，
    /// 跟随时保留 Cookie 头；任何其他跳转目标都按会话失效处理，避免 access token 外泄到外部域名。
    /// 设置了个性化 URL 的账号枚举创意工坊时会从 /profiles/{id}/... 302 到 /id/{vanity}/...，属合法跳转。
    /// </summary>
    private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpMethod method,
        Uri requestUri,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var current = requestUri;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, current);
            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", "Mozilla/5.0");

            var response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is not (>= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest))
            {
                return response;
            }

            // 3xx：自行决定是否跟随，决定前先把这一跳的响应释放掉。
            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
            {
                throw new InvalidOperationException(Loc.T("Workshop_Error_RedirectNoLocation"));
            }

            // Location 可能是相对地址（如 /id/{vanity}/...），基于当前 Uri 解析为绝对地址。
            var target = new Uri(current, location);

            if (hop >= MaxRedirects)
            {
                throw new InvalidOperationException(Loc.T("Workshop_Error_TooManyRedirects"));
            }

            if (!IsTrustedSteamCommunityUri(target))
            {
                // 跳出 steamcommunity.com 通常意味着 access token 不被接受、被导向登录页等。
                throw new InvalidOperationException(Loc.T("Workshop_Error_RedirectedExternal"));
            }

            current = target;
        }
    }

    /// <summary>判断目标是否为可安全携带 Cookie 跟随的 https + steamcommunity.com（或其子域）地址。</summary>
    private static bool IsTrustedSteamCommunityUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"filedetails/\?id=(\d+)""[^>]*><div class=""workshopItemPreviewHolder", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionIdRegex();

    [GeneratedRegex(@"filedetails/\?id=(\d+)""[^>]*><div class=""workshopItemTitle"">([^<]*)<", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionTitleRegex();

    [GeneratedRegex(@"of ([\d,]+) entries", RegexOptions.CultureInvariant)]
    private static partial Regex TotalEntriesRegex();
}
