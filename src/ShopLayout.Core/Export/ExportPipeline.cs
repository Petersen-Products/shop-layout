using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShopLayout.Core.Classification;
using ShopLayout.Core.Config;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Json;
using ShopLayout.Core.Model;

namespace ShopLayout.Core.Export
{
    public sealed class ExportOptions
    {
        /// <summary>Validation mode: compute everything, write nothing, and by default do not resolve lightweight components.</summary>
        public bool DryRun;
        /// <summary>In dry-run mode, still allow temporary lightweight resolution (more accurate dimensions).</summary>
        public bool AllowResolveInDryRun;
        public string ExporterVersion = "1.0.0";
        /// <summary>Timestamp for the run block; defaults to now (UTC).</summary>
        public DateTime? Timestamp;
        public Action<string> Progress;
    }

    public sealed class ExportResult
    {
        public RawAssembly Assembly;
        public FloorFrame Floor;
        public Dictionary<string, ComponentClass> Classes;
        public List<Placement> Placements = new List<Placement>();
        public List<Definition> Definitions = new List<Definition>();
        public IssueLog Log;
        public JsonObject Layout;
        public JsonObject Library;
        public JsonObject Report;
        public string LayoutJson;
        public string LibraryJson;
        public string ReportJson;
        public string ReportText;
        public bool Fatal;

        public int ErrorCount => Log.Count(Severity.Error);
        public int WarningCount => Log.Count(Severity.Warning);
    }

    /// <summary>
    /// End-to-end export: read structure → classify → place → build definition geometry → emit JSON.
    /// Independent of SolidWorks; everything CAD-specific is behind <see cref="IAssemblySource"/>.
    /// </summary>
    public sealed class ExportPipeline
    {
        private readonly IAssemblySource _source;
        private readonly ExportConfig _cfg;
        private readonly ExportOptions _opt;

        public ExportPipeline(IAssemblySource source, ExportConfig cfg, ExportOptions options = null)
        {
            _source = source;
            _cfg = cfg ?? new ExportConfig();
            _opt = options ?? new ExportOptions();
        }

        private void Progress(string s) => _opt.Progress?.Invoke(s);

        public ExportResult Run()
        {
            var log = new IssueLog();
            var res = new ExportResult { Log = log };

            Progress("Reading assembly structure…");
            var asm = _source.ReadStructure();
            res.Assembly = asm;
            log.AddRange(asm.ReadIssues);
            Prepare(asm, log);

            res.Floor = BuildFloor(log);
            if (res.Floor == null) { res.Fatal = true; FinishSource(log); return BuildOutputs(res); }

            Progress("Classifying components…");
            res.Classes = new RoleClassifier(_cfg, log).Classify(asm);

            Progress("Computing placements…");
            foreach (var c in asm.AllComponents().OrderBy(c => c.IdPath, IdPathComparer.Instance))
            {
                if (!res.Classes.TryGetValue(c.IdPath, out var cls) || cls.OwnerId != null) continue;
                PlacedKind kind;
                if (cls.Role == LayoutRole.Equipment) kind = PlacedKind.Equipment;
                else if (cls.Role == LayoutRole.Building) kind = PlacedKind.Building;
                else if (cls.Role == LayoutRole.Clearance) kind = PlacedKind.Zone;
                else continue;

                if (c.LoadState == LoadState.Suppressed && !_cfg.Classification.IncludeSuppressed)
                {
                    log.Info("SUPPRESSED_EXCLUDED", c.IdPath, $"{kind} '{c.Name2}' is suppressed and was left out of the placed {kind.ToString().ToLowerInvariant()} list (it remains in the component tree).");
                    continue;
                }
                res.Placements.Add(MakePlacement(c, cls, kind, res.Floor, log));
            }

            AssignInstanceIds(res.Placements, log);
            BuildDefinitions(res, log);

            bool allowResolve = _cfg.Geometry.ResolveLightweight && (!_opt.DryRun || _opt.AllowResolveInDryRun);
            var builder = new DefinitionGeometryBuilder(_source, _cfg, res.Classes, log, allowResolve);
            int n = 0;
            foreach (var def in res.Definitions)
            {
                n++;
                if (def.Kind == PlacedKind.Building && !_cfg.Geometry.ComputeBuildingGeometry) { def.Geometry = new DefinitionGeometry(); continue; }
                Progress($"Geometry {n}/{res.Definitions.Count}: {def.Representative.Name2}");
                var rep = def.Instances[0];
                def.Geometry = rep.TransformMissing ? new DefinitionGeometry() : builder.Build(def, rep);
            }

            FinishSource(log);
            return BuildOutputs(res);
        }

