using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ShopLayout.Core.Export;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Model;

namespace ShopLayout.Drawio
{
    /// <summary>
    /// Turns an export result into a diagram, applying the one-time cleanup: drop duplicate instances,
    /// square up near-orthogonal rotations, clean labels, and redraw all building elements uniformly as a
    /// plan section (walls cut at one height) with detected rooms.
    /// </summary>
    public sealed class LayoutImporter
    {
        public const string LayerBuilding = "layer-building";
        public const string LayerRooms = "layer-rooms";
        public const string LayerZones = "layer-zones";
        public const string LayerEquipment = "layer-equipment";
        public const string LayerClearances = "layer-clearances";
        public const string LayerNotes = "layer-notes";

        public const string StyleEquipment = "rounded=0;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;fontSize=10;";
        public const string StyleWall = "rounded=0;html=1;fillColor=#4d4d4d;strokeColor=none;";
        public const string StyleFloor = "rounded=0;html=1;fillColor=#f5f5f5;strokeColor=#b3b3b3;";
        public const string StyleRoom = "rounded=0;whiteSpace=wrap;html=1;fillColor=#fff2cc;opacity=35;strokeColor=#d6b656;dashed=1;fontSize=16;fontStyle=1;verticalAlign=top;";
        public const string StyleZone = "rounded=0;whiteSpace=wrap;html=1;fillColor=#d5e8d4;opacity=35;strokeColor=#82b366;dashed=1;fontSize=12;verticalAlign=top;";
        public const string StyleClearance = "rounded=0;html=1;fillColor=#f8cecc;opacity=30;strokeColor=#b85450;dashed=1;";
        public const string StyleText = "text;html=1;align=left;verticalAlign=top;fontSize=20;fontStyle=1;";

        private static readonly Regex InstanceSuffix = new Regex(@"-\d+$", RegexOptions.CultureInvariant);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly ExportResult _res;
        private readonly ImportCleanup _c;
        private readonly DiagramModel _m = new DiagramModel();
        private readonly Dictionary<Definition, string> _titles = new Dictionary<Definition, string>();

        public LayoutImporter(ExportResult result, ImportCleanup cleanup)
        {
            _res = result;
            _c = cleanup ?? new ImportCleanup();
        }

        public DiagramModel Build()
        {
            _m.Name = _c.DiagramName;
            _m.Scale = _c.ScalePxPerMeter;
            _m.GridSizePx = _c.GridSizePx;
            _m.MarginMeters = _c.MarginMeters;
            _m.Layers.Add(new DiagramLayer { Id = LayerBuilding, Name = "Building", Locked = true });
            _m.Layers.Add(new DiagramLayer { Id = LayerRooms, Name = "Rooms" });
            _m.Layers.Add(new DiagramLayer { Id = LayerZones, Name = "Zones" });
            _m.Layers.Add(new DiagramLayer { Id = LayerEquipment, Name = "Equipment" });
            _m.Layers.Add(new DiagramLayer { Id = LayerClearances, Name = "Clearances" });
            _m.Layers.Add(new DiagramLayer { Id = LayerNotes, Name = "Notes" });

            var equipment = _res.Placements.Where(p => p.Kind == PlacedKind.Equipment)
                .OrderBy(p => p.Component.IdPath, IdPathComparer.Instance).ToList();
            var kept = DropDuplicates(equipment);
            AddEquipment(kept);
            var building = _res.Placements.Where(p => p.Kind == PlacedKind.Building).ToList();
            var segments = AddBuilding(building);
            AddRooms(segments);
            AddZones();
            AddNotes();

            _m.Log.Summary.Insert(0, $"Source: {Path.GetFileName(_res.Assembly?.RootFilePath ?? "")} [{_res.Assembly?.RootConfiguration}], floor plane {_res.Floor?.Name}");
            _m.Log.Summary.Add($"Equipment: {kept.Count} placed ({equipment.Count - kept.Count} duplicates dropped), {_m.Library.Count} library shapes");
            _m.Log.Summary.Add($"Rotations squared up (within {_c.SnapToleranceDeg.ToString(Inv)}°): {_m.Log.Snapped.Count}");
            _m.Log.Summary.Add($"Labels changed: {_m.Log.Renamed.Count} distinct names");
            return _m;
        }

