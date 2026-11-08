using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeSwitch
{
    /// <summary>
    /// Small, lossless JSON helpers. Claude's own config files are edited by splicing text,
    /// so every byte we don't mean to touch (formatting, number precision, key order) stays exactly as it was.
    /// </summary>
    internal static class JsonText
    {
        delegate bool MemberVisitor(string key, int memberStart, int valueStart, int valueEnd);

        public static JavaScriptSerializer Serializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 512;
            return s;
        }

        public static Dictionary<string, object> ParseObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return Serializer().DeserializeObject(raw) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static List<string> TopLevelKeys(string json)
        {
            var keys = new List<string>();
            Walk(json, (k, ms, vs, ve) => { keys.Add(k); return false; });
            return keys;
        }

        public static bool Find(string s, string key, out int memberStart, out int valueStart, out int valueEnd)
        {
            int a = -1, b = -1, c = -1;
            Walk(s, (k, ms, vs, ve) =>
            {
                if (k != key) return false;
                a = ms; b = vs; c = ve;
                return true;
            });
            memberStart = a; valueStart = b; valueEnd = c;
            return a >= 0;
        }

        /// <summary>Raw JSON text of a top-level property's value, or null when it is missing.</summary>
        public static string GetRaw(string s, string key)
        {
            int ms, vs, ve;
            return Find(s, key, out ms, out vs, out ve) ? s.Substring(vs, ve - vs) : null;
        }

        public static string GetString(string s, string key)
        {
            string raw = GetRaw(s, key);
            if (string.IsNullOrEmpty(raw) || raw[0] != '"') return null;
            int i = 0;
            return ReadString(raw, ref i);
        }

        /// <summary>Replaces (or adds) a top-level property, leaving the rest of the text untouched.</summary>
        public static string SetRaw(string s, string key, string raw)
        {
            int ms, vs, ve;
            if (Find(s, key, out ms, out vs, out ve)) return s.Substring(0, vs) + raw + s.Substring(ve);

            int open = SkipWs(s, 0);
            int first = SkipWs(s, open + 1);
            string member = Quote(key) + ": " + raw;
            if (first < s.Length && s[first] == '}') return s.Substring(0, open + 1) + member + s.Substring(first);
            string indent = s.Substring(open + 1, first - open - 1);
            return s.Substring(0, open + 1) + indent + member + "," + s.Substring(open + 1);
        }

        /// <summary>Removes a top-level property and its comma, leaving the rest of the text untouched.</summary>
        public static string Remove(string s, string key)
        {
            int ms, vs, ve;
            if (!Find(s, key, out ms, out vs, out ve)) return s;

            int after = SkipWs(s, ve);
            if (after < s.Length && s[after] == ',')
            {
                int next = SkipWs(s, after + 1);
                return s.Substring(0, ms) + s.Substring(next);
            }

            int before = ms - 1;
            while (before >= 0 && IsWs(s[before])) before--;
            if (before >= 0 && s[before] == ',') return s.Substring(0, before) + s.Substring(ve);
            return s.Substring(0, ms) + s.Substring(ve);
        }

        public static string Quote(string v)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Indents compact JSON (used for ClaudeSwitch's own files).</summary>
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inStr = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        int j = SkipWs(json, i + 1);
                        if (j < json.Length && (json[j] == '}' || json[j] == ']'))
                        {
                            sb.Append(c).Append(json[j]);
                            i = j;
                        }
                        else
                        {
                            sb.Append(c);
                            indent++;
                            NewLine(sb, indent);
                        }
                        break;
                    case '}':
                    case ']':
                        indent--;
                        NewLine(sb, indent);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        NewLine(sb, indent);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---- scanner -------------------------------------------------------------------------

        static void Walk(string s, MemberVisitor visit)
        {
            int i = SkipWs(s, 0);
            if (i >= s.Length || s[i] != '{') throw new FormatException("JSON text is not an object.");
            i++;
            while (true)
            {
                i = SkipWs(s, i);
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");
                char c = s[i];
                if (c == '}') return;
                if (c == ',') { i++; continue; }
                if (c != '"') throw new FormatException("Expected a property name at offset " + i + ".");
                int ms = i;
                string key = ReadString(s, ref i);
                i = SkipWs(s, i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at offset " + i + ".");
                i = SkipWs(s, i + 1);
                int vs = i;
                i = SkipValue(s, i);
                if (visit(key, ms, vs, i)) return;
            }
        }

        static bool IsWs(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        static int SkipWs(string s, int i)
        {
            while (i < s.Length && IsWs(s[i])) i++;
            return i;
        }

        static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n').Append(' ', Math.Max(0, indent) * 2);
        }

        static int SkipValue(string s, int i)
        {
            if (i >= s.Length) throw new FormatException("Missing value.");
            char c = s[i];
            if (c == '"')
            {
                SkipString(s, ref i);
                return i;
            }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (ch == '"') { SkipString(s, ref i); continue; }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                    i++;
                }
                throw new FormatException("Unterminated object or array.");
            }
            int start = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            if (i == start) throw new FormatException("Invalid value at offset " + start + ".");
            return i;
        }

        static void SkipString(string s, ref int i)
        {
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '\\') { i++; continue; }
                if (c == '"') return;
            }
            throw new FormatException("Unterminated string.");
        }

        public static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape.");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated string.");
        }
    }
}
