using System.Text;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal static class VdfDocument
{
    // Steam 自己生成的 VDF 从不带 BOM；带 BOM 的 config.vdf Steam 读不动会整个重置
    // （排查经过见 SteamConfigService.UpdateConfigVdf 注释），所以写盘必须用无 BOM 编码。
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Dictionary<string, object> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        try
        {
            return new VdfParser(File.ReadAllText(path, Encoding.UTF8)).Parse();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                Loc.Tf("Steam_Error_VdfReadFailed_Format", Path.GetFileName(path), ex.Message),
                ex);
        }
    }

    // 登录写盘用：解析失败不应中止上号。参考 SteamEYA_GUI.exe 的做法——
    // 解析不了就当作空文档继续（最坏只是重新生成该文件），而不是整个流程抛异常。
    public static Dictionary<string, object> LoadOrEmpty(string path)
    {
        try
        {
            return Load(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"解析 {Path.GetFileName(path)} 失败，按空文档继续（将重新生成该文件）：{ex.Message}");
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }

    public static void Save(string path, Dictionary<string, object> document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再原子替换（同 AccountHistoryService.WriteDocument），
        // 避免进程中断把 Steam 核心配置截成半截文件；随机后缀防止并发保存互踩临时文件。
        var tempPath = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tempPath, Write(document), Utf8NoBom);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 清理失败只会残留临时文件，不影响正式文件，吞掉以保留原始异常。
            }

            throw;
        }
    }

    // ---------- 读取辅助 ----------
    // VDF 键大小写在不同 Steam 版本间不稳定，读取一律先精确命中、再忽略大小写扫描。
    // 字典比较器保持 Ordinal：写盘必须保留原始大小写，改比较器会把仅大小写不同的键静默合并。

    public static object? GetValue(Dictionary<string, object> parent, string key)
    {
        if (parent.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var (candidate, candidateValue) in parent)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return candidateValue;
            }
        }

        return null;
    }

    public static Dictionary<string, object>? GetObject(Dictionary<string, object> parent, string key) =>
        GetValue(parent, key) as Dictionary<string, object>;

    public static string? GetString(Dictionary<string, object> parent, string key) =>
        GetValue(parent, key) as string;

    private static string Write(Dictionary<string, object> document)
    {
        var builder = new StringBuilder();
        WriteObject(builder, document, 0);
        return builder.ToString();
    }

    private static void WriteObject(StringBuilder builder, Dictionary<string, object> obj, int indent)
    {
        foreach (var (key, value) in obj)
        {
            var prefix = new string('\t', indent);
            if (value is Dictionary<string, object> child)
            {
                builder.Append(prefix).Append('"').Append(Escape(key)).AppendLine("\"");
                builder.Append(prefix).AppendLine("{");
                WriteObject(builder, child, indent + 1);
                builder.Append(prefix).AppendLine("}");
            }
            else
            {
                builder.Append(prefix)
                    .Append('"').Append(Escape(key)).Append("\"\t\t\"")
                    .Append(Escape(value?.ToString() ?? string.Empty))
                    .AppendLine("\"");
            }
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private sealed class VdfParser
    {
        private readonly List<string> _tokens;
        private int _position;

        public VdfParser(string text)
        {
            _tokens = Tokenize(text);
        }

        public Dictionary<string, object> Parse()
        {
            var document = ParseObject();
            if (_position != _tokens.Count)
            {
                throw new FormatException(Loc.T("Steam_Error_Vdf_TrailingContent"));
            }

            return document;
        }

        private Dictionary<string, object> ParseObject()
        {
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);

            while (_position < _tokens.Count)
            {
                if (Peek() == "}")
                {
                    break;
                }

                var key = Read();
                if (key is "{" or "}")
                {
                    throw new FormatException(Loc.T("Steam_Error_Vdf_BadKeyPosition"));
                }

                if (_position >= _tokens.Count)
                {
                    throw new FormatException(Loc.T("Steam_Error_Vdf_MissingValue"));
                }

                if (Peek() == "{")
                {
                    Read();
                    obj[key] = ParseObject();
                    Expect("}");
                }
                else
                {
                    obj[key] = Read();
                }
            }

            return obj;
        }

        private string Peek() => _tokens[_position];

        private string Read() => _tokens[_position++];

        private void Expect(string expected)
        {
            if (_position >= _tokens.Count || Read() != expected)
            {
                throw new FormatException(Loc.Tf("Steam_Error_Vdf_MissingExpected_Format", expected));
            }
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var index = 0;

            while (index < text.Length)
            {
                var current = text[index];

                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }

                if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    index += 2;
                    while (index < text.Length && text[index] is not '\r' and not '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (current is '{' or '}')
                {
                    tokens.Add(current.ToString());
                    index++;
                    continue;
                }

                // Valve 条件标记（如 "key" "value" [$WIN32]）：整段跳过，按无条件处理，
                // 否则会被当成下一个键、导致结构解析失败而中止上号。
                if (current == '[')
                {
                    index++;
                    while (index < text.Length && text[index] != ']')
                    {
                        index++;
                    }

                    if (index < text.Length)
                    {
                        index++;
                    }

                    continue;
                }

                // 孤立的 ']'（条件标记残缺等畸形输入）：ReadBare 会在它前面停下且不前进，
                // 若落到下面的裸字符串分支会原地死循环，这里直接跳过。
                if (current == ']')
                {
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    tokens.Add(ReadQuoted(text, ref index));
                    continue;
                }

                var bare = ReadBare(text, ref index);
                if (bare.Length == 0)
                {
                    // ReadBare 未前进说明遇到未被上面分支覆盖的停止字符，
                    // 强制跳过以保证分词在任何输入下都能推进。
                    index++;
                    continue;
                }

                tokens.Add(bare);
            }

            return tokens;
        }

        private static string ReadQuoted(string text, ref int index)
        {
            var builder = new StringBuilder();
            index++;

            while (index < text.Length)
            {
                var current = text[index++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current == '\\' && index < text.Length)
                {
                    var escaped = text[index++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => escaped
                    });
                    continue;
                }

                builder.Append(current);
            }

            throw new FormatException(Loc.T("Steam_Error_Vdf_UnterminatedString"));
        }

        private static string ReadBare(string text, ref int index)
        {
            var start = index;
            while (index < text.Length &&
                !char.IsWhiteSpace(text[index]) &&
                text[index] is not '{' and not '}' and not '[' and not ']')
            {
                index++;
            }

            return text[start..index];
        }
    }
}