        private void FinishSource(IssueLog log)
        {
            try { log.AddRange(_source.Finish()); }
            catch (Exception ex) { log.Warn("SOURCE_FINISH_FAILED", null, "Cleaning up the CAD session failed: " + ex.Message); }
        }

        private ExportResult BuildOutputs(ExportResult res)
        {
            var output = new OutputBuilder(_cfg, _opt, res);
            output.Build();
            return res;
        }

        // ---------------------------------------------------------------------------------------------

        private static void Prepare(RawAssembly asm, IssueLog log)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Visit(RawComponent c, RawComponent parent, int depth)
            {
                c.Parent = parent;
                c.Depth = depth;
                if (string.IsNullOrEmpty(c.IdPath))
                    c.IdPath = (parent != null ? parent.IdPath + "/" : "") + c.LocalId;
                if (!seen.Add(c.IdPath))
                {
                    string orig = c.IdPath;
                    int k = 2;
                    while (!seen.Add(orig + "#" + k)) k++;
                    c.IdPath = orig + "#" + k;
                    log.Error("DUPLICATE_COMPONENT_ID", c.IdPath, $"Component ID path {orig} occurs more than once; '{c.Name2}' was renamed to {c.IdPath}. IDs for these components may not be stable.");
                }
                if (c.IdIsFallback)
                    log.Warn("COMPONENT_ID_FALLBACK", c.IdPath, $"No SolidWorks component ID for '{c.Name2}'; a name-based ID was used, which changes if the component is renamed.");
                log.AddRange(c.ReadIssues);
                c.Children.Sort((a, b) => a.LocalId != b.LocalId ? a.LocalId.CompareTo(b.LocalId) : string.CompareOrdinal(a.Name2, b.Name2));
                foreach (var ch in c.Children) Visit(ch, c, depth + 1);
            }
            asm.TopLevel.Sort((a, b) => a.LocalId != b.LocalId ? a.LocalId.CompareTo(b.LocalId) : string.CompareOrdinal(a.Name2, b.Name2));
            foreach (var c in asm.TopLevel) Visit(c, null, 1);

            foreach (var c in asm.AllComponents())
            {
                if (c.FileExists == false)
                    log.Warn("FILE_NOT_FOUND", c.IdPath, $"Referenced file of '{c.Name2}' was not found: {c.FilePath}");
                if (c.LoadState == LoadState.IdMismatch)
                    log.Warn("COMPONENT_ID_MISMATCH", c.IdPath, $"SolidWorks reports an internal ID mismatch for '{c.Name2}'.");
            }
        }

        private FloorFrame BuildFloor(IssueLog log)
        {
            var f = _cfg.Floor;
            var origin = f.Origin != null && f.Origin.Length >= 3 ? Vec3.FromArray(f.Origin) : Vec3.Zero;
            try
            {
                string plane = (f.Plane ?? "XY").Trim();
                if (plane.Equals("custom", StringComparison.OrdinalIgnoreCase))
                    return new FloorFrame("custom", origin, Vec3.FromArray(f.U), Vec3.FromArray(f.V));
                if (plane.Equals("referencePlane", StringComparison.OrdinalIgnoreCase))
                {
                    var arr = _source.GetReferencePlaneTransform(f.ReferencePlaneName);
                    if (arr == null)
                    {
                        log.Error("FLOOR_PLANE_NOT_FOUND", null, $"Reference plane '{f.ReferencePlaneName}' was not found in the root assembly.");
                        return null;
                    }
                    return FloorFrame.FromPlaneTransform("referencePlane:" + f.ReferencePlaneName, arr);
                }
                return FloorFrame.FromPreset(plane, origin);
            }
            catch (ArgumentException ex)
            {
                log.Error("FLOOR_CONFIG_INVALID", null, ex.Message);
                return null;
            }
        }

