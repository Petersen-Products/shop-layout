using System;
using System.Collections.Generic;
using System.Linq;
using ShopLayout.Core.Config;
using ShopLayout.Core.Model;

namespace ShopLayout.Core.Classification
{
    public enum LayoutRole
    {
        /// <summary>Organizational container: not placed itself; its children are classified.</summary>
        Group,
        /// <summary>A placed piece of equipment. Its whole subtree belongs to it (never double-counted).</summary>
        Equipment,
        /// <summary>Building geometry: walls, columns, slabs, doors…</summary>
        Building,
        /// <summary>Explicit clearance/keep-out zone. Top-level: a layout zone. Inside equipment: that equipment's clearance.</summary>
        Clearance,
        /// <summary>Excluded from the layout (fasteners, reference geometry, people models…).</summary>
        Ignore,
        /// <summary>Derived: part of an Equipment/Building/Clearance subtree.</summary>
        Contained
    }

    public sealed class ComponentClass
    {
        public LayoutRole Role;
        /// <summary>"property:LayoutRole", "rule:&lt;name&gt;", "builtin:toolbox", "default", "root", "inherited".</summary>
        public string Source;
        public string Kind;
        public string Category;
        /// <summary>IdPath of the Equipment/Building/Clearance/Ignore node whose subtree contains this node.</summary>
        public string OwnerId;
        /// <summary>True for a node inside an owner that should not contribute to the owner's footprint.</summary>
        public bool ExcludedFromOwnerGeometry;
        /// <summary>IdPath of the clearance-marker component this node belongs to (inside equipment), if any.</summary>
        public string ClearanceMarkerId;
    }

    /// <summary>
    /// Assigns one <see cref="LayoutRole"/> to every component.
    /// Precedence per node: LayoutRole property → first matching rule → built-in Toolbox rule → default.
    /// Classification is top-down: once a node is Equipment/Building/Clearance/Ignore, every descendant
    /// becomes Contained (or an explicit clearance/ignore marker inside it), so subassembly children can
    /// never be exported as separate equipment.
    /// </summary>
    public sealed class RoleClassifier
    {
        private readonly ExportConfig _cfg;
        private readonly IssueLog _log;
        private readonly Dictionary<string, ComponentClass> _result = new Dictionary<string, ComponentClass>(StringComparer.Ordinal);

        public RoleClassifier(ExportConfig cfg, IssueLog log)
        {
            _cfg = cfg;
            _log = log;
        }

        public Dictionary<string, ComponentClass> Classify(RawAssembly asm)
        {
            _result.Clear();
            if (!TryParseRole(_cfg.Classification.DefaultTopLevelRole, out _))
                _log.Error("CONFIG_INVALID_DEFAULT_ROLE", null, $"defaultTopLevelRole '{_cfg.Classification.DefaultTopLevelRole}' is not a valid role; using Equipment.");
            foreach (var c in asm.TopLevel) ClassifyTop(c);
            return _result;
        }

        private void ClassifyTop(RawComponent c)
        {
            var d = Decide(c, contained: false);
            ComponentClass cls;
            if (d != null && d.Role != LayoutRole.Contained) cls = d;
            else
            {
                TryParseRole(_cfg.Classification.DefaultTopLevelRole, out var def);
                if (def == LayoutRole.Contained) def = LayoutRole.Equipment;
                cls = new ComponentClass { Role = def, Source = "default", Kind = d?.Kind, Category = d?.Category };
            }

            if (cls.Role == LayoutRole.Group && c.DocKind == DocKind.Part)
            {
                _log.Warn("GROUP_ROLE_ON_PART", c.IdPath, $"'{c.Name2}' is a part but was classified as Group; treating it as Equipment.");
                cls.Role = LayoutRole.Equipment;
            }
            _result[c.IdPath] = cls;

            if (!c.PropertiesAvailable && c.LoadState != LoadState.Suppressed)
                _log.Info("PROPERTIES_UNAVAILABLE", c.IdPath, $"Custom properties of '{c.Name2}' could not be read ({c.LoadState}); classified by rules/defaults only.");

            switch (cls.Role)
            {
                case LayoutRole.Group:
                    foreach (var ch in c.Children) ClassifyTop(ch);
                    break;
                case LayoutRole.Ignore:
                    foreach (var dsc in c.Descendants())
                        _result[dsc.IdPath] = new ComponentClass { Role = LayoutRole.Ignore, Source = "inherited", OwnerId = c.IdPath, ExcludedFromOwnerGeometry = true };
                    break;
                default:
                    foreach (var ch in c.Children) ClassifyContained(ch, c.IdPath, null, false);
                    break;
            }
        }

