using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ShopLayout.Core.Json
{
    /// <summary>
    /// Deterministic JSON serializer.
    /// - Object keys keep insertion order (<see cref="JsonObject"/>); plain dictionaries are sorted ordinally.
    /// - Numbers use invariant culture, a fixed maximum number of decimals, and never print "-0".
    /// - Arrays that contain only scalars are written on a single line so points and matrices stay compact.
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly int _decimals;
        private readonly string _numberFormat;
        private readonly bool _indent;

        /// <param name="decimals">Maximum decimals; a negative value writes exact round-trip ("R") numbers (used for snapshots).</param>
        public JsonWriter(int decimals = 9, bool indent = true)
        {
            _decimals = decimals;
            _numberFormat = decimals > 0 ? "0." + new string('#', decimals) : "0";
            _indent = indent;
        }

        public static string Serialize(object value, int decimals = 9, bool indent = true)
            => new JsonWriter(decimals, indent).Write(value);

        public string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            if (_indent) sb.Append('\n');
            return sb.ToString();
        }

        public string FormatNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (_decimals < 0) return d == 0 ? "0" : d.ToString("R", CultureInfo.InvariantCulture);
            double r = Math.Round(d, _decimals, MidpointRounding.AwayFromZero);
            if (r == 0) return "0"; // also normalizes -0
            return r.ToString(_numberFormat, CultureInfo.InvariantCulture);
        }

        private void WriteValue(StringBuilder sb, object value, int depth)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: sb.Append(FormatNumber(d)); return;
                case float f: sb.Append(FormatNumber(f)); return;
                case decimal m: sb.Append(FormatNumber((double)m)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case JsonObject o: WriteObject(sb, o.Items, depth); return;
                case IDictionary dict:
                    {
                        var items = new List<KeyValuePair<string, object>>();
                        foreach (DictionaryEntry e in dict) items.Add(new KeyValuePair<string, object>(Convert.ToString(e.Key, CultureInfo.InvariantCulture), e.Value));
                        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                        WriteObject(sb, items, depth);
                        return;
                    }
                case IEnumerable en: WriteArray(sb, en.Cast<object>().ToList(), depth); return;
                default: WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture)); return;
            }
        }

        private void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object>> items, int depth)
        {
            var list = items as IList<KeyValuePair<string, object>> ?? items.ToList();
            if (list.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NewLine(sb, depth + 1);
                WriteString(sb, list[i].Key);
                sb.Append(_indent ? ": " : ":");
                WriteValue(sb, list[i].Value, depth + 1);
            }
            NewLine(sb, depth);
            sb.Append('}');
        }

        private void WriteArray(StringBuilder sb, List<object> list, int depth)
        {
            if (list.Count == 0) { sb.Append("[]"); return; }
            bool inline = !_indent || list.All(IsScalar);
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(inline && _indent ? ", " : ",");
                if (!inline) NewLine(sb, depth + 1);
                WriteValue(sb, list[i], depth + 1);
            }
            if (!inline) NewLine(sb, depth);
            sb.Append(']');
        }

        private static bool IsScalar(object o) =>
            o == null || o is string || o is bool || o is double || o is float || o is int || o is long || o is decimal;

        private void NewLine(StringBuilder sb, int depth)
        {
            if (!_indent) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
