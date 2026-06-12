using System.Text;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 轻量级文件日志，用于诊断「部分用户无法拉起 Steam」这类只在特定机器复现的问题。
/// 落在 %AppData%\SteamEYA\logs\steameya.log，超过 1 MB 滚动一个 .1 备份。
/// 全部 best-effort：日志自身的任何异常都不得影响主流程（catch 后静默）。
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string LogDirectory;

    /// <summary>诊断日志文件完整路径，便于在 UI 上提示用户回传。</summary>
    public static string LogFilePath { get; }

    static AppLog()
    {
        string directory;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            directory = Path.Combine(appData, "SteamEYA", "logs");
        }
        catch
        {
            directory = Path.Combine(Path.GetTempPath(), "SteamEYA", "logs");
        }

        LogDirectory = directory;
        LogFilePath = Path.Combine(directory, "steameya.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{Describe(exception)}");

    private static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.Append("    ")
                .Append(current.GetType().FullName)
                .Append(": ")
                .AppendLine(current.Message);
        }

        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            builder.AppendLine(exception.StackTrace);
        }

        return builder.ToString().TrimEnd();
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RollIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程。
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogFilePath);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var backup = LogFilePath + ".1";
            File.Delete(backup); // 文件不存在时不抛异常。
            File.Move(LogFilePath, backup);
        }
        catch
        {
            // 滚动失败就继续往原文件追加，不影响主流程。
        }
    }
}