        private void ClassifyContained(RawComponent c, string ownerId, string markerId, bool excluded)
        {
            var d = markerId == null && !excluded ? Decide(c, contained: true) : null;
            var cls = new ComponentClass { Role = LayoutRole.Contained, Source = "inherited", OwnerId = ownerId, ClearanceMarkerId = markerId, ExcludedFromOwnerGeometry = excluded };

            if (d != null)
            {
                switch (d.Role)
                {
                    case LayoutRole.Clearance:
                        cls = new ComponentClass { Role = LayoutRole.Clearance, Source = d.Source, Kind = d.Kind, OwnerId = ownerId, ClearanceMarkerId = c.IdPath, ExcludedFromOwnerGeometry = true };
                        markerId = c.IdPath;
                        break;
                    case LayoutRole.Ignore:
                        cls = new ComponentClass { Role = LayoutRole.Ignore, Source = d.Source, OwnerId = ownerId, ExcludedFromOwnerGeometry = true };
                        excluded = true;
                        break;
                    case LayoutRole.Equipment:
                    case LayoutRole.Building:
                    case LayoutRole.Group:
                        _log.Warn("NESTED_ROLE_IGNORED", c.IdPath,
                            $"'{c.Name2}' is marked {d.Role} ({d.Source}) but lies inside '{ownerId}', which is already placed; it is treated as part of that object to avoid double counting. Mark the parent as Group to place children individually.");
                        break;
                }
            }
            _result[c.IdPath] = cls;
            foreach (var ch in c.Children) ClassifyContained(ch, ownerId, markerId, excluded || cls.Role == LayoutRole.Ignore);
        }

        /// <summary>Returns the explicit decision for a node, or null if only the default applies.</summary>
        private ComponentClass Decide(RawComponent c, bool contained)
        {
            var props = _cfg.Properties;
            string kindProp = NonEmpty(c.GetProperty(props.Kind)?.Value);
            string catProp = NonEmpty(c.GetProperty(props.Category)?.Value);

            var roleProp = c.GetProperty(props.Role);
            string roleText = NonEmpty(roleProp?.Value);
            if (roleText != null)
            {
                if (TryParseRole(roleText, out var role) && role != LayoutRole.Contained)
                    return new ComponentClass { Role = role, Source = "property:" + props.Role, Kind = kindProp, Category = catProp };
                _log.Warn("INVALID_LAYOUT_ROLE", c.IdPath, $"Property {props.Role}='{roleText}' on '{c.Name2}' is not one of Group, Equipment, Building, Clearance, Ignore; falling back to rules.");
            }

            var rules = _cfg.Classification.Rules;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                string scope = (r.AppliesTo ?? "top").Trim().ToLowerInvariant();
                if (scope != "any" && scope != (contained ? "contained" : "top")) continue;
                if (!Matches(r, c)) continue;
                if (!TryParseRole(r.Role, out var role) || role == LayoutRole.Contained)
                {
                    _log.Error("CONFIG_INVALID_RULE_ROLE", null, $"Rule '{r.Name}' has invalid role '{r.Role}'; rule skipped.");
                    continue;
                }
                return new ComponentClass { Role = role, Source = "rule:" + r.Name, Kind = kindProp ?? r.Kind, Category = catProp ?? r.Category };
            }

