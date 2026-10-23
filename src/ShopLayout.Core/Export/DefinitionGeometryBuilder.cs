using System;
using System.Collections.Generic;
using System.Linq;
using ShopLayout.Core.Classification;
using ShopLayout.Core.Config;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Model;

namespace ShopLayout.Core.Export
{
    /// <summary>
    /// Builds the local geometry of one definition from its representative component:
    /// gathers the part meshes of the subtree (minus ignored/clearance/envelope parts), expresses them in
    /// the definition's own frame, and derives the outline, hull, oriented rectangle and authored shapes.
    /// </summary>
    public sealed class DefinitionGeometryBuilder
    {
        private readonly IAssemblySource _source;
        private readonly ExportConfig _cfg;
        private readonly Dictionary<string, ComponentClass> _classes;
        private readonly IssueLog _log;
        private readonly bool _allowResolve;

        public DefinitionGeometryBuilder(IAssemblySource source, ExportConfig cfg, Dictionary<string, ComponentClass> classes, IssueLog log, bool allowResolve)
        {
            _source = source;
            _cfg = cfg;
            _classes = classes;
            _log = log;
            _allowResolve = allowResolve;
        }

        public OutlineOptions OutlineOptions => new OutlineOptions
        {
            Resolution = _cfg.Geometry.FootprintResolution,
            MaxCells = _cfg.Geometry.MaxRasterCells,
            ClosingRadiusCells = _cfg.Geometry.ClosingRadiusCells,
            SimplifyFactor = _cfg.Geometry.SimplifyFactor,
            MinIslandArea = _cfg.Geometry.MinIslandArea,
            MinHoleArea = _cfg.Geometry.MinHoleArea
        };

        public DefinitionGeometry Build(Definition def, Placement rep)
        {
            var g = new DefinitionGeometry();
            var up = def.Up;
            var comp = def.Representative;
            string id = comp.IdPath;

            if (comp.LoadState == LoadState.Suppressed)
            {
                _log.Warn("SUPPRESSED_NO_GEOMETRY", id, $"'{comp.Name2}' is suppressed; no geometry is available.");
                return FallbackFromRootBox(g, def, rep);
            }

            Affine3 rootToLocal;
            try { rootToLocal = rep.RootTransform.Inverse(); }
            catch (InvalidOperationException)
            {
                _log.Error("SINGULAR_TRANSFORM", id, $"Transform of '{comp.Name2}' is singular; geometry skipped.");
                return g;
            }

            // 1. Collect parts: body parts for the footprint, and part groups of each clearance marker.
            var bodyParts = new List<RawComponent>();
            var markerParts = new SortedDictionary<string, List<RawComponent>>(IdPathComparer.Instance);
            CollectParts(comp, comp, bodyParts, markerParts);

            // 2. Meshes → local frame.
            foreach (var part in bodyParts)
                AppendPartMesh(part, rootToLocal, g.LocalTriangles, g, countStats: true);

            // 3. Derived shapes in footprint coordinates.
            var tri2d = new List<double>(g.LocalTriangles.Count / 9 * 6);
            var pts2d = new List<Vec2>();
            ProjectTriangles(g.LocalTriangles, up, tri2d, pts2d, g);

            if (g.TriangleCount > 0)
            {
                g.LocalBoxSource = "tessellation";
                var outline = RasterOutline.Build(tri2d, OutlineOptions);
                if (outline.Regions.Count > 0)
                {
                    g.Outline = new Footprint
                    {
                        Source = "tessellatedOutline",
                        Accuracy = "tessellated",
                        Resolution = outline.EffectiveResolution,
                        Complete = g.Complete,
                        Regions = outline.Regions
                    };
                    if (outline.EffectiveResolution > _cfg.Geometry.FootprintResolution * 1.0001)
                        _log.Info("OUTLINE_RESOLUTION_COARSENED", id, $"Footprint grid coarsened to {outline.EffectiveResolution:0.###} m to stay within maxRasterCells.");
                }
                var hull = Polygon2.ConvexHull(pts2d);
                if (hull.Count >= 3)
                {
                    g.ConvexHull = Footprint.FromRing("convexHull", "approximate", hull);
                    g.ConvexHull.Complete = g.Complete;
                    g.Rect = Polygon2.MinAreaRect(hull);
                }
            }

            if (g.PartsTotal > 0 && g.PartsWithGeometry < g.PartsTotal)
                _log.Warn("PARTIAL_GEOMETRY", id,
                    $"{g.PartsTotal - g.PartsWithGeometry} of {g.PartsTotal} part(s) of '{comp.Name2}' returned no geometry; the footprint may be incomplete.");
            if (g.PartsTotal == 0)
                _log.Warn("NO_PARTS", id, $"'{comp.Name2}' contains no part components that contribute geometry (all ignored, suppressed or envelopes).");

            // 4. Authored sketches (exact footprint / clearance zones).
            if (_cfg.Geometry.ReadSketches)
                ReadSketches(def, g);

            // 5. Clearance marker components inside the definition.
            foreach (var kv in markerParts)
            {
                var tris = new List<double>();
                var dummy = new DefinitionGeometry();
                foreach (var part in kv.Value) AppendPartMesh(part, rootToLocal, tris, dummy, countStats: false);
                var t2 = new List<double>();
                ProjectTriangles(tris, up, t2, new List<Vec2>(), null);
                var marker = comp.Descendants().FirstOrDefault(d => d.IdPath == kv.Key);
                string name = marker?.InstanceName ?? kv.Key;
                if (t2.Count == 0)
                {
                    _log.Warn("CLEARANCE_NO_GEOMETRY", kv.Key, $"Clearance component '{name}' has no geometry.");
                    continue;
                }
                var o = RasterOutline.Build(t2, OutlineOptions);
                g.Clearances.Add(new Zone
                {
                    Name = name,
                    DefinedBy = kv.Key,
                    Shape = new Footprint { Source = "clearanceComponent", Accuracy = "tessellated", Resolution = o.EffectiveResolution, Regions = o.Regions }
                });
            }

            // 6. Primary footprint, by preference.
            g.Primary = g.Authored ?? g.Outline ?? g.ConvexHull;
            if (g.Primary == null)
                return FallbackFromRootBox(g, def, rep);

            // 7. Property-defined clearances (offsets from the footprint's bounding rectangle).
            AddPropertyClearances(def, g);
            return g;
        }

