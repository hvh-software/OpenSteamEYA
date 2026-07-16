using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class GitHubUpdateService
{
    public const string RepositoryUrl = "https://github.com/hvh-software/OpenSteamEYA/";
    public const string ReleasesUrl = $"{RepositoryUrl}/releases";
    private const string LatestMetadataUrl = "https://github.com/hvh-software/OpenSteamEYA/releases/latest/download/latest.json";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static readonly IReadOnlyList<GitHubProxySite> ProxySites =
    [
        new("direct", "Direct", null),
        new("gh-proxy.org", "gh-proxy.org", "https://gh-proxy.org/"),
        new("v4.gh-proxy.org", "v4.gh-proxy.org", "https://v4.gh-proxy.org/"),
        new("v6.gh-proxy.org", "v6.gh-proxy.org", "https://v6.gh-proxy.org/"),
        new("cdn.gh-proxy.org", "cdn.gh-proxy.org", "https://cdn.gh-proxy.org/")
    ];

    private string _selectedProxyCode = "direct";

    public static string CurrentVersion { get; } = GetCurrentVersion();

    public string SelectedProxyCode => _selectedProxyCode;

    public IReadOnlyList<GitHubProxySite> GetProxySites() => ProxySites;

    public void SetProxySite(string? proxyCode)
    {
        _selectedProxyCode = ResolveSite(proxyCode).Code;
    }

    public async Task<GitHubUpdateInfo> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var site = ResolveSite(_selectedProxyCode);

        using var metadataResponse = await HttpClient.GetAsync(BuildUrl(site, LatestMetadataUrl), cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();

        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReleaseMetadataDto metadata = await JsonSerializer.DeserializeAsync(
            metadataStream,
            GitHubUpdateJsonContext.Default.GitHubReleaseMetadataDto,
            cancellationToken)
            ?? throw new InvalidOperationException(Loc.T("Update_EmptyResponse"));

        var latestTag = string.IsNullOrWhiteSpace(metadata.Tag)
            ? "latest"
            : metadata.Tag!;
        var latestVersion = NormalizeVersion(metadata.Version ?? metadata.Tag);
        var currentVersion = CurrentVersion;
        var changelog = metadata.Changelog?.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            ?? [];
        var artifactName = metadata.ArtifactName;
        var artifactUrl = BuildArtifactUrl(site, latestTag, artifactName);
        var releaseUrl = string.Equals(latestTag, "latest", StringComparison.OrdinalIgnoreCase)
            ? ReleasesUrl
            : $"{ReleasesUrl}/tag/{latestTag}";

        return new GitHubUpdateInfo(
            currentVersion,
            latestVersion,
            latestTag,
            IsNewerVersion(latestVersion, currentVersion),
            BuildUrl(site, releaseUrl),
            artifactName,
            artifactUrl,
            metadata.ArtifactSize,
            metadata.ArtifactType,
            metadata.ArtifactSha256,
            changelog,
            DateTimeOffset.Now);
    }

    public async Task<TimeSpan> ProbeLatencyAsync(string? proxyCode = null, CancellationToken cancellationToken = default)
    {
        var site = ResolveSite(proxyCode ?? _selectedProxyCode);
        var probeUrl = BuildUrl(site, LatestMetadataUrl);
        var stopwatch = Stopwatch.StartNew();
        using var response = await HttpClient.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static GitHubProxySite ResolveSite(string? code)
    {
        return ProxySites.FirstOrDefault(item =>
            string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? ProxySites[0];
    }

    private static string BuildUrl(GitHubProxySite site, string url)
    {
        if (string.IsNullOrWhiteSpace(site.UrlPrefix) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        // 这些代理是“前缀 + 完整 GitHub URL”模式，仅代理 github.com 相关链接。
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return site.UrlPrefix + url;
    }

    private static string? BuildArtifactUrl(GitHubProxySite site, string latestTag, string? artifactName)
    {
        if (string.IsNullOrWhiteSpace(artifactName))
        {
            return null;
        }

        var baseUrl = string.Equals(latestTag, "latest", StringComparison.OrdinalIgnoreCase)
            ? $"{ReleasesUrl}/latest/download/{artifactName}"
            : $"{ReleasesUrl}/download/{latestTag}/{artifactName}";
        return BuildUrl(site, baseUrl);
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return NormalizeVersion(informational);
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        return TryParseVersion(latestVersion, out var latest) &&
            TryParseVersion(currentVersion, out var current) &&
            latest.CompareTo(current) > 0;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        var normalized = NormalizeVersion(value);
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        while (parts.Length < 3)
        {
            parts = [.. parts, "0"];
        }

        return Version.TryParse(string.Join('.', parts.Take(4)), out version!);
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffixIndex = normalized.IndexOfAny(['+', '-']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized;
    }

    internal sealed record GitHubReleaseMetadataDto(
        string? Version,
        string? Tag,
        string? ArtifactName,
        long? ArtifactSize,
        string? ArtifactType,
        string? ArtifactSha256,
        IReadOnlyList<string>? Changelog);

    internal sealed record GitHubProxySite(
        string Code,
        string DisplayName,
        string? UrlPrefix);
}

// JsonSerializerDefaults.Web 保持旧版反射序列化语义：camelCase + 大小写不敏感，
// latest.json 的字段（version/tag/artifactName...）依赖该命名策略。
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GitHubUpdateService.GitHubReleaseMetadataDto))]
internal sealed partial class GitHubUpdateJsonContext : JsonSerializerContext;