            if (_cfg.Classification.IgnoreToolbox && IsToolboxPath(c.FilePath))
                return new ComponentClass { Role = LayoutRole.Ignore, Source = "builtin:toolbox" };

            if (kindProp != null || catProp != null)
                return new ComponentClass { Role = LayoutRole.Contained, Kind = kindProp, Category = catProp }; // carries kind/category to the default
            return null;
        }

        public static bool IsToolboxPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = Glob.Normalize(path).ToLowerInvariant();
            return p.Contains("/toolbox/") || p.Contains("/solidworks data/") || System.Text.RegularExpressions.Regex.IsMatch(p, @"/solidworks data \(\d+\)/");
        }

        public static bool Matches(ClassificationRule r, RawComponent c)
        {
            if (r.FileName != null && !Glob.IsMatch(r.FileName, c.FileName)) return false;
            if (r.FilePath != null && !Glob.IsMatch(r.FilePath, c.FilePath)) return false;
            if (r.InstanceName != null && !Glob.IsMatch(r.InstanceName, c.InstanceName)) return false;
            if (r.NamePath != null && !Glob.IsMatch(r.NamePath, c.Name2)) return false;
            if (r.Configuration != null && !Glob.IsMatch(r.Configuration, c.Configuration)) return false;
            if (r.ParentInstanceName != null && (c.Parent == null || !Glob.IsMatch(r.ParentInstanceName, c.Parent.InstanceName))) return false;
            if (r.AncestorInstanceName != null && !c.Ancestors().Any(a => Glob.IsMatch(r.AncestorInstanceName, a.InstanceName))) return false;
            if (r.DocType != null)
            {
                string dt = r.DocType.Trim().ToLowerInvariant();
                if (dt == "part" && c.DocKind != DocKind.Part) return false;
                if (dt == "assembly" && c.DocKind != DocKind.Assembly) return false;
            }
            if (r.Depth.HasValue && c.Depth != r.Depth.Value) return false;
            if (r.MinDepth.HasValue && c.Depth < r.MinDepth.Value) return false;
            if (r.MaxDepth.HasValue && c.Depth > r.MaxDepth.Value) return false;
            if (r.IsEnvelope.HasValue && c.IsEnvelope != r.IsEnvelope.Value) return false;
            if (r.IsVirtual.HasValue && c.IsVirtual != r.IsVirtual.Value) return false;
            if (r.ExcludeFromBom.HasValue && c.ExcludeFromBom != r.ExcludeFromBom.Value) return false;
            if (r.Suppressed.HasValue && (c.LoadState == LoadState.Suppressed) != r.Suppressed.Value) return false;
            if (r.Lightweight.HasValue && (c.LoadState == LoadState.Lightweight) != r.Lightweight.Value) return false;
            foreach (var pc in r.Properties)
            {
                var p = c.GetProperty(pc.Name);
                bool exists = p != null && !string.IsNullOrEmpty(p.Value);
                if (pc.Exists.HasValue && exists != pc.Exists.Value) return false;
                if (pc.EqualsValue != null && (!exists || !string.Equals(p.Value.Trim(), pc.EqualsValue, StringComparison.OrdinalIgnoreCase))) return false;
                if (pc.Matches != null && (!exists || !Glob.IsMatch(pc.Matches, p.Value.Trim()))) return false;
            }
            return true;
        }

        public static bool TryParseRole(string s, out LayoutRole role)
        {
            role = LayoutRole.Equipment;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "group": case "container": role = LayoutRole.Group; return true;
                case "equipment": case "machine": role = LayoutRole.Equipment; return true;
                case "building": case "structure": role = LayoutRole.Building; return true;
                case "clearance": case "zone": case "keepout": role = LayoutRole.Clearance; return true;
                case "ignore": case "none": case "exclude": role = LayoutRole.Ignore; return true;
                default: return false;
            }
        }

        private static string NonEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