        private void CollectParts(RawComponent owner, RawComponent node, List<RawComponent> bodyParts, SortedDictionary<string, List<RawComponent>> markers)
        {
            var geo = _cfg.Geometry;
            if (node != owner)
            {
                if (!_classes.TryGetValue(node.IdPath, out var cls)) return;
                if (cls.ClearanceMarkerId != null)
                {
                    if (node.LoadState == LoadState.Suppressed) return;
                    if (node.DocKind == DocKind.Part)
                    {
                        if (!markers.TryGetValue(cls.ClearanceMarkerId, out var list)) markers[cls.ClearanceMarkerId] = list = new List<RawComponent>();
                        list.Add(node);
                    }
                    foreach (var ch in node.Children) CollectParts(owner, ch, bodyParts, markers);
                    return;
                }
                if (cls.ExcludedFromOwnerGeometry) return;
                if (node.LoadState == LoadState.Suppressed) return;
                if (geo.ExcludeEnvelopes && node.IsEnvelope) return;
                if (!geo.IncludeHiddenChildren && node.Visible == false) return;
            }
            if (node.DocKind == DocKind.Part) bodyParts.Add(node);
            else foreach (var ch in node.Children) CollectParts(owner, ch, bodyParts, markers);
        }

        private void AppendPartMesh(RawComponent part, Affine3 rootToLocal, List<double> target, DefinitionGeometry stats, bool countStats)
        {
            if (countStats) stats.PartsTotal++;
            if (part.TransformArray == null)
            {
                _log.Warn("TRANSFORM_UNAVAILABLE", part.IdPath, $"No transform for '{part.Name2}'; its geometry is skipped.");
                return;
            }
            MeshResult mesh;
            try { mesh = _source.GetMesh(part, _allowResolve); }
            catch (Exception ex)
            {
                _log.Warn("MESH_READ_FAILED", part.IdPath, $"Reading geometry of '{part.Name2}' failed: {ex.Message}");
                return;
            }
            _log.AddRange(mesh.Issues);
            if (!mesh.Success || mesh.Triangles == null || mesh.Triangles.Length < 9) return;
            if (countStats)
            {
                stats.PartsWithGeometry++;
                if (mesh.ResolvedForGeometry) stats.PartsResolved++;
                stats.MeshSources.Add(mesh.Source);
            }
            var partToLocal = rootToLocal.Multiply(Affine3.FromSolidWorksArray(part.TransformArray));
            var t = mesh.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var p = partToLocal.Apply(new Vec3(t[i], t[i + 1], t[i + 2]));
                target.Add(p.X); target.Add(p.Y); target.Add(p.Z);
            }
        }

