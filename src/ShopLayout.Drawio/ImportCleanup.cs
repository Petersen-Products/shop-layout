using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Json;

namespace ShopLayout.Drawio
{
    /// <summary>Settings of the one-time SolidWorks → diagrams.net import (config/import_cleanup.json).</summary>
    public sealed class ImportCleanup
    {
        public string DiagramName = "Layout";
        public double ScalePxPerMeter = 100;
        public int GridSizePx = 10;
        public double MarginMeters = 2;
        public double SnapToleranceDeg = 1.0;
        public bool DropDuplicates = true;
        public double DuplicatePositionTolerance = 0.001;
        public double DuplicateAngleToleranceDeg = 0.01;
        public bool StripInstanceSuffix = true;
        public Dictionary<string, string> LabelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public double CutHeight = 1.2;
        public double WallResolution = 0.01;
        public double MinWallArea = 0.005;
        public List<string> FloorFromElements = new List<string>();
        public bool DetectRooms = true;
        public double RoomResolution = 0.05;
        public double MaxOpeningWidth = 1.3;
        public double MinRoomArea = 4.0;
        public List<RoomName> RoomNames = new List<RoomName>();
        public List<ZoneDef> Zones = new List<ZoneDef>();

        public sealed class RoomName { public Vec2 At; public string Name; public string Type = "room"; }
        public sealed class ZoneDef { public string Name; public string Type = "zone"; public List<Vec2> Polygon = new List<Vec2>(); }

        public static ImportCleanup Load(string path) => FromJson(JsonParser.ParseObject(File.ReadAllText(path)));

        public static ImportCleanup FromJson(JsonObject o)
        {
            var c = new ImportCleanup();
            if (o == null) return c;
            c.DiagramName = o.GetString("diagramName", c.DiagramName);
            c.ScalePxPerMeter = o.GetDouble("scalePxPerMeter", c.ScalePxPerMeter);
            c.GridSizePx = o.GetInt("gridSizePx", c.GridSizePx);
            c.MarginMeters = o.GetDouble("marginMeters", c.MarginMeters);
            c.SnapToleranceDeg = o.GetDouble("snapToleranceDeg", c.SnapToleranceDeg);

            var d = o.GetObject("duplicates");
            if (d != null)
            {
                c.DropDuplicates = d.GetBool("drop", c.DropDuplicates);
                c.DuplicatePositionTolerance = d.GetDouble("positionTolerance", c.DuplicatePositionTolerance);
                c.DuplicateAngleToleranceDeg = d.GetDouble("angleToleranceDeg", c.DuplicateAngleToleranceDeg);
            }

            var l = o.GetObject("labels");
            if (l != null)
            {
                c.StripInstanceSuffix = l.GetBool("stripInstanceSuffix", c.StripInstanceSuffix);
                var map = l.GetObject("map");
                if (map != null) foreach (var kv in map.Items) if (kv.Value is string s) c.LabelMap[kv.Key.Trim()] = s;
            }

            var b = o.GetObject("building");
            if (b != null)
            {
                c.CutHeight = b.GetDouble("cutHeight", c.CutHeight);
                c.WallResolution = b.GetDouble("wallResolution", c.WallResolution);
                c.MinWallArea = b.GetDouble("minWallArea", c.MinWallArea);
                var f = b.GetArray("floorFromElements");
                if (f != null) c.FloorFromElements = f.OfType<string>().ToList();
            }

            var r = o.GetObject("rooms");
            if (r != null)
            {
                c.DetectRooms = r.GetBool("detect", c.DetectRooms);
                c.RoomResolution = r.GetDouble("resolution", c.RoomResolution);
                c.MaxOpeningWidth = r.GetDouble("maxOpeningWidth", c.MaxOpeningWidth);
                c.MinRoomArea = r.GetDouble("minArea", c.MinRoomArea);
                foreach (var n in (r.GetArray("names") ?? new List<object>()).OfType<JsonObject>())
                {
                    var at = n.GetDoubleArray("at");
                    if (at == null || at.Length < 2) continue;
                    c.RoomNames.Add(new RoomName { At = new Vec2(at[0], at[1]), Name = n.GetString("name", ""), Type = n.GetString("type", "room") });
                }
            }

            foreach (var z in (o.GetArray("zones") ?? new List<object>()).OfType<JsonObject>())
            {
                var zd = new ZoneDef { Name = z.GetString("name", "Zone"), Type = z.GetString("type", "zone") };
                foreach (var p in (z.GetArray("polygon") ?? new List<object>()).OfType<List<object>>())
                    if (p.Count >= 2 && p[0] is double x && p[1] is double y) zd.Polygon.Add(new Vec2(x, y));
                if (zd.Polygon.Count >= 3) c.Zones.Add(zd);
            }
            return c;
        }
    }
}
