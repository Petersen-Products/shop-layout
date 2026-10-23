using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShopLayout.Core.Json
{
    /// <summary>
    /// Small JSON reader for configuration and snapshot files. Produces <see cref="JsonObject"/>,
    /// List&lt;object&gt;, string, double, bool and null. Accepts // and /* */ comments and trailing
    /// commas so hand-edited config files are forgiving.
    /// </summary>
    public sealed class JsonParser
    {
        private readonly string _s;
        private int _p;

        private JsonParser(string s) { _s = s; }

        public static object Parse(string text)
        {
            var parser = new JsonParser(text ?? "");
            parser.SkipWs();
            var v = parser.ReadValue();
            parser.SkipWs();
            if (parser._p < parser._s.Length) throw parser.Error("Unexpected trailing content");
            return v;
        }

        public static JsonObject ParseObject(string text)
        {
            var v = Parse(text);
            return v as JsonObject ?? throw new FormatException("JSON root must be an object.");
        }

        private object ReadValue()
        {
            if (_p >= _s.Length) throw Error("Unexpected end of input");
            char c = _s[_p];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw Error($"Unexpected character '{c}'");
            }
        }

        private JsonObject ReadObject()
        {
            var o = new JsonObject();
            _p++; // {
            SkipWs();
            while (true)
            {
                if (Peek() == '}') { _p++; return o; }
                if (Peek() != '"') throw Error("Expected property name");
                string key = ReadString();
                SkipWs();
                if (Peek() != ':') throw Error("Expected ':'");
                _p++;
                SkipWs();
                o.Add(key, ReadValue());
                SkipWs();
                char c = Peek();
                if (c == ',') { _p++; SkipWs(); continue; }
                if (c == '}') { _p++; return o; }
                throw Error("Expected ',' or '}'");
            }
        }

        private List<object> ReadArray()
        {
            var list = new List<object>();
            _p++; // [
            SkipWs();
            while (true)
            {
                if (Peek() == ']') { _p++; return list; }
                list.Add(ReadValue());
                SkipWs();
                char c = Peek();
                if (c == ',') { _p++; SkipWs(); continue; }
                if (c == ']') { _p++; return list; }
                throw Error("Expected ',' or ']'");
            }
        }

        private string ReadString()
        {
            var sb = new StringBuilder();
            _p++; // opening quote
            while (_p < _s.Length)
            {
                char c = _s[_p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_p >= _s.Length) break;
                char e = _s[_p++];
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
                        if (_p + 4 > _s.Length) throw Error("Bad unicode escape");
                        sb.Append((char)int.Parse(_s.Substring(_p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _p += 4;
                        break;
                    default: throw Error($"Bad escape '\\{e}'");
                }
            }
            throw Error("Unterminated string");
        }

        private double ReadNumber()
        {
            int start = _p;
            if (Peek() == '-') _p++;
            while (_p < _s.Length && "0123456789+-.eE".IndexOf(_s[_p]) >= 0) _p++;
            string token = _s.Substring(start, _p - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw Error($"Bad number '{token}'");
            return d;
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_s, _p, word, 0, word.Length) != 0) throw Error($"Expected '{word}'");
            _p += word.Length;
        }

        private char Peek() => _p < _s.Length ? _s[_p] : '\0';

        private void SkipWs()
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (char.IsWhiteSpace(c) || c == '﻿') { _p++; continue; }
                if (c == '/' && _p + 1 < _s.Length && _s[_p + 1] == '/')
                {
                    while (_p < _s.Length && _s[_p] != '\n') _p++;
                    continue;
                }
                if (c == '/' && _p + 1 < _s.Length && _s[_p + 1] == '*')
                {
                    int end = _s.IndexOf("*/", _p + 2, StringComparison.Ordinal);
                    _p = end < 0 ? _s.Length : end + 2;
                    continue;
                }
                break;
            }
        }

        private FormatException Error(string message)
        {
            int line = 1, col = 1;
            for (int i = 0; i < _p && i < _s.Length; i++)
            {
                if (_s[i] == '\n') { line++; col = 1; } else col++;
            }
            return new FormatException($"{message} at line {line}, column {col}.");
        }
    }
}
