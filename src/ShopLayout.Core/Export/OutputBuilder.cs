using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ShopLayout.Core.Classification;
using ShopLayout.Core.Config;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Json;
using ShopLayout.Core.Model;

namespace ShopLayout.Core.Export
{
    /// <summary>Turns an <see cref="ExportResult"/> into shop_layout.json, equipment_library.json and the validation report.</summary>
    public sealed class OutputBuilder
    {
        public const string LayoutSchema = "petersen.shop-layout";
        public const string LibrarySchema = "petersen.equipment-library";
        public const string SchemaVersion = "1.0.0";

        private readonly ExportConfig _cfg;
        private readonly ExportOptions _opt;
        private readonly ExportResult _res;
        private readonly int _dec;
        private string _rootDir;

        public OutputBuilder(ExportConfig cfg, ExportOptions opt, ExportResult res)
        {
            _cfg = cfg;
            _opt = opt;
            _res = res;
            _dec = Math.Max(0, Math.Min(9, cfg.Output.LengthDecimals));
        }

        public void Build()
        {
            var asm = _res.Assembly;
            _rootDir = string.IsNullOrEmpty(asm?.RootFilePath) ? null : Path.GetDirectoryName(asm.RootFilePath);
            var writer = new JsonWriter(9, _cfg.Output.Indent);

            // Library first: the layout references its hash.
            var lib = BuildLibrary();
            string libHash = Seal(lib, writer);
            var layout = BuildLayout(libHash);
            Seal(layout, writer);

            _res.Library = lib;
            _res.Layout = layout;
            _res.LibraryJson = writer.Write(lib);
            _res.LayoutJson = writer.Write(layout);
            _res.Report = BuildReportJson();
            _res.ReportJson = writer.Write(_res.Report);
            _res.ReportText = BuildReportText();
        }

