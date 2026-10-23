using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShopLayout.Core.Json
{
    /// <summary>
    /// Insertion-ordered JSON object. Output key order is exactly the order of <see cref="Add"/> calls,
    /// which is what keeps exports deterministic and diff-friendly.
    /// </summary>
    public sealed class JsonObject
    {
        private readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public IEnumerable<KeyValuePair<string, object>> Items => _items;
        public int Count => _items.Count;

        public JsonObject Add(string key, object value)
        {
            if (_index.TryGetValue(key, out int i))
                _items[i] = new KeyValuePair<string, object>(key, value);
            else
            {
                _index[key] = _items.Count;
                _items.Add(new KeyValuePair<string, object>(key, value));
            }
            return this;
        }

        /// <summary>Adds the value only when it is not null (keeps optional fields out of the output).</summary>
        public JsonObject AddIfNotNull(string key, object value)
        {
            if (value != null) Add(key, value);
            return this;
        }

        public bool Remove(string key)
        {
            if (!_index.TryGetValue(key, out int i)) return false;
            _items.RemoveAt(i);
            _index.Clear();
            for (int k = 0; k < _items.Count; k++) _index[_items[k].Key] = k;
            return true;
        }

        public bool ContainsKey(string key) => _index.ContainsKey(key);

        public object this[string key] => _index.TryGetValue(key, out int i) ? _items[i].Value : null;

        public bool TryGet(string key, out object value)
        {
            if (_index.TryGetValue(key, out int i)) { value = _items[i].Value; return true; }
            value = null;
            return false;
        }

        // Typed accessors used when reading configuration and snapshots.
        public string GetString(string key, string fallback = null)
        {
            var v = this[key];
            if (v == null) return fallback;
            if (v is string s) return s;
            if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return v.ToString();
        }

        public double GetDouble(string key, double fallback)
        {
            var v = this[key];
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return p;
            return fallback;
        }

        public double? GetDoubleOrNull(string key)
        {
            var v = this[key];
            if (v is double d) return d;
            return null;
        }

        public int GetInt(string key, int fallback) => (int)Math.Round(GetDouble(key, fallback));

        public long GetLong(string key, long fallback) => (long)Math.Round(GetDouble(key, fallback));

        public bool GetBool(string key, bool fallback)
        {
            var v = this[key];
            if (v is bool b) return b;
            if (v is string s && bool.TryParse(s, out bool p)) return p;
            return fallback;
        }

        public bool? GetBoolOrNull(string key) => this[key] is bool b ? b : (bool?)null;

        public JsonObject GetObject(string key) => this[key] as JsonObject;

        public List<object> GetArray(string key) => this[key] as List<object>;

        public double[] GetDoubleArray(string key)
        {
            var a = GetArray(key);
            if (a == null) return null;
            var r = new double[a.Count];
            for (int i = 0; i < a.Count; i++) r[i] = a[i] is double d ? d : double.NaN;
            return r;
        }
    }
}
