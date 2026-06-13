using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SteamEyaWinUI.Models;

public sealed partial class CachedSteamLoginAccount
{
    public string AccountName { get; set; } = "";

    public string SteamId { get; set; } = "";

    public string? PersonaName { get; set; }

    public string? AvatarUrl { get; set; }

    public string? AvatarPath { get; set; }

    public DateTimeOffset CachedAt { get; set; }

    [JsonIgnore]
    public string CacheKey => string.IsNullOrWhiteSpace(SteamId) ? $"name:{AccountName}" : $"id:{SteamId}";

    [JsonIgnore]
    public string AccountTitle => string.IsNullOrWhiteSpace(AccountName) ? "未知账号" : AccountName;

    [JsonIgnore]
    public string PersonaDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? "Steam 资料未同步" : PersonaName;

    [JsonIgnore]
    public string SteamIdDisplay => string.IsNullOrWhiteSpace(SteamId) ? "Steam64 未记录" : SteamId;

    [JsonIgnore]
    public string CachedAtText => CachedAt == default
        ? "未知时间"
        : CachedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string CachedAtShortText => CachedAt == default
        ? "未知"
        : CachedAt.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public BitmapImage? AvatarImage
    {
        get
        {
            var localPath = AvatarPath;
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                return new BitmapImage(new Uri(localPath, UriKind.Absolute));
            }

            if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
                Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatarUri))
            {
                return new BitmapImage(avatarUri);
            }

            return null;
        }
    }
}
