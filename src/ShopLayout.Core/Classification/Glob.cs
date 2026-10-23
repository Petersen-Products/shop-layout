using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopLayout.Core.Classification
{
    /// <summary>Case-insensitive whole-string glob matching with * and ?. Backslashes are treated as forward slashes.</summary>
    public static class Glob
    {
        private static readonly ConcurrentDictionary<string, Regex> Cache = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        public static bool IsMatch(string pattern, string input)
        {
            if (pattern == null) return true;
            var rx = Cache.GetOrAdd(pattern, Compile);
            return rx.IsMatch(Normalize(input ?? ""));
        }

        public static string Normalize(string s) => s.Replace('\\', '/');

        private static Regex Compile(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (char c in Normalize(pattern))
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }
    }
}
