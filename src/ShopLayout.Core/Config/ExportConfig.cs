using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShopLayout.Core.Json;

namespace ShopLayout.Core.Config
{
    /// <summary>All export settings. Every field has a default, so an empty config file is valid.</summary>
    public sealed class ExportConfig
    {
        public FloorSettings Floor = new FloorSettings();
        public PropertyNames Properties = new PropertyNames();
        public IdSettings Ids = new IdSettings();
        public ClassificationSettings Classification = new ClassificationSettings();
        public GeometrySettings Geometry = new GeometrySettings();
        public OutputSettings Output = new OutputSettings();

        /// <summary>Where the config came from (for the report); null for built-in defaults.</summary>
        public string SourcePath;

        public static ExportConfig Load(string path)
        {
            var cfg = FromJson(JsonParser.ParseObject(File.ReadAllText(path)));
            cfg.SourcePath = Path.GetFullPath(path);
            return cfg;
        }

        public static ExportConfig FromJson(JsonObject o)
        {
            var c = new ExportConfig();
            if (o == null) return c;

            var f = o.GetObject("floor");
            if (f != null)
            {
                c.Floor.Plane = f.GetString("plane", c.Floor.Plane);
                c.Floor.ReferencePlaneName = f.GetString("referencePlaneName", c.Floor.ReferencePlaneName);
                c.Floor.Origin = f.GetDoubleArray("origin") ?? c.Floor.Origin;
                c.Floor.U = f.GetDoubleArray("u") ?? c.Floor.U;
                c.Floor.V = f.GetDoubleArray("v") ?? c.Floor.V;
                c.Floor.PlanarToleranceDeg = f.GetDouble("planarToleranceDeg", c.Floor.PlanarToleranceDeg);
            }

            var p = o.GetObject("properties");
            if (p != null)
            {
                c.Properties.Role = p.GetString("role", c.Properties.Role);
                c.Properties.Kind = p.GetString("kind", c.Properties.Kind);
                c.Properties.Category = p.GetString("category", c.Properties.Category);
                c.Properties.Label = p.GetString("label", c.Properties.Label);
                c.Properties.AssetTag = p.GetString("assetTag", c.Properties.AssetTag);
                c.Properties.UpAxis = p.GetString("upAxis", c.Properties.UpAxis);
                var cl = p.GetObject("clearance");
                if (cl != null)
                {
                    c.Properties.ClearanceFront = cl.GetString("front", c.Properties.ClearanceFront);
                    c.Properties.ClearanceBack = cl.GetString("back", c.Properties.ClearanceBack);
                    c.Properties.ClearanceLeft = cl.GetString("left", c.Properties.ClearanceLeft);
                    c.Properties.ClearanceRight = cl.GetString("right", c.Properties.ClearanceRight);
                    c.Properties.ClearanceDefaultUnit = cl.GetString("defaultUnit", c.Properties.ClearanceDefaultUnit);
                }
            }

            var ids = o.GetObject("ids");
            if (ids != null)
                c.Ids.InstanceIdStrategy = ids.GetString("instanceIdStrategy", c.Ids.InstanceIdStrategy);

            var cls = o.GetObject("classification");
            if (cls != null)
            {
                c.Classification.DefaultTopLevelRole = cls.GetString("defaultTopLevelRole", c.Classification.DefaultTopLevelRole);
                c.Classification.IncludeSuppressed = cls.GetBool("includeSuppressed", c.Classification.IncludeSuppressed);
                c.Classification.IgnoreToolbox = cls.GetBool("ignoreToolbox", c.Classification.IgnoreToolbox);
                var rules = cls.GetArray("rules");
                if (rules != null)
                {
                    c.Classification.Rules = new List<ClassificationRule>();
                    int i = 0;
                    foreach (var r in rules.OfType<JsonObject>())
                        c.Classification.Rules.Add(ClassificationRule.FromJson(r, ++i));
                }
            }

            var g = o.GetObject("geometry");
            if (g != null)
            {
                var gs = c.Geometry;
                gs.ResolveLightweight = g.GetBool("resolveLightweight", gs.ResolveLightweight);
                gs.RestoreLightweight = g.GetBool("restoreLightweight", gs.RestoreLightweight);
                gs.FootprintResolution = g.GetDouble("footprintResolution", gs.FootprintResolution);
                gs.MaxRasterCells = g.GetLong("maxRasterCells", gs.MaxRasterCells);
                gs.ClosingRadiusCells = g.GetInt("closingRadiusCells", gs.ClosingRadiusCells);
                gs.SimplifyFactor = g.GetDouble("simplifyFactor", gs.SimplifyFactor);
                gs.MinIslandArea = g.GetDouble("minIslandArea", gs.MinIslandArea);
                gs.MinHoleArea = g.GetDouble("minHoleArea", gs.MinHoleArea);
                gs.IncludeHiddenChildren = g.GetBool("includeHiddenChildren", gs.IncludeHiddenChildren);
                gs.ExcludeEnvelopes = g.GetBool("excludeEnvelopes", gs.ExcludeEnvelopes);
                gs.FootprintSketchName = g.GetString("footprintSketchName", gs.FootprintSketchName);
                gs.ClearanceSketchPrefix = g.GetString("clearanceSketchPrefix", gs.ClearanceSketchPrefix);
                gs.ReadSketches = g.GetBool("readSketches", gs.ReadSketches);
                gs.ArcChordTolerance = g.GetDouble("arcChordTolerance", gs.ArcChordTolerance);
                gs.FrameMatchTolerance = g.GetDouble("frameMatchTolerance", gs.FrameMatchTolerance);
                gs.ComputeBuildingGeometry = g.GetBool("computeBuildingGeometry", gs.ComputeBuildingGeometry);
            }

            var outp = o.GetObject("output");
            if (outp != null)
            {
                var os = c.Output;
                os.LayoutFile = outp.GetString("layoutFile", os.LayoutFile);
                os.LibraryFile = outp.GetString("libraryFile", os.LibraryFile);
                os.ReportFile = outp.GetString("reportFile", os.ReportFile);
                os.IncludeRunInfo = outp.GetBool("includeRunInfo", os.IncludeRunInfo);
                os.IncludeComponentTree = outp.GetBool("includeComponentTree", os.IncludeComponentTree);
                os.IncludeAbsolutePaths = outp.GetBool("includeAbsolutePaths", os.IncludeAbsolutePaths);
                os.LengthDecimals = outp.GetInt("lengthDecimals", os.LengthDecimals);
                os.Indent = outp.GetBool("indent", os.Indent);
            }
            return c;
        }

