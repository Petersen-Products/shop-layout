using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Json;

namespace ShopLayout.Core.Model
{
    /// <summary>
    /// Wraps a live source and records everything it returns, so the same extraction can be replayed
    /// offline (<see cref="SnapshotSource"/>) to tune classification rules or footprint settings without SolidWorks.
    /// </summary>
    public sealed class RecordingSource : IAssemblySource
    {
        private readonly IAssemblySource _inner;
        private RawAssembly _asm;
        private readonly SortedDictionary<string, MeshResult> _meshes = new SortedDictionary<string, MeshResult>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, SketchResult> _sketches = new SortedDictionary<string, SketchResult>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, double[]> _planes = new SortedDictionary<string, double[]>(StringComparer.Ordinal);

        public RecordingSource(IAssemblySource inner) { _inner = inner; }

        public string Description => _inner.Description;

        public RawAssembly ReadStructure() => _asm = _inner.ReadStructure();

        public MeshResult GetMesh(RawComponent part, bool allowResolve) => _meshes[part.IdPath] = _inner.GetMesh(part, allowResolve);

        public SketchResult GetLayoutSketches(RawComponent c, string f, string p, bool allowResolve) =>
            _sketches[c.IdPath] = _inner.GetLayoutSketches(c, f, p, allowResolve);

        public double[] GetReferencePlaneTransform(string name) => _planes[name ?? ""] = _inner.GetReferencePlaneTransform(name);

        public List<Issue> Finish() => _inner.Finish();

        public void Save(string path)
        {
            var o = new JsonObject()
                .Add("schema", "petersen.shop-layout-snapshot")
                .Add("schemaVersion", "1.0.0")
                .Add("description", _inner.Description)
                .Add("assembly", SnapshotIo.Write(_asm))
                .Add("meshes", ToObj(_meshes, SnapshotIo.Write))
                .Add("sketches", ToObj(_sketches, SnapshotIo.Write))
                .Add("planes", ToObj(_planes, a => a == null ? null : (object)a.Cast<object>().ToList()));
            File.WriteAllText(path, new JsonWriter(-1, false).Write(o));
        }

        private static JsonObject ToObj<T>(SortedDictionary<string, T> d, Func<T, object> f)
        {
            var o = new JsonObject();
            foreach (var kv in d) o.Add(kv.Key, f(kv.Value));
            return o;
        }
    }

    /// <summary>Replays a snapshot written by <see cref="RecordingSource"/>. Also used to build test fixtures.</summary>
    public sealed class SnapshotSource : IAssemblySource
    {
        private readonly JsonObject _doc;
        private readonly string _path;

        public SnapshotSource(string path)
        {
            _path = path;
            _doc = JsonParser.ParseObject(File.ReadAllText(path));
        }

        public string Description => "snapshot " + Path.GetFileName(_path) + " (" + _doc.GetString("description", "") + ")";

        public RawAssembly ReadStructure() => SnapshotIo.ReadAssembly(_doc.GetObject("assembly"));

        public MeshResult GetMesh(RawComponent part, bool allowResolve)
        {
            var m = _doc.GetObject("meshes")?.GetObject(part.IdPath);
            if (m == null)
                return new MeshResult { Issues = { Issue.Warn("SNAPSHOT_MISSING_MESH", part.IdPath, $"Snapshot has no mesh for '{part.Name2}' (it was not needed when the snapshot was recorded).") } };
            return SnapshotIo.ReadMesh(m);
        }

        public SketchResult GetLayoutSketches(RawComponent c, string f, string p, bool allowResolve)
        {
            var s = _doc.GetObject("sketches")?.GetObject(c.IdPath);
            return s == null ? new SketchResult() : SnapshotIo.ReadSketches(s);
        }

        public double[] GetReferencePlaneTransform(string name) => _doc.GetObject("planes")?.GetDoubleArray(name ?? "");

        public List<Issue> Finish() => new List<Issue>();
    }