        // ------------------------------------------------------------------ labels

        public string BaseName(string instanceName)
        {
            string s = (instanceName ?? "").Trim();
            return _c.StripInstanceSuffix ? InstanceSuffix.Replace(s, "") : s;
        }

        public string CleanLabel(string baseName) =>
            _c.LabelMap.TryGetValue(baseName.Trim(), out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : baseName.Trim();

        // ------------------------------------------------------------------ equipment

        private List<Placement> DropDuplicates(List<Placement> list)
        {
            if (!_c.DropDuplicates) return list;
            var kept = new List<Placement>();
            foreach (var p in list)
            {
                var twin = kept.FirstOrDefault(k => k.Definition == p.Definition && k.Mirrored == p.Mirrored
                    && (k.T - p.T).Length <= _c.DuplicatePositionTolerance
                    && Math.Abs(k.Elevation - p.Elevation) <= _c.DuplicatePositionTolerance
                    && AngleDiff(k.RotationDeg, p.RotationDeg) <= _c.DuplicateAngleToleranceDeg);
                if (twin == null) { kept.Add(p); continue; }
                _m.Log.Duplicates.Add(new[] { p.Component.InstanceName, p.Component.IdPath, twin.Component.InstanceName, twin.Component.IdPath });
            }
            return kept;
        }

        private static double AngleDiff(double a, double b)
        {
            double d = Math.Abs(((a - b) % 360 + 540) % 360 - 180);
            return d;
        }

        private void AddEquipment(List<Placement> placements)
        {
            var renamed = new SortedDictionary<string, (string to, int count)>(StringComparer.Ordinal);
            var libraryByDef = new Dictionary<Definition, LibraryShape>();
            foreach (var p in placements)
            {
                var g = p.Definition?.Geometry;
                var fp = g?.Primary;
                if (fp == null || fp.IsEmpty)
                {
                    _m.Log.Notes.Add($"{p.Component.InstanceName} ({p.Component.IdPath}) has no footprint and was not drawn.");
                    continue;
                }
                string baseName = BaseName(p.Component.InstanceName);
                string label = CleanLabel(baseName);
                if (label != p.Component.InstanceName)
                {
                    renamed.TryGetValue(baseName, out var r);
                    renamed[baseName] = (label, r.count + 1);
                }

                double theta = p.RotationDeg;
                double k = Math.Round(theta / 90.0);
                double delta = theta - 90 * k;
                if (Math.Abs(delta) <= _c.SnapToleranceDeg && Math.Abs(delta) > 1e-9)
                {
                    double snapped = ((90 * k) % 360 + 360) % 360;
                    _m.Log.Snapped.Add(new[] { label, p.Component.IdPath, theta.ToString("0.###", Inv), snapped.ToString("0", Inv) });
                    theta = snapped;
                }

                var item = FootprintItem(fp, g, p, theta);
                item.Id = "eq-" + SafeId(p.InstanceId);
                item.Layer = LayerEquipment;
                item.Kind = "equipment";
                item.Label = label;
                item.Style = item.Shape == ShapeKind.Rect ? StyleEquipment : PolygonStyle(StyleEquipment, item.PolyCoords);
                string defTitle = LibraryTitle(p.Definition);
                AddEquipmentData(item, p, g, theta, delta, defTitle);
                _m.Items.Add(item);

                foreach (var z in g.Clearances)
                {
                    var zfp = z.Shape.Map(p.MapFootprint, p.Mirrored);
                    foreach (var reg in zfp.Regions)
                    {
                        var zi = RingItem(reg.Outer, $"clr-{SafeId(p.InstanceId)}-{SafeId(z.Name)}", LayerClearances, "clearance", StyleClearance);
                        zi.Label = "";
                        zi.Data["name"] = z.Name;
                        zi.Data["equipment"] = item.Id;
                        _m.Items.Add(zi);
                    }
                }

                if (!libraryByDef.ContainsKey(p.Definition))
                {
                    var tmpl = FootprintItem(fp, g, p, 0, atOrigin: true);
                    tmpl.Label = defTitle;
                    tmpl.Kind = "equipment";
                    tmpl.Style = tmpl.Shape == ShapeKind.Rect ? StyleEquipment : PolygonStyle(StyleEquipment, tmpl.PolyCoords);
                    tmpl.Data["definition"] = defTitle;
                    tmpl.Data["width_m"] = F(item.Width);
                    tmpl.Data["depth_m"] = F(item.Height);
                    tmpl.Data["height_m"] = item.Data["height_m"];
                    tmpl.Data["sw_file"] = p.Component.FileName;
                    var shape = new LibraryShape { Title = defTitle, Template = tmpl };
                    libraryByDef[p.Definition] = shape;
                }
            }
            _m.Library.AddRange(libraryByDef.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase));
            foreach (var kv in renamed)
                _m.Log.Renamed.Add(new[] { kv.Key, kv.Value.to, kv.Value.count.ToString(Inv) });
        }