        private Placement MakePlacement(RawComponent c, ComponentClass cls, PlacedKind kind, FloorFrame floor, IssueLog log)
        {
            var p = new Placement { Component = c, Class = cls, Kind = kind };
            if (c.TransformArray == null || c.TransformArray.Length < 12)
            {
                p.TransformMissing = true;
                p.RootTransform = Affine3.Identity();
                log.Warn("TRANSFORM_UNAVAILABLE", c.IdPath, $"No transform available for '{c.Name2}' ({c.LoadState}); placed at the origin.");
            }
            else p.RootTransform = Affine3.FromSolidWorksArray(c.TransformArray);

            double ortho = p.RootTransform.OrthogonalityError();
            if (ortho > 1e-4)
                log.Warn("TRANSFORM_NOT_RIGID", c.IdPath, $"Transform of '{c.Name2}' is not a rotation (error {ortho:0.######}); poses may be distorted.");

            p.FloorTransform = floor.RootToFloor.Multiply(p.RootTransform);
            var detected = LocalAxis.DetectUp(p.FloorTransform, out double tilt);
            p.Up = detected;
            p.UpSource = "detected";

            var upProp = c.GetProperty(_cfg.Properties.UpAxis);
            if (upProp != null && !string.IsNullOrWhiteSpace(upProp.Value))
            {
                var forced = LocalAxis.Parse(upProp.Value);
                if (forced == null)
                    log.Warn("UP_AXIS_PROPERTY_INVALID", c.IdPath, $"{_cfg.Properties.UpAxis}='{upProp.Value}' is not one of +X,-X,+Y,-Y,+Z,-Z; using detected {detected.Name}.");
                else
                {
                    p.Up = forced;
                    p.UpSource = "property";
                    var img = p.FloorTransform.ApplyVector(forced.Up);
                    tilt = Math.Acos(Math.Max(-1, Math.Min(1, img.Z / Math.Max(img.Length, 1e-300)))) * 180 / Math.PI;
                }
            }
            p.TiltDeg = tilt;
            p.Planar = tilt <= _cfg.Floor.PlanarToleranceDeg;
            if (!p.Planar)
                log.Warn("NON_PLANAR_PLACEMENT", c.IdPath, $"'{c.Name2}' is tilted {tilt:0.##}° from the floor normal (local up {p.Up.Name}); its floor footprint is projected from the 3D geometry and the 2D rotation is approximate.");

            var a3 = p.FloorTransform.ApplyVector(p.Up.A);
            var b3 = p.FloorTransform.ApplyVector(p.Up.B);
            p.A = new Vec2(a3.X, a3.Y);
            p.B = new Vec2(b3.X, b3.Y);
            p.T = new Vec2(p.FloorTransform.T.X, p.FloorTransform.T.Y);
            p.Elevation = p.FloorTransform.T.Z;
            p.Scale = p.FloorTransform.UniformScale;
            p.Mirrored = p.A.Cross(p.B) < 0;
            double rot = Math.Atan2(a3.Y, a3.X) * 180 / Math.PI;
            rot = Math.Round(rot, 6);
            p.RotationDeg = ((rot % 360) + 360) % 360;
            if (Math.Abs(p.RotationDeg - 360) < 1e-9) p.RotationDeg = 0;
            if (p.Mirrored)
                log.Info("MIRRORED_PLACEMENT", c.IdPath, $"'{c.Name2}' is placed mirrored (negative determinant).");
            if (Math.Abs(p.Scale - 1) > 1e-6)
                log.Warn("SCALED_PLACEMENT", c.IdPath, $"'{c.Name2}' has a transform scale of {p.Scale:0.######}.");

            var labelProp = c.GetProperty(_cfg.Properties.Label);
            p.Label = labelProp != null && !string.IsNullOrWhiteSpace(labelProp.Value) ? labelProp.Value.Trim() : c.InstanceName;
            var tag = c.GetProperty(_cfg.Properties.AssetTag);
            p.AssetTag = tag != null && !string.IsNullOrWhiteSpace(tag.Value) ? tag.Value.Trim() : null;
            return p;
        }