        /// <summary>The settings that affect the content of the export, embedded in shop_layout.json for traceability.</summary>
        public JsonObject ToSettingsJson() => new JsonObject()
            .Add("floorPlane", Floor.Plane)
            .AddIfNotNull("referencePlaneName", string.IsNullOrEmpty(Floor.ReferencePlaneName) ? null : Floor.ReferencePlaneName)
            .Add("planarToleranceDeg", Floor.PlanarToleranceDeg)
            .Add("instanceIdStrategy", Ids.InstanceIdStrategy)
            .Add("roleProperty", Properties.Role)
            .Add("defaultTopLevelRole", Classification.DefaultTopLevelRole)
            .Add("ruleCount", Classification.Rules.Count)
            .Add("footprintResolution", Geometry.FootprintResolution)
            .Add("closingRadiusCells", Geometry.ClosingRadiusCells)
            .Add("minIslandArea", Geometry.MinIslandArea)
            .Add("minHoleArea", Geometry.MinHoleArea)
            .Add("includeHiddenChildren", Geometry.IncludeHiddenChildren)
            .Add("excludeEnvelopes", Geometry.ExcludeEnvelopes)
            .Add("resolveLightweight", Geometry.ResolveLightweight);
    }

    public sealed class FloorSettings
    {
        /// <summary>XY | XZ | YZ | custom | referencePlane</summary>
        public string Plane = "XY";
        public string ReferencePlaneName = "";
        public double[] Origin = { 0, 0, 0 };
        /// <summary>Used when Plane = custom: floor x and y directions in root-assembly coordinates.</summary>
        public double[] U = { 1, 0, 0 };
        public double[] V = { 0, 1, 0 };
        /// <summary>Instances whose up axis deviates more than this from the floor normal are reported as non-planar.</summary>
        public double PlanarToleranceDeg = 0.5;
    }

    public sealed class PropertyNames
    {
        public string Role = "LayoutRole";
        public string Kind = "LayoutKind";
        public string Category = "LayoutCategory";
        public string Label = "LayoutLabel";
        public string AssetTag = "AssetTag";
        /// <summary>Optional override of the detected local up axis (+Z, -Y, ...).</summary>
        public string UpAxis = "LayoutUpAxis";
        public string ClearanceFront = "ClearanceFront";
        public string ClearanceBack = "ClearanceBack";
        public string ClearanceLeft = "ClearanceLeft";
        public string ClearanceRight = "ClearanceRight";
        /// <summary>Unit for clearance values without an explicit unit: mm | cm | m | in | ft.</summary>
        public string ClearanceDefaultUnit = "mm";
    }

    public sealed class IdSettings
    {
        /// <summary>
        /// componentIdPath: instanceId = SolidWorks component-ID path (default, always available).
        /// assetTagOrComponentIdPath: use the AssetTag property when present and unique, else the ID path.
        /// </summary>
        public string InstanceIdStrategy = "componentIdPath";
    }

    public sealed class ClassificationSettings
    {
        /// <summary>Role given to a direct child of a Group when no property or rule applies.</summary>
        public string DefaultTopLevelRole = "Equipment";
        /// <summary>Include suppressed Equipment/Building instances in the placed lists (always kept in the component tree).</summary>
        public bool IncludeSuppressed = false;
        /// <summary>Built-in rule: components stored under a Toolbox / "SOLIDWORKS Data" folder are ignored.</summary>
        public bool IgnoreToolbox = true;
        public List<ClassificationRule> Rules = new List<ClassificationRule>();
    }