        private static void ProjectTriangles(List<double> tris, LocalAxis up, List<double> tri2d, List<Vec2> pts, DefinitionGeometry g)
        {
            for (int i = 0; i + 8 < tris.Count; i += 9)
            {
                for (int k = 0; k < 3; k++)
                {
                    var p = new Vec3(tris[i + 3 * k], tris[i + 3 * k + 1], tris[i + 3 * k + 2]);
                    var f = up.ToFootprint(p);
                    tri2d.Add(f.X); tri2d.Add(f.Y);
                    pts.Add(new Vec2(f.X, f.Y));
                    if (g != null)
                    {
                        g.LocalBox.Add(p);
                        if (!g.HasHeight) { g.HMin = g.HMax = f.Z; g.HasHeight = true; }
                        else { if (f.Z < g.HMin) g.HMin = f.Z; if (f.Z > g.HMax) g.HMax = f.Z; }
                    }
                }
            }
        }

        private void ReadSketches(Definition def, DefinitionGeometry g)
        {
            var comp = def.Representative;
            var geo = _cfg.Geometry;
            SketchResult res;
            try { res = _source.GetLayoutSketches(comp, geo.FootprintSketchName, geo.ClearanceSketchPrefix, _allowResolve); }
            catch (Exception ex)
            {
                _log.Warn("SKETCH_READ_FAILED", comp.IdPath, $"Reading layout sketches of '{comp.Name2}' failed: {ex.Message}");
                return;
            }
            if (res == null) return;
            _log.AddRange(res.Issues);
            foreach (var sk in res.Sketches.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (sk.UnsupportedSegments > 0)
                    _log.Warn("SKETCH_SEGMENT_UNSUPPORTED", comp.IdPath, $"Sketch '{sk.Name}' has {sk.UnsupportedSegments} segment(s) other than lines/arcs/circles; they were skipped.");
                var loops3 = SketchLoops.ChainClosedLoops(sk.Polylines, 1e-5, out int open);
                if (open > 0)
                    _log.Warn("SKETCH_NOT_CLOSED", comp.IdPath, $"Sketch '{sk.Name}' has {open} open chain(s); only closed loops are used.");
                if (loops3.Count == 0) continue;
                var n = SketchLoops.Normal(loops3[0]);
                if (n.Length > 0.5 && Math.Abs(n.Dot(def.Up.Up)) < Math.Cos(1.0 * Math.PI / 180))
                    _log.Warn("SKETCH_NOT_FLOOR_PARALLEL", comp.IdPath, $"Sketch '{sk.Name}' is not perpendicular to the up axis {def.Up.Name}; it was projected onto the floor.");
                var loops2 = loops3.Select(l => l.Select(p => { var f = def.Up.ToFootprint(p); return new Vec2(f.X, f.Y); }).ToList()).ToList();
                var regions = SketchLoops.LoopsToRegions(loops2);
                if (regions.Count == 0) continue;
                var shape = new Footprint { Source = "authoredSketch", Accuracy = "exact", Regions = regions };

                if (string.Equals(sk.Name, geo.FootprintSketchName, StringComparison.OrdinalIgnoreCase))
                    g.Authored = shape;
                else
                {
                    string name = sk.Name.Substring(Math.Min(sk.Name.Length, geo.ClearanceSketchPrefix.Length)).Trim(' ', '_', '-', ':');
                    g.Clearances.Add(new Zone { Name = name.Length > 0 ? name : "default", DefinedBy = "sketch:" + sk.Name, Shape = shape });
                }
            }
        }

