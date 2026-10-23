using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopLayout.Core.Geometry
{
    public sealed class BuildingPlanOptions
    {
        /// <summary>Plan cut height (m above floor); openings are gaps in the walls at this height.</summary>
        public double CutHeight = 1.2;
        /// <summary>Cuts above door heads; wall that exists here but not at the plan cut is an opening.</summary>
        public double[] HeaderCuts = { 2.3, 4.0 };
        public double Resolution = 0.05;
        /// <summary>Vertical sampling step used to find each opening's sill and head heights.</summary>
        public double ProfileStep = 0.05;
        public double MinOpeningWidth = 0.5;
        public double MaxWallDepth = 1.0;
        /// <summary>Empty margin around the building (m), so later morphology (gap closing) never touches the grid edge.</summary>
        public double Margin = 2.0;
        // Classification by sill / head height and width (metres).
        public double DockSillMin = 0.6, DockSillMax = 1.5, DockMinHeight = 1.8, DockMinWidth = 2.0;
        public double DoorMaxSill = 0.3;
        public double OverheadMinHead = 2.5;
        /// <summary>Height at which raised platforms are probed (above curbs and slab edges).</summary>
        public double PlatformProbeHeight = 0.3;
        public double MinPlatformArea = 2.0;
        public double MinPlatformDepth = 1.0;
    }

    public sealed class Opening
    {
        /// <summary>loadingDock | door | overheadDoor | window</summary>
        public string Type;
        /// <summary>Axis-aligned extent in floor metres (the gap in the wall).</summary>
        public Vec2 Min, Max;
        public double Width => Math.Max(Max.X - Min.X, Max.Y - Min.Y);
        public double Depth => Math.Min(Max.X - Min.X, Max.Y - Min.Y);
        public bool AlongX => Max.X - Min.X >= Max.Y - Min.Y;
        public Vec2 Center => new Vec2((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2);
        public double Sill, Head;
    }

    public sealed class BuildingPlanResult
    {
        public double Ox, Oy, R;
        public int Nx, Ny;
        /// <summary>Wall material at the plan cut.</summary>
        public bool[] Walls;
        /// <summary>Cells of detected openings (gaps that have wall above them).</summary>
        public bool[] OpeningCells;
        public List<Opening> Openings = new List<Opening>();
        public List<Platform> Platforms = new List<Platform>();
    }

    /// <summary>A raised floor area below the plan cut (e.g. a dock platform), in floor metres.</summary>
    public sealed class Platform
    {
        public List<Vec2> Outline;
        public double Area;
        /// <summary>Approximate top height above the floor (m).</summary>
        public double Top;
    }

    /// <summary>
    /// Finds door, overhead-door, loading-dock and window openings in building walls by comparing the plan
    /// cut with cuts above the door heads, then reading each opening's sill and head height from a stack
    /// of horizontal sections.
    /// </summary>
    public static class BuildingPlan
    {
        public static BuildingPlanResult Analyze(IReadOnlyList<double> tris, BuildingPlanOptions o)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                minX = Math.Min(minX, tris[i]); maxX = Math.Max(maxX, tris[i]);
                minY = Math.Min(minY, tris[i + 1]); maxY = Math.Max(maxY, tris[i + 1]);
                maxZ = Math.Max(maxZ, tris[i + 2]);
            }
            double r = o.Resolution;
            var res = new BuildingPlanResult { R = r };
            if (minX > maxX) { res.Walls = res.OpeningCells = new bool[0]; return res; }
            int pad = 2 + (int)Math.Ceiling(o.Margin / r);
            res.Ox = (Math.Floor(minX / r) - pad) * r;
            res.Oy = (Math.Floor(minY / r) - pad) * r;
            res.Nx = (int)Math.Ceiling((maxX - res.Ox) / r) + pad + 1;
            res.Ny = (int)Math.Ceiling((maxY - res.Oy) / r) + pad + 1;
            int nx = res.Nx, ny = res.Ny;
            bool[] Cut(double h) => RasterOutline.FillSection(PlanSection.Segments(tris, h), res.Ox, res.Oy, nx, ny, r);

            res.Walls = Cut(o.CutHeight);
            var diff = new bool[nx * ny];
            foreach (double h in o.HeaderCuts.Where(h => h > o.CutHeight && h < maxZ))
            {
                var above = Cut(h);
                for (int i = 0; i < diff.Length; i++) if (above[i] && !res.Walls[i]) diff[i] = true;
            }

            // Vertical profile: sections from just above the floor to the top of the building.
            var heights = new List<double>();
            for (double h = o.ProfileStep; h < maxZ; h += o.ProfileStep) heights.Add(Math.Round(h, 6));
            var layers = heights.Select(Cut).ToList();

            res.OpeningCells = new bool[nx * ny];
            foreach (var comp in Components(diff, nx, ny))
            {
                int i0 = comp.Min(c => c % nx), i1 = comp.Max(c => c % nx), j0 = comp.Min(c => c / nx), j1 = comp.Max(c => c / nx);
                var op = new Opening
                {
                    Min = new Vec2(res.Ox + i0 * r, res.Oy + j0 * r),
                    Max = new Vec2(res.Ox + (i1 + 1) * r, res.Oy + (j1 + 1) * r)
                };
                if (op.Width < o.MinOpeningWidth || op.Depth > o.MaxWallDepth) continue;
                if (!WallAtBothEnds(res.Walls, nx, ny, i0, i1, j0, j1, op.AlongX)) continue;

                // Material present in the opening footprint at each height (≥30% of its cells).
                var solid = layers.Select(g => comp.Count(c => g[c]) >= 0.3 * comp.Count).ToList();
                int k = 0;
                while (k < solid.Count && solid[k]) k++;
                op.Sill = k == 0 ? 0 : Math.Round(heights[k - 1] + o.ProfileStep / 2, 3);
                int cutIdx = heights.FindIndex(h => h >= o.CutHeight);
                int top = cutIdx < 0 ? -1 : solid.FindIndex(cutIdx, s => s);
                op.Head = top < 0 ? Math.Round(maxZ, 3) : Math.Round(heights[top] - o.ProfileStep / 2, 3);
                op.Type = Classify(op, o);
                res.Openings.Add(op);
                foreach (int c in comp) res.OpeningCells[c] = true;
            }
            res.Openings = res.Openings.OrderBy(q => Math.Round(q.Center.X, 2)).ThenByDescending(q => Math.Round(q.Center.Y, 2)).ToList();

            // Raised platforms: solid floor-like areas below the plan cut that are not walls
            // (dock platforms, landings). Their top is the highest sampled height that is still solid.
            var low = Cut(o.PlatformProbeHeight);
            var plat = new bool[nx * ny];
            for (int i = 0; i < plat.Length; i++) plat[i] = low[i] && !res.Walls[i];
            foreach (var comp in Components(plat, nx, ny))
            {
                double area = comp.Count * r * r;
                int i0 = comp.Min(c => c % nx), i1 = comp.Max(c => c % nx), j0 = comp.Min(c => c / nx), j1 = comp.Max(c => c / nx);
                double w = (i1 - i0 + 1) * r, d = (j1 - j0 + 1) * r;
                if (area < o.MinPlatformArea || Math.Min(w, d) < o.MinPlatformDepth) continue;
                double top = o.PlatformProbeHeight;
                for (int k = 0; k < heights.Count && heights[k] < o.CutHeight; k++)
                    if (heights[k] >= o.PlatformProbeHeight && comp.Count(c => layers[k][c]) >= 0.5 * comp.Count) top = heights[k];
                var grid = new bool[nx * ny];
                foreach (int c in comp) grid[c] = true;
                var outline = RasterOutline.FromGrid(grid, nx, ny, res.Ox, res.Oy, r, new OutlineOptions { Resolution = r, SimplifyFactor = 1, MinIslandArea = 0, MinHoleArea = 1e9 });
                foreach (var reg in outline.Regions)
                    res.Platforms.Add(new Platform { Outline = reg.Outer, Area = Math.Round(reg.Area, 2), Top = Math.Round(top + o.ProfileStep / 2, 2) });
            }
            return res;
        }

        public static string Classify(Opening op, BuildingPlanOptions o)
        {
            if (op.Sill >= o.DockSillMin && op.Sill <= o.DockSillMax && op.Head - op.Sill >= o.DockMinHeight && op.Width >= o.DockMinWidth)
                return "loadingDock";
            if (op.Sill > o.DoorMaxSill) return "window";
            return op.Head > o.OverheadMinHead ? "overheadDoor" : "door";
        }

        private static bool WallAtBothEnds(bool[] walls, int nx, int ny, int i0, int i1, int j0, int j1, bool alongX)
        {
            bool Any(int ia, int ib, int ja, int jb)
            {
                for (int j = Math.Max(0, ja); j <= Math.Min(ny - 1, jb); j++)
                    for (int i = Math.Max(0, ia); i <= Math.Min(nx - 1, ib); i++)
                        if (walls[j * nx + i]) return true;
                return false;
            }
            return alongX
                ? Any(i0 - 2, i0 - 1, j0, j1) && Any(i1 + 1, i1 + 2, j0, j1)
                : Any(i0, i1, j0 - 2, j0 - 1) && Any(i0, i1, j1 + 1, j1 + 2);
        }

        /// <summary>4-connected components of a boolean grid, in scan order.</summary>
        private static List<List<int>> Components(bool[] g, int nx, int ny)
        {
            var seen = new bool[g.Length];
            var list = new List<List<int>>();
            var stack = new Stack<int>();
            for (int s = 0; s < g.Length; s++)
            {
                if (!g[s] || seen[s]) continue;
                var comp = new List<int>();
                seen[s] = true;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    comp.Add(c);
                    int i = c % nx, j = c / nx;
                    foreach (int n in new[] { i > 0 ? c - 1 : -1, i < nx - 1 ? c + 1 : -1, j > 0 ? c - nx : -1, j < ny - 1 ? c + nx : -1 })
                        if (n >= 0 && g[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                }
                list.Add(comp);
            }
            return list;
        }
    }
}