    public sealed class GeometrySettings
    {
        /// <summary>Temporarily resolve lightweight components when no geometry is available otherwise. Never saved.</summary>
        public bool ResolveLightweight = true;
        /// <summary>Set components resolved by the exporter back to lightweight afterwards.</summary>
        public bool RestoreLightweight = true;
        public double FootprintResolution = 0.005;
        public long MaxRasterCells = 4_000_000;
        public int ClosingRadiusCells = 1;
        public double SimplifyFactor = 1.0;
        public double MinIslandArea = 0.0004;
        public double MinHoleArea = 0.01;
        public bool IncludeHiddenChildren = true;
        public bool ExcludeEnvelopes = true;
        public bool ReadSketches = true;
        public string FootprintSketchName = "LAYOUT_FOOTPRINT";
        public string ClearanceSketchPrefix = "LAYOUT_CLEARANCE";
        public double ArcChordTolerance = 0.002;
        /// <summary>Max box deviation (m) accepted when checking which frame tessellation data is in.</summary>
        public double FrameMatchTolerance = 0.02;
        public bool ComputeBuildingGeometry = true;
    }

    public sealed class OutputSettings
    {
        public string LayoutFile = "shop_layout.json";
        public string LibraryFile = "equipment_library.json";
        public string ReportFile = "validation_report.json";
        /// <summary>Adds a "run" block (timestamp, machine). Excluded from contentHash either way.</summary>
        public bool IncludeRunInfo = true;
        public bool IncludeComponentTree = true;
        public bool IncludeAbsolutePaths = true;
        /// <summary>Decimals for lengths in metres (6 = micrometre).</summary>
        public int LengthDecimals = 6;
        public bool Indent = true;
    }

    public sealed class PropertyCondition
    {
        public string Name;
        public string EqualsValue;
        public string Matches;
        public bool? Exists;
    }

    /// <summary>
    /// One classification rule. All specified criteria must match; the first matching rule (file order) wins.
    /// Glob patterns support * and ? and are case-insensitive; path separators may be / or \.
    /// </summary>
    public sealed class ClassificationRule
    {
        public string Name;
        public string Role;
        public string Kind;
        public string Category;
        /// <summary>top: nodes directly under a Group (default). contained: nodes inside an Equipment/Building. any: both.</summary>
        public string AppliesTo = "top";
        public string FileName;
        public string FilePath;
        public string InstanceName;
        public string NamePath;
        public string Configuration;
        public string ParentInstanceName;
        public string AncestorInstanceName;
        public string DocType;
        public int? Depth, MinDepth, MaxDepth;
        public bool? IsEnvelope, IsVirtual, ExcludeFromBom, Suppressed, Lightweight;
        public List<PropertyCondition> Properties = new List<PropertyCondition>();

        public static ClassificationRule FromJson(JsonObject o, int index)
        {
            var r = new ClassificationRule
            {
                Name = o.GetString("name", "rule " + index),
                Role = o.GetString("role"),
                Kind = o.GetString("kind"),
                Category = o.GetString("category"),
                AppliesTo = o.GetString("appliesTo", "top"),
                FileName = o.GetString("fileName"),
                FilePath = o.GetString("filePath"),
                InstanceName = o.GetString("instanceName"),
                NamePath = o.GetString("namePath"),
                Configuration = o.GetString("configuration"),
                ParentInstanceName = o.GetString("parentInstanceName"),
                AncestorInstanceName = o.GetString("ancestorInstanceName"),
                DocType = o.GetString("docType"),
                IsEnvelope = o.GetBoolOrNull("isEnvelope"),
                IsVirtual = o.GetBoolOrNull("isVirtual"),
                ExcludeFromBom = o.GetBoolOrNull("excludeFromBom"),
                Suppressed = o.GetBoolOrNull("suppressed"),
                Lightweight = o.GetBoolOrNull("lightweight"),
            };
            var d = o.GetDoubleOrNull("depth"); if (d.HasValue) r.Depth = (int)d.Value;
            d = o.GetDoubleOrNull("minDepth"); if (d.HasValue) r.MinDepth = (int)d.Value;
            d = o.GetDoubleOrNull("maxDepth"); if (d.HasValue) r.MaxDepth = (int)d.Value;

            var single = o.GetObject("property");
            if (single != null) r.Properties.Add(ReadCondition(single));
            var many = o.GetArray("properties");
            if (many != null) r.Properties.AddRange(many.OfType<JsonObject>().Select(ReadCondition));
            return r;
        }

        private static PropertyCondition ReadCondition(JsonObject p) => new PropertyCondition
        {
            Name = p.GetString("name"),
            EqualsValue = p.GetString("equals"),
            Matches = p.GetString("matches"),
            Exists = p.GetBoolOrNull("exists")
        };
    }
}
