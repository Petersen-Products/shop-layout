using System;
using System.Collections.Generic;

namespace ShopLayout.Core.Geometry
{
    public sealed class OutlineOptions
    {
        /// <summary>Target grid cell size in metres. Doubled automatically until the grid fits <see cref="MaxCells"/>.</summary>
        public double Resolution = 0.005;
        public long MaxCells = 4_000_000;
        /// <summary>Morphological closing radius in cells (0 = off). Closes slits narrower than ~2·radius cells.</summary>
        public int ClosingRadiusCells = 0;
        /// <summary>Douglas–Peucker tolerance as a multiple of the effective resolution.</summary>
        public double SimplifyFactor = 1.0;
        public double MinIslandArea = 0.0004;
        public double MinHoleArea = 0.01;
    }

    public sealed class OutlineResult
    {
        public List<PolygonRegion> Regions = new List<PolygonRegion>();
        public double EffectiveResolution;
        public int GridWidth, GridHeight;
        public int TriangleCount;
    }

    /// <summary>
    /// Top-down outline of a triangle soup: union of the projected triangles, computed by rasterizing
    /// onto a fixed metric grid and tracing the cell boundaries. Unlike a convex hull it keeps concave
    /// shapes (L-shaped walls, U-shaped machines) and holes. Accuracy is about ± one cell; the grid is
    /// aligned to multiples of the resolution so repeated exports of the same geometry are identical.
    /// </summary>
    public static class RasterOutline
    {
        /// <param name="tri2d">Projected triangles, 6 doubles each (x0,y0,x1,y1,x2,y2).</param>
        public static OutlineResult Build(IReadOnlyList<double> tri2d, OutlineOptions opt)
        {
            var result = new OutlineResult { TriangleCount = tri2d.Count / 6 };
            if (tri2d.Count < 6) return result;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i + 1 < tri2d.Count; i += 2)
            {
                double x = tri2d[i], y = tri2d[i + 1];
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            if (minX > maxX) return result;

            SetupGrid(minX, minY, maxX, maxY, opt, result, out double ox, out double oy, out int nx, out int ny, out double r);
            var grid = new bool[nx * ny];
            for (int t = 0; t + 5 < tri2d.Count; t += 6)
                FillTriangle(grid, nx, ny, ox, oy, r,
                    tri2d[t], tri2d[t + 1], tri2d[t + 2], tri2d[t + 3], tri2d[t + 4], tri2d[t + 5]);
            return FinishGrid(grid, nx, ny, ox, oy, r, opt, result);
        }