        private void AssignInstanceIds(List<Placement> placements, IssueLog log)
        {
            bool useTags = string.Equals(_cfg.Ids.InstanceIdStrategy, "assetTagOrComponentIdPath", StringComparison.OrdinalIgnoreCase);
            if (!useTags && !string.Equals(_cfg.Ids.InstanceIdStrategy, "componentIdPath", StringComparison.OrdinalIgnoreCase))
                log.Error("CONFIG_INVALID_ID_STRATEGY", null, $"Unknown instanceIdStrategy '{_cfg.Ids.InstanceIdStrategy}'; using componentIdPath.");

            var tagCounts = placements.Where(p => p.AssetTag != null)
                .GroupBy(p => p.AssetTag, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var p in placements)
            {
                p.InstanceId = p.Component.IdPath;
                if (p.AssetTag != null && tagCounts[p.AssetTag] > 1)
                {
                    log.Warn("DUPLICATE_ASSET_TAG", p.Component.IdPath,
                        $"{_cfg.Properties.AssetTag} '{p.AssetTag}' is shared by {tagCounts[p.AssetTag]} placed components (file/configuration properties are shared by every instance of a file). The component ID path is used instead.");
                    continue;
                }
                if (useTags && p.AssetTag != null) p.InstanceId = "asset:" + p.AssetTag;
            }
        }

        private void BuildDefinitions(ExportResult res, IssueLog log)
        {
            string rootDir = string.IsNullOrEmpty(res.Assembly.RootFilePath) ? null : Path.GetDirectoryName(res.Assembly.RootFilePath);
            var byId = new Dictionary<string, Definition>(StringComparer.Ordinal);
            foreach (var p in res.Placements)
            {
                var c = p.Component;
                string rel = c.IsVirtual || string.IsNullOrEmpty(c.FilePath)
                    ? "virtual/" + (string.IsNullOrEmpty(c.FileName) ? c.InstanceName : c.FileName)
                    : PathUtil.MakeRelative(rootDir, c.FilePath);
                string prefix = p.Kind == PlacedKind.Equipment ? "eq:" : p.Kind == PlacedKind.Building ? "bld:" : "zone:";
                string id = prefix + rel.ToLowerInvariant() + "@" + (c.Configuration ?? "") + "#" + p.Up.Name;
                if (c.IsFlexible)
                {
                    id += "!" + c.IdPath;
                    log.Info("FLEXIBLE_SUBASSEMBLY", c.IdPath, $"'{c.Name2}' is a flexible subassembly; it gets its own definition because its children can move per instance.");
                }
                else if (c.IsVirtual)
                    id += "!" + c.IdPath;

                if (!byId.TryGetValue(id, out var def))
                {
                    def = new Definition
                    {
                        Id = id,
                        Kind = p.Kind,
                        Representative = c,
                        RepresentativeClass = p.Class,
                        Up = p.Up,
                        RelativePath = rel,
                        Configuration = c.Configuration
                    };
                    byId[id] = def;
                }
                def.Instances.Add(p);
                p.Definition = def;
            }
            res.Definitions = byId.Values.OrderBy(d => d.Kind).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
            foreach (var d in res.Definitions)
            {
                d.Instances.Sort((a, b) => IdPathComparer.Instance.Compare(a.Component.IdPath, b.Component.IdPath));
                d.Representative = d.Instances[0].Component;
                d.RepresentativeClass = d.Instances[0].Class;
            }
        }
    }
}
