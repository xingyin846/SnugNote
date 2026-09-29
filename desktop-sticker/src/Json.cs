// S1 sticker: minimal JSON reader/writer (no NuGet, no System.Web.Extensions dependency).
// Pure ASCII source. Only supports the JSON subset needed by notes.json / sticker-state.json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TieTieSticker
{
    internal enum JsonKind { Null, Bool, Number, String, Array, Object }

    internal sealed class JsonValue
    {
        public JsonKind Kind;
        public bool Bool;
        public double Number;
        public string Str;
        public List<JsonValue> Items;
        public List<KeyValuePair<string, JsonValue>> Members;

        public bool IsObject { get { return Kind == JsonKind.Object; } }
        public bool IsArray { get { return Kind == JsonKind.Array; } }

        public static JsonValue NewObject()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Object;
            v.Members = new List<KeyValuePair<string, JsonValue>>();
            return v;
        }

        public static JsonValue NewArray()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Array;
            v.Items = new List<JsonValue>();
            return v;
        }

        public static JsonValue FromString(string s)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.String;
            v.Str = s == null ? "" : s;
            return v;
        }

        public static JsonValue FromInt(long n)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Number;
            v.Number = (double)n;
            return v;
        }

        public static JsonValue FromBool(bool b)
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Bool;
            v.Bool = b;
            return v;
        }

        public static JsonValue Null()
        {
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Null;
            return v;
        }

        public void Put(string key, JsonValue value)
        {
            Members.Add(new KeyValuePair<string, JsonValue>(key, value));
        }

        public bool TryGet(string key, out JsonValue value)
        {
            value = null;
            if (Members == null) return false;
            for (int i = 0; i < Members.Count; i++)
            {
                if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
                {
                    value = Members[i].Value;
                    return true;
                }
            }
            return false;
        }

        public JsonValue Get(string key)
        {
            JsonValue v;
            if (TryGet(key, out v)) return v;
            return null;
        }

        public static string StrOr(JsonValue parent, string key, string fallback)
        {
            if (parent == null) return fallback;
            JsonValue v = parent.Get(key);
            if (v == null) return fallback;
            if (v.Kind == JsonKind.String) return v.Str ?? fallback;
            return fallback;
        }

        public static bool BoolOr(JsonValue parent, string key, bool fallback)
        {
            if (parent == null) return fallback;
            JsonValue v = parent.Get(key);
            if (v == null) return fallback;
            if (v.Kind == JsonKind.Bool) return v.Bool;
            return fallback;
        }
    }

    internal sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    internal static class Json
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonException("text is null");
            int i = 0;
            SkipWs(text, ref i);
            JsonValue v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new JsonException("trailing garbage at offset " + i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            if (i >= s.Length) throw new JsonException("unexpected end of input");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return JsonValue.FromString(ParseString(s, ref i));
                case 't':
                    Expect(s, ref i, "true");
                    return JsonValue.FromBool(true);
                case 'f':
                    Expect(s, ref i, "false");
                    return JsonValue.FromBool(false);
                case 'n':
                    Expect(s, ref i, "null");
                    return JsonValue.Null();
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
                    throw new JsonException("unexpected character '" + c + "' at offset " + i);
            }
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new JsonException("expected '" + literal + "' at offset " + i);
            i += literal.Length;
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            JsonValue o = JsonValue.NewObject();
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new JsonException("expected object key at offset " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new JsonException("expected ':' at offset " + i);
                i++;
                SkipWs(s, ref i);
                JsonValue val = ParseValue(s, ref i);
                o.Members.Add(new KeyValuePair<string, JsonValue>(key, val));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new JsonException("expected ',' or '}' at offset " + i);
            }
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            JsonValue a = JsonValue.NewArray();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                SkipWs(s, ref i);
                a.Items.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new JsonException("expected ',' or ']' at offset " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new JsonException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new JsonException("unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new JsonException("bad \\u escape");
                        int code = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int d = HexDigit(s[i + k]);
                            if (d < 0) throw new JsonException("bad \\u escape");
                            code = (code << 4) | d;
                        }
                        i += 4;
                        sb.Append((char)code);
                        break;
                    default:
                        throw new JsonException("unsupported escape '\\" + e + "'");
                }
            }
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            string raw = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new JsonException("bad number '" + raw + "'");
            JsonValue v = new JsonValue();
            v.Kind = JsonKind.Number;
            v.Number = d;
            return v;
        }

        // ---------- writing ----------

        public static string Write(JsonValue v)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue v)
        {
            if (v == null) { sb.Append("null"); return; }
            switch (v.Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(v.Bool ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(((long)Math.Round(v.Number)).ToString(CultureInfo.InvariantCulture)); break;
                case JsonKind.String: WriteString(sb, v.Str); break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < v.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteValue(sb, v.Items[i]);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < v.Members.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, v.Members[i].Key);
                        sb.Append(':');
                        WriteValue(sb, v.Members[i].Value);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
