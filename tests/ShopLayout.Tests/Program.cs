using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ShopLayout.Core.Classification;
using ShopLayout.Core.Config;
using ShopLayout.Core.Export;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Json;
using ShopLayout.Core.Model;

namespace ShopLayout.Tests
{
    internal static partial class Program
    {
        private static int _pass, _fail;
        private static string _section;
        private static readonly List<string> Failures = new List<string>();

        private static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma: proves output is culture-invariant
            Run("JSON writer/parser", JsonTests);
            Run("Transforms and floor frames", TransformTests);
            Run("Raster outline", OutlineTests);
            Run("Hull, rectangle, sketches", ShapeTests);
            Run("Classification", ClassificationTests);
            Run("Pipeline end-to-end", PipelineTests);
            Run("Floor plane XZ", FloorXzTests);
            Run("Snapshot round trip", SnapshotTests);
            Run("diagrams.net import", DrawioTests);

            Console.WriteLine();
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            foreach (var f in Failures) Console.WriteLine("  FAIL " + f);
            if (args.Length > 0 && args[0] == "--write-sample") WriteSample(args.Length > 1 ? args[1] : "sample_output");
            return _fail == 0 ? 0 : 1;
        }

        private static void Run(string name, Action a)
        {
            _section = name;
            Console.WriteLine("== " + name);
            try { a(); }
            catch (Exception ex) { Check(false, "threw " + ex); }
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Failures.Add($"[{_section}] {what}"); Console.WriteLine("   FAIL: " + what); }
        }

        private static void Near(double actual, double expected, double tol, string what) =>
            Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected} ± {tol}, got {actual}");

        // ------------------------------------------------------------------------------------------

        private static void JsonTests()
        {
            var o = new JsonObject().Add("b", 1.0).Add("a", -0.0).Add("c", 0.1 + 0.2).Add("s", "q\"\\\n").Add("arr", new List<object> { 1.5, true, null });
            string s = JsonWriter.Serialize(o, 6, false);
            Check(s == "{\"b\":1,\"a\":0,\"c\":0.3,\"s\":\"q\\\"\\\\\\n\",\"arr\":[1.5,true,null]}", "deterministic compact output: " + s);
            var back = JsonParser.ParseObject("// comment\n{ \"x\": [1, 2,], /* c */ \"y\": { \"z\": \"w\" }, }");
            Check(back.GetArray("x").Count == 2 && back.GetObject("y").GetString("z") == "w", "parser accepts comments and trailing commas");
            Check(JsonWriter.Serialize(new JsonObject().Add("v", 1234.5678), 2, false) == "{\"v\":1234.57}", "rounding with invariant culture");
        }

        private static void TransformTests()
        {
            var t = Fake.Transform(90, 5, 3);
            var m = Affine3.FromSolidWorksArray(t);
            var p = m.Apply(new Vec3(1, 0, 0));
            Near(p.X, 5, 1e-9, "rotZ90 x"); Near(p.Y, 4, 1e-9, "rotZ90 y");
            var q = m.Inverse().Apply(p);
            Near(q.X, 1, 1e-9, "inverse x"); Near(q.Y, 0, 1e-9, "inverse y");
            Near(m.Determinant, 1, 1e-12, "det");

            var xz = FloorFrame.FromPreset("XZ", Vec3.Zero);
            var f = xz.RootToFloor.Apply(new Vec3(1, 5, -2));
            Near(f.X, 1, 1e-12, "XZ floor x"); Near(f.Y, 2, 1e-12, "XZ floor y = -Z"); Near(f.Z, 5, 1e-12, "XZ elevation = Y");

            // Part rotated +90° about X: its local +Y ends up pointing along root +Z.
            var rx = new double[] { 1, 0, 0, 0, 0, 1, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0 };
            var up = LocalAxis.DetectUp(Affine3.FromSolidWorksArray(rx), out double tilt);
            Check(up.Name == "+Y", "up axis of a part lying on its side is +Y, got " + up.Name);
            Near(tilt, 0, 1e-9, "tilt");
        }

        private static List<double> Tris2D(float[] box3)
        {
            var r = new List<double>();
            for (int i = 0; i + 2 < box3.Length; i += 3) { r.Add(box3[i]); r.Add(box3[i + 1]); }
            return r;
        }

        private static void OutlineTests()
        {
            var opt = new OutlineOptions { Resolution = 0.01, ClosingRadiusCells = 0, MinIslandArea = 0, MinHoleArea = 0 };
            var l = Tris2D(Fake.Concat(Fake.Box(0, 0, 0, 2, 1, 1), Fake.Box(1.5, 1, 0, 2, 1.5, 1)));
            var res = RasterOutline.Build(l, opt);
            Check(res.Regions.Count == 1, "L-shape is one region, got " + res.Regions.Count);
            Near(res.Regions[0].Area, 2.25, 0.06, "L-shape area");
            Check(res.Regions[0].Outer.Count == 6, "L-shape has 6 vertices, got " + res.Regions[0].Outer.Count);
            Check(Polygon2.SignedArea(res.Regions[0].Outer) > 0, "outer ring is counter-clockwise");

            var ring = Tris2D(Fake.Concat(Fake.Box(0, 0, 0, 3, 0.5, 1), Fake.Box(0, 2.5, 0, 3, 3, 1), Fake.Box(0, 0.5, 0, 0.5, 2.5, 1), Fake.Box(2.5, 0.5, 0, 3, 2.5, 1)));
            res = RasterOutline.Build(ring, opt);
            Check(res.Regions.Count == 1 && res.Regions[0].Holes.Count == 1, "frame shape has one hole");
            if (res.Regions.Count == 1) Near(res.Regions[0].Area, 9 - 4, 0.15, "frame area");

            var two = Tris2D(Fake.Concat(Fake.Box(0, 0, 0, 1, 1, 1), Fake.Box(3, 0, 0, 4, 1, 1)));
            res = RasterOutline.Build(two, opt);
            Check(res.Regions.Count == 2, "separate parts give two regions");

            var again = RasterOutline.Build(l, opt);
            Check(JsonWriter.Serialize(again.Regions.Select(r => r.Outer.Select(p => new List<object> { p.X, p.Y }).ToList()).ToList()) ==
                  JsonWriter.Serialize(RasterOutline.Build(l, opt).Regions.Select(r => r.Outer.Select(p => new List<object> { p.X, p.Y }).ToList()).ToList()),
                  "outline is deterministic");
        }

        private static void ShapeTests()
        {
            double a = 30 * Math.PI / 180;
            var ux = new Vec2(Math.Cos(a), Math.Sin(a));
            var uy = new Vec2(-Math.Sin(a), Math.Cos(a));
            var pts = new[] { new Vec2(0, 0), ux * 2, ux * 2 + uy, uy, ux * 1 + uy * 0.5 };
            var hull = Polygon2.ConvexHull(pts);
            Check(hull.Count == 4, "hull of rotated rectangle has 4 points");
            var rect = Polygon2.MinAreaRect(hull);
            double w = Math.Max(rect.Width, rect.Depth), d = Math.Min(rect.Width, rect.Depth);
            Near(w, 2, 1e-9, "min rect length"); Near(d, 1, 1e-9, "min rect width");
            Check(Math.Abs(rect.AngleDeg - 30) < 1e-6 || Math.Abs(rect.AngleDeg - 60) < 1e-6, "min rect angle 30/60, got " + rect.AngleDeg);

            var circle = SketchLoops.DiscretizeArc(new Vec2(0, 0), new Vec2(1, 0), new Vec2(1, 0), true, 0.001);
            var poly = new List<List<Vec3>> { circle.Select(p => new Vec3(p.X, p.Y, 0)).ToList() };
            var loops = SketchLoops.ChainClosedLoops(poly, 1e-6, out int open);
            Check(loops.Count == 1 && open == 0, "circle is a closed loop");
            Near(Math.Abs(Polygon2.SignedArea(loops[0].Select(p => new Vec2(p.X, p.Y)).ToList())), Math.PI, 0.01, "circle area");

            var square = new List<List<Vec3>> { Fake.Line(0, 0, 1, 0), Fake.Line(1, 1, 1, 0), Fake.Line(1, 1, 0, 1), Fake.Line(0, 1, 0, 0) };
            loops = SketchLoops.ChainClosedLoops(square, 1e-6, out open);
            Check(loops.Count == 1 && open == 0 && loops[0].Count == 4, "lines (one reversed) chain into a square");
            loops = SketchLoops.ChainClosedLoops(square.Take(3), 1e-6, out open);
            Check(loops.Count == 0 && open == 1, "open chain detected");

            Near(UnitParser.ParseLength("600", "mm") ?? -1, 0.6, 1e-12, "600 default mm");
            Near(UnitParser.ParseLength("24 in", "mm") ?? -1, 0.6096, 1e-12, "24 in");
            Check(UnitParser.ParseLength("wide", "mm") == null, "non-length rejected");
        }

        private static void ClassificationTests()
        {
            var cfg = ShopConfig();
            var asm = BuildShop(false).Assembly;
            var log = new IssueLog();
            // Pipeline normally prepares parents/depths; do it via a run on a fake to reuse that code.
            var res = new ExportPipeline(new FakeSourceWrapper(asm), cfg).Run();
            var cls = res.Classes;
            Check(cls["1"].Role == LayoutRole.Equipment && cls["1"].Source == "default", "top-level machine defaults to Equipment");
            Check(cls["1/1"].Role == LayoutRole.Contained && cls["1/1"].OwnerId == "1", "machine child is contained");
            Check(cls["1/3"].Role == LayoutRole.Ignore && cls["1/3"].Source == "builtin:toolbox", "toolbox fastener ignored");
            Check(cls["1/4"].Role == LayoutRole.Clearance && cls["1/4"].ClearanceMarkerId == "1/4", "clearance body inside machine is a marker");
            Check(cls["3"].Role == LayoutRole.Building && cls["3"].Kind == "wall", "wall by rule");
            Check(cls["6"].Role == LayoutRole.Group, "cell is a group by rule");
            Check(cls["6/1"].Role == LayoutRole.Equipment, "robot inside group is equipment");
            Check(cls["7"].Role == LayoutRole.Ignore && cls["7"].Source == "property:LayoutRole", "LayoutRole=Ignore property wins");
            Check(res.Log.Sorted().Any(i => i.Code == "NESTED_ROLE_IGNORED" && i.ComponentId == "1/1"), "nested Equipment marker warned");
        }

        private static ExportConfig ShopConfig()
        {
            var cfg = new ExportConfig();
            cfg.Classification.Rules.Add(new ClassificationRule { Name = "walls", FileName = "*wall*", Role = "Building", Kind = "wall" });
            cfg.Classification.Rules.Add(new ClassificationRule { Name = "cells", InstanceName = "Cell*", DocType = "assembly", Role = "Group" });
            cfg.Classification.Rules.Add(new ClassificationRule { Name = "clearance bodies", FileName = "*clearance*", Role = "Clearance", AppliesTo = "contained" });
            cfg.Geometry.FootprintResolution = 0.01;
            cfg.Geometry.ClosingRadiusCells = 0;
            return cfg;
        }

        private static double[] Compose(double[] parentRoot, double[] childLocal)
        {
            var m = Affine3.FromSolidWorksArray(parentRoot).Multiply(Affine3.FromSolidWorksArray(childLocal));
            var r = m.ToRowMajor4x4();
            return new[] { r[0], r[4], r[8], r[1], r[5], r[9], r[2], r[6], r[10], r[3], r[7], r[11], 1, 0, 0, 0 };
        }

        private static RawComponent Mill(int id, double rot, double x, double y)
        {
            var t = Fake.Transform(rot, x, y);
            string n = "Mill-" + id;
            var basePart = Fake.Comp(1, n + "/Base-1", @"C:\Shop\Equipment\Mill Base.SLDPRT", Compose(t, Fake.Identity()));
            basePart.ConfigProperties.Set("LayoutRole", "Equipment", "Equipment"); // wrongly marked: must not be double counted
            var column = Fake.Comp(2, n + "/Column-1", @"C:\Shop\Equipment\Mill Column.SLDPRT", Compose(t, Fake.Identity()));
            var bolt = Fake.Comp(3, n + "/Bolt-1", @"C:\SOLIDWORKS Data\browser\ansi inch\bolts\hex bolt.sldprt", Compose(t, Fake.Transform(0, 1, 0.5, 1)));
            var clr = Fake.Comp(4, n + "/Operator Clearance-1", @"C:\Shop\Equipment\Mill_clearance.SLDPRT", Compose(t, Fake.Identity()));
            var mill = Fake.Comp(id, n, @"C:\Shop\Equipment\Mill.SLDASM", t, DocKind.Assembly, basePart, column, bolt, clr);
            mill.FileProperties.Set("LayoutCategory", "CNC", "CNC");
            mill.FileProperties.Set("ClearanceBack", "500", "500");
            mill.FileProperties.Set("Description", "$PRP:\"SW-File Name\"", "Mill");
            mill.RootBox = Fake.RootBox(t, 0, -0.8, 0, 2, 1.5, 2.5);
            return mill;
        }

        private static FakeSource BuildShop(bool shuffled)
        {
            var src = new FakeSource();
            src.MeshesByFile[@"C:\Shop\Equipment\Mill Base.SLDPRT"] = Fake.Box(0, 0, 0, 2, 1, 1);
            src.MeshesByFile[@"C:\Shop\Equipment\Mill Column.SLDPRT"] = Fake.Box(1.5, 1, 0, 2, 1.5, 2.5);
            src.MeshesByFile[@"C:\SOLIDWORKS Data\browser\ansi inch\bolts\hex bolt.sldprt"] = Fake.Box(0, 0, 0, 5, 5, 5); // would wreck the footprint if not ignored
            src.MeshesByFile[@"C:\Shop\Equipment\Mill_clearance.SLDPRT"] = Fake.Box(0, -0.8, 0, 2, 0, 0.01);
            src.MeshesByFile[@"C:\Shop\Building\Wall_L.SLDPRT"] = Fake.Concat(Fake.Box(0, 0, 0, 10, 0.2, 4), Fake.Box(0, 0.2, 0, 0.2, 8, 4));
            src.MeshesByFile[@"C:\Shop\Equipment\Robot.SLDPRT"] = Fake.Box(-0.5, -0.5, 0, 0.5, 0.5, 2);
            src.MeshesByFile[@"C:\Shop\Misc\Person.SLDPRT"] = Fake.Box(0, 0, 0, 0.5, 0.5, 1.8);
            src.SketchesByFile[@"C:\Shop\Equipment\Robot.SLDPRT"] = new List<RawSketch>
            {
                new RawSketch
                {
                    Name = "LAYOUT_FOOTPRINT",
                    Polylines = { SketchLoops.DiscretizeArc(new Vec2(0, 0), new Vec2(0.6, 0), new Vec2(0.6, 0), true, 0.0005).Select(p => new Vec3(p.X, p.Y, 0)).ToList() }
                },
                new RawSketch
                {
                    Name = "LAYOUT_CLEARANCE_Swing",
                    Polylines = { SketchLoops.DiscretizeArc(new Vec2(0, 0), new Vec2(1.5, 0), new Vec2(1.5, 0), true, 0.002).Select(p => new Vec3(p.X, p.Y, 0)).ToList() }
                }
            };

            var wall = Fake.Comp(3, "Wall-1", @"C:\Shop\Building\Wall_L.SLDPRT", Fake.Identity());
            var latheT = Fake.Transform(0, 20, 0);
            var lathe = Fake.Comp(4, "Lathe-1", @"C:\Shop\Equipment\Lightweight Lathe.SLDPRT", latheT);
            lathe.LoadState = LoadState.Lightweight;
            lathe.PropertiesAvailable = false;
            lathe.RootBox = Fake.RootBox(latheT, 0, 0, 0, 3, 1, 1.5);
            var saw = Fake.Comp(5, "Saw-1", @"C:\Shop\Equipment\Saw.SLDPRT", null);
            saw.LoadState = LoadState.Suppressed;
            var cellT = Fake.Identity();
            var robot = Fake.Comp(1, "Cell A-1/Robot-1", @"C:\Shop\Equipment\Robot.SLDPRT", Fake.Transform(45, 30, 5));
            var cell = Fake.Comp(6, "Cell A-1", @"C:\Shop\Cells\Cell A.SLDASM", cellT, DocKind.Assembly, robot);
            var person = Fake.Comp(7, "Person-1", @"C:\Shop\Misc\Person.SLDPRT", Fake.Transform(0, 2, 2));
            person.FileProperties.Set("LayoutRole", "Ignore", "Ignore");

            var top = new List<RawComponent> { Mill(1, 90, 5, 3), Mill(2, 30, 10, 0), wall, lathe, saw, cell, person };
            if (shuffled)
            {
                top.Reverse();
                foreach (var c in top) c.Children.Reverse();
            }
            src.Assembly.TopLevel.AddRange(top);
            return src;
        }

        private static JsonObject Eq(ExportResult r, string id) =>
            r.Layout.GetArray("equipment").OfType<JsonObject>().First(e => e.GetString("componentId") == id);

        private static double[] Bounds(JsonObject shape, string which) => shape.GetObject("bounds").GetDoubleArray(which);

        private static void PipelineTests()
        {
            var src = BuildShop(false);
            var opt = new ExportOptions { Timestamp = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc) };
            var res = new ExportPipeline(src, ShopConfig(), opt).Run();
            Check(src.Finished, "source Finish() called");

            var eq = res.Layout.GetArray("equipment").OfType<JsonObject>().ToList();
            Check(eq.Select(e => e.GetString("componentId")).SequenceEqual(new[] { "1", "2", "4", "6/1" }), "equipment = mills, lathe, robot (no children, no suppressed): " + string.Join(",", eq.Select(e => e.GetString("componentId"))));
            Check(res.Layout.GetArray("building").Count == 1, "one building element");
            Check(res.Log.Sorted().Any(i => i.Code == "SUPPRESSED_EXCLUDED" && i.ComponentId == "5"), "suppressed saw reported");

            var defs = res.Library.GetArray("definitions").OfType<JsonObject>().ToList();
            var mill = defs.First(d => d.GetString("definitionId").Contains("mill.sldasm"));
            Check(mill.GetArray("instanceIds").Count == 2, "both mills share one definition");
            Check(mill.GetString("definitionId") == "eq:equipment/mill.sldasm@Default#+Z", "definition id: " + mill.GetString("definitionId"));
            var dims = mill.GetObject("dimensions");
            Near(dims.GetDouble("width", 0), 2, 0.011, "mill width");
            Near(dims.GetDouble("depth", 0), 1.5, 0.011, "mill depth (toolbox bolt and clearance body excluded)");
            Near(dims.GetDouble("height", 0), 2.5, 1e-6, "mill height");
            var primary = mill.GetObject("footprint").GetObject("primary");
            Check(primary.GetString("source") == "tessellatedOutline", "mill footprint from tessellation");
            Near(primary.GetDouble("area", 0), 2.25, 0.06, "mill L-shaped area");
            Check(primary.GetArray("regions").Count == 1 && ((JsonObject)primary.GetArray("regions")[0]).GetArray("outer").Count == 6, "mill footprint is a 6-vertex L");
            var clr = mill.GetArray("clearances").OfType<JsonObject>().ToList();
            Check(clr.Count == 2 && clr.Any(c => c.GetString("name") == "Operator Clearance-1") && clr.Any(c => c.GetString("name") == "properties"), "mill clearances: component + properties");
            Check(mill.GetObject("properties").GetObject("file").GetObject("Description")?.GetString("value") == "Mill", "property value and expression kept");

            // Mill-1: rotated 90° at (5,3): local x∈[0,2], y∈[0,1.5] → floor x∈[3.5,5], y∈[3,5]
            var m1 = Eq(res, "1");
            Near(m1.GetObject("pose").GetDouble("rotationDeg", -1), 90, 1e-6, "mill-1 rotation");
            var fb = m1.GetObject("floorFootprint");
            Near(Bounds(fb, "min")[0], 3.5, 0.011, "mill-1 floor min x"); Near(Bounds(fb, "max")[0], 5, 0.011, "mill-1 floor max x");
            Near(Bounds(fb, "min")[1], 3, 0.011, "mill-1 floor min y"); Near(Bounds(fb, "max")[1], 5, 0.011, "mill-1 floor max y");
            Near(fb.GetDouble("area", 0), primary.GetDouble("area", 0), 1e-6, "rotation preserves footprint area");
            var m2 = Eq(res, "2");
            Near(m2.GetObject("pose").GetDouble("rotationDeg", -1), 30, 1e-6, "mill-2 rotation");
            Check(m2.GetObject("transform").GetObject("root").GetArray("solidworksArrayData").Count == 16, "original SolidWorks matrix preserved");

            var lathe = defs.First(d => d.GetString("definitionId").Contains("lathe"));
            Check(lathe.GetObject("footprint").GetObject("primary").GetString("source") == "rootBoundingBox", "lightweight lathe falls back to bounding box");
            Near(lathe.GetObject("dimensions").GetDouble("width", 0), 3, 1e-6, "lathe width from box");
            Check(res.Log.Sorted().Any(i => i.Code == "GEOMETRY_FROM_BOUNDING_BOX" && i.ComponentId == "4"), "bounding-box fallback warned");
            Check(src.ResolveRequests.Contains("4"), "export mode allows resolving the lightweight lathe");

            var robot = defs.First(d => d.GetString("definitionId").Contains("robot"));
            var rp = robot.GetObject("footprint").GetObject("primary");
            Check(rp.GetString("source") == "authoredSketch" && rp.GetString("accuracy") == "exact", "robot uses authored sketch footprint");
            Near(rp.GetDouble("area", 0), Math.PI * 0.36, 0.01, "robot circular footprint area");
            Check(robot.GetArray("clearances").OfType<JsonObject>().Any(c => c.GetString("name") == "Swing" && c.GetObject("shape").GetString("source") == "authoredSketch"), "robot swing clearance from sketch");

            var wall = res.Layout.GetArray("building").OfType<JsonObject>().First();
            var wf = wall.GetObject("floorFootprint");
            Near(wf.GetDouble("area", 0), 3.56, 0.12, "L-shaped wall area");
            Check(((JsonObject)wf.GetArray("regions")[0]).GetArray("outer").Count == 6, "wall outline keeps its concave L shape");

            // Determinism: same input → same hash; shuffled input order → same hash; different timestamp → same hash.
            var res2 = new ExportPipeline(BuildShop(false), ShopConfig(), new ExportOptions { Timestamp = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc) }).Run();
            var res3 = new ExportPipeline(BuildShop(true), ShopConfig(), opt).Run();
            Check(res.Layout.GetString("contentHash") == res2.Layout.GetString("contentHash"), "contentHash independent of timestamp");
            Check(res.Layout.GetString("contentHash") == res3.Layout.GetString("contentHash"), "contentHash independent of traversal order");
            Check(res.Library.GetString("contentHash") == res3.Library.GetString("contentHash"), "library hash independent of traversal order");

            // Validation mode does not resolve lightweight components.
            var dry = BuildShop(false);
            var vres = new ExportPipeline(dry, ShopConfig(), new ExportOptions { DryRun = true }).Run();
            Check(dry.ResolveRequests.Count == 0, "validate mode does not request lightweight resolution");
            Check(vres.ReportText.Contains("Mill-1") && vres.ReportText.Contains("rootBoundingBox"), "report lists equipment and geometry sources");
        }

        private static void FloorXzTests()
        {
            // Equipment modelled Y-up (SolidWorks default), floor = XZ.
            var src = new FakeSource();
            src.MeshesByFile[@"C:\Shop\Equipment\Press.SLDPRT"] = Fake.Box(0, 0, 0, 2, 1.2, 1); // 2 along X, 1.2 tall (Y), 1 along Z
            src.Assembly.TopLevel.Add(Fake.Comp(1, "Press-1", @"C:\Shop\Equipment\Press.SLDPRT", Fake.Identity()));
            var cfg = new ExportConfig();
            cfg.Floor.Plane = "XZ";
            cfg.Geometry.FootprintResolution = 0.01;
            var res = new ExportPipeline(src, cfg).Run();
            var e = Eq(res, "1");
            Check(e.GetObject("pose").GetString("upAxis") == "+Y", "Y-up equipment detected on XZ floor");
            Near(e.GetObject("pose").GetDouble("rotationDeg", -1), 0, 1e-9, "unrotated Y-up part reads 0°");
            Check(res.Library.GetArray("definitions").OfType<JsonObject>().First().GetObject("localFrame").GetString("footprintAxisB") == "-Z", "Y-up footprint axes are +X / -Z");
            var d = res.Library.GetArray("definitions").OfType<JsonObject>().First().GetObject("dimensions");
            Near(d.GetDouble("height", 0), 1.2, 1e-6, "height along Y");
            Near(d.GetDouble("width", 0) * d.GetDouble("depth", 0), 2.0, 0.05, "plan area 2 x 1");
            var fb = e.GetObject("floorFootprint");
            Near(Bounds(fb, "min")[0], 0, 0.011, "floor x min"); Near(Bounds(fb, "max")[0], 2, 0.011, "floor x max");
            Near(Bounds(fb, "min")[1], -1, 0.011, "floor y min (= -Z)"); Near(Bounds(fb, "max")[1], 0, 0.011, "floor y max");
        }

        private static void SnapshotTests()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "shoplayout_snapshot_test.json");
            var rec = new RecordingSource(BuildShop(false));
            var opt = new ExportOptions { Timestamp = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc) };
            var a = new ExportPipeline(rec, ShopConfig(), opt).Run();
            rec.Save(tmp);
            var b = new ExportPipeline(new SnapshotSource(tmp), ShopConfig(), opt).Run();
            Check(a.Layout.GetString("contentHash") == b.Layout.GetString("contentHash"), "replaying a snapshot reproduces the export exactly");
            File.Delete(tmp);
        }

        private static void WriteSample(string dir)
        {
            Directory.CreateDirectory(dir);
            var rec = new RecordingSource(BuildShop(false));
            var sampleCfg = ShopConfig();
            sampleCfg.Output.IncludeRunInfo = false;
            sampleCfg.Output.IncludeAbsolutePaths = false;
            var res = new ExportPipeline(rec, sampleCfg, new ExportOptions { Timestamp = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc) }).Run();
            rec.Save(Path.Combine(dir, "shop_snapshot.json"));
            File.WriteAllText(Path.Combine(dir, "shop_layout.json"), res.LayoutJson);
            File.WriteAllText(Path.Combine(dir, "equipment_library.json"), res.LibraryJson);
            File.WriteAllText(Path.Combine(dir, "validation_report.txt"), res.ReportText);
            Console.WriteLine("Sample written to " + Path.GetFullPath(dir));
        }

        /// <summary>Feeds an existing RawAssembly through the pipeline without geometry.</summary>
        private sealed class FakeSourceWrapper : IAssemblySource
        {
            private readonly RawAssembly _a;
            public FakeSourceWrapper(RawAssembly a) { _a = a; }
            public string Description => "wrapper";
            public RawAssembly ReadStructure() => _a;
            public MeshResult GetMesh(RawComponent part, bool allowResolve) => new MeshResult();
            public SketchResult GetLayoutSketches(RawComponent c, string f, string p, bool r) => new SketchResult();
            public double[] GetReferencePlaneTransform(string name) => null;
            public List<Issue> Finish() => new List<Issue>();
        }
    }
}
