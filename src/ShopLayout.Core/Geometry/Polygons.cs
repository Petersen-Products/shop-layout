using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopLayout.Core.Geometry
{
    /// <summary>One polygon with holes. Outer ring is counter-clockwise, holes clockwise.</summary>
    public sealed class PolygonRegion
    {
        public List<Vec2> Outer { get; set; } = new List<Vec2>();
        public List<List<Vec2>> Holes { get; set; } = new List<List<Vec2>>();

        public double Area => Math.Abs(Polygon2.SignedArea(Outer)) - Holes.Sum(h => Math.Abs(Polygon2.SignedArea(h)));

        public PolygonRegion Map(Func<Vec2, Vec2> f, bool flipsOrientation)
        {
            var r = new PolygonRegion
            {
                Outer = Outer.Select(f).ToList(),
                Holes = Holes.Select(h => h.Select(f).ToList()).ToList()
            };
            if (flipsOrientation)
            {
                r.Outer.Reverse();
                foreach (var h in r.Holes) h.Reverse();
            }
            Polygon2.Canonicalize(r);
            return r;
        }
    }

    /// <summary>Oriented rectangle (minimum-area bounding rectangle of a footprint).</summary>
    public sealed class OrientedRect
    {
        public Vec2 Center;
        /// <summary>Extent along the rectangle's own x axis (rotated by <see cref="AngleDeg"/>).</summary>
        public double Width;
        /// <summary>Extent along the rectangle's own y axis.</summary>
        public double Depth;
        /// <summary>Counter-clockwise angle of the rectangle's x axis, normalized to [0, 90).</summary>
        public double AngleDeg;

        public List<Vec2> Corners()
        {
            double a = AngleDeg * Math.PI / 180.0;
            var ux = new Vec2(Math.Cos(a), Math.Sin(a));
            var uy = new Vec2(-Math.Sin(a), Math.Cos(a));
            double hw = Width / 2, hd = Depth / 2;
            return new List<Vec2>
            {
                Center - ux * hw - uy * hd,
                Center + ux * hw - uy * hd,
                Center + ux * hw + uy * hd,
                Center - ux * hw + uy * hd
            };
        }
    }

    public static class Polygon2
    {
        public static double SignedArea(IReadOnlyList<Vec2> ring)
        {
            double a = 0;
            for (int i = 0, n = ring.Count; i < n; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % n];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }

        /// <summary>Even-odd point-in-ring test.</summary>
        public static bool Contains(IReadOnlyList<Vec2> ring, Vec2 p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        public static (Vec2 min, Vec2 max) Bounds(IEnumerable<Vec2> pts)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            return (new Vec2(minX, minY), new Vec2(maxX, maxY));
        }

        /// <summary>Andrew's monotone chain. Returns a counter-clockwise hull without repeated end point.</summary>
        public static List<Vec2> ConvexHull(IEnumerable<Vec2> points)
        {
            var pts = points.Distinct(new Vec2Comparer(1e-9)).ToList();
            pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            if (pts.Count < 3) return pts;
            var hull = new Vec2[2 * pts.Count];
            int k = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                while (k >= 2 && (hull[k - 1] - hull[k - 2]).Cross(pts[i] - hull[k - 2]) <= 1e-15) k--;
                hull[k++] = pts[i];
            }
            for (int i = pts.Count - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && (hull[k - 1] - hull[k - 2]).Cross(pts[i] - hull[k - 2]) <= 1e-15) k--;
                hull[k++] = pts[i];
            }
            return hull.Take(k - 1).ToList();
        }

        /// <summary>Minimum-area enclosing rectangle via rotating the frame onto each hull edge.</summary>
        public static OrientedRect MinAreaRect(IReadOnlyList<Vec2> hull)
        {
            if (hull == null || hull.Count == 0) return null;
            if (hull.Count < 3)
            {
                var (mn, mx) = Bounds(hull);
                return new OrientedRect { Center = (mn + mx) * 0.5, Width = mx.X - mn.X, Depth = mx.Y - mn.Y, AngleDeg = 0 };
            }
            OrientedRect best = null;
            double bestArea = double.MaxValue;
            var candidates = new List<double> { 0 };
            for (int i = 0; i < hull.Count; i++)
            {
                var e = hull[(i + 1) % hull.Count] - hull[i];
                if (e.Length < 1e-12) continue;
                double ang = Math.Atan2(e.Y, e.X) * 180.0 / Math.PI;
                ang = ((ang % 90) + 90) % 90;
                candidates.Add(ang);
            }
            foreach (double angDeg in candidates.Select(a => Math.Round(a, 9)).Distinct().OrderBy(a => a))
            {
                double a = angDeg * Math.PI / 180.0;
                var ux = new Vec2(Math.Cos(a), Math.Sin(a));
                var uy = new Vec2(-Math.Sin(a), Math.Cos(a));
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (var p in hull)
                {
                    double u = p.Dot(ux), v = p.Dot(uy);
                    if (u < minU) minU = u; if (u > maxU) maxU = u;
                    if (v < minV) minV = v; if (v > maxV) maxV = v;
                }
                double area = (maxU - minU) * (maxV - minV);
                // Strictly-smaller (with tolerance) keeps the smallest angle on ties, so axis-aligned shapes report 0°.
                if (area < bestArea * (1 - 1e-9) - 1e-15)
                {
                    bestArea = area;
                    double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
                    best = new OrientedRect
                    {
                        Center = ux * cu + uy * cv,
                        Width = maxU - minU,
                        Depth = maxV - minV,
                        AngleDeg = angDeg >= 90 - 1e-9 ? 0 : angDeg
                    };
                }
            }
            return best;
        }

        /// <summary>Removes consecutive duplicates and collinear vertices from a closed ring.</summary>
        public static List<Vec2> RemoveCollinear(List<Vec2> ring, double eps = 1e-12)
        {
            var r = new List<Vec2>(ring);
            bool changed = true;
            while (changed && r.Count > 3)
            {
                changed = false;
                for (int i = 0; i < r.Count && r.Count > 3; i++)
                {
                    var prev = r[(i - 1 + r.Count) % r.Count];
                    var cur = r[i];
                    var next = r[(i + 1) % r.Count];
                    double cross = (cur - prev).Cross(next - cur);
                    double scale = Math.Max((cur - prev).Length * (next - cur).Length, 1e-300);
                    if ((cur - prev).Length < eps || Math.Abs(cross) / scale < eps && (cur - prev).Dot(next - cur) >= 0)
                    {
                        r.RemoveAt(i);
                        changed = true;
                        i--;
                    }
                }
            }
            return r;
        }

        /// <summary>Douglas–Peucker simplification of a closed ring. Falls back to the input if simplification degenerates it.</summary>
        public static List<Vec2> SimplifyClosed(List<Vec2> ring, double tolerance)
        {
            if (ring.Count < 5 || tolerance <= 0) return ring;
            int far = 0;
            double farD = -1;
            for (int i = 1; i < ring.Count; i++)
            {
                double d = (ring[i] - ring[0]).Length;
                if (d > farD) { farD = d; far = i; }
            }
            var first = ring.GetRange(0, far + 1);
            var second = ring.GetRange(far, ring.Count - far);
            second.Add(ring[0]);
            var a = DouglasPeucker(first, tolerance);
            var b = DouglasPeucker(second, tolerance);
            var result = new List<Vec2>(a);
            result.AddRange(b.Skip(1).Take(b.Count - 2));
            double before = SignedArea(ring), after = SignedArea(result);
            if (result.Count < 3 || Math.Sign(before) != Math.Sign(after) || Math.Abs(after) < Math.Abs(before) * 0.5)
                return ring;
            return result;
        }

        private static List<Vec2> DouglasPeucker(List<Vec2> pts, double tol)
        {
            if (pts.Count < 3) return new List<Vec2>(pts);
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                double maxD = -1;
                int idx = -1;
                for (int i = s + 1; i < e; i++)
                {
                    double d = DistanceToSegment(pts[i], pts[s], pts[e]);
                    if (d > maxD) { maxD = d; idx = i; }
                }
                if (idx >= 0 && maxD > tol)
                {
                    keep[idx] = true;
                    stack.Push((s, idx));
                    stack.Push((idx, e));
                }
            }
            var r = new List<Vec2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) r.Add(pts[i]);
            return r;
        }

        public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            double len2 = ab.Dot(ab);
            if (len2 < 1e-300) return (p - a).Length;
            double t = Math.Max(0, Math.Min(1, (p - a).Dot(ab) / len2));
            return (p - (a + ab * t)).Length;
        }

        /// <summary>
        /// Makes a region's representation deterministic: outer CCW, holes CW, each ring starting at its
        /// lowest-(y, x) vertex, holes sorted.
        /// </summary>
        public static void Canonicalize(PolygonRegion r)
        {
            r.Outer = CanonicalRing(r.Outer, true);
            r.Holes = r.Holes.Select(h => CanonicalRing(h, false))
                .OrderBy(h => h[0].Y).ThenBy(h => h[0].X).ToList();
        }

        public static List<Vec2> CanonicalRing(List<Vec2> ring, bool ccw)
        {
            if (ring.Count == 0) return ring;
            var r = new List<Vec2>(ring);
            if ((SignedArea(r) > 0) != ccw) r.Reverse();
            int start = 0;
            for (int i = 1; i < r.Count; i++)
                if (r[i].Y < r[start].Y - 1e-12 || (Math.Abs(r[i].Y - r[start].Y) <= 1e-12 && r[i].X < r[start].X)) start = i;
            var res = new List<Vec2>(r.Count);
            for (int i = 0; i < r.Count; i++) res.Add(r[(start + i) % r.Count]);
            return res;
        }

        /// <summary>Groups rings into regions: positive-area rings are outers, negative are holes assigned to the smallest containing outer.</summary>
        public static List<PolygonRegion> BuildRegions(IEnumerable<List<Vec2>> rings, double minIslandArea, double minHoleArea)
        {
            var outers = new List<PolygonRegion>();
            var holes = new List<List<Vec2>>();
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                double a = SignedArea(ring);
                if (a > 0) { if (a >= minIslandArea) outers.Add(new PolygonRegion { Outer = ring }); }
                else if (-a >= minHoleArea) holes.Add(ring);
            }
            foreach (var h in holes)
            {
                PolygonRegion owner = null;
                double ownerArea = double.MaxValue;
                foreach (var o in outers)
                {
                    int votes = 0, n = Math.Min(5, h.Count);
                    for (int i = 0; i < n; i++) if (Contains(o.Outer, h[i * h.Count / n])) votes++;
                    double oa = Math.Abs(SignedArea(o.Outer));
                    if (votes * 2 > n && oa < ownerArea) { owner = o; ownerArea = oa; }
                }
                owner?.Holes.Add(h);
            }
            foreach (var o in outers) Canonicalize(o);
            return outers
                .OrderByDescending(o => Math.Round(o.Area, 9))
                .ThenBy(o => o.Outer[0].Y).ThenBy(o => o.Outer[0].X)
                .ToList();
        }

        public sealed class Vec2Comparer : IEqualityComparer<Vec2>
        {
            private readonly double _q;
            public Vec2Comparer(double quantum) { _q = quantum; }
            public bool Equals(Vec2 a, Vec2 b) => Math.Round(a.X / _q) == Math.Round(b.X / _q) && Math.Round(a.Y / _q) == Math.Round(b.Y / _q);
            public int GetHashCode(Vec2 p) => (Math.Round(p.X / _q).GetHashCode() * 397) ^ Math.Round(p.Y / _q).GetHashCode();
        }
    }
}
