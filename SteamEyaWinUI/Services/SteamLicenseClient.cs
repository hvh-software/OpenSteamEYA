using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed record SteamAccountData(string Token, string User, string SteamId);

// partial：实例会作为 ComboBox ItemsSource 跨越 WinRT ABI，需要 CsWinRT 源生成 vtable（AOT）。
internal sealed partial record SteamUpstreamServer(string Name, string BaseUrl)
{
    public override string ToString() => Name;
}

internal sealed class SteamLicenseClient
{
    private const string KeyDataPath = "/keygetdata?key=";
    private const int HeaderSkipBytes = 8;

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // 用 List 而不是集合表达式：[...] 生成的 <>z__ReadOnlyArray 无法跨 WinRT ABI
    // 传给 ComboBox.ItemsSource（CsWinRT 已知限制），运行时抛 ArgumentException。
    public static IReadOnlyList<SteamUpstreamServer> Servers { get; } = new List<SteamUpstreamServer>
    {
        new("奶味", "http://111.170.18.37:9095"),
        new("伊万/小岛", "http://70.39.201.195:9099"),
        new("路飞", "http://38.76.193.80:9099")
    };

    public async Task<SteamAccountData> GetAccountDataAsync(
        string licenseKey,
        SteamUpstreamServer server,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new ArgumentException(Loc.T("License_Error_EmptyKey"), nameof(licenseKey));
        }

        var url = $"{server.BaseUrl.TrimEnd('/')}{KeyDataPath}{Uri.EscapeDataString(licenseKey.Trim())}";
        using var response = await DefaultHttpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException(Loc.T("License_Error_InvalidKeyOrServer"));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(Loc.T("License_Error_WrongKeyOrServer"));
        }

        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var json = DecompressKeyDataResponse(raw);
        return ParseAccountJson(json);
    }

    private static string DecompressKeyDataResponse(byte[] raw)
    {
        if (raw.Length <= HeaderSkipBytes)
        {
            throw new InvalidDataException(Loc.T("License_Error_ResponseTooShort"));
        }

        using var input = new MemoryStream(raw, HeaderSkipBytes, raw.Length - HeaderSkipBytes, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static SteamAccountData ParseAccountJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array when document.RootElement.GetArrayLength() > 0 =>
                document.RootElement[0],
            JsonValueKind.Array =>
                throw new JsonException(Loc.T("License_Error_NoAccountData")),
            _ => document.RootElement
        };

        return new SteamAccountData(
            Token: RequireString(root, "token"),
            User: RequireString(root, "user"),
            SteamId: RequireString(root, "steamid"));
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(Loc.Tf("License_Error_MissingField_Format", name));
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException(Loc.Tf("License_Error_EmptyField_Format", name));
        }

        return text;
    }
}
