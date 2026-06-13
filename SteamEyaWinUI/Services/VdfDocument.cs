using System.Text;

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
                $"无法读取 {Path.GetFileName(path)}，已停止以避免覆盖现有 Steam 配置。{ex.Message}",
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
                throw new FormatException("VDF 文件包含无法解析的多余内容。");
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
                    throw new FormatException("VDF 键名位置不正确。");
                }

                if (_position >= _tokens.Count)
                {
                    throw new FormatException("VDF 键缺少对应的值。");
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
                throw new FormatException($"VDF 缺少 {expected}。");
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

            throw new FormatException("VDF 引号字符串没有结束。");
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