    /// <summary>JSON (de)serialization of the raw model.</summary>
    public static class SnapshotIo
    {
        /// <summary>
        /// Makes a snapshot safe to share: the root assembly path becomes its bare file name, component
        /// paths lose their drive/share root (R:\LAYOUT\PARTS\x.SLDPRT → LAYOUT\PARTS\x.SLDPRT), and any
        /// original path quoted in messages or the description is replaced the same way. Geometry,
        /// IDs and names are untouched, so exports from the result differ only in paths.
        /// </summary>
        public static string Sanitize(string snapshotJson, out int pathsRewritten)
        {
            var doc = JsonParser.ParseObject(snapshotJson);
            var asm = doc.GetObject("assembly");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string root = asm?.GetString("rootFilePath") ?? "";
            string rootDir = string.IsNullOrEmpty(root) ? "" : Path.GetDirectoryName(root) ?? "";
            if (!string.IsNullOrEmpty(root)) map[root] = Path.GetFileName(root);

            void Collect(JsonObject c)
            {
                string p = c.GetString("filePath", "");
                if (!string.IsNullOrEmpty(p) && !map.ContainsKey(p))
                {
                    string pr = Path.GetPathRoot(p) ?? "";
                    map[p] = p.Substring(pr.Length);
                }
                foreach (var ch in (c.GetArray("children") ?? new List<object>()).OfType<JsonObject>()) Collect(ch);
            }
            foreach (var c in (asm?.GetArray("components") ?? new List<object>()).OfType<JsonObject>()) Collect(c);
            pathsRewritten = map.Count;

            // Longest first, so a folder never replaces part of a longer file path.
            var ordered = map.OrderByDescending(kv => kv.Key.Length).ToList();
            string Clean(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                if (map.TryGetValue(s, out var exact)) return exact;
                foreach (var kv in ordered) s = s.Replace(kv.Key, kv.Value);
                if (rootDir.Length > 0) s = s.Replace(rootDir, "");
                return s;
            }
            object Walk(object v)
            {
                switch (v)
                {
                    case string s: return Clean(s);
                    case JsonObject o:
                        var r = new JsonObject();
                        foreach (var kv in o.Items) r.Add(kv.Key, Walk(kv.Value));
                        return r;
                    case List<object> l:
                        return l.Select(Walk).ToList();
                    default: return v;
                }
            }
            var clean = (JsonObject)Walk(doc);
            clean.Add("description", (asm?.GetString("sourceApplication") ?? "SOLIDWORKS") + " " + (asm?.GetString("sourceVersion") ?? "") + " - " + Path.GetFileName(root) + " (paths sanitized)");
            return new JsonWriter(-1, false).Write(clean);
        }

        public static JsonObject Write(RawAssembly a) => new JsonObject()
            .Add("rootFilePath", a.RootFilePath)
            .Add("rootConfiguration", a.RootConfiguration)
            .Add("sourceApplication", a.SourceApplication)
            .Add("sourceVersion", a.SourceVersion)
            .Add("rootProperties", Write(a.RootProperties))
            .Add("readIssues", a.ReadIssues.Select(i => (object)i.ToJson()).ToList())
            .Add("components", a.TopLevel.Select(c => (object)Write(c)).ToList());

        public static JsonObject Write(RawComponent c) => new JsonObject()
            .Add("localId", c.LocalId)
            .Add("idPath", c.IdPath)
            .Add("idIsFallback", c.IdIsFallback)
            .Add("name2", c.Name2)
            .Add("instanceName", c.InstanceName)
            .Add("componentReference", c.ComponentReference)
            .Add("filePath", c.FilePath)
            .Add("fileExists", c.FileExists)
            .Add("docKind", c.DocKind.ToString())
            .Add("configuration", c.Configuration)
            .Add("loadState", c.LoadState.ToString())
            .Add("isLoaded", c.IsLoaded)
            .Add("visible", c.Visible)
            .Add("isEnvelope", c.IsEnvelope)
            .Add("isVirtual", c.IsVirtual)
            .Add("excludeFromBom", c.ExcludeFromBom)
            .Add("isFixed", c.IsFixed)
            .Add("isFlexible", c.IsFlexible)
            .Add("isPatternInstance", c.IsPatternInstance)
            .Add("isMirrored", c.IsMirrored)
            .Add("transform", c.TransformArray?.Cast<object>().ToList())
            .Add("rootBox", c.RootBox?.Cast<object>().ToList())
            .Add("propertiesAvailable", c.PropertiesAvailable)
            .Add("fileProperties", Write(c.FileProperties))
            .Add("configProperties", Write(c.ConfigProperties))
            .Add("readIssues", c.ReadIssues.Select(i => (object)i.ToJson()).ToList())
            .Add("children", c.Children.Select(ch => (object)Write(ch)).ToList());

        public static object Write(PropertySet s) =>
            s.All.Select(p => (object)new List<object> { p.Name, p.Raw, p.Value }).ToList();

        public static object Write(MeshResult m) => new JsonObject()
            .Add("success", m.Success)
            .Add("source", m.Source)
            .Add("frameDetection", m.FrameDetection)
            .Add("resolvedForGeometry", m.ResolvedForGeometry)
            .Add("issues", m.Issues.Select(i => (object)i.ToJson()).ToList())
            .Add("triangles", m.Triangles.Select(f => (object)(double)f).ToList());

        public static object Write(SketchResult s) => new JsonObject()
            .Add("modelAvailable", s.ModelAvailable)
            .Add("issues", s.Issues.Select(i => (object)i.ToJson()).ToList())
            .Add("sketches", s.Sketches.Select(k => (object)new JsonObject()
                .Add("name", k.Name)
                .Add("unsupportedSegments", k.UnsupportedSegments)
                .Add("polylines", k.Polylines.Select(pl => (object)pl.SelectMany(p => new object[] { p.X, p.Y, p.Z }).ToList()).ToList())).ToList());

