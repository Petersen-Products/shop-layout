using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopLayout.Core.Geometry
{
    /// <summary>Horizontal cut through triangle meshes (floor frame: z = elevation), as in an architectural plan.</summary>
    public static class PlanSection
    {
        /// <summary>
        /// Intersects each triangle with the plane z = <paramref name="cutHeight"/> and returns the crossing
        /// segments as (x0,y0,x1,y1). Closed solids give closed cross-section loops. Each segment is
        /// oriented from its triangle's winding (material consistently on one side), which lets the
        /// section fill use the non-zero rule so overlapping solids add up instead of cancelling.
        /// </summary>
        /// <param name="tris">Triangles in floor coordinates, 9 doubles each.</param>
        public static List<double> Segments(IReadOnlyList<double> tris, double cutHeight)
        {
            // Nudge the plane so vertices never lie exactly on it (coplanar edges would double-count).
            double h = cutHeight + 1.37e-7;
            var seg = new List<double>();
            var pts = new List<Vec2>(2);
            for (int t = 0; t + 8 < tris.Count; t += 9)
            {
                pts.Clear();
                for (int e = 0; e < 3; e++)
                {
                    int a = t + 3 * e, b = t + 3 * ((e + 1) % 3);
                    double za = tris[a + 2] - h, zb = tris[b + 2] - h;
                    if ((za > 0) == (zb > 0)) continue;
                    double f = za / (za - zb);
                    pts.Add(new Vec2(tris[a] + f * (tris[b] - tris[a]), tris[a + 1] + f * (tris[b + 1] - tris[a + 1])));
                }
                if (pts.Count == 2 && (pts[0] - pts[1]).Length > 1e-9)
                {
                    // Horizontal part of the triangle normal; walk so it points to the right of travel.
                    double ux = tris[t + 3] - tris[t], uy = tris[t + 4] - tris[t + 1], uz = tris[t + 5] - tris[t + 2];
                    double vx = tris[t + 6] - tris[t], vy = tris[t + 7] - tris[t + 1], vz = tris[t + 8] - tris[t + 2];
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz;
                    var d = pts[1] - pts[0];
                    bool flip = d.X * -ny + d.Y * nx < 0;
                    var a = flip ? pts[1] : pts[0];
                    var b = flip ? pts[0] : pts[1];
                    seg.Add(a.X); seg.Add(a.Y); seg.Add(b.X); seg.Add(b.Y);
                }
            }
            return seg;
        }

        /// <summary>True when every edge of every ring is horizontal or vertical (within <paramref name="eps"/>).</summary>
        public static bool IsRectilinear(PolygonRegion r, double eps = 1e-6)
        {
            foreach (var ring in new[] { r.Outer }.Concat(r.Holes))
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (Math.Abs(a.X - b.X) > eps && Math.Abs(a.Y - b.Y) > eps) return false;
                }
            return true;
        }

        /// <summary>
        /// Splits a rectilinear region (with holes) into non-overlapping axis-aligned rectangles
        /// (min x, min y, max x, max y): compressed-coordinate grid, horizontal runs merged downwards.
        /// </summary>
        public static List<(double x0, double y0, double x1, double y1)> Rectangles(PolygonRegion r)
        {
            var all = new[] { r.Outer }.Concat(r.Holes).SelectMany(x => x).ToList();
            var xs = all.Select(p => Math.Round(p.X, 9)).Distinct().OrderBy(v => v).ToArray();
            var ys = all.Select(p => Math.Round(p.Y, 9)).Distinct().OrderBy(v => v).ToArray();
            int nx = xs.Length - 1, ny = ys.Length - 1;
            var inside = new bool[Math.Max(0, nx) * Math.Max(0, ny)];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    var c = new Vec2((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2);
                    inside[j * nx + i] = Polygon2.Contains(r.Outer, c) && !r.Holes.Any(h => Polygon2.Contains(h, c));
                }

            var result = new List<(double, double, double, double)>();
            // Open rectangles keyed by their x-run; extended while the next row has the identical run.
            var open = new Dictionary<(int, int), int>();
            for (int j = 0; j <= ny; j++)
            {
                var runs = new HashSet<(int, int)>();
                if (j < ny)
                    for (int i = 0; i < nx; i++)
                    {
                        if (!inside[j * nx + i]) continue;
                        int s = i;
                        while (i + 1 < nx && inside[j * nx + i + 1]) i++;
                        runs.Add((s, i));
                    }
                foreach (var key in open.Keys.ToList())
                {
                    if (runs.Contains(key)) { runs.Remove(key); continue; }
                    int startRow = open[key];
                    result.Add((xs[key.Item1], ys[startRow], xs[key.Item2 + 1], ys[j]));
                    open.Remove(key);
                }
                foreach (var run in runs) open[run] = j;
            }
            return result.OrderBy(q => q.Item2).ThenBy(q => q.Item1).ToList();
        }
    }
}
