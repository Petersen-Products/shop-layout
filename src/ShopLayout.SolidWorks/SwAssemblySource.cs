using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ShopLayout.SolidWorks
{
    public sealed class SwSourceOptions
    {
        /// <summary>Set components that this exporter resolved back to lightweight in <see cref="SwAssemblySource.Finish"/>.</summary>
        public bool RestoreLightweight = true;
        /// <summary>Box deviation (m) under which a tessellation-frame check counts as a clear match.</summary>
        public double FrameMatchTolerance = 0.02;
        public double ArcChordTolerance = 0.002;
        public Action<string> Progress;
    }

    /// <summary>
    /// Reads a SolidWorks assembly through the API. Read-only except for one opt-in, temporary action:
    /// resolving a lightweight component when its geometry is otherwise unavailable (undone in Finish when
    /// RestoreLightweight is set). It never saves, rebuilds, moves, or changes configurations.
    /// Usable from the console host (out-of-process) or from an add-in (pass the add-in's ISldWorks).
    /// </summary>
    public sealed class SwAssemblySource : IAssemblySource
    {
        private readonly SldWorks _app;
        private readonly SwSourceOptions _opt;
        private ModelDoc2 _doc;
        private bool _saveFlagBefore;
        private readonly Dictionary<string, Component2> _components = new Dictionary<string, Component2>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, Component2>> _resolvedByUs = new List<KeyValuePair<string, Component2>>();
        private readonly Dictionary<string, MeshResult> _meshCache = new Dictionary<string, MeshResult>(StringComparer.OrdinalIgnoreCase);
        private int _readCount;

        public SwAssemblySource(SldWorks app, SwSourceOptions options = null, ModelDoc2 document = null)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _opt = options ?? new SwSourceOptions();
            _doc = document;
        }

        public string Description => "SOLIDWORKS " + Safe(() => _app.RevisionNumber(), "?") + (_doc != null ? " – " + Safe(() => _doc.GetTitle(), "") : "");

        // ------------------------------------------------------------------ structure

        public RawAssembly ReadStructure()
        {
            _doc = _doc ?? _app.ActiveDoc as ModelDoc2;
            if (_doc == null)
                throw new InvalidOperationException("SolidWorks has no active document. Open the shop floor assembly and make it the active window.");
            if (_doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException($"The active document '{_doc.GetTitle()}' is not an assembly.");

            _saveFlagBefore = Safe(() => _doc.GetSaveFlag(), false);
            var cfg = _doc.ConfigurationManager.ActiveConfiguration;
            var asm = new RawAssembly
            {
                RootFilePath = Safe(() => _doc.GetPathName(), ""),
                RootConfiguration = Safe(() => cfg.Name, ""),
                SourceApplication = "SOLIDWORKS",
                SourceVersion = Safe(() => _app.RevisionNumber(), "")
            };
            if (string.IsNullOrEmpty(asm.RootFilePath))
                asm.ReadIssues.Add(Issue.Warn("ROOT_NOT_SAVED", null, "The assembly has never been saved, so file paths cannot be made relative to it."));

            ReadProperties(Safe(() => _doc.Extension.get_CustomPropertyManager(""), null), asm.RootProperties);
            ReadProperties(Safe(() => _doc.Extension.get_CustomPropertyManager(asm.RootConfiguration), null), asm.RootProperties);

            // Resolve=false: never load extra components just to traverse.
            var root = cfg.GetRootComponent3(false);
            if (root == null) throw new InvalidOperationException("Could not get the root component of the active configuration.");
            foreach (var child in Children(root))
                asm.TopLevel.Add(ReadComponent(child, null));
            return asm;
        }

        private RawComponent ReadComponent(Component2 c, RawComponent parent)
        {
            var r = new RawComponent();
            void AddReadIssue(string what, Exception ex) =>
                r.ReadIssues.Add(Issue.Warn("API_READ_FAILED", null, $"Could not read {what} of '{r.Name2}': {ex.Message}"));
            T Try<T>(string what, Func<T> f, T fallback)
            {
                try { return f(); }
                catch (Exception ex) { AddReadIssue(what, ex); return fallback; }
            }

            r.Name2 = Try("name", () => c.Name2, "") ?? "";
            r.InstanceName = r.Name2.IndexOf('/') >= 0 ? r.Name2.Substring(r.Name2.LastIndexOf('/') + 1) : r.Name2;
            int id = Try("component ID", () => c.GetID(), -1);
            r.LocalId = id;
            if (id < 0)
            {
                r.IdIsFallback = true;
                r.IdPath = (parent != null ? parent.IdPath + "/" : "") + "n:" + r.InstanceName;
            }
            else r.IdPath = (parent != null ? parent.IdPath + "/" : "") + id;
            foreach (var i in r.ReadIssues) i.ComponentId = r.IdPath;

            if (++_readCount % 50 == 0) _opt.Progress?.Invoke($"Read {_readCount} components…");

            r.ComponentReference = Try("component reference", () => c.ComponentReference, "");
            r.FilePath = Try("path", () => c.GetPathName(), "") ?? "";
            r.FileExists = string.IsNullOrEmpty(r.FilePath) ? (bool?)null : File.Exists(r.FilePath);
            string ext = Path.GetExtension(r.FilePath ?? "").ToLowerInvariant();
            r.DocKind = ext == ".sldprt" ? DocKind.Part : ext == ".sldasm" ? DocKind.Assembly : DocKind.Other;
            r.Configuration = Try("referenced configuration", () => c.ReferencedConfiguration, "") ?? "";

            int supp = Try("suppression state", () => c.GetSuppression2(), -1);
            switch (supp)
            {
                case (int)swComponentSuppressionState_e.swComponentSuppressed: r.LoadState = LoadState.Suppressed; break;
                case (int)swComponentSuppressionState_e.swComponentLightweight:
                case (int)swComponentSuppressionState_e.swComponentFullyLightweight: r.LoadState = LoadState.Lightweight; break;
                case (int)swComponentSuppressionState_e.swComponentResolved:
                case (int)swComponentSuppressionState_e.swComponentFullyResolved: r.LoadState = LoadState.Resolved; break;
                case (int)swComponentSuppressionState_e.swComponentInternalIdMismatch: r.LoadState = LoadState.IdMismatch; break;
                default: r.LoadState = LoadState.Unknown; break;
            }
            if (r.FileExists == false && r.LoadState != LoadState.Suppressed)
                r.ReadIssues.Add(Issue.Warn("UNRESOLVED_REFERENCE", r.IdPath, $"'{r.Name2}' references a file that is not on disk; geometry comes only from what SolidWorks still has in memory."));

            r.IsLoaded = Try<bool?>("load state", () => c.IsLoaded(), null);
            int vis = Try("visibility", () => c.Visible, -1);
            r.Visible = vis == (int)swComponentVisibilityState_e.swComponentVisible ? true : vis == (int)swComponentVisibilityState_e.swComponentHidden ? false : (bool?)null;
            r.IsEnvelope = Try("envelope flag", () => c.IsEnvelope(), false);
            r.IsVirtual = Try("virtual flag", () => c.IsVirtual, false);
            r.ExcludeFromBom = Try("BOM flag", () => c.ExcludeFromBOM, false);
            r.IsFixed = Try("fixed flag", () => c.IsFixed(), false);
            r.IsFlexible = Try("solving mode", () => c.Solving, 0) == (int)swComponentSolvingOption_e.swComponentFlexibleSolving;
            r.IsPatternInstance = Try("pattern flag", () => c.IsPatternInstance(), false);
            r.IsMirrored = Try("mirror flag", () => c.IsMirrored(), false);

            if (r.LoadState != LoadState.Suppressed)
            {
                r.TransformArray = Try("transform", () => (c.Transform2?.ArrayData as double[])?.ToArray(), null);
                r.RootBox = Try("bounding box", () => (c.GetBox(false, false) as double[])?.ToArray(), null);
            }

            // Properties: through the component first (works for lightweight components), then the model document.
            bool got = false;
            got |= ReadProperties(Try("file properties", () => c.get_CustomPropertyManager("") as CustomPropertyManager, null), r.FileProperties);
            if (!string.IsNullOrEmpty(r.Configuration))
                got |= ReadProperties(Try("configuration properties", () => c.get_CustomPropertyManager(r.Configuration) as CustomPropertyManager, null), r.ConfigProperties);
            if (!got && r.LoadState == LoadState.Resolved)
            {
                var md = Try("model document", () => c.GetModelDoc2() as ModelDoc2, null);
                if (md != null)
                {
                    got |= ReadProperties(Try("file properties", () => md.Extension.get_CustomPropertyManager(""), null), r.FileProperties);
                    got |= ReadProperties(Try("configuration properties", () => md.Extension.get_CustomPropertyManager(r.Configuration), null), r.ConfigProperties);
                }
            }
            r.PropertiesAvailable = got;

            _components[r.IdPath] = c;
            if (r.LoadState != LoadState.Suppressed)
                foreach (var ch in Children(c))
                    r.Children.Add(ReadComponent(ch, r));
            return r;
        }

        private static IEnumerable<Component2> Children(Component2 c)
        {
            object[] arr;
            try { arr = c.GetChildren() as object[]; }
            catch (Exception) { arr = null; }
            return arr == null ? Enumerable.Empty<Component2>() : arr.OfType<Component2>();
        }

        /// <returns>True when the manager was available (even if it holds no properties).</returns>
        private static bool ReadProperties(CustomPropertyManager mgr, PropertySet target)
        {
            if (mgr == null) return false;
            object namesObj;
            try { namesObj = mgr.GetNames(); }
            catch (Exception) { return false; }
            if (!(namesObj is object[] names)) return true;
            foreach (var n in names.OfType<string>())
            {
                try
                {
                    mgr.Get6(n, false, out string raw, out string resolved, out bool _, out bool _);
                    target.Set(n, raw, string.IsNullOrEmpty(resolved) ? raw : resolved);
                }
                catch (Exception) { /* one unreadable property must not stop the export */ }
            }
            return true;
        }

        // ------------------------------------------------------------------ geometry

        public MeshResult GetMesh(RawComponent part, bool allowResolve)
        {
            var result = new MeshResult();
            if (!_components.TryGetValue(part.IdPath, out var c))
            {
                result.Issues.Add(Issue.Warn("COMPONENT_NOT_FOUND", part.IdPath, $"Component '{part.Name2}' is no longer available in the session."));
                return result;
            }
            string cacheKey = (part.FilePath ?? "") + "|" + (part.Configuration ?? "");
            if (!part.IsVirtual && _meshCache.TryGetValue(cacheKey, out var cached))
                return new MeshResult { Success = cached.Success, Triangles = cached.Triangles, Source = cached.Source, FrameDetection = cached.FrameDetection };

            bool hasUserBodies;
            var tris = ReadBodyTriangles(c, out hasUserBodies);
            result.Source = "faceTessellation";

            if (tris.Count == 0 && part.LoadState == LoadState.Lightweight)
            {
                var lw = Safe(() => c.GetTessTriangles(true) as float[], null);
                if (lw != null && lw.Length >= 9) { tris.AddRange(lw); result.Source = "componentTessellation"; }
            }
            if (tris.Count == 0 && part.LoadState == LoadState.Lightweight)
            {
                if (allowResolve && Resolve(part, c, result.Issues))
                {
                    result.ResolvedForGeometry = true;
                    tris = ReadBodyTriangles(c, out hasUserBodies);
                    result.Source = "faceTessellation";
                }
                else if (!allowResolve)
                    result.Issues.Add(Issue.Info("LIGHTWEIGHT_NOT_RESOLVED", part.IdPath, $"'{part.Name2}' is lightweight and returned no geometry; it was not resolved (validation mode or resolveLightweight=false)."));
            }
            if (tris.Count < 9)
            {
                result.Issues.Add(Issue.Warn("NO_TESSELLATION", part.IdPath, $"'{part.Name2}' returned no solid-body tessellation ({part.LoadState})."));
                return result;
            }

            var arr = tris.ToArray();
            result.FrameDetection = NormalizeFrame(part, arr, result.Issues);
            result.Triangles = arr;
            result.Success = true;
            if (hasUserBodies)
                result.Issues.Add(Issue.Info("ASSEMBLY_LEVEL_BODIES", part.IdPath, $"'{part.Name2}' has bodies modified by assembly features; its geometry was read per instance."));
            else if (!part.IsVirtual)
                _meshCache[cacheKey] = result;
            return result;
        }

        private static List<float> ReadBodyTriangles(Component2 c, out bool hasUserBodies)
        {
            hasUserBodies = false;
            var tris = new List<float>();
            object info;
            object[] bodies;
            try { bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[]; }
            catch (Exception) { return tris; }
            if (bodies == null) return tris;
            if (info is int[] kinds && kinds.Any(k => k == 0)) hasUserBodies = true;
            foreach (var b in bodies.OfType<Body2>())
            {
                object[] faces;
                try { faces = b.GetFaces() as object[]; }
                catch (Exception) { continue; }
                if (faces == null) continue;
                foreach (var f in faces.OfType<Face2>())
                {
                    try
                    {
                        if (f.GetTessTriangles(true) is float[] t) tris.AddRange(t);
                    }
                    catch (Exception) { /* skip faces that cannot be tessellated */ }
                }
            }
            return tris;
        }

        /// <summary>
        /// Body tessellation obtained through a component is expected in the part's own frame. Rather than
        /// trusting that, compare both interpretations with SolidWorks' root-space bounding box and convert
        /// to the local frame when the data turns out to be in root coordinates.
        /// </summary>
        private string NormalizeFrame(RawComponent part, float[] t, List<Issue> issues)
        {
            if (part.TransformArray == null) return "assumedLocal";
            var rootBox = Box3.FromCorners(part.RootBox);
            if (rootBox == null || rootBox.IsEmpty) return "assumedLocal";
            var m = Affine3.FromSolidWorksArray(part.TransformArray);
            var raw = new Box3();
            var xf = new Box3();
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var p = new Vec3(t[i], t[i + 1], t[i + 2]);
                raw.Add(p);
                xf.Add(m.Apply(p));
            }
            double dLocal = xf.MaxDeviation(rootBox);
            double dRoot = raw.MaxDeviation(rootBox);
            double tol = Math.Max(_opt.FrameMatchTolerance, 0.05 * rootBox.Size.Length);
            if (Math.Min(dLocal, dRoot) > tol)
                issues.Add(Issue.Info("FRAME_CHECK_INCONCLUSIVE", part.IdPath,
                    $"Tessellation of '{part.Name2}' matches SolidWorks' bounding box only within {Math.Min(dLocal, dRoot):0.###} m; assumed {(dLocal <= dRoot ? "part" : "root")} coordinates."));
            if (dLocal <= dRoot) return "local";

            var inv = m.Inverse();
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var p = inv.Apply(new Vec3(t[i], t[i + 1], t[i + 2]));
                t[i] = (float)p.X; t[i + 1] = (float)p.Y; t[i + 2] = (float)p.Z;
            }
            return "root";
        }

        private bool Resolve(RawComponent part, Component2 c, List<Issue> issues)
        {
            int r;
            try { r = c.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved); }
            catch (Exception ex)
            {
                issues.Add(Issue.Warn("LIGHTWEIGHT_RESOLVE_FAILED", part.IdPath, $"Resolving '{part.Name2}' failed: {ex.Message}"));
                return false;
            }
            if (r != (int)swSuppressionError_e.swSuppressionChangeOk)
            {
                issues.Add(Issue.Warn("LIGHTWEIGHT_RESOLVE_FAILED", part.IdPath, $"SolidWorks refused to resolve '{part.Name2}' (code {r})."));
                return false;
            }
            _resolvedByUs.Add(new KeyValuePair<string, Component2>(part.IdPath, c));
            issues.Add(Issue.Info("LIGHTWEIGHT_RESOLVED", part.IdPath, $"'{part.Name2}' was temporarily resolved to read its geometry."));
            return true;
        }

        public SketchResult GetLayoutSketches(RawComponent component, string footprintName, string clearancePrefix, bool allowResolve)
        {
            var res = new SketchResult();
            if (!_components.TryGetValue(component.IdPath, out var c)) return res;
            var md = Safe(() => c.GetModelDoc2() as ModelDoc2, null);
            if (md == null && component.LoadState == LoadState.Lightweight && allowResolve && Resolve(component, c, res.Issues))
            {
                res.ResolvedForSketches = true;
                md = Safe(() => c.GetModelDoc2() as ModelDoc2, null);
            }
            if (md == null)
            {
                if (component.LoadState == LoadState.Lightweight)
                    res.Issues.Add(Issue.Info("SKETCHES_UNAVAILABLE", component.IdPath, $"'{component.Name2}' is lightweight, so layout sketches ({footprintName}, {clearancePrefix}*) could not be checked."));
                return res;
            }
            res.ModelAvailable = true;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(Feature f)
            {
                string type = Safe(() => f.GetTypeName2(), "");
                if (type != "ProfileFeature" && type != "3DProfileFeature") return;
                string name = Safe(() => f.Name, "") ?? "";
                bool isFootprint = string.Equals(name, footprintName, StringComparison.OrdinalIgnoreCase);
                bool isClearance = !string.IsNullOrEmpty(clearancePrefix) && name.StartsWith(clearancePrefix, StringComparison.OrdinalIgnoreCase);
                if ((!isFootprint && !isClearance) || !seen.Add(name)) return;
                var sketch = Safe(() => f.GetSpecificFeature2() as Sketch, null);
                if (sketch == null) return;
                res.Sketches.Add(ReadSketch(sketch, name, component.IdPath, res.Issues));
            }

            var feat = Safe(() => md.FirstFeature() as Feature, null);
            int guard = 0;
            while (feat != null && guard++ < 100000)
            {
                Visit(feat);
                var sub = Safe(() => feat.GetFirstSubFeature() as Feature, null);
                while (sub != null)
                {
                    Visit(sub);
                    var cur = sub;
                    sub = Safe(() => cur.GetNextSubFeature() as Feature, null);
                }
                var f0 = feat;
                feat = Safe(() => f0.GetNextFeature() as Feature, null);
            }
            return res;
        }

        private RawSketch ReadSketch(Sketch sketch, string name, string componentId, List<Issue> issues)
        {
            var rs = new RawSketch { Name = name };
            Affine3 toModel;
            try
            {
                var arr = sketch.ModelToSketchTransform.ArrayData as double[];
                toModel = Affine3.FromSolidWorksArray(arr).Inverse();
            }
            catch (Exception ex)
            {
                issues.Add(Issue.Warn("SKETCH_READ_FAILED", componentId, $"Could not read the transform of sketch '{name}': {ex.Message}"));
                return rs;
            }

            var segs = Safe(() => sketch.GetSketchSegments() as object[], null);
            if (segs == null) return rs;
            foreach (var seg in segs.OfType<SketchSegment>())
            {
                try
                {
                    if (seg.ConstructionGeometry) continue;
                    int type = seg.GetType();
                    if (type == (int)swSketchSegments_e.swSketchLINE)
                    {
                        var ln = (SketchLine)seg;
                        var a = (SketchPoint)ln.GetStartPoint2();
                        var b = (SketchPoint)ln.GetEndPoint2();
                        rs.Polylines.Add(new List<Vec3> { toModel.Apply(new Vec3(a.X, a.Y, a.Z)), toModel.Apply(new Vec3(b.X, b.Y, b.Z)) });
                    }
                    else if (type == (int)swSketchSegments_e.swSketchARC)
                    {
                        var arc = (SketchArc)seg;
                        var cp = (SketchPoint)arc.GetCenterPoint2();
                        var sp = (SketchPoint)arc.GetStartPoint2();
                        var ep = (SketchPoint)arc.GetEndPoint2();
                        bool circle = arc.IsCircle() == 1;
                        var pts = SketchLoops.DiscretizeArc(new Vec2(cp.X, cp.Y), new Vec2(sp.X, sp.Y), circle ? new Vec2(sp.X, sp.Y) : new Vec2(ep.X, ep.Y),
                            arc.GetRotationDir() == 1, _opt.ArcChordTolerance);
                        rs.Polylines.Add(pts.Select(p => toModel.Apply(new Vec3(p.X, p.Y, sp.Z))).ToList());
                    }
                    else if (type != (int)swSketchSegments_e.swSketchTEXT)
                        rs.UnsupportedSegments++;
                }
                catch (Exception)
                {
                    rs.UnsupportedSegments++;
                }
            }
            return rs;
        }

        public double[] GetReferencePlaneTransform(string planeName)
        {
            if (_doc == null || string.IsNullOrEmpty(planeName)) return null;
            var feat = Safe(() => _doc.FirstFeature() as Feature, null);
            while (feat != null)
            {
                if (Safe(() => feat.GetTypeName2(), "") == "RefPlane" && string.Equals(Safe(() => feat.Name, ""), planeName, StringComparison.OrdinalIgnoreCase))
                {
                    var plane = Safe(() => feat.GetSpecificFeature2() as RefPlane, null);
                    return Safe(() => (plane?.Transform?.ArrayData as double[])?.ToArray(), null);
                }
                var cur = feat;
                feat = Safe(() => cur.GetNextFeature() as Feature, null);
            }
            return null;
        }

        // ------------------------------------------------------------------ cleanup

        public List<Issue> Finish()
        {
            var issues = new List<Issue>();
            if (_opt.RestoreLightweight && _resolvedByUs.Count > 0)
            {
                int restored = 0;
                for (int i = _resolvedByUs.Count - 1; i >= 0; i--)
                {
                    var kv = _resolvedByUs[i];
                    int r = Safe(() => kv.Value.SetSuppression2((int)swComponentSuppressionState_e.swComponentLightweight), -1);
                    if (r == (int)swSuppressionError_e.swSuppressionChangeOk) restored++;
                    else issues.Add(Issue.Warn("LIGHTWEIGHT_RESTORE_FAILED", kv.Key, "Could not set the component back to lightweight; it stays resolved in this session."));
                }
                issues.Add(Issue.Info("LIGHTWEIGHT_RESTORED", null, $"{restored} of {_resolvedByUs.Count} temporarily resolved component(s) were set back to lightweight."));
            }
            else if (_resolvedByUs.Count > 0)
                issues.Add(Issue.Info("LIGHTWEIGHT_LEFT_RESOLVED", null, $"{_resolvedByUs.Count} component(s) were resolved for geometry and left resolved (restoreLightweight=false)."));
            _resolvedByUs.Clear();

            if (_doc != null && !_saveFlagBefore && Safe(() => _doc.GetSaveFlag(), false))
                issues.Add(Issue.Warn("DOCUMENT_MARKED_MODIFIED", null,
                    "SolidWorks now flags the assembly as modified (resolving lightweight components does this). The exporter saved nothing; close without saving, or keep the resolved state if you prefer."));
            return issues;
        }

        private static T Safe<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception) { return fallback; }
        }
    }
}
