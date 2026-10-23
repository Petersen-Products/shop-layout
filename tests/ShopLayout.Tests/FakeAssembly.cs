using System;
using System.Collections.Generic;
using System.Linq;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Model;

namespace ShopLayout.Tests
{
    /// <summary>In-memory stand-in for SolidWorks: components, meshes (local frame) and sketches.</summary>
    internal sealed class FakeSource : IAssemblySource
    {
        public RawAssembly Assembly = new RawAssembly { RootFilePath = @"C:\Shop\Shop Floor.SLDASM", RootConfiguration = "Default", SourceVersion = "33.2.0" };
        public Dictionary<string, float[]> MeshesByFile = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<RawSketch>> SketchesByFile = new Dictionary<string, List<RawSketch>>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ResolveRequests = new HashSet<string>();
        public bool Finished;

        public string Description => "fake";
        public RawAssembly ReadStructure() => Assembly;

        public MeshResult GetMesh(RawComponent part, bool allowResolve)
        {
            if (part.LoadState == LoadState.Lightweight && part.FilePath.Contains("Lightweight"))
            {
                if (allowResolve) ResolveRequests.Add(part.IdPath);
                return new MeshResult { Issues = { Issue.Warn("NO_TESSELLATION", part.IdPath, "lightweight, no data") } };
            }
            if (!MeshesByFile.TryGetValue(part.FilePath, out var m)) return new MeshResult();
            return new MeshResult { Success = true, Triangles = m, Source = "faceTessellation", FrameDetection = "local" };
        }

        public SketchResult GetLayoutSketches(RawComponent c, string f, string p, bool allowResolve)
        {
            var r = new SketchResult { ModelAvailable = true };
            if (SketchesByFile.TryGetValue(c.FilePath, out var list)) r.Sketches.AddRange(list);
            return r;
        }

        public double[] GetReferencePlaneTransform(string name) => null;
        public List<Issue> Finish() { Finished = true; return new List<Issue>(); }
    }

    internal static class Fake
    {
        public static double[] Transform(double rotZDeg, double x, double y, double z = 0)
        {
            double a = rotZDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            // local X image, local Y image, local Z image, translation, scale
            return new[] { c, s, 0, -s, c, 0, 0, 0, 1, x, y, z, 1, 0, 0, 0 };
        }

        public static double[] Identity() => Transform(0, 0, 0);

        public static RawComponent Comp(int id, string name, string file, double[] transform, DocKind kind = DocKind.Part, params RawComponent[] children)
        {
            var c = new RawComponent
            {
                LocalId = id,
                Name2 = name,
                InstanceName = name.Contains("/") ? name.Substring(name.LastIndexOf('/') + 1) : name,
                FilePath = file,
                FileExists = true,
                DocKind = kind,
                Configuration = "Default",
                LoadState = LoadState.Resolved,
                Visible = true,
                TransformArray = transform,
                PropertiesAvailable = true
            };
            c.Children.AddRange(children);
            return c;
        }

        /// <summary>Axis-aligned box mesh (12 triangles) from min to max.</summary>
        public static float[] Box(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            var v = new[]
            {
                new Vec3(x0,y0,z0), new Vec3(x1,y0,z0), new Vec3(x1,y1,z0), new Vec3(x0,y1,z0),
                new Vec3(x0,y0,z1), new Vec3(x1,y0,z1), new Vec3(x1,y1,z1), new Vec3(x0,y1,z1)
            };
            int[] f = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
            return f.SelectMany(i => new[] { (float)v[i].X, (float)v[i].Y, (float)v[i].Z }).ToArray();
        }

        public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

        /// <summary>Root box of a local box placed with a transform (axis-aligned in root).</summary>
        public static double[] RootBox(double[] transform, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            var m = Affine3.FromSolidWorksArray(transform);
            var b = new Box3();
            var lb = new Box3();
            lb.Add(new Vec3(x0, y0, z0)); lb.Add(new Vec3(x1, y1, z1));
            foreach (var c in lb.Corners()) b.Add(m.Apply(c));
            return new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z };
        }

        public static List<Vec3> Line(double x0, double y0, double x1, double y1) => new List<Vec3> { new Vec3(x0, y0, 0), new Vec3(x1, y1, 0) };
    }
}