        public static RawAssembly ReadAssembly(JsonObject o)
        {
            var a = new RawAssembly
            {
                RootFilePath = o.GetString("rootFilePath"),
                RootConfiguration = o.GetString("rootConfiguration"),
                SourceApplication = o.GetString("sourceApplication", "SOLIDWORKS"),
                SourceVersion = o.GetString("sourceVersion"),
                RootProperties = ReadProps(o.GetArray("rootProperties")),
                ReadIssues = ReadIssues(o.GetArray("readIssues"))
            };
            foreach (var c in (o.GetArray("components") ?? new List<object>()).OfType<JsonObject>())
                a.TopLevel.Add(ReadComponent(c));
            return a;
        }

        public static RawComponent ReadComponent(JsonObject o)
        {
            var c = new RawComponent
            {
                LocalId = o.GetInt("localId", 0),
                IdPath = o.GetString("idPath"),
                IdIsFallback = o.GetBool("idIsFallback", false),
                Name2 = o.GetString("name2", ""),
                InstanceName = o.GetString("instanceName", ""),
                ComponentReference = o.GetString("componentReference"),
                FilePath = o.GetString("filePath", ""),
                FileExists = o.GetBoolOrNull("fileExists"),
                DocKind = Enum.TryParse(o.GetString("docKind", "Other"), out DocKind dk) ? dk : DocKind.Other,
                Configuration = o.GetString("configuration", ""),
                LoadState = Enum.TryParse(o.GetString("loadState", "Unknown"), out LoadState ls) ? ls : LoadState.Unknown,
                IsLoaded = o.GetBoolOrNull("isLoaded"),
                Visible = o.GetBoolOrNull("visible"),
                IsEnvelope = o.GetBool("isEnvelope", false),
                IsVirtual = o.GetBool("isVirtual", false),
                ExcludeFromBom = o.GetBool("excludeFromBom", false),
                IsFixed = o.GetBool("isFixed", false),
                IsFlexible = o.GetBool("isFlexible", false),
                IsPatternInstance = o.GetBool("isPatternInstance", false),
                IsMirrored = o.GetBool("isMirrored", false),
                TransformArray = o.GetDoubleArray("transform"),
                RootBox = o.GetDoubleArray("rootBox"),
                PropertiesAvailable = o.GetBool("propertiesAvailable", false),
                FileProperties = ReadProps(o.GetArray("fileProperties")),
                ConfigProperties = ReadProps(o.GetArray("configProperties")),
                ReadIssues = ReadIssues(o.GetArray("readIssues"))
            };
            foreach (var ch in (o.GetArray("children") ?? new List<object>()).OfType<JsonObject>())
                c.Children.Add(ReadComponent(ch));
            return c;
        }

        public static MeshResult ReadMesh(JsonObject o)
        {
            var tris = o.GetArray("triangles") ?? new List<object>();
            return new MeshResult
            {
                Success = o.GetBool("success", false),
                Source = o.GetString("source", "none"),
                FrameDetection = o.GetString("frameDetection"),
                ResolvedForGeometry = o.GetBool("resolvedForGeometry", false),
                Issues = ReadIssues(o.GetArray("issues")),
                Triangles = tris.Select(x => x is double d ? (float)d : 0f).ToArray()
            };
        }

        public static SketchResult ReadSketches(JsonObject o)
        {
            var r = new SketchResult { ModelAvailable = o.GetBool("modelAvailable", false), Issues = ReadIssues(o.GetArray("issues")) };
            foreach (var s in (o.GetArray("sketches") ?? new List<object>()).OfType<JsonObject>())
            {
                var sk = new RawSketch { Name = s.GetString("name", ""), UnsupportedSegments = s.GetInt("unsupportedSegments", 0) };
                foreach (var pl in (s.GetArray("polylines") ?? new List<object>()).OfType<List<object>>())
                {
                    var pts = new List<Vec3>();
                    for (int i = 0; i + 2 < pl.Count; i += 3)
                        pts.Add(new Vec3((double)pl[i], (double)pl[i + 1], (double)pl[i + 2]));
                    sk.Polylines.Add(pts);
                }
                r.Sketches.Add(sk);
            }
            return r;
        }

        private static PropertySet ReadProps(List<object> arr)
        {
            var s = new PropertySet();
            if (arr == null) return s;
            foreach (var e in arr.OfType<List<object>>())
                if (e.Count >= 3) s.Set(e[0] as string, e[1] as string, e[2] as string);
            return s;
        }

        private static List<Issue> ReadIssues(List<object> arr)
        {
            var list = new List<Issue>();
            if (arr == null) return list;
            foreach (var o in arr.OfType<JsonObject>())
            {
                string sev = o.GetString("severity", "warning");
                var s = sev == "error" ? Severity.Error : sev == "info" ? Severity.Info : Severity.Warning;
                list.Add(new Issue(s, o.GetString("code", ""), o.GetString("componentId"), o.GetString("message", "")));
            }
            return list;
        }
    }
}