        private void AddEquipmentData(DiagramItem item, Placement p, DefinitionGeometry g, double theta, double delta, string defTitle)
        {
            var c = p.Component;
            item.Data["component_id"] = c.IdPath;
            item.Data["definition"] = defTitle;
            item.Data["sw_instance"] = c.InstanceName;
            item.Data["sw_file"] = c.FileName;
            item.Data["sw_configuration"] = c.Configuration ?? "";
            item.Data["width_m"] = F(item.Width);
            item.Data["depth_m"] = F(item.Height);
            item.Data["height_m"] = g.HasHeight ? F((g.HMax - g.HMin) * p.Scale) : "";
            item.Data["elevation_m"] = g.HasHeight ? F(p.Elevation + g.HMin * p.Scale) : F(p.Elevation);
            item.Data["rotation_deg"] = theta.ToString("0.###", Inv);
            item.Data["footprint"] = g.Primary.Source;
            if (Math.Abs(theta - p.RotationDeg) > 1e-9)
                item.Data["import_note"] = $"rotation squared from {p.RotationDeg.ToString("0.###", Inv)} deg";
            if (p.Class.Category != null) item.Data["category"] = p.Class.Category;
        }

        private string LibraryTitle(Definition d)
        {
            string fileBase = Path.GetFileNameWithoutExtension(d.Representative.FileName ?? "");
            string t = CleanLabel(BaseName(string.IsNullOrEmpty(fileBase) ? d.Representative.InstanceName : fileBase));
            // The same file mounted differently (e.g. upside down) is a separate definition; keep titles unique.
            if (!_titles.TryGetValue(d, out var unique))
            {
                unique = t;
                for (int i = 2; _titles.Values.Contains(unique); i++) unique = $"{t} ({i})";
                _titles[d] = unique;
            }
            return unique;
        }