        /// <summary>
        /// Plan section: the area enclosed by cross-section segments (from cutting closed solids with a
        /// horizontal plane), filled with the even-odd rule. This is how an architectural floor plan shows
        /// walls: door and window openings below/above the cut stay open.
        /// </summary>
        /// <param name="seg2d">Section segments, 4 doubles each (x0,y0,x1,y1).</param>
        public static OutlineResult BuildSection(IReadOnlyList<double> seg2d, OutlineOptions opt)
        {
            var result = new OutlineResult { TriangleCount = seg2d.Count / 4 };
            if (seg2d.Count < 4) return result;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i + 1 < seg2d.Count; i += 2)
            {
                double x = seg2d[i], y = seg2d[i + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            SetupGrid(minX, minY, maxX, maxY, opt, result, out double ox, out double oy, out int nx, out int ny, out double r);
            var grid = FillSection(seg2d, ox, oy, nx, ny, r);
            return FinishGrid(grid, nx, ny, ox, oy, r, opt, result);
        }

        /// <summary>
        /// Rasterizes oriented section segments onto a caller-defined grid (cell centres at ox + (i+½)r).
        /// Uses the non-zero winding rule so overlapping solids union; a row whose winding does not return
        /// to zero (inconsistent triangle orientation) falls back to even-odd for that row.
        /// </summary>
        public static bool[] FillSection(IReadOnlyList<double> seg2d, double ox, double oy, int nx, int ny, double r)
        {
            var grid = new bool[nx * ny];
            var rows = new List<int>[ny];
            for (int s = 0; s + 3 < seg2d.Count; s += 4)
            {
                double y0 = seg2d[s + 1], y1 = seg2d[s + 3];
                int j0 = Math.Max(0, (int)Math.Floor((Math.Min(y0, y1) - oy) / r - 0.5));
                int j1 = Math.Min(ny - 1, (int)Math.Ceiling((Math.Max(y0, y1) - oy) / r - 0.5));
                for (int j = j0; j <= j1; j++) (rows[j] ?? (rows[j] = new List<int>())).Add(s);
            }
            var xs = new List<(double x, int dir)>();
            for (int j = 0; j < ny; j++)
            {
                if (rows[j] == null) continue;
                double cy = oy + (j + 0.5) * r;
                xs.Clear();
                foreach (int s in rows[j])
                {
                    double x0 = seg2d[s], y0 = seg2d[s + 1], x1 = seg2d[s + 2], y1 = seg2d[s + 3];
                    if ((y0 > cy) == (y1 > cy)) continue;
                    xs.Add((x0 + (cy - y0) * (x1 - x0) / (y1 - y0), y1 > y0 ? 1 : -1));
                }
                xs.Sort((a, b) => a.x.CompareTo(b.x));
                int total = 0;
                foreach (var c in xs) total += c.dir;
                if (total == 0)
                {
                    int wind = 0;
                    for (int k = 0; k + 1 < xs.Count; k++)
                    {
                        wind += xs[k].dir;
                        if (wind != 0) FillSpan(grid, j, nx, ox, r, xs[k].x, xs[k + 1].x);
                    }
                }
                else
                {
                    for (int k = 0; k + 1 < xs.Count; k += 2) FillSpan(grid, j, nx, ox, r, xs[k].x, xs[k + 1].x);
                }
            }
            MarkThinMaterial(grid, seg2d, ox, oy, nx, ny, r);
            return grid;
        }

        /// <summary>
        /// Solids thinner than a cell (sheet-metal siding, 2 mm skins) have no cell centre inside them and
        /// would vanish. Where a segment's material side (left of travel) has no filled cell, the cell on
        /// the segment is marked, so such walls survive as a one-cell line. Normal walls are untouched.
        /// </summary>
        private static void MarkThinMaterial(bool[] grid, IReadOnlyList<double> seg2d, double ox, double oy, int nx, int ny, double r)
        {
            var marks = new List<int>();
            for (int s = 0; s + 3 < seg2d.Count; s += 4)
            {
                double x0 = seg2d[s], y0 = seg2d[s + 1], x1 = seg2d[s + 2], y1 = seg2d[s + 3];
                double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-12) continue;
                double lx = -dy / len * 0.5 * r, ly = dx / len * 0.5 * r; // half a cell towards the material
                int steps = Math.Max(1, (int)Math.Ceiling(len / (0.5 * r)));
                for (int k = 0; k <= steps; k++)
                {
                    double px = x0 + dx * k / steps, py = y0 + dy * k / steps;
                    int qi = (int)Math.Floor((px + lx - ox) / r), qj = (int)Math.Floor((py + ly - oy) / r);
                    if (qi < 0 || qj < 0 || qi >= nx || qj >= ny || grid[qj * nx + qi]) continue;
                    int pi = (int)Math.Floor((px - ox) / r), pj = (int)Math.Floor((py - oy) / r);
                    if (pi >= 0 && pj >= 0 && pi < nx && pj < ny) marks.Add(pj * nx + pi);
                }
            }
            foreach (int m in marks) grid[m] = true;
        }

        private static void FillSpan(bool[] grid, int j, int nx, double ox, double r, double xa, double xb)
        {
            int i0 = Math.Max(0, (int)Math.Ceiling((xa - ox) / r - 0.5));
            int i1 = Math.Min(nx - 1, (int)Math.Floor((xb - ox) / r - 0.5));
            for (int i = i0; i <= i1; i++) grid[j * nx + i] = true;
        }

        /// <summary>Traces a caller-provided occupancy grid into regions (same tracing as <see cref="Build"/>).</summary>
        public static OutlineResult FromGrid(bool[] grid, int nx, int ny, double ox, double oy, double r, OutlineOptions opt) =>
            FinishGrid(grid, nx, ny, ox, oy, r, opt, new OutlineResult { EffectiveResolution = r, GridWidth = nx, GridHeight = ny });

        private static void SetupGrid(double minX, double minY, double maxX, double maxY, OutlineOptions opt, OutlineResult result,
            out double ox, out double oy, out int nx, out int ny, out double r)
        {
            int pad = 1 + Math.Max(0, opt.ClosingRadiusCells);
            r = opt.Resolution > 0 ? opt.Resolution : 0.005;
            while (true)
            {
                ox = (Math.Floor(minX / r) - pad) * r;
                oy = (Math.Floor(minY / r) - pad) * r;
                nx = (int)Math.Ceiling((maxX - ox) / r) + pad + 1;
                ny = (int)Math.Ceiling((maxY - oy) / r) + pad + 1;
                if ((long)nx * ny <= Math.Max(1024, opt.MaxCells)) break;
                r *= 2;
            }
            result.EffectiveResolution = r;
            result.GridWidth = nx;
            result.GridHeight = ny;
        }

