using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ncrust.Core.Platform;

namespace Ncrust.Platform
{
    /// <summary>
    /// <see cref="ISettingsStore"/> 的 WinUI 3 实现：单个 JSON 文件（%LOCALAPPDATA%\Ncrust\settings.json）。
    /// 非打包应用没有 LocalSettings，这里用内存字典 + 原子落盘替代；键名与 Android 的 ncrust_settings 相同。
    /// 大块数据（队列、音乐库、歌词缓存）不要放这里，用 <see cref="LocalFileStore"/>。
    /// </summary>
    public sealed class LocalSettingsStore : ISettingsStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly string _path = Path.Combine(NcrustPaths.DataDirectory, "settings.json");

        public LocalSettingsStore()
        {
            Load();
        }

        public int GetInt(string key, int fallback)
        {
            lock (_lock)
            {
                return _values.TryGetValue(key, out var v) && v is long l ? (int)l : fallback;
            }
        }

        public void SetInt(string key, int value) => SetValue(key, (long)value);

        public bool GetBool(string key, bool fallback)
        {
            lock (_lock)
            {
                return _values.TryGetValue(key, out var v) && v is bool b ? b : fallback;
            }
        }

        public void SetBool(string key, bool value) => SetValue(key, value);

        public string GetString(string key)
        {
            lock (_lock)
            {
                return _values.TryGetValue(key, out var v) && v is string s ? s : null;
            }
        }

        public void SetString(string key, string value) => SetValue(key, value);

        private void SetValue(string key, object value)
        {
            lock (_lock)
            {
                if (value == null)
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = value;
                }

                Save();
            }
        }

        private void Load()
        {
            string text;
            try
            {
                text = File.ReadAllText(_path);
            }
            catch
            {
                return;
            }

            var index = 0;
            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != '{')
            {
                return;
            }

            index++;
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    return;
                }

                if (text[index] == '}')
                {
                    return;
                }

                if (text[index] != '"')
                {
                    return;
                }

                var key = ReadString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                {
                    return;
                }

                index++;
                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    return;
                }

                object value;
                switch (text[index])
                {
                    case '"':
                        value = ReadString(text, ref index);
                        break;
                    case 't':
                    case 'f':
                        value = text[index] == 't';
                        index += (bool)value ? 4 : 5;
                        break;
                    default:
                        var start = index;
                        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '-'))
                        {
                            index++;
                        }

                        if (!long.TryParse(text.Substring(start, index - start), out var number))
                        {
                            return;
                        }

                        value = number;
                        break;
                }

                _values[key] = value;
                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    return;
                }

                if (text[index] == ',')
                {
                    index++;
                }
                else if (text[index] != '}')
                {
                    return;
                }
            }
        }

        private void Save()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            var first = true;
            foreach (var pair in _values)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('"').Append(EscapeKey(pair.Key)).Append("\":");
                if (pair.Value is string s)
                {
                    sb.Append(Quote(s));
                }
                else if (pair.Value is bool b)
                {
                    sb.Append(b ? "true" : "false");
                }
                else if (pair.Value is long l)
                {
                    sb.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            sb.Append('}');

            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                File.Move(tmp, _path, true);
            }
            catch
            {
                // 设置落盘失败不致命：下次改动会重试。
            }
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        private static string ReadString(string text, ref int index)
        {
            index++; // 跳过开引号
            var sb = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\\' && index < text.Length)
                {
                    var e = text[index++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (index + 4 <= text.Length &&
                                ushort.TryParse(text.Substring(index, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out var code))
                            {
                                sb.Append((char)code);
                                index += 4;
                            }

                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string EscapeKey(string key) =>
            key.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Quote(string value) => "\"" +
            value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }
}
