using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using ShopLayout.Core.Config;
using ShopLayout.Core.Export;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Model;
using ShopLayout.Drawio;

namespace ShopLayout.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// A 10 × 8 m room (0.2 m walls, 5 m high, on a slab) split by a full-height partition at x = 5.
        /// Front wall: a 1 m man door (left half). Back wall: a 3 m loading dock (sill 0.9 m, head 3.6 m)
        /// and a 3 m overhead door. Right wall: 2 mm sheet siding. Equipment: a rack 0.4° off square, an
        /// exact duplicate of it, a typo-named table, and an L-shaped machine.
        /// </summary>
        private static FakeSource BuildImportFixture()
        {
            var src = new FakeSource();
            src.MeshesByFile[@"C:\Shop\Building\Room.SLDPRT"] = Fake.Concat(
                Fake.Box(0, 0, -0.15, 10, 8, 0),          // slab
                Fake.Box(0, 0, 0, 1, 0.2, 5),             // front wall, left of door
                Fake.Box(2, 0, 0, 10, 0.2, 5),            // front wall, right of door
                Fake.Box(1, 0, 2.1, 2, 0.2, 5),           // door header
                Fake.Box(0, 7.8, 0, 0.5, 8, 5),           // back wall pieces between openings
                Fake.Box(3.5, 7.8, 0, 6, 8, 5),
                Fake.Box(9, 7.8, 0, 10, 8, 5),
                Fake.Box(0.5, 7.8, 0, 3.5, 8, 0.9),       // dock: wall below the 0.9 m sill
                Fake.Box(0.5, 7.8, 3.6, 3.5, 8, 5),       // dock header
                Fake.Box(6, 7.8, 3.2, 9, 8, 5),           // overhead door header (opening from the floor)
                Fake.Box(0, 0.2, 0, 0.2, 7.8, 5),         // left wall
                Fake.Box(9.998, 0.2, 0, 10, 7.8, 5),      // right wall: 2 mm sheet siding
                Fake.Box(5, 0.2, 0, 5.2, 7.8, 5));        // partition
            src.MeshesByFile[@"C:\Shop\Equipment\parts rack.SLDPRT"] = Fake.Box(0, 0, 0, 1.8, 0.4, 2);
            src.MeshesByFile[@"C:\Shop\Equipment\sew talble 1.SLDPRT"] = Fake.Box(0, 0, 0, 2, 1, 0.9);
            src.MeshesByFile[@"C:\Shop\Equipment\Winch.SLDPRT"] = Fake.Concat(Fake.Box(0, 0, 0, 2, 0.5, 1), Fake.Box(0, 0.5, 0, 0.5, 1.5, 1));

            src.Assembly.TopLevel.Add(Fake.Comp(1, "Room-1", @"C:\Shop\Building\Room.SLDPRT", Fake.Identity()));
            src.Assembly.TopLevel.Add(Fake.Comp(2, "parts rack-1", @"C:\Shop\Equipment\parts rack.SLDPRT", Fake.Transform(359.6, 1, 2)));
            src.Assembly.TopLevel.Add(Fake.Comp(3, "parts rack-2", @"C:\Shop\Equipment\parts rack.SLDPRT", Fake.Transform(359.6, 1, 2)));
            src.Assembly.TopLevel.Add(Fake.Comp(4, "sew talble 1-1", @"C:\Shop\Equipment\sew talble 1.SLDPRT", Fake.Transform(90, 7, 3)));
            src.Assembly.TopLevel.Add(Fake.Comp(5, "BUNDLEWINCH-1", @"C:\Shop\Equipment\Winch.SLDPRT", Fake.Transform(0, 6, 5)));
            return src;
        }

        private static ImportCleanup ImportFixtureCleanup()
        {
            var c = new ImportCleanup { DiagramName = "Test Area", FloorFromElements = { "Room" }, MinRoomArea = 4 };
            c.LabelMap["sew talble 1"] = "Sewing Table 1";
            c.LabelMap["parts rack"] = "Parts Rack";
            c.LabelMap["BUNDLEWINCH"] = "Bundle Winch";
            c.RoomNames.Add(new ImportCleanup.RoomName { At = { new Vec2(2, 4) }, Name = "Office" });
            return c;
        }

        private static (DiagramModel model, string xml) RunImport(Action<ImportCleanup> tweak = null)
        {
            var cfg = new ExportConfig();
            cfg.Classification.Rules.Add(new ClassificationRule { Name = "rooms", FileName = "room*", Role = "Building", Kind = "room" });
            cfg.Geometry.FootprintResolution = 0.01;
            var res = new ExportPipeline(BuildImportFixture(), cfg).Run();
            var cleanup = ImportFixtureCleanup();
            tweak?.Invoke(cleanup);
            var model = new LayoutImporter(res, cleanup).Build();
            return (model, DrawioWriter.WriteDiagram(model));
        }

        private static void DrawioTests()
        {
            var (m, xml) = RunImport();
            var eq = m.Items.Where(i => i.Kind == "equipment").ToList();
            Check(eq.Count == 3, "duplicate rack dropped: 3 equipment shapes, got " + eq.Count);
            Check(m.Log.Duplicates.Count == 1 && m.Log.Duplicates[0][1] == "3", "rack 3 logged as duplicate of 2");

            var rack = eq.First(i => i.Data["component_id"] == "2");
            Near(rack.RotationDeg, 0, 1e-9, "rack squared to 0°");
            Check(rack.Label == "Parts Rack" && rack.Data["sw_instance"] == "parts rack-1", "suffix dropped, original name kept");
            Check(m.Log.Snapped.Count == 1, "one rotation snapped");
            Near(rack.Width, 1.8, 1e-6, "rack width"); Near(rack.Height, 0.4, 1e-6, "rack depth");

            var table = eq.First(i => i.Data["component_id"] == "4");
            Check(table.Label == "Sewing Table 1", "typo fixed via label map: " + table.Label);
            Near(table.RotationDeg, 90, 1e-9, "table keeps 90°");

            var winch = eq.First(i => i.Data["component_id"] == "5");
            Check(winch.Shape == ShapeKind.Polygon && winch.Style.Contains("shape=mxgraph.basic.polygon") && winch.PolyCoords.Count == 6, "L-shaped machine is a 6-point polygon");

            var walls = m.Items.Where(i => i.Kind == "wall").ToList();
            Check(walls.Count > 0 && walls.All(w => w.Shape == ShapeKind.Rect), "walls decomposed into rectangles");
            bool Covers(DiagramItem w, double x, double y) => Math.Abs(w.Center.X - x) < w.Width / 2 && Math.Abs(w.Center.Y - y) < w.Height / 2;
            Check(!walls.Any(w => Covers(w, 1.5, 0.1)), "door opening (below the 1.2 m cut) stays open in the wall plan");
            var sheet = walls.FirstOrDefault(w => Covers(w, 9.999, 4));
            Check(sheet != null && sheet.Data.ContainsKey("drawn_thickness_m"), "2 mm sheet wall survives and is drawn at minimum thickness");
            if (sheet != null) Near(sheet.Width, 0.1, 1e-9, "sheet wall drawn 0.1 m thick");
            Check(m.Items.Count(i => i.Kind == "floor") == 1, "slab becomes one floor outline");

            var door = m.Items.FirstOrDefault(i => i.Kind == "door");
            var dock = m.Items.FirstOrDefault(i => i.Kind == "loadingDock");
            var oh = m.Items.FirstOrDefault(i => i.Kind == "overheadDoor");
            Check(door != null && Covers(door, 1.5, 0.1), "man door detected in the front wall");
            Check(dock != null && Covers(dock, 2, 7.9) && dock.Data["name"] == "Dock 1", "loading dock detected and named");
            if (dock != null)
            {
                Near(double.Parse(dock.Data["sill_m"], System.Globalization.CultureInfo.InvariantCulture), 0.9, 0.06, "dock sill height");
                Near(double.Parse(dock.Data["width_m"], System.Globalization.CultureInfo.InvariantCulture), 3.0, 0.06, "dock width");
            }
            Check(oh != null && Covers(oh, 7.5, 7.9), "overhead door detected");
            var dockLabel = m.Items.FirstOrDefault(i => i.Id == "dock-1-label");
            Check(dockLabel != null && dockLabel.Center.Y > 8, "dock callout placed outside the building");

            var rooms = m.Items.Where(i => i.Kind == "room").ToList();
            Check(rooms.Count == 2, "two rooms either side of the partition (openings closed), got " + rooms.Count);
            Check(rooms.Any(r => r.Data["name"] == "Office") && rooms.Any(r => r.Data["name"] == "Space 1"), "room named by point; unnamed one is Space 1");
            double Area(DiagramItem r) => double.Parse(r.Data["area_m2"], System.Globalization.CultureInfo.InvariantCulture);
            Near(Area(rooms.First(r => r.Data["name"] == "Office")), 4.8 * 7.6, 0.4, "left room area");
            Near(Area(rooms.First(r => r.Data["name"] == "Space 1")), 4.8 * 7.6, 0.4, "right room area (to the sheet wall)");

            // Two points in one entry merge both rooms into a single shape spanning the partition.
            var (mm, _) = RunImport(c => { c.RoomNames.Clear(); c.RoomNames.Add(new ImportCleanup.RoomName { Name = "Shop", At = { new Vec2(2, 4), new Vec2(8, 4) } }); });
            var merged = mm.Items.Where(i => i.Kind == "room").ToList();
            Check(merged.Count == 1 && merged[0].Data["name"] == "Shop" && merged[0].Data.ContainsKey("merged_from"), "rooms merged into one");
            if (merged.Count == 1)
            {
                Near(Area(merged[0]), 2 * 4.8 * 7.6, 0.8, "merged area is the sum of the rooms");
                Check(Math.Abs(merged[0].Center.X - 5.1) < 0.2 && merged[0].Width > 9, "merged outline spans both rooms across the partition");
            }

            var doc = XDocument.Parse(xml);
            var cells = doc.Descendants("mxCell").ToList();
            var layers = cells.Where(c => (string)c.Attribute("parent") == "0").ToList();
            Check(layers.Count == 7, "seven layers");
            Check(layers.Any(l => (string)l.Attribute("value") == "Building" && ((string)l.Attribute("style") ?? "").Contains("locked=1")), "building layer locked");
            var rackObj = doc.Descendants("object").First(o => (string)o.Attribute("component_id") == "2");
            Check((string)rackObj.Attribute("label") == "Parts Rack" && (string)rackObj.Attribute("import_note") != null, "shape carries data attributes");
            var geo = rackObj.Descendants("mxGeometry").First();
            Near(double.Parse((string)geo.Attribute("width"), System.Globalization.CultureInfo.InvariantCulture), 180, 0.01, "1.8 m = 180 px");

            string lib = DrawioWriter.WriteLibrary(m);
            Check(lib.StartsWith("<mxlibrary>[") && m.Library.Count == 3, "library has one shape per definition");
            string roundTrip = DrawioWriter.Decompress(DrawioWriter.Compress("<a b=\"ü 1%\"/>"));
            Check(roundTrip == "<a b=\"ü 1%\"/>", "diagrams.net compression round-trips");

            Check(RunImport().xml == xml, "import output is deterministic");
        }
    }
}