        private static OutlineResult FinishGrid(bool[] grid, int nx, int ny, double ox, double oy, double r, OutlineOptions opt, OutlineResult result)
        {
            if (opt.ClosingRadiusCells > 0)
            {
                grid = Dilate(grid, nx, ny, opt.ClosingRadiusCells);
                grid = Erode(grid, nx, ny, opt.ClosingRadiusCells);
            }

            var rings = TraceBoundaries(grid, nx, ny);
            var worldRings = new List<List<Vec2>>(rings.Count);
            double tol = r * opt.SimplifyFactor;
            foreach (var ring in rings)
            {
                var w = new List<Vec2>(ring.Count);
                foreach (var (i, j) in ring) w.Add(new Vec2(ox + i * r, oy + j * r));
                w = Polygon2.RemoveCollinear(w);
                w = Polygon2.SimplifyClosed(w, tol);
                worldRings.Add(w);
            }
            result.Regions = Polygon2.BuildRegions(worldRings, opt.MinIslandArea, opt.MinHoleArea);
            return result;
        }

        private static void Mark(bool[] g, int nx, int ny, double ox, double oy, double r, double x, double y)
        {
            int i = (int)Math.Floor((x - ox) / r), j = (int)Math.Floor((y - oy) / r);
            if (i >= 0 && j >= 0 && i < nx && j < ny) g[j * nx + i] = true;
        }

        private static void FillTriangle(bool[] g, int nx, int ny, double ox, double oy, double r,
            double x0, double y0, double x1, double y1, double x2, double y2)
        {
            double area2 = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            double e01 = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            double e12 = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            double e20 = Math.Sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2));
            double longest = Math.Max(e01, Math.Max(e12, e20));
            // Triangles seen exactly edge-on (vertical faces of solids) carry no plan area; the faces
            // above/below them define the outline, so they are skipped to avoid an outward bias.
            if (longest < 1e-12 || Math.Abs(area2) <= 1e-9 * longest * longest) return;

            // Thin triangles (narrower than a cell: rails, sheet-metal edges) are marked conservatively
            // along their edges so they never disappear; everything else uses unbiased cell-centre tests.
            if (Math.Abs(area2) / longest < r)
            {
                Mark(g, nx, ny, ox, oy, r, x0, y0);
                Mark(g, nx, ny, ox, oy, r, x1, y1);
                Mark(g, nx, ny, ox, oy, r, x2, y2);
                MarkEdge(g, nx, ny, ox, oy, r, x0, y0, x1, y1);
                MarkEdge(g, nx, ny, ox, oy, r, x1, y1, x2, y2);
                MarkEdge(g, nx, ny, ox, oy, r, x2, y2, x0, y0);
            }