        /// <summary>Shape for a local footprint placed with rotation <paramref name="theta"/>, centred where the original placement put it.</summary>
        private DiagramItem FootprintItem(Footprint fp, DefinitionGeometry g, Placement p, double theta, bool atOrigin = false)
        {
            var (mn, mx) = Polygon2.Bounds(fp.AllPoints());
            double s = p.Scale;
            var centerLocal = new Vec2((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2);
            var item = new DiagramItem
            {
                Center = atOrigin ? new Vec2(0, 0) : p.MapFootprint(centerLocal),
                Width = (mx.X - mn.X) * s,
                Height = (mx.Y - mn.Y) * s,
                RotationDeg = theta
            };
            var main = fp.Regions.OrderByDescending(r => r.Area).First();
            double boxArea = (mx.X - mn.X) * (mx.Y - mn.Y);
            bool rectLike = fp.Regions.Count == 1 && main.Holes.Count == 0 && boxArea > 0 && main.Area / boxArea >= 0.95;
            if (!rectLike)
            {
                item.Shape = ShapeKind.Polygon;
                bool flipV = p.Mirrored && !atOrigin;
                item.PolyCoords = main.Outer.Select(q => new Vec2(
                    Clamp01((q.X - mn.X) / Math.Max(mx.X - mn.X, 1e-12)),
                    flipV ? Clamp01((q.Y - mn.Y) / Math.Max(mx.Y - mn.Y, 1e-12)) : Clamp01((mx.Y - q.Y) / Math.Max(mx.Y - mn.Y, 1e-12)))).ToList();
                if (!atOrigin && (fp.Regions.Count > 1 || main.Holes.Count > 0))
                    _m.Log.Notes.Add($"{p.Component.InstanceName}: footprint has {fp.Regions.Count} region(s) / {main.Holes.Count} hole(s); drawn as its largest outline only.");
            }
            return item;
        }

        // ------------------------------------------------------------------ building

        private List<double> AddBuilding(List<Placement> building)
        {
            var tris = new List<double>();
            double maxHeight = 0;
            foreach (var p in building)
            {
                var g = p.Definition?.Geometry;
                if (g == null || g.LocalTriangles.Count < 9)
                {
                    _m.Log.Notes.Add($"Building element {p.Component.InstanceName} has no mesh; it was not cut into walls.");
                    continue;
                }
                for (int i = 0; i + 2 < g.LocalTriangles.Count; i += 3)
                {
                    var q = p.FloorTransform.Apply(new Vec3(g.LocalTriangles[i], g.LocalTriangles[i + 1], g.LocalTriangles[i + 2]));
                    tris.Add(q.X); tris.Add(q.Y); tris.Add(q.Z);
                    if (q.Z > maxHeight) maxHeight = q.Z;
                }
            }

            // Floor outline from the chosen elements (their full top-down footprint, e.g. the slab).
            int floorN = 0;
            foreach (var p in building.Where(b => _c.FloorFromElements.Any(f => string.Equals(f, BaseName(b.Component.InstanceName), StringComparison.OrdinalIgnoreCase))))
            {
                var fp = p.Definition?.Geometry?.Primary;
                if (fp == null) continue;
                foreach (var reg in fp.Map(p.MapFootprint, p.Mirrored).Regions)
                {
                    var fi = RingItem(reg.Outer, "floor-" + (++floorN), LayerBuilding, "floor", StyleFloor);
                    fi.Label = "";
                    fi.Data["source"] = p.Component.InstanceName;
                    fi.Data["area_m2"] = reg.Area.ToString("0.0", Inv);
                    _m.Items.Insert(0, fi);
                }
            }

            var segs = PlanSection.Segments(tris, _c.CutHeight);
            if (segs.Count == 0)
            {
                _m.Log.Notes.Add($"No building geometry crosses the cut height {_c.CutHeight} m; no walls drawn.");
                return segs;
            }
            var walls = RasterOutline.BuildSection(segs, new OutlineOptions
            {
                Resolution = _c.WallResolution,
                MaxCells = 80_000_000,
                ClosingRadiusCells = 0,
                SimplifyFactor = 1.0,
                MinIslandArea = _c.MinWallArea,
                MinHoleArea = 0.0001
            });
            int n = 0, polys = 0;
            double totalLength = 0;
            string sources = string.Join(", ", building.Select(b => b.Component.InstanceName).OrderBy(x => x, StringComparer.Ordinal));
            foreach (var reg in walls.Regions)
            {
                if (PlanSection.IsRectilinear(reg))
                {
                    foreach (var (x0, y0, x1, y1) in PlanSection.Rectangles(reg))
                    {
                        double w = x1 - x0, h = y1 - y0;
                        var wi = new DiagramItem
                        {
                            Id = "wall-" + (++n), Layer = LayerBuilding, Kind = "wall", Label = "", Style = StyleWall,
                            Center = new Vec2((x0 + x1) / 2, (y0 + y1) / 2), Width = w, Height = h
                        };
                        wi.Data["kind"] = "wall";
                        wi.Data["thickness_m"] = F(Math.Min(w, h));
                        wi.Data["length_m"] = F(Math.Max(w, h));
                        wi.Data["height_m"] = F(maxHeight);
                        wi.Data["source"] = sources;
                        totalLength += Math.Max(w, h);
                        _m.Items.Add(wi);
                    }
                }
                else
                {
                    var wi = RingItem(reg.Outer, "wall-" + (++n), LayerBuilding, "wall", StyleWall);
                    wi.Label = "";
                    wi.Data["kind"] = "wall";
                    wi.Data["height_m"] = F(maxHeight);
                    wi.Data["source"] = sources;
                    _m.Items.Add(wi);
                    polys++;
                    if (reg.Holes.Count > 0) _m.Log.Notes.Add($"Wall piece {wi.Id} has holes that diagrams.net cannot show; drawn as outline only.");
                }
            }
            _m.Log.Summary.Add($"Building: {building.Count} element(s) ({sources}) cut at {_c.CutHeight.ToString(Inv)} m -> {n} wall shape(s){(polys > 0 ? $" ({polys} non-rectangular)" : "")}, ~{totalLength:0.#} m of straight wall; {floorN} floor outline(s)");
            return segs;
        }

        private void AddRooms(List<double> segs)
        {
            if (!_c.DetectRooms || segs.Count == 0) return;
            double res = _c.RoomResolution;
            int k = (int)Math.Ceiling(_c.MaxOpeningWidth / (2 * res));
            var plan = RasterOutline.BuildSection(segs, new OutlineOptions
            {
                Resolution = res,
                MaxCells = 40_000_000,
                ClosingRadiusCells = k,
                SimplifyFactor = 1.0,
                MinIslandArea = 0,
                MinHoleArea = _c.MinRoomArea
            });
            var rooms = plan.Regions.SelectMany(r => r.Holes)
                .Select(h => Polygon2.CanonicalRing(h, true))
                .OrderByDescending(h => Math.Round(Polygon2.SignedArea(h), 6))
                .ThenBy(h => h[0].Y).ThenBy(h => h[0].X)
                .ToList();
            var names = new string[rooms.Count];
            var types = new string[rooms.Count];
            foreach (var rn in _c.RoomNames)
            {
                int idx = rooms.FindIndex(r => Polygon2.Contains(r, rn.At));
                if (idx < 0) { _m.Log.Notes.Add($"Room name '{rn.Name}' at ({rn.At.X}, {rn.At.Y}) is not inside any detected room."); continue; }
                names[idx] = rn.Name;
                types[idx] = rn.Type;
            }
            for (int i = 0; i < rooms.Count; i++)
            {
                var ring = rooms[i];
                double area = Polygon2.SignedArea(ring);
                var ri = RingItem(ring, "room-" + (i + 1), LayerRooms, "room", StyleRoom);
                ri.Data["name"] = names[i] ?? $"Room {i + 1}";
                ri.Data["type"] = types[i] ?? "room";
                ri.Data["area_m2"] = area.ToString("0.0", Inv);
                ri.Label = "%name%<br><span style=\"font-weight:normal;font-size:12px\">%area_m2% m²</span>";
                ri.UsePlaceholders = true;
                _m.Items.Add(ri);
                var (mn, mx) = Polygon2.Bounds(ring);
                _m.Log.Rooms.Add(new[] { ri.Data["name"], ri.Data["area_m2"], $"{F(mx.X - mn.X)} x {F(mx.Y - mn.Y)}", $"({F((mn.X + mx.X) / 2)}, {F((mn.Y + mx.Y) / 2)})" });
            }
            _m.Log.Summary.Add($"Rooms: {rooms.Count} enclosed space(s) of at least {_c.MinRoomArea.ToString(Inv)} m² (openings up to {_c.MaxOpeningWidth.ToString(Inv)} m bridged)");
        }

        private void AddZones()
        {
            int n = 0;
            foreach (var z in _c.Zones)
            {
                var ring = Polygon2.CanonicalRing(z.Polygon, true);
                var zi = RingItem(ring, "zone-" + (++n), LayerZones, "zone", StyleZone);
                zi.Data["name"] = z.Name;
                zi.Data["type"] = z.Type;
                zi.Data["area_m2"] = Polygon2.SignedArea(ring).ToString("0.0", Inv);
                zi.Label = "%name%";
                zi.UsePlaceholders = true;
                _m.Items.Add(zi);
            }
        }

        private void AddNotes()
        {
            var pts = _m.Items.SelectMany(i => i.Corners()).ToList();
            if (pts.Count == 0) return;
            var (mn, mx) = Polygon2.Bounds(pts);
            var title = new DiagramItem
            {
                Id = "note-title", Layer = LayerNotes, Kind = "note", Shape = ShapeKind.Text, Style = StyleText,
                Label = $"{_c.DiagramName}<br><span style=\"font-weight:normal;font-size:12px\">Scale {_c.ScalePxPerMeter.ToString(Inv)} px = 1 m · grid {(_c.GridSizePx / _c.ScalePxPerMeter * 100).ToString("0.#", Inv)} cm · imported from SolidWorks {Path.GetFileName(_res.Assembly?.RootFilePath ?? "")}</span>",
                Center = new Vec2(mn.X + 6, mx.Y + 1.0), Width = 12, Height = 1.2
            };
            title.Data["px_per_meter"] = _c.ScalePxPerMeter.ToString(Inv);
            _m.Items.Add(title);
            var bar = new DiagramItem
            {
                Id = "note-scalebar", Layer = LayerNotes, Kind = "scalebar", Label = "5 m",
                Style = "rounded=0;html=1;fillColor=#000000;strokeColor=none;fontColor=#000000;labelPosition=center;verticalLabelPosition=bottom;align=center;verticalAlign=top;fontSize=12;",
                Center = new Vec2(mn.X + 2.5, mn.Y - 0.8), Width = 5, Height = 0.1
            };
            _m.Items.Add(bar);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Axis-aligned rectangle for a 4-corner axis-aligned ring, otherwise a polygon in its bounding box.</summary>
        private static DiagramItem RingItem(List<Vec2> ring, string id, string layer, string kind, string style)
        {
            var (mn, mx) = Polygon2.Bounds(ring);
            var item = new DiagramItem
            {
                Id = id, Layer = layer, Kind = kind,
                Center = new Vec2((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2),
                Width = mx.X - mn.X, Height = mx.Y - mn.Y, Style = style
            };
            var r = Polygon2.RemoveCollinear(ring, 1e-9);
            bool isRect = r.Count == 4 && PlanSection.IsRectilinear(new PolygonRegion { Outer = r });
            if (!isRect)
            {
                item.Shape = ShapeKind.Polygon;
                item.PolyCoords = r.Select(q => new Vec2(
                    Clamp01((q.X - mn.X) / Math.Max(mx.X - mn.X, 1e-12)),
                    Clamp01((mx.Y - q.Y) / Math.Max(mx.Y - mn.Y, 1e-12)))).ToList();
                item.Style = PolygonStyle(style, item.PolyCoords);
            }
            return item;
        }

        public static string PolygonStyle(string baseStyle, List<Vec2> coords) =>
            "shape=mxgraph.basic.polygon;polyline=0;polyCoords=[" +
            string.Join(",", coords.Select(c => "[" + Math.Round(c.X, 5).ToString(Inv) + "," + Math.Round(c.Y, 5).ToString(Inv) + "]")) +
            "];" + baseStyle;

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static string SafeId(string s) => Regex.Replace(s ?? "", @"[^A-Za-z0-9_\-]", "_");

        private static string F(double v) => Math.Round(v, 3).ToString("0.###", Inv);
    }
}
