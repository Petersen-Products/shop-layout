using System;
using System.Collections.Generic;
using System.Linq;
using ShopLayout.Core.Json;

namespace ShopLayout.Core.Model
{
    public enum Severity { Info = 0, Warning = 1, Error = 2 }

    /// <summary>A warning or error tied (optionally) to a component. Codes are stable and documented in the README.</summary>
    public sealed class Issue
    {
        public Severity Severity;
        public string Code;
        public string ComponentId;
        public string Message;

        public Issue(Severity severity, string code, string componentId, string message)
        {
            Severity = severity; Code = code; ComponentId = componentId; Message = message;
        }

        public static Issue Info(string code, string componentId, string message) => new Issue(Severity.Info, code, componentId, message);
        public static Issue Warn(string code, string componentId, string message) => new Issue(Severity.Warning, code, componentId, message);
        public static Issue Error(string code, string componentId, string message) => new Issue(Severity.Error, code, componentId, message);

        public string SeverityName => Severity == Severity.Error ? "error" : Severity == Severity.Warning ? "warning" : "info";

        public JsonObject ToJson() => new JsonObject()
            .Add("severity", SeverityName)
            .Add("code", Code)
            .AddIfNotNull("componentId", ComponentId)
            .Add("message", Message);

        public override string ToString() => $"[{SeverityName}] {Code}{(ComponentId != null ? " @" + ComponentId : "")}: {Message}";
    }

    /// <summary>Collects issues, removes duplicates, and returns them in a deterministic order.</summary>
    public sealed class IssueLog
    {
        private readonly List<Issue> _issues = new List<Issue>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public void Add(Issue i)
        {
            if (i == null) return;
            string key = $"{i.Severity}|{i.Code}|{i.ComponentId}|{i.Message}";
            if (_seen.Add(key)) _issues.Add(i);
        }

        public void AddRange(IEnumerable<Issue> issues)
        {
            if (issues == null) return;
            foreach (var i in issues) Add(i);
        }

        public void Info(string code, string id, string msg) => Add(Issue.Info(code, id, msg));
        public void Warn(string code, string id, string msg) => Add(Issue.Warn(code, id, msg));
        public void Error(string code, string id, string msg) => Add(Issue.Error(code, id, msg));

        public int Count(Severity s) => _issues.Count(i => i.Severity == s);

        public IEnumerable<Issue> ForComponent(string id) => Sorted().Where(i => i.ComponentId == id);

        public List<Issue> Sorted() => _issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ThenBy(i => i.ComponentId ?? "", IdPathComparer.Instance)
            .ThenBy(i => i.Message, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Orders "12/4/7"-style ID paths numerically per segment (so 2 sorts before 10).</summary>
    public sealed class IdPathComparer : IComparer<string>
    {
        public static readonly IdPathComparer Instance = new IdPathComparer();

        public int Compare(string a, string b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            var sa = a.Split('/');
            var sb = b.Split('/');
            for (int i = 0; i < Math.Min(sa.Length, sb.Length); i++)
            {
                bool na = long.TryParse(sa[i], out long la);
                bool nb = long.TryParse(sb[i], out long lb);
                int c = na && nb ? la.CompareTo(lb) : string.CompareOrdinal(sa[i], sb[i]);
                if (c != 0) return c;
            }
            return sa.Length.CompareTo(sb.Length);
        }
    }
}