            int i0 = Math.Max(0, (int)Math.Floor((Math.Min(x0, Math.Min(x1, x2)) - ox) / r));
            int i1 = Math.Min(nx - 1, (int)Math.Floor((Math.Max(x0, Math.Max(x1, x2)) - ox) / r));
            int j0 = Math.Max(0, (int)Math.Floor((Math.Min(y0, Math.Min(y1, y2)) - oy) / r));
            int j1 = Math.Min(ny - 1, (int)Math.Floor((Math.Max(y0, Math.Max(y1, y2)) - oy) / r));
            double sign = area2 > 0 ? 1 : -1;
            for (int j = j0; j <= j1; j++)
            {
                double cy = oy + (j + 0.5) * r;
                for (int i = i0; i <= i1; i++)
                {
                    double cx = ox + (i + 0.5) * r;
                    double e0 = ((x1 - x0) * (cy - y0) - (y1 - y0) * (cx - x0)) * sign;
                    double e1 = ((x2 - x1) * (cy - y1) - (y2 - y1) * (cx - x1)) * sign;
                    double e2 = ((x0 - x2) * (cy - y2) - (y0 - y2) * (cx - x2)) * sign;
                    if (e0 >= 0 && e1 >= 0 && e2 >= 0) g[j * nx + i] = true;
                }
            }
        }

        private static void MarkEdge(bool[] g, int nx, int ny, double ox, double oy, double r,
            double xa, double ya, double xb, double yb)
        {
            double len = Math.Sqrt((xb - xa) * (xb - xa) + (yb - ya) * (yb - ya));
            int steps = (int)Math.Ceiling(len / (r * 0.5));
            for (int s = 1; s < steps; s++)
            {
                double t = (double)s / steps;
                Mark(g, nx, ny, ox, oy, r, xa + (xb - xa) * t, ya + (yb - ya) * t);
            }
        }

        // Square-kernel morphology, done separably (rows then columns) so large radii stay fast.
        private static bool[] Dilate(bool[] g, int nx, int ny, int k) => Morph(g, nx, ny, k, dilate: true);

        private static bool[] Erode(bool[] g, int nx, int ny, int k) => Morph(g, nx, ny, k, dilate: false);

        private static bool[] Morph(bool[] g, int nx, int ny, int k, bool dilate)
        {
            var tmp = new bool[g.Length];
            var o = new bool[g.Length];
            // Sliding-window count of filled cells; outside the grid counts as empty.
            for (int j = 0; j < ny; j++)
            {
                int count = 0;
                for (int i = -k; i <= k; i++) if (i >= 0 && i < nx && g[j * nx + i]) count++;
                for (int i = 0; i < nx; i++)
                {
                    tmp[j * nx + i] = dilate ? count > 0 : count == 2 * k + 1;
                    int outI = i - k, inI = i + k + 1;
                    if (outI >= 0 && g[j * nx + outI]) count--;
                    if (inI < nx && g[j * nx + inI]) count++;
                }
            }
            for (int i = 0; i < nx; i++)
            {
                int count = 0;
                for (int j = -k; j <= k; j++) if (j >= 0 && j < ny && tmp[j * nx + i]) count++;
                for (int j = 0; j < ny; j++)
                {
                    o[j * nx + i] = dilate ? count > 0 : count == 2 * k + 1;
                    int outJ = j - k, inJ = j + k + 1;
                    if (outJ >= 0 && tmp[outJ * nx + i]) count--;
                    if (inJ < ny && tmp[inJ * nx + i]) count++;
                }
            }
            return o;
        }

        // Directions: 0 = +x, 1 = +y, 2 = −x, 3 = −y.
        private static readonly int[] Dx = { 1, 0, -1, 0 };
        private static readonly int[] Dy = { 0, 1, 0, -1 };

        /// <summary>
        /// Traces cell-boundary rings with filled cells on the left, so outer boundaries come out
        /// counter-clockwise and holes clockwise. At diagonal "saddle" vertices the left turn is taken,
        /// which keeps diagonally-touching regions as separate simple rings.
        /// </summary>
        private static List<List<(int, int)>> TraceBoundaries(bool[] g, int nx, int ny)
        {
            int vx = nx + 1, vy = ny + 1;
            var outgoing = new byte[vx * vy];
            bool Filled(int i, int j) => i >= 0 && j >= 0 && i < nx && j < ny && g[j * nx + i];

            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    if (!g[j * nx + i]) continue;
                    if (!Filled(i, j - 1)) outgoing[j * vx + i] |= 1;                 // bottom edge, +x
                    if (!Filled(i + 1, j)) outgoing[j * vx + (i + 1)] |= 2;           // right edge, +y
                    if (!Filled(i, j + 1)) outgoing[(j + 1) * vx + (i + 1)] |= 4;     // top edge, −x
                    if (!Filled(i - 1, j)) outgoing[(j + 1) * vx + i] |= 8;           // left edge, −y
                }

            var rings = new List<List<(int, int)>>();
            for (int sj = 0; sj < vy; sj++)
                for (int si = 0; si < vx; si++)
                {
                    while (outgoing[sj * vx + si] != 0)
                    {
                        int startDir = LowestBit(outgoing[sj * vx + si]);
                        var ring = new List<(int, int)>();
                        int ci = si, cj = sj, dir = startDir;
                        outgoing[sj * vx + si] &= (byte)~(1 << dir);
                        ring.Add((ci, cj));
                        int guard = 0, limit = 4 * vx * vy + 8;
                        while (guard++ < limit)
                        {
                            ci += Dx[dir]; cj += Dy[dir];
                            int v = cj * vx + ci;
                            int next = -1;
                            bool closed = false;
                            foreach (int cand in new[] { (dir + 1) & 3, dir, (dir + 3) & 3 })
                            {
                                if (ci == si && cj == sj && cand == startDir) { closed = true; break; }
                                if ((outgoing[v] & (1 << cand)) != 0) { next = cand; break; }
                            }
                            if (closed || next < 0) break;
                            outgoing[v] &= (byte)~(1 << next);
                            if (next != dir) ring.Add((ci, cj));
                            dir = next;
                        }
                        if (dir == startDir && ring.Count > 1) ring.RemoveAt(0); // start vertex was not a corner
                        if (ring.Count >= 3) rings.Add(ring);
                    }
                }
            return rings;
        }

        private static int LowestBit(byte b)
        {
            for (int i = 0; i < 4; i++) if ((b & (1 << i)) != 0) return i;
            return -1;
        }
    }
}
