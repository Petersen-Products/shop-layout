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
        public const string LayerOpenings = "layer-openings";
        public const string LayerRooms = "layer-rooms";
        public const string LayerZones = "layer-zones";
        public const string LayerEquipment = "layer-equipment";
        public const string LayerClearances = "layer-clearances";
        public const string LayerNotes = "layer-notes";

        public const string StyleEquipment = "rounded=0;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;fontSize=10;";
        public const string StyleWall = "rounded=0;html=1;fillColor=#4d4d4d;strokeColor=none;";
        public const string StyleFloor = "rounded=0;html=1;fillColor=#f5f5f5;strokeColor=#b3b3b3;";
        // Rooms are identified by their walls; the room shape itself is invisible and only carries the label and data.
        public const string StyleRoom = "rounded=0;whiteSpace=wrap;html=1;fillColor=none;strokeColor=none;fontSize=16;fontStyle=1;fontColor=#333333;verticalAlign=middle;align=center;";
        public const string StyleRoomLabel = "text;html=1;align=center;verticalAlign=middle;fontSize=16;fontStyle=1;fontColor=#333333;";
        public const string StyleZone = "rounded=0;whiteSpace=wrap;html=1;fillColor=#d5e8d4;opacity=35;strokeColor=#82b366;dashed=1;fontSize=12;verticalAlign=top;";
        public const string StyleClearance = "rounded=0;html=1;fillColor=#f8cecc;opacity=30;strokeColor=#b85450;dashed=1;";
        public const string StyleText = "text;html=1;align=left;verticalAlign=top;fontSize=20;fontStyle=1;";
        public const string StyleDock = "rounded=0;html=1;fillColor=#fa6800;strokeColor=#c73500;";
        public const string StyleDockLabel = "text;html=1;align=center;verticalAlign=middle;fontSize=14;fontStyle=1;fontColor=#c73500;";
        public const string StyleOverheadDoor = "rounded=0;html=1;fillColor=#ffffff;strokeColor=#4d4d4d;dashed=1;dashPattern=4 2;";
        public const string StyleDoor = "rounded=0;html=1;fillColor=#ffffff;strokeColor=#4d4d4d;";
        public const string StyleWindow = "rounded=0;html=1;fillColor=#dae8fc;strokeColor=#4d4d4d;";
        public const string StylePlatform = "rounded=0;whiteSpace=wrap;html=1;fillColor=#e6e6e6;strokeColor=#808080;dashed=1;fontSize=10;fontColor=#4d4d4d;";
        public const string StyleOpeningLabel = "text;html=1;align=center;verticalAlign=middle;fontSize=10;fontColor=#4d4d4d;";

        private static readonly Regex InstanceSuffix = new Regex(@"-\d+$", RegexOptions.CultureInvariant);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly ExportResult _res;
        private readonly ImportCleanup _c;
        private readonly DiagramModel _m = new DiagramModel();
        private readonly Dictionary<Definition, string> _titles = new Dictionary<Definition, string>();
        private readonly List<List<Vec2>> _floorRings = new List<List<Vec2>>();

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
            _m.Layers.Add(new DiagramLayer { Id = LayerOpenings, Name = "Doors & Docks", Locked = true });
            _m.Layers.Add(new DiagramLayer { Id = LayerRooms, Name = "Rooms", Locked = true });
            _m.Layers.Add(new DiagramLayer { Id = LayerZones, Name = "Zones" });
            _m.Layers.Add(new DiagramLayer { Id = LayerEquipment, Name = "Equipment" });
            _m.Layers.Add(new DiagramLayer { Id = LayerClearances, Name = "Clearances" });
            _m.Layers.Add(new DiagramLayer { Id = LayerNotes, Name = "Notes" });

            var equipment = _res.Placements.Where(p => p.Kind == PlacedKind.Equipment)
                .OrderBy(p => p.Component.IdPath, IdPathComparer.Instance).ToList();
            var kept = DropDuplicates(equipment);
            AddEquipment(kept);
            var building = _res.Placements.Where(p => p.Kind == PlacedKind.Building).ToList();
            var buildingTris = AddBuilding(building);
            var plan = AddOpenings(buildingTris);
            AddRooms(plan);
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
                    _floorRings.Add(reg.Outer);
                }
            }

            var segs = PlanSection.Segments(tris, _c.CutHeight);
            if (segs.Count == 0)
            {
                _m.Log.Notes.Add($"No building geometry crosses the cut height {_c.CutHeight} m; no walls drawn.");
                return tris;
            }
            var walls = RasterOutline.BuildSection(segs, new OutlineOptions
            {
                Resolution = _c.WallResolution,
                MaxCells = 80_000_000,
                ClosingRadiusCells = 0,
                SimplifyFactor = 0, // keep exact cell steps so walls stay rectilinear and split into rectangles
                MinIslandArea = _c.MinWallArea,
                MinHoleArea = 0.0001
            });
            int n = 0, polys = 0, thinCount = 0;
            double totalLength = 0;
            string sources = string.Join(", ", building.Select(b => b.Component.InstanceName).OrderBy(x => x, StringComparer.Ordinal));
            foreach (var reg in walls.Regions)
            {
                if (PlanSection.IsRectilinear(reg))
                {
                    foreach (var (x0, y0, x1, y1) in PlanSection.Rectangles(reg))
                    {
                        double w = x1 - x0, h = y1 - y0;
                        double thickness = Math.Min(w, h);
                        // Sheet walls (siding a few mm thick) are drawn at a visible minimum thickness.
                        bool thin = thickness < _c.MinDisplayThickness;
                        var wi = new DiagramItem
                        {
                            Id = "wall-" + (++n), Layer = LayerBuilding, Kind = "wall", Label = "", Style = StyleWall,
                            Center = new Vec2((x0 + x1) / 2, (y0 + y1) / 2),
                            Width = thin && w <= h ? _c.MinDisplayThickness : w,
                            Height = thin && h < w ? _c.MinDisplayThickness : h
                        };
                        wi.Data["kind"] = "wall";
                        wi.Data["thickness_m"] = F(thickness);
                        if (thin) { wi.Data["drawn_thickness_m"] = F(_c.MinDisplayThickness); thinCount++; }
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
            _m.Log.Summary.Add($"Building: {building.Count} element(s) ({sources}) cut at {_c.CutHeight.ToString(Inv)} m -> {n} wall shape(s){(polys > 0 ? $" ({polys} non-rectangular)" : "")}, ~{totalLength:0.#} m of straight wall{(thinCount > 0 ? $" ({thinCount} sheet-thin, drawn {_c.MinDisplayThickness.ToString(Inv)} m thick)" : "")}; {floorN} floor outline(s)");
            return tris;
        }

        /// <summary>Detects doors, overhead doors, loading docks and windows, and draws them in the wall gaps.</summary>
        private BuildingPlanResult AddOpenings(List<double> tris)
        {
            if (tris.Count < 9) return null;
            var opt = _c.Openings;
            opt.CutHeight = _c.CutHeight;
            opt.Resolution = _c.RoomResolution;
            opt.Margin = Math.Max(opt.Margin, _c.MaxOpeningWidth + 0.5);
            var plan = BuildingPlan.Analyze(tris, opt);
            if (!_c.DetectOpenings) { plan.Openings.Clear(); Array.Clear(plan.OpeningCells, 0, plan.OpeningCells.Length); return plan; }

            // Manual designations (e.g. which overhead doors are loading docks) by a point near the opening.
            var forcedName = new Dictionary<Opening, string>();
            foreach (var ov in _c.OpeningOverrides)
            {
                var hit = plan.Openings
                    .Where(q => ov.At.X >= q.Min.X - 0.5 && ov.At.X <= q.Max.X + 0.5 && ov.At.Y >= q.Min.Y - 0.5 && ov.At.Y <= q.Max.Y + 0.5)
                    .OrderBy(q => (q.Center - ov.At).Length).FirstOrDefault();
                if (hit == null) { _m.Log.Notes.Add($"Opening override at ({ov.At.X}, {ov.At.Y}) is not near any detected opening."); continue; }
                if (!string.IsNullOrEmpty(ov.Type)) hit.Type = ov.Type;
                if (!string.IsNullOrEmpty(ov.Name)) forcedName[hit] = ov.Name;
            }

            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var op in plan.Openings)
            {
                counters.TryGetValue(op.Type, out int k);
                counters[op.Type] = ++k;
                string style = op.Type == "loadingDock" ? StyleDock : op.Type == "overheadDoor" ? StyleOverheadDoor : op.Type == "window" ? StyleWindow : StyleDoor;
                string name = forcedName.TryGetValue(op, out var fn) ? fn
                    : op.Type == "loadingDock" ? $"Dock {k}" : op.Type == "overheadDoor" ? $"OH Door {k}" : op.Type == "window" ? $"Window {k}" : $"Door {k}";
                string id = (op.Type == "loadingDock" ? "dock-" : op.Type == "overheadDoor" ? "ohdoor-" : op.Type == "window" ? "window-" : "door-") + k;
                var item = new DiagramItem
                {
                    Id = id, Layer = LayerOpenings, Kind = op.Type, Label = "", Style = style,
                    Center = op.Center, Width = op.Max.X - op.Min.X, Height = op.Max.Y - op.Min.Y
                };
                item.Data["name"] = name;
                item.Data["type"] = op.Type;
                item.Data["width_m"] = F(op.Width);
                item.Data["sill_m"] = F(op.Sill);
                item.Data["head_m"] = F(op.Head);
                _m.Items.Add(item);
                _m.Log.Openings.Add(new[] { name, TypeName(op.Type), F(op.Width), F(op.Sill), F(op.Head), $"({F(op.Center.X)}, {F(op.Center.Y)})" });

                // Docks and overhead doors get a callout on the outside of the building.
                if (op.Type == "loadingDock" || op.Type == "overheadDoor")
                {
                    var outward = OutsideDirection(op);
                    double off = op.Type == "loadingDock" ? 1.0 : 0.6;
                    var lbl = new DiagramItem
                    {
                        Id = id + "-label", Layer = LayerOpenings, Kind = "label", Shape = ShapeKind.Text,
                        Style = op.Type == "loadingDock" ? StyleDockLabel : StyleOpeningLabel,
                        Label = op.Type == "loadingDock" ? $"{name}<br><span style=\"font-weight:normal;font-size:10px\">{F(op.Width)} m</span>" : name,
                        Center = op.Center + outward * off, Width = Math.Max(op.Width, 2.0), Height = 0.8,
                        RotationDeg = op.AlongX ? 0 : 90
                    };
                    _m.Items.Add(lbl);
                }
            }
            if (_c.DrawPlatforms)
            {
                int pn = 0;
                foreach (var pf in plan.Platforms)
                {
                    var pi = RingItem(pf.Outline, "platform-" + (++pn), LayerOpenings, "platform", StylePlatform);
                    pi.Label = $"Raised platform<br>{F(pf.Top)} m";
                    pi.Data["type"] = "raisedPlatform";
                    pi.Data["top_m"] = F(pf.Top);
                    pi.Data["area_m2"] = pf.Area.ToString("0.0", Inv);
                    _m.Items.Add(pi);
                    _m.Log.Openings.Add(new[] { "Platform " + pn, "Raised platform", "", "", F(pf.Top), $"({F(pi.Center.X)}, {F(pi.Center.Y)}), {pi.Data["area_m2"]} m²" });
                }
                if (pn > 0) _m.Log.Summary.Add($"Raised platforms (below the plan cut): {pn}");
            }
            string Count(string t) => counters.TryGetValue(t, out int c) ? c.ToString(Inv) : "0";
            _m.Log.Summary.Add($"Openings: {Count("loadingDock")} loading dock(s), {Count("overheadDoor")} overhead door(s), {Count("door")} door(s), {Count("window")} window(s)");
            return plan;
        }

        private static string TypeName(string t) =>
            t == "loadingDock" ? "Loading dock" : t == "overheadDoor" ? "Overhead door" : t == "window" ? "Window" : "Door";

        /// <summary>Unit normal of an opening's wall pointing away from the floor slab (outside the building).</summary>
        private Vec2 OutsideDirection(Opening op)
        {
            var n = op.AlongX ? new Vec2(0, 1) : new Vec2(1, 0);
            bool Inside(Vec2 p) => _floorRings.Any(r => Polygon2.Contains(r, p));
            double probe = op.Depth / 2 + 0.5;
            if (Inside(op.Center + n * probe) && !Inside(op.Center - n * probe)) return n * -1;
            return n;
        }

        private void AddRooms(BuildingPlanResult planGrid)
        {
            if (!_c.DetectRooms || planGrid == null || planGrid.Walls.Length == 0) return;
            double res = planGrid.R;
            // Walls with every detected opening closed; small remaining gaps (interior doors) bridged by closing.
            var closed = new bool[planGrid.Walls.Length];
            for (int i = 0; i < closed.Length; i++) closed[i] = planGrid.Walls[i] || planGrid.OpeningCells[i];
            int k = (int)Math.Ceiling(_c.MaxOpeningWidth / (2 * res));
            var plan = RasterOutline.FromGrid(closed, planGrid.Nx, planGrid.Ny, planGrid.Ox, planGrid.Oy, res, new OutlineOptions
            {
                Resolution = res,
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
            // Named rooms (config order); several points in one entry merge those rooms into one.
            var claimed = new bool[rooms.Count];
            var output = new List<(List<Vec2> ring, double area, string name, string type, int members)>();
            foreach (var rn in _c.RoomNames)
            {
                var members = new List<int>();
                foreach (var pt in rn.At)
                {
                    int idx = rooms.FindIndex(r => Polygon2.Contains(r, pt));
                    if (idx < 0) { _m.Log.Notes.Add($"Room '{rn.Name}': point ({pt.X}, {pt.Y}) is not inside any detected room."); continue; }
                    if (claimed[idx]) { _m.Log.Notes.Add($"Room '{rn.Name}': point ({pt.X}, {pt.Y}) is in a room already named by an earlier entry; ignored."); continue; }
                    if (!members.Contains(idx)) members.Add(idx);
                }
                if (members.Count == 0) continue;
                foreach (int m in members) claimed[m] = true;
                var ring = members.Count == 1 ? rooms[members[0]] : MergeRooms(members.Select(m => rooms[m]).ToList(), res);
                double area = members.Sum(m => Polygon2.SignedArea(rooms[m]));
                output.Add((ring, area, rn.Name, rn.Type, members.Count));
            }
            int unnamed = 0;
            for (int i = 0; i < rooms.Count; i++)
                if (!claimed[i]) output.Add((rooms[i], Polygon2.SignedArea(rooms[i]), $"Space {++unnamed}", "room", 1));

            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (ring, area, name, type, members) in output)
            {
                string id = "room-" + Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                for (int k2 = 2; !usedIds.Add(id); k2++) id = Regex.Replace(id, @"-\d+$", "") + "-" + k2;
                var ri = RingItem(ring, id, LayerRooms, "room", StyleRoom);
                ri.Data["name"] = name;
                ri.Data["type"] = type;
                ri.Data["area_m2"] = area.ToString("0.0", Inv);
                if (members > 1) ri.Data["merged_from"] = members.ToString(Inv) + " detected spaces";
                const string label = "%name%<br><span style=\"font-weight:normal;font-size:12px\">%area_m2% m²</span>";
                ri.UsePlaceholders = true;
                _m.Items.Add(ri);
                // Centre label when the box centre is well inside the room; otherwise (L-shapes) a child text
                // at the most interior point. The child reads %name%/%area_m2% from the room through placeholders.
                var (mn, mx) = Polygon2.Bounds(ring);
                var boxCenter = new Vec2((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2);
                if (Polygon2.Contains(ring, boxCenter) && Polygon2.DistanceToBoundary(ring, boxCenter) >= 0.6) ri.Label = label;
                else
                {
                    ri.Label = "";
                    _m.Items.Add(new DiagramItem
                    {
                        Id = id + "-label", Layer = LayerRooms, Parent = ri, Kind = "label", Shape = ShapeKind.Text, Style = StyleRoomLabel,
                        Label = label, UsePlaceholders = true, Center = Polygon2.PoleOfInaccessibility(ring, 0.1), Width = 4, Height = 1.2
                    });
                }
                _m.Log.Rooms.Add(new[] { name + (members > 1 ? $" (merged {members})" : ""), ri.Data["area_m2"], $"{F(mx.X - mn.X)} x {F(mx.Y - mn.Y)}", $"({F((mn.X + mx.X) / 2)}, {F((mn.Y + mx.Y) / 2)})" });
            }
            _m.Log.Summary.Add($"Rooms: {rooms.Count} enclosed space(s) of at least {_c.MinRoomArea.ToString(Inv)} m² (detected openings closed; other gaps up to {_c.MaxOpeningWidth.ToString(Inv)} m bridged)");
        }

        /// <summary>
        /// One outline around several neighbouring rooms: rasterize them, bridge the walls between them by
        /// closing (up to ~0.8 m), and keep the largest outline.
        /// </summary>
        private static List<Vec2> MergeRooms(List<List<Vec2>> rings, double res)
        {
            int k = (int)Math.Ceiling(0.4 / res);
            var (mn, mx) = Polygon2.Bounds(rings.SelectMany(r => r));
            double ox = mn.X - (k + 2) * res, oy = mn.Y - (k + 2) * res;
            int nx = (int)Math.Ceiling((mx.X - ox) / res) + k + 3, ny = (int)Math.Ceiling((mx.Y - oy) / res) + k + 3;
            var grid = new bool[nx * ny];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    var c = new Vec2(ox + (i + 0.5) * res, oy + (j + 0.5) * res);
                    if (rings.Any(r => Polygon2.Contains(r, c))) grid[j * nx + i] = true;
                }
            var o = RasterOutline.FromGrid(grid, nx, ny, ox, oy, res, new OutlineOptions { Resolution = res, ClosingRadiusCells = k, SimplifyFactor = 1, MinIslandArea = 0, MinHoleArea = 1e9 });
            return o.Regions.Count > 0 ? o.Regions[0].Outer : rings[0];
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