        private void AddPropertyClearances(Definition def, DefinitionGeometry g)
        {
            var p = _cfg.Properties;
            var comp = def.Representative;
            double? Read(string prop)
            {
                var v = comp.GetProperty(prop);
                if (v == null || string.IsNullOrWhiteSpace(v.Value)) return null;
                var d = UnitParser.ParseLength(v.Value, p.ClearanceDefaultUnit);
                if (d == null) _log.Warn("CLEARANCE_PROPERTY_INVALID", comp.IdPath, $"Property {prop}='{v.Value}' is not a length (e.g. 600, 600mm, 0.6m, 24in).");
                return d;
            }
            double? front = Read(p.ClearanceFront), back = Read(p.ClearanceBack), left = Read(p.ClearanceLeft), right = Read(p.ClearanceRight);
            if (front == null && back == null && left == null && right == null) return;
            var b = g.FootprintBounds();
            if (b == null) return;
            var (mn, mx) = b.Value;
            // Footprint axes: a = left→right, b = front→back. Front is the −b side.
            var ring = new List<Vec2>
            {
                new Vec2(mn.X - (left ?? 0), mn.Y - (front ?? 0)),
                new Vec2(mx.X + (right ?? 0), mn.Y - (front ?? 0)),
                new Vec2(mx.X + (right ?? 0), mx.Y + (back ?? 0)),
                new Vec2(mn.X - (left ?? 0), mx.Y + (back ?? 0)),
            };
            g.Clearances.Add(new Zone { Name = "properties", DefinedBy = "properties", Shape = Footprint.FromRing("properties", "approximate", ring) });
        }

        /// <summary>
        /// No usable tessellation: derive a box footprint from SolidWorks' root-aligned bounding box.
        /// Exact-ish when the component is rotated by multiples of 90° relative to the root, inflated otherwise.
        /// </summary>
        private DefinitionGeometry FallbackFromRootBox(DefinitionGeometry g, Definition def, Placement rep)
        {
            var comp = def.Representative;
            var box = Box3.FromCorners(comp.RootBox);
            if (box == null || box.IsEmpty)
            {
                _log.Warn("NO_GEOMETRY", comp.IdPath, $"No geometry or bounding box is available for '{comp.Name2}' ({comp.LoadState}{(comp.FileExists == false ? ", file not found" : "")}).");
                return g;
            }
            Affine3 rootToLocal;
            try { rootToLocal = rep.RootTransform.Inverse(); } catch (InvalidOperationException) { return g; }
            var local = new Box3();
            foreach (var c in box.Corners()) local.Add(rootToLocal.Apply(c));
            g.LocalBox = local;
            g.LocalBoxSource = "rootBoundingBox";
            double aMin = double.MaxValue, aMax = double.MinValue, bMin = double.MaxValue, bMax = double.MinValue;
            foreach (var c in local.Corners())
            {
                var f = def.Up.ToFootprint(c);
                aMin = Math.Min(aMin, f.X); aMax = Math.Max(aMax, f.X);
                bMin = Math.Min(bMin, f.Y); bMax = Math.Max(bMax, f.Y);
                if (!g.HasHeight) { g.HMin = g.HMax = f.Z; g.HasHeight = true; }
                else { g.HMin = Math.Min(g.HMin, f.Z); g.HMax = Math.Max(g.HMax, f.Z); }
            }
            bool axisAligned = IsAxisAligned(rep.RootTransform);
            var ring = new List<Vec2> { new Vec2(aMin, bMin), new Vec2(aMax, bMin), new Vec2(aMax, bMax), new Vec2(aMin, bMax) };
            g.Primary = Footprint.FromRing("rootBoundingBox", axisAligned ? "approximate" : "approximateInflated", ring);
            g.Primary.Complete = false;
            _log.Warn("GEOMETRY_FROM_BOUNDING_BOX", comp.IdPath,
                $"'{comp.Name2}' footprint uses SolidWorks' approximate bounding box{(axisAligned ? "" : " (component is rotated off the root axes, so the box is oversized)")}.");
            AddPropertyClearances(def, g);
            return g;
        }

        private static bool IsAxisAligned(Affine3 m)
        {
            double tol = Math.Cos(0.5 * Math.PI / 180);
            for (int c = 0; c < 3; c++)
            {
                var col = m.Column(c).Normalized();
                if (Math.Max(Math.Abs(col.X), Math.Max(Math.Abs(col.Y), Math.Abs(col.Z))) < tol) return false;
            }
            return true;
        }
    }
}
