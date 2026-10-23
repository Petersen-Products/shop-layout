using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopLayout.Core.Export
{
    public static class PathUtil
    {
        /// <summary>Path of <paramref name="file"/> relative to <paramref name="baseDir"/> with forward slashes; absolute if on another volume.</summary>
        public static string MakeRelative(string baseDir, string file)
        {
            if (string.IsNullOrEmpty(file)) return "";
            if (string.IsNullOrEmpty(baseDir)) return file.Replace('\\', '/');
            try
            {
                var b = new Uri(AppendSlash(Path.GetFullPath(baseDir)));
                var f = new Uri(Path.GetFullPath(file));
                if (!string.Equals(b.Scheme, f.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetPathRoot(baseDir), Path.GetPathRoot(file), StringComparison.OrdinalIgnoreCase))
                    return file.Replace('\\', '/');
                return Uri.UnescapeDataString(b.MakeRelativeUri(f).ToString()).Replace('\\', '/');
            }
            catch (Exception)
            {
                return file.Replace('\\', '/');
            }
        }

        private static string AppendSlash(string p) => p.EndsWith("\\") || p.EndsWith("/") ? p : p + "\\";
    }

    public static class HashUtil
    {
        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder("sha256:");
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    public static class UnitParser
    {
        private static readonly Regex Rx = new Regex(@"^\s*(-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(mm|cm|m|in|inch|inches|""|ft|feet|')?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Parses "500", "500mm", "0.5 m", "18in", "2'" into metres. Returns null when not a length.</summary>
        public static double? ParseLength(string text, string defaultUnit)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Rx.Match(text);
            if (!m.Success) return null;
            double v = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            string unit = m.Groups[2].Success ? m.Groups[2].Value : defaultUnit;
            double? f = Factor(unit);
            return f.HasValue ? v * f.Value : (double?)null;
        }

        public static double? Factor(string unit)
        {
            switch ((unit ?? "mm").Trim().ToLowerInvariant())
            {
                case "mm": return 0.001;
                case "cm": return 0.01;
                case "m": return 1.0;
                case "in": case "inch": case "inches": case "\"": return 0.0254;
                case "ft": case "feet": case "'": return 0.3048;
                default: return null;
            }
        }
    }
}