        /// <summary>Adds contentHash (over everything except contentHash and run) and the optional run block.</summary>
        private string Seal(JsonObject doc, JsonWriter writer)
        {
            string hash = HashUtil.Sha256(new JsonWriter(9, false).Write(doc));
            doc.Add("contentHash", hash);
            if (_cfg.Output.IncludeRunInfo)
            {
                doc.Add("run", new JsonObject()
                    .Add("exportedAtUtc", (_opt.Timestamp ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                    .Add("machine", Environment.MachineName)
                    .Add("mode", _opt.DryRun ? "validate" : "export")
                    .Add("note", "The run block is excluded from contentHash; compare contentHash to detect real changes."));
            }
            return hash;
        }

        // ------------------------------------------------------------------ common pieces

        private double L(double v) => Math.Round(v, _dec, MidpointRounding.AwayFromZero);

        private List<object> P2(Vec2 p) => new List<object> { L(p.X), L(p.Y) };
        private List<object> P3(Vec3 p) => new List<object> { L(p.X), L(p.Y), L(p.Z) };

        private static List<object> Dir(Vec3 v) => new List<object> { Math.Round(v.X, 9), Math.Round(v.Y, 9), Math.Round(v.Z, 9) };

        private static List<object> Mat(double[] m) => m.Select(x => (object)Math.Round(x, 9)).ToList();

        private JsonObject Source()
        {
            var asm = _res.Assembly;
            var o = new JsonObject()
                .Add("application", asm?.SourceApplication ?? "")
                .Add("applicationVersion", asm?.SourceVersion ?? "")
                .Add("rootFile", string.IsNullOrEmpty(asm?.RootFilePath) ? "" : Path.GetFileName(asm.RootFilePath));
            if (_cfg.Output.IncludeAbsolutePaths) o.Add("rootPath", asm?.RootFilePath ?? "");
            o.Add("rootConfiguration", asm?.RootConfiguration ?? "");
            o.Add("exporter", new JsonObject().Add("name", "ShopLayoutExport").Add("version", _opt.ExporterVersion));
            return o;
        }

        private static JsonObject Units() => new JsonObject()
            .Add("length", "m")
            .Add("angle", "deg")
            .Add("matrixConvention", "4x4 row-major, column vectors: p_target = M · [p_local, 1]. 'solidworksArrayData' is IMathTransform.ArrayData unchanged (rotation [0..8] = images of local X,Y,Z axes, translation [9..11], scale [12]).");

        private object Regions(Footprint fp)
        {
            return fp.Regions.Select(r =>
            {
                var o = new JsonObject().Add("outer", r.Outer.Select(p => (object)P2(p)).ToList());
                if (r.Holes.Count > 0) o.Add("holes", r.Holes.Select(h => (object)h.Select(p => (object)P2(p)).ToList()).ToList());
                return (object)o;
            }).ToList();
        }

        private JsonObject Shape(Footprint fp, string method = null)
        {
            if (fp == null) return null;
            var o = new JsonObject()
                .Add("source", fp.Source)
                .Add("accuracy", fp.Accuracy);
            if (fp.Resolution.HasValue) o.Add("resolution", L(fp.Resolution.Value));
            o.Add("complete", fp.Complete);
            if (method != null) o.Add("method", method);
            var all = fp.AllPoints().ToList();
            if (all.Count > 0)
            {
                var (mn, mx) = Polygon2.Bounds(all);
                o.Add("bounds", new JsonObject().Add("min", P2(mn)).Add("max", P2(mx)));
                o.Add("area", Math.Round(fp.Regions.Sum(r => r.Area), Math.Min(9, _dec + 3)));
            }
            o.Add("regions", Regions(fp));
            return o;
        }

        private List<object> WarningCodes(string componentId) =>
            _res.Log.ForComponent(componentId).Select(i => (object)(i.SeverityName + ":" + i.Code)).Distinct().ToList();

        private JsonObject Properties(RawComponent c)
        {
            JsonObject Set(PropertySet s)
            {
                var o = new JsonObject();
                foreach (var p in s.All)
                {
                    if (p.Raw != null && p.Raw != p.Value)
                        o.Add(p.Name, new JsonObject().Add("value", p.Value).Add("expression", p.Raw));
                    else o.Add(p.Name, p.Value);
                }
                return o;
            }
            return new JsonObject()
                .Add("available", c.PropertiesAvailable)
                .Add("file", Set(c.FileProperties))
                .Add("configuration", Set(c.ConfigProperties));
        }

        private JsonObject State(RawComponent c) => new JsonObject()
            .Add("suppression", LoadName(c.LoadState))
            .AddIfNotNull("loaded", c.IsLoaded)
            .AddIfNotNull("visible", c.Visible)
            .AddIfNotNull("fileFound", c.FileExists)
            .Add("envelope", c.IsEnvelope)
            .Add("virtual", c.IsVirtual)
            .Add("excludeFromBom", c.ExcludeFromBom)
            .Add("fixed", c.IsFixed)
            .Add("solving", c.IsFlexible ? "flexible" : "rigid")
            .Add("patternInstance", c.IsPatternInstance)
            .Add("mirroredComponent", c.IsMirrored);

        public static string LoadName(LoadState s)
        {
            switch (s)
            {
                case LoadState.Resolved: return "resolved";
                case LoadState.Lightweight: return "lightweight";
                case LoadState.Suppressed: return "suppressed";
                case LoadState.IdMismatch: return "idMismatch";
                default: return "unknown";
            }
        }

        private JsonObject FileInfo(RawComponent c)
        {
            var o = new JsonObject()
                .Add("fileName", c.FileName)
                .Add("relativePath", c.IsVirtual ? "" : PathUtil.MakeRelative(_rootDir, c.FilePath));
            if (_cfg.Output.IncludeAbsolutePaths) o.Add("absolutePath", c.FilePath ?? "");
            o.Add("docType", c.DocKind == DocKind.Part ? "part" : c.DocKind == DocKind.Assembly ? "assembly" : "other");
            return o;
        }

        // ------------------------------------------------------------------ library

        private JsonObject BuildLibrary()
        {
            var lib = new JsonObject()
                .Add("schema", LibrarySchema)
                .Add("schemaVersion", SchemaVersion)
                .Add("units", Units())
                .Add("source", Source())
                .Add("description", "Reusable equipment definitions keyed by file + configuration + local up axis. Footprints are in each definition's local footprint frame (a, b); see localFrame.");
            var defs = _res.Definitions.Where(d => d.Kind == PlacedKind.Equipment).Select(d => (object)Definition(d)).ToList();
            lib.Add("definitions", defs);
            return lib;
        }

        private JsonObject Definition(Definition d)
        {
            var c = d.Representative;
            var g = d.Geometry ?? new DefinitionGeometry();
            var o = new JsonObject()
                .Add("definitionId", d.Id)
                .Add("kind", d.Kind.ToString().ToLowerInvariant())
                .Add("file", FileInfo(c))
                .Add("configuration", d.Configuration ?? "")
                .AddIfNotNull("category", d.RepresentativeClass?.Category)
                .Add("localFrame", new JsonObject()
                    .Add("upAxis", d.Up.Name)
                    .Add("footprintAxisA", d.Up.AName)
                    .Add("footprintAxisB", d.Up.BName)
                    .Add("description", "Footprint coordinates (a, b) are the component's own coordinates along footprintAxisA/B; height is along upAxis. Place with the instance's transform.footprintToFloor."));

            var bounds = g.FootprintBounds();
            if (bounds != null && g.HasHeight)
            {
                var (mn, mx) = bounds.Value;
                o.Add("dimensions", new JsonObject()
                    .Add("width", L(mx.X - mn.X))
                    .Add("depth", L(mx.Y - mn.Y))
                    .Add("height", L(g.HMax - g.HMin))
                    .Add("basis", g.Primary?.Source ?? "")
                    .Add("note", "width along footprintAxisA, depth along footprintAxisB, height along upAxis"));
            }
            if (!g.LocalBox.IsEmpty)
                o.Add("localBoundingBox", new JsonObject().Add("min", P3(g.LocalBox.Min)).Add("max", P3(g.LocalBox.Max)).Add("source", g.LocalBoxSource ?? ""));
            if (g.HasHeight) o.Add("heightRange", new List<object> { L(g.HMin), L(g.HMax) });

            var fp = new JsonObject();
            fp.Add("primary", Shape(g.Primary));
            var alt = new JsonObject();
            if (g.Outline != null && g.Outline != g.Primary) alt.Add("tessellatedOutline", Shape(g.Outline));
            if (g.ConvexHull != null && g.ConvexHull != g.Primary) alt.Add("convexHull", Shape(g.ConvexHull));
            if (g.Rect != null)
                alt.Add("orientedRect", new JsonObject()
                    .Add("source", "orientedRect")
                    .Add("accuracy", "approximate")
                    .Add("center", P2(g.Rect.Center))
                    .Add("width", L(g.Rect.Width))
                    .Add("depth", L(g.Rect.Depth))
                    .Add("angleDeg", Math.Round(g.Rect.AngleDeg, 6))
                    .Add("corners", g.Rect.Corners().Select(p => (object)P2(p)).ToList()));
            fp.Add("alternatives", alt);
            o.Add("footprint", fp);

            o.Add("clearances", g.Clearances.Select(z => (object)new JsonObject()
                .Add("name", z.Name)
                .Add("definedBy", z.DefinedBy)
                .Add("zoneType", "clearance")
                .Add("shape", Shape(z.Shape))).ToList());

            o.Add("properties", Properties(c));
            o.Add("geometryStats", new JsonObject()
                .Add("partsTotal", g.PartsTotal)
                .Add("partsWithGeometry", g.PartsWithGeometry)
                .Add("partsResolvedForGeometry", g.PartsResolved)
                .Add("triangles", g.TriangleCount)
                .Add("meshSources", g.MeshSources.Cast<object>().ToList())
                .Add("complete", g.Primary != null && g.Primary.Complete));
            o.Add("representativeComponentId", c.IdPath);
            o.Add("instanceIds", d.Instances.Select(p => (object)p.InstanceId).ToList());
            o.Add("warnings", WarningCodes(c.IdPath));
            return o;
        }

        // ------------------------------------------------------------------ layout

        private JsonObject BuildLayout(string libraryHash)
        {
            var f = _res.Floor;
            var layout = new JsonObject()
                .Add("schema", LayoutSchema)
                .Add("schemaVersion", SchemaVersion)
                .Add("units", Units())
                .Add("source", Source())
                .Add("settings", _cfg.ToSettingsJson());

            if (f != null)
                layout.Add("coordinateSystem", new JsonObject()
                    .Add("floorPlane", f.Name)
                    .Add("origin", P3(f.Origin))
                    .Add("u", Dir(f.U))
                    .Add("v", Dir(f.V))
                    .Add("normal", Dir(f.N))
                    .Add("rootToFloor", Mat(f.RootToFloor.ToRowMajor4x4()))
                    .Add("description", "Floor x/y are measured along u/v from origin; elevation along normal (right-handed, y up when viewed from above). All floor* values use this frame; transform.root uses the root assembly frame."));

            layout.Add("library", new JsonObject().Add("file", _cfg.Output.LibraryFile).Add("contentHash", libraryHash));

            var eq = _res.Placements.Where(p => p.Kind == PlacedKind.Equipment).ToList();
            var bl = _res.Placements.Where(p => p.Kind == PlacedKind.Building).ToList();
            var zn = _res.Placements.Where(p => p.Kind == PlacedKind.Zone).ToList();
            layout.Add("summary", new JsonObject()
                .Add("components", _res.Assembly?.AllComponents().Count() ?? 0)
                .Add("equipment", eq.Count)
                .Add("equipmentDefinitions", _res.Definitions.Count(d => d.Kind == PlacedKind.Equipment))
                .Add("building", bl.Count)
                .Add("zones", zn.Count)
                .Add("errors", _res.ErrorCount)
                .Add("warnings", _res.WarningCount));

            layout.Add("equipment", eq.Select(p => (object)PlacementJson(p)).ToList());
            layout.Add("building", bl.Select(p => (object)PlacementJson(p)).ToList());
            layout.Add("zones", zn.Select(p => (object)PlacementJson(p)).ToList());
            if (_cfg.Output.IncludeComponentTree && _res.Assembly != null)
                layout.Add("components", _res.Assembly.AllComponents()
                    .OrderBy(c => c.IdPath, IdPathComparer.Instance)
                    .Select(c => (object)ComponentJson(c)).ToList());
            layout.Add("warnings", _res.Log.Sorted().Select(i => (object)i.ToJson()).ToList());
            return layout;
        }

        private JsonObject PlacementJson(Placement p)
        {
            var c = p.Component;
            var d = p.Definition;
            var g = d?.Geometry ?? new DefinitionGeometry();
            var o = new JsonObject()
                .Add("instanceId", p.InstanceId)
                .Add("componentId", c.IdPath)
                .AddIfNotNull("assetTag", p.AssetTag)
                .Add("label", p.Label)
                .Add("instanceName", c.InstanceName)
                .Add("namePath", c.Name2);
            if (p.Kind == PlacedKind.Equipment) o.Add("definitionId", d?.Id ?? "");
            else
            {
                o.Add("kind", p.Class.Kind ?? (p.Kind == PlacedKind.Building ? "building" : "clearance"));
                o.Add("file", FileInfo(c));
                o.Add("configuration", c.Configuration ?? "");
            }
            o.AddIfNotNull("category", p.Class.Category);
            o.Add("classification", new JsonObject().Add("role", p.Class.Role.ToString()).Add("source", p.Class.Source));
            o.Add("state", State(c));

            o.Add("pose", new JsonObject()
                .Add("x", L(p.T.X))
                .Add("y", L(p.T.Y))
                .Add("elevation", L(p.Elevation))
                .Add("rotationDeg", Math.Round(p.RotationDeg, 6))
                .Add("mirrored", p.Mirrored)
                .Add("scale", Math.Round(p.Scale, 9))
                .Add("upAxis", p.Up.Name)
                .Add("upAxisSource", p.UpSource)
                .Add("tiltDeg", Math.Round(p.TiltDeg, 6))
                .Add("planar", p.Planar)
                .Add("note", "x/y/elevation = component origin in floor coordinates; rotationDeg = counter-clockwise angle of footprintAxisA from floor x."));

            o.Add("transform", new JsonObject()
                .Add("root", new JsonObject()
                    .Add("matrix", Mat(p.RootTransform.ToRowMajor4x4()))
                    .AddIfNotNull("solidworksArrayData", c.TransformArray == null ? null : Mat(c.TransformArray)))
                .Add("floor", new JsonObject().Add("matrix", Mat(p.FloorTransform.ToRowMajor4x4())))
                .Add("footprintToFloor", new JsonObject()
                    .Add("a", new List<object> { Math.Round(p.A.X, 9), Math.Round(p.A.Y, 9) })
                    .Add("b", new List<object> { Math.Round(p.B.X, 9), Math.Round(p.B.Y, 9) })
                    .Add("t", P2(p.T))
                    .Add("note", "floor = t + a·fa + b·fb for library footprint point (fa, fb)")));

            // Floor-space geometry.
            var (fpFloor, method) = FloorFootprint(p, g);
            o.Add("floorFootprint", fpFloor == null ? null : Shape(fpFloor, method));
            o.Add("floorClearances", g.Clearances.Select(z => (object)new JsonObject()
                .Add("name", z.Name)
                .Add("definedBy", z.DefinedBy)
                .Add("zoneType", "clearance")
                .Add("shape", Shape(z.Shape.Map(p.MapFootprint, p.Mirrored), "mappedFromDefinition"))).ToList());
            var fb = FloorBounds(p, g, fpFloor);
            if (fb != null) o.Add("floorBounds", new JsonObject().Add("min", P3(fb.Min)).Add("max", P3(fb.Max)));
            var rb = Box3.FromCorners(c.RootBox);
            if (rb != null) o.Add("rootBoundingBox", new JsonObject()
                .Add("min", P3(rb.Min)).Add("max", P3(rb.Max))
                .Add("source", "solidworksGetBox")
                .Add("accuracy", "approximate"));
            if (p.Kind != PlacedKind.Equipment)
            {
                o.Add("properties", Properties(c));
                if (g.HasHeight) o.Add("heightRange", new List<object> { L(g.HMin), L(g.HMax) });
            }
            o.Add("warnings", WarningCodes(c.IdPath));
            return o;
        }

        private (Footprint, string) FloorFootprint(Placement p, DefinitionGeometry g)
        {
            var local = g.Primary;
            if (local == null) return (null, null);
            if (!p.Planar && local.Source != "authoredSketch" && g.LocalTriangles.Count >= 9)
            {
                // Tilted instance: project its actual 3D geometry instead of mapping the upright footprint.
                var floorFromLocal = p.FloorTransform;
                var tri2d = new List<double>(g.LocalTriangles.Count / 9 * 6);
                for (int i = 0; i + 2 < g.LocalTriangles.Count; i += 3)
                {
                    var q = floorFromLocal.Apply(new Vec3(g.LocalTriangles[i], g.LocalTriangles[i + 1], g.LocalTriangles[i + 2]));
                    tri2d.Add(q.X); tri2d.Add(q.Y);
                }
                var builderOpts = new OutlineOptions
                {
                    Resolution = _cfg.Geometry.FootprintResolution,
                    MaxCells = _cfg.Geometry.MaxRasterCells,
                    ClosingRadiusCells = _cfg.Geometry.ClosingRadiusCells,
                    SimplifyFactor = _cfg.Geometry.SimplifyFactor,
                    MinIslandArea = _cfg.Geometry.MinIslandArea,
                    MinHoleArea = _cfg.Geometry.MinHoleArea
                };
                var o = RasterOutline.Build(tri2d, builderOpts);
                if (o.Regions.Count > 0)
                    return (new Footprint { Source = "tessellatedOutline", Accuracy = "tessellated", Resolution = o.EffectiveResolution, Complete = local.Complete, Regions = o.Regions }, "projectedInstanceGeometry");
            }
            return (local.Map(p.MapFootprint, p.Mirrored), "mappedFromDefinition");
        }

        private static Box3 FloorBounds(Placement p, DefinitionGeometry g, Footprint fpFloor)
        {
            var b = new Box3();
            if (!g.LocalBox.IsEmpty)
                foreach (var c in g.LocalBox.Corners()) b.Add(p.FloorTransform.Apply(c));
            else return null;
            if (fpFloor != null && !fpFloor.IsEmpty && p.Planar)
            {
                // Use the (tighter) footprint for x/y; keep z from the box.
                var (mn, mx) = Polygon2.Bounds(fpFloor.AllPoints());
                b = new Box3 { Min = new Vec3(mn.X, mn.Y, b.Min.Z), Max = new Vec3(mx.X, mx.Y, b.Max.Z) };
            }
            return b;
        }

        private JsonObject ComponentJson(RawComponent c)
        {
            _res.Classes.TryGetValue(c.IdPath, out var cls);
            var o = new JsonObject()
                .Add("componentId", c.IdPath)
                .Add("parentId", c.Parent?.IdPath)
                .Add("localId", c.LocalId)
                .Add("depth", c.Depth)
                .Add("name2", c.Name2)
                .Add("instanceName", c.InstanceName)
                .AddIfNotNull("componentReference", string.IsNullOrEmpty(c.ComponentReference) ? null : c.ComponentReference)
                .Add("file", FileInfo(c))
                .Add("configuration", c.Configuration ?? "")
                .Add("role", cls?.Role.ToString() ?? "Unclassified")
                .Add("roleSource", cls?.Source ?? "")
                .AddIfNotNull("ownerId", cls?.OwnerId)
                .Add("state", State(c));
            if (c.TransformArray != null)
                o.Add("transformRoot", Mat(Affine3.FromSolidWorksArray(c.TransformArray).ToRowMajor4x4()));
            var rb = Box3.FromCorners(c.RootBox);
            if (rb != null) o.Add("rootBoundingBox", new JsonObject().Add("min", P3(rb.Min)).Add("max", P3(rb.Max)));
            o.Add("childIds", c.Children.Select(ch => (object)ch.IdPath).ToList());
            return o;
        }

        // ------------------------------------------------------------------ report

        private JsonObject BuildReportJson()
        {
            var o = new JsonObject()
                .Add("schema", "petersen.shop-layout-validation")
                .Add("schemaVersion", SchemaVersion)
                .Add("mode", _opt.DryRun ? "validate" : "export")
                .Add("source", Source())
                .Add("floorPlane", _res.Floor?.Name ?? "")
                .Add("summary", new JsonObject()
                    .Add("components", _res.Assembly?.AllComponents().Count() ?? 0)
                    .Add("equipment", _res.Placements.Count(p => p.Kind == PlacedKind.Equipment))
                    .Add("definitions", _res.Definitions.Count(d => d.Kind == PlacedKind.Equipment))
                    .Add("building", _res.Placements.Count(p => p.Kind == PlacedKind.Building))
                    .Add("zones", _res.Placements.Count(p => p.Kind == PlacedKind.Zone))
                    .Add("errors", _res.ErrorCount)
                    .Add("warnings", _res.WarningCount)
                    .Add("info", _res.Log.Count(Severity.Info)));

            if (_res.Classes != null && _res.Assembly != null)
                o.Add("classification", _res.Assembly.AllComponents()
                    .Where(c => _res.Classes.TryGetValue(c.IdPath, out var k) && k.OwnerId == null || IsMarker(c))
                    .OrderBy(c => c.IdPath, IdPathComparer.Instance)
                    .Select(c =>
                    {
                        var k = _res.Classes[c.IdPath];
                        return (object)new JsonObject()
                            .Add("componentId", c.IdPath).Add("name", c.Name2).Add("role", k.Role.ToString())
                            .Add("source", k.Source).AddIfNotNull("ownerId", k.OwnerId)
                            .Add("file", c.FileName).Add("suppression", LoadName(c.LoadState));
                    }).ToList());

            o.Add("placed", _res.Placements.Select(p =>
            {
                var g = p.Definition?.Geometry ?? new DefinitionGeometry();
                var b = g.FootprintBounds();
                var j = new JsonObject()
                    .Add("instanceId", p.InstanceId).Add("kind", p.Kind.ToString()).Add("label", p.Label)
                    .Add("definitionId", p.Definition?.Id ?? "")
                    .Add("x", L(p.T.X)).Add("y", L(p.T.Y)).Add("rotationDeg", Math.Round(p.RotationDeg, 3));
                if (b != null && g.HasHeight)
                    j.Add("width", L(b.Value.max.X - b.Value.min.X)).Add("depth", L(b.Value.max.Y - b.Value.min.Y)).Add("height", L(g.HMax - g.HMin));
                j.Add("geometry", g.Primary?.Source ?? "none").Add("accuracy", g.Primary?.Accuracy ?? "none")
                 .Add("clearances", g.Clearances.Count)
                 .Add("warnings", WarningCodes(p.Component.IdPath));
                return (object)j;
            }).ToList());
            o.Add("issues", _res.Log.Sorted().Select(i => (object)i.ToJson()).ToList());
            return o;
        }

        private bool IsMarker(RawComponent c) =>
            _res.Classes.TryGetValue(c.IdPath, out var k) && k.Role == LayoutRole.Clearance && k.ClearanceMarkerId == c.IdPath;

        private string BuildReportText()
        {
            var sb = new StringBuilder();
            var asm = _res.Assembly;
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            sb.AppendLine($"Shop layout {(_opt.DryRun ? "validation" : "export")}: {Path.GetFileName(asm?.RootFilePath ?? "")} [{asm?.RootConfiguration}]");
            sb.AppendLine($"Floor plane: {_res.Floor?.Name ?? "(invalid)"}   Components: {asm?.AllComponents().Count() ?? 0}   " +
                          $"Equipment: {_res.Placements.Count(p => p.Kind == PlacedKind.Equipment)} ({_res.Definitions.Count(d => d.Kind == PlacedKind.Equipment)} definitions)   " +
                          $"Building: {_res.Placements.Count(p => p.Kind == PlacedKind.Building)}   Zones: {_res.Placements.Count(p => p.Kind == PlacedKind.Zone)}");
            sb.AppendLine();

            if (_res.Classes != null && asm != null)
            {
                sb.AppendLine("CLASSIFICATION (placed and grouping levels)");
                var rows = new List<string[]> { new[] { "ID", "Name", "Role", "Source", "State" } };
                foreach (var c in asm.AllComponents().OrderBy(c => c.IdPath, IdPathComparer.Instance))
                {
                    if (!_res.Classes.TryGetValue(c.IdPath, out var k)) continue;
                    if (k.OwnerId != null && !IsMarker(c)) continue;
                    rows.Add(new[] { c.IdPath, Indent(c) + c.Name2.Split('/').Last(), k.Role + (k.OwnerId != null ? " (in " + k.OwnerId + ")" : ""), k.Source, LoadName(c.LoadState) });
                }
                Table(sb, rows);
                sb.AppendLine();
            }

            sb.AppendLine("PLACED OBJECTS (metres, degrees, floor frame)");
            var prow = new List<string[]> { new[] { "Instance", "Kind", "Label", "W x D x H", "X", "Y", "Rot", "Geometry", "Issues" } };
            foreach (var p in _res.Placements)
            {
                var g = p.Definition?.Geometry ?? new DefinitionGeometry();
                var b = g.FootprintBounds();
                string dims = b != null && g.HasHeight ? $"{F(b.Value.max.X - b.Value.min.X)} x {F(b.Value.max.Y - b.Value.min.Y)} x {F(g.HMax - g.HMin)}" : "-";
                string geo = (g.Primary?.Source ?? "none") + (g.Primary != null && !g.Primary.Complete ? " (partial)" : "");
                int issues = _res.Log.ForComponent(p.Component.IdPath).Count(i => i.Severity >= Severity.Warning);
                prow.Add(new[] { p.InstanceId, p.Kind.ToString(), p.Label, dims, F(p.T.X), F(p.T.Y), F(p.RotationDeg), geo, issues == 0 ? "" : issues.ToString(CultureInfo.InvariantCulture) });
            }
            Table(sb, prow);
            sb.AppendLine();

            var issuesAll = _res.Log.Sorted();
            sb.AppendLine($"ISSUES: {_res.ErrorCount} error(s), {_res.WarningCount} warning(s), {_res.Log.Count(Severity.Info)} info");
            foreach (var i in issuesAll) sb.AppendLine("  " + i);
            return sb.ToString();
        }

        private static string Indent(RawComponent c) => new string(' ', Math.Max(0, c.Depth - 1) * 2);

        private static void Table(StringBuilder sb, List<string[]> rows)
        {
            int cols = rows[0].Length;
            var w = new int[cols];
            foreach (var r in rows) for (int i = 0; i < cols; i++) w[i] = Math.Min(48, Math.Max(w[i], (r[i] ?? "").Length));
            foreach (var r in rows)
            {
                var parts = new List<string>();
                for (int i = 0; i < cols; i++)
                {
                    string s = r[i] ?? "";
                    if (s.Length > w[i]) s = s.Substring(0, w[i] - 1) + "…";
                    parts.Add(s.PadRight(w[i]));
                }
                sb.AppendLine("  " + string.Join("  ", parts).TrimEnd());
            }
        }
    }
}
