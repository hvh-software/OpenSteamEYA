namespace SteamEyaWinUI.Models;

// partial：实例会作为导入对话框 ListView 的 ItemsSource 跨越 WinRT ABI，需要 CsWinRT 源生成 vtable（AOT）。
public sealed partial class AccountImportEntry
{
    public string AccountName { get; set; } = "";

    public string EyaToken { get; set; } = "";

    public string SteamId { get; set; } = "";

    public DateTimeOffset? TokenExpiresAt { get; set; }

    public bool TokenIsValid { get; set; }

    public string TokenStatus { get; set; } = "";

    public bool AlreadyExists { get; set; }

    public string SummaryText => AlreadyExists
        ? $"{SteamId} · 已存在，导入将覆盖"
        : $"{SteamId} · 新账号";

    public string TokenSummaryText => TokenIsValid && TokenExpiresAt.HasValue
        ? $"令牌有效至 {TokenExpiresAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}"
        : $"令牌状态：{TokenStatus}";
}
